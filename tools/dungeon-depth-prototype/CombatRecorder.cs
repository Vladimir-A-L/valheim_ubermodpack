using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace DungeonDepthPrototype;

// Наблюдение без HUD, правок HP, смены владельца и вызова создающего компоненты RuneWard.Of.
internal sealed class CombatRecorder : MonoBehaviour
{
    internal const string EntityKey = "uberdungeon.combat.entity";
    private static CombatRecorder? instance;
    private StreamWriter? writer;
    private ZNet? session;
    private string stream = "", expectedWorld = "";
    private long sequence, bytes;
    private bool failed;
    private float nextFlush, nextSnapshot;
    private Type? wardType;
    private readonly Dictionary<string, Healing> healing = new Dictionary<string, Healing>();
    private const long MaxBytes = 64L * 1024 * 1024;
    internal static bool Suspended;
    [ThreadStatic] private static Stack<Capture>? calls;

    internal sealed class Capture
    {
        internal string Id = "", Parent = "";
        internal object? Before, Attacker, Hit;
        internal float Health, Ward;
        internal bool Pushed;
    }

    private sealed class Healing
    {
        internal object? Target;
        internal int Calls;
        internal float Requested, Actual;
        internal string Context = "", First = "", Last = "";
    }

    internal static void Install(GameObject host, Harmony harmony, string world)
    {
        if (!Plugin.Instance.Config.Bind("CombatAnalysis", "Enabled", false,
                "Приватные JSONL боя без HUD; нужны журналы владельцев персонажа и охраны.").Value) return;
        if (instance != null) return;
        instance = host.AddComponent<CombatRecorder>();
        instance.expectedWorld = world;
        instance.wardType = PovBridge.Assembly.GetType("PassiveTree.RuneWard", true);
        void Patch(string method, string prefix, string finalizer) => harmony.Patch(
            AccessTools.Method(typeof(Character), method),
            prefix: new HarmonyMethod(typeof(CombatRecorder), prefix) { priority = int.MaxValue },
            finalizer: new HarmonyMethod(typeof(CombatRecorder), finalizer) { priority = Priority.Last });
        Patch("RPC_Damage", nameof(BeginRpc), nameof(EndRpc));
        Patch("ApplyDamage", nameof(BeginHealth), nameof(EndHealth));
        Patch("RPC_Heal", nameof(BeginHeal), nameof(EndHeal));
    }

    internal static void Close()
    {
        if (instance != null) Destroy(instance);
        instance = null;
    }

    private bool Ready => writer != null && !failed && session != null && session == ZNet.instance;
    private static string Utc() => DateTime.UtcNow.ToString("O");
    private static float[] V(Vector3 p) => new[] { p.x, p.y, p.z };

    private void Update()
    {
        try
        {
            if (ZNet.instance == null || ZNet.instance.GetWorldName() != expectedWorld) return;
            if (session == null && !failed)
            {
                session = ZNet.instance; stream = Guid.NewGuid().ToString("N");
                string folder = Path.Combine(Paths.ConfigPath, "CombatAnalysis");
                Directory.CreateDirectory(folder);
                writer = new StreamWriter(new FileStream(Path.Combine(folder,
                    DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + stream + ".jsonl"),
                    FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
                Write("start", new { version = Plugin.Instance.Info.Metadata.Version.ToString(), world = expectedWorld,
                    role = session.IsServer() ? "server" : "client", max_bytes = MaxBytes,
                    coverage = "damage-and-heal-on-target-owner; nearby-snapshots-may-be-replicas" });
            }
            if (!Ready) return;
            if (Time.realtimeSinceStartup >= nextSnapshot)
            {
                nextSnapshot = Time.realtimeSinceStartup + 1;
                var players = Player.GetAllPlayers().Where(p => p != null).ToArray();
                var nearby = Character.GetAllCharacters().Where(c => c != null && View(c) != null &&
                    View(c).IsValid() && Interesting(c) &&
                    players.Any(p => Vector3.Distance(p.transform.position, c.transform.position) <= 35))
                    .OrderBy(c => players.Min(p =>
                        Vector3.Distance(p.transform.position, c.transform.position)))
                    .ToArray();
                Write("nearby", new { limit = 128, omitted = Math.Max(0, nearby.Length - 128),
                    entities = nearby.Take(128).Select(State).ToArray() });
            }
            if (Time.realtimeSinceStartup >= nextFlush)
            {
                nextFlush = Time.realtimeSinceStartup + 1;
                FlushHealing(); writer?.Flush();
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private static ZNetView View(Character c) => c.GetComponent<ZNetView>();

    private static bool Interesting(Character c) => c is Player ||
        View(c) != null && View(c).IsValid() &&
        View(c).GetZDO().GetString(ForestRuntime.KindKey) == "guard";

    private static bool Observe(Character c) => !Suspended && instance != null && instance.Ready && c != null &&
        View(c) != null && View(c).IsValid() && View(c).IsOwner() && Interesting(c);

    private float Ward(Character c)
    {
        var component = wardType == null ? null : c.GetComponent(wardType);
        return component == null ? View(c).GetZDO().GetFloat("pt.ward.cur") :
            (float)wardType!.GetField("Current")!.GetValue(component);
    }

    private object State(Character c)
    {
        var zdo = View(c).GetZDO();
        string stable = zdo.GetString(EntityKey);
        var weapon = (c as Humanoid)?.GetCurrentWeapon();
        int mods = zdo.GetInt(PovBridge.ModsKey);
        return new { id = stable.Length > 0 ? stable : "native:" + zdo.m_uid,
            persistent_id = stable.Length > 0, prefab = Utils.GetPrefabName(c.gameObject),
            player = c is Player, name = c is Player p ? p.GetPlayerName() : "",
            owner = View(c).IsOwner(), dungeon = zdo.GetString(ForestRuntime.Tag),
            room = zdo.GetInt(ForestRuntime.RoomKey, -1), depth = zdo.GetInt(ForestRuntime.DepthKey, -1),
            level = c.GetLevel(), modifier_mask = mods, modifier_count = PovBridge.Count(mods),
            modifiers = c is Player ? Array.Empty<string>() : PovBridge.Ids(mods),
            position = V(c.transform.position), hp = c.GetHealth(), max_hp = c.GetMaxHealth(),
            ward = Ward(c), ward_max = zdo.GetFloat("pt.ward.max"), dead = c.IsDead(),
            god = c.InGodMode(), ghost = c.InGhostMode(), stamina_fraction = c.GetStaminaPercentage(),
            weapon_current = weapon?.m_dropPrefab == null ? "" : weapon.m_dropPrefab.name,
            weapon_quality_current = weapon?.m_quality ?? 0,
            weapon_durability_current = weapon?.m_durability ?? 0 };
    }

    private static object Hit(HitData hit) => new { blunt = hit.m_damage.m_blunt, slash = hit.m_damage.m_slash,
        pierce = hit.m_damage.m_pierce, generic = hit.m_damage.m_damage, fire = hit.m_damage.m_fire,
        frost = hit.m_damage.m_frost, lightning = hit.m_damage.m_lightning, poison = hit.m_damage.m_poison,
        spirit = hit.m_damage.m_spirit, total = hit.GetTotalDamage(), skill = hit.m_skill.ToString(),
        skill_level = hit.m_skillLevel, item_quality = hit.m_itemLevel,
        dodgeable = hit.m_dodgeable, blockable = hit.m_blockable };

    private static Capture? Begin(Character c, HitData? hit)
    {
        if (!Observe(c)) return null;
        var recorder = instance!;
        var attacker = hit?.GetAttacker();
        return new Capture { Id = Guid.NewGuid().ToString("N"),
            Parent = calls != null && calls.Count > 0 ? calls.Peek().Id : "",
            Before = recorder.State(c), Health = c.GetHealth(), Ward = recorder.Ward(c),
            Attacker = attacker != null && View(attacker) != null && View(attacker).IsValid() ?
                recorder.State(attacker) : null, Hit = hit == null ? null : Hit(hit) };
    }

    private static void BeginRpc(Character __instance, HitData hit, out Capture? __state)
    {
        __state = null;
        try
        {
            __state = Begin(__instance, hit);
            if (__state == null) return;
            calls ??= new Stack<Capture>(); calls.Push(__state); __state.Pushed = true;
        }
        catch (Exception error) { instance?.Fail(error); }
    }

    private static void EndRpc(Character __instance, HitData hit, Capture? __state, Exception? __exception)
    {
        try { Finish("damage_rpc", __instance, hit, __state, __exception); }
        finally
        {
            if (__state?.Pushed == true && calls != null && calls.Count > 0 && calls.Peek() == __state) calls.Pop();
        }
    }

    private static void BeginHealth(Character __instance, HitData hit, out Capture? __state)
    {
        __state = null;
        try { __state = Begin(__instance, hit); }
        catch (Exception error) { instance?.Fail(error); }
    }

    private static void EndHealth(Character __instance, HitData hit, Capture? __state, Exception? __exception) =>
        Finish("health_damage", __instance, hit, __state, __exception);

    private static void Finish(string kind, Character target, HitData hit, Capture? captured, Exception? error)
    {
        if (captured == null || instance == null) return;
        try
        {
            instance.Write(kind, new { event_id = captured.Id, parent_rpc = captured.Parent,
                before = captured.Before, after = instance.State(target), attacker = captured.Attacker,
                input = captured.Hit, output = Hit(hit), hp_loss = captured.Health - target.GetHealth(),
                ward_loss = captured.Ward - instance.Ward(target), error = error?.GetType().Name });
        }
        catch (Exception failure) { instance.Fail(failure); }
    }

    private static void BeginHeal(Character __instance, out float __state)
    {
        __state = -1;
        try { if (Observe(__instance)) __state = __instance.GetHealth(); }
        catch (Exception error) { instance?.Fail(error); }
    }

    private static void EndHeal(Character __instance, float hp, float __state, Exception? __exception)
    {
        if (__state < 0 || instance == null || __exception != null) return;
        try
        {
            float actual = __instance.GetHealth() - __state;
            if (actual <= 0) return;
            string context = calls != null && calls.Count > 0 ? "inside_damage_rpc" : "outside_damage_rpc";
            string key = __instance.GetZDOID() + ":" + context;
            if (!instance.healing.TryGetValue(key, out var row))
            {
                if (instance.healing.Count >= 256) instance.FlushHealing();
                row = new Healing { First = Utc(), Context = context };
                instance.healing.Add(key, row);
            }
            row.Target = instance.State(__instance); row.Last = Utc(); row.Calls++;
            row.Requested += hp; row.Actual += actual;
        }
        catch (Exception error) { instance.Fail(error); }
    }

    private void FlushHealing()
    {
        foreach (var row in healing.Values) Write("healing", new { target = row.Target,
            calls = row.Calls, requested = row.Requested, actual = row.Actual,
            context = row.Context, first_utc = row.First, last_utc = row.Last });
        healing.Clear();
    }

    private void Write(string kind, object value)
    {
        if (writer == null || failed) return;
        string line = JsonConvert.SerializeObject(new { schema = 1, stream, sequence = sequence++,
            observed_at_utc = Utc(), kind, value });
        if (bytes + Encoding.UTF8.GetByteCount(line) > MaxBytes)
        {
            writer.WriteLine(JsonConvert.SerializeObject(new { schema = 1, stream, sequence,
                observed_at_utc = Utc(), kind = "truncated", value = new { reason = "size_limit" } }));
            throw new IOException("Достигнут предел приватного журнала боя 64 MiB");
        }
        writer.WriteLine(line); bytes += Encoding.UTF8.GetByteCount(line) + 1;
    }

    private void Fail(Exception error)
    {
        if (failed) return;
        failed = true;
        try { writer?.Flush(); writer?.Dispose(); } catch (IOException) { }
        writer = null; healing.Clear();
        Plugin.LogError("COMBAT_ANALYSIS_STOPPED " + error.GetType().Name);
    }

    private void OnDestroy()
    {
        try { FlushHealing(); Write("end", new { complete = !failed }); writer?.Dispose(); }
        catch (Exception error) { Fail(error); }
        writer = null; calls?.Clear();
    }

    internal static void Flush() { instance?.FlushHealing(); instance?.writer?.Flush(); }
}
