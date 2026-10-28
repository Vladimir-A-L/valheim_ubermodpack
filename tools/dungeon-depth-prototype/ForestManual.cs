using System;
using System.IO;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace DungeonDepthPrototype;

// Изолированная ручная проверка: штатный god оставляет 1 HP, ghost всегда выключен.
internal sealed class ForestManual : MonoBehaviour
{
    private sealed class Settings
    {
        public bool Enabled = false, Record = false, StartFullOnce = false, AutoPrepareOnce = false;
        public string PlayerName = "RewardQA";
        public float[] StartPosition = Array.Empty<float>();
    }
    private static Settings settings = new Settings();
    private static string attempt = "";
    private static int sequence, probeSequence;
    private static string probeAttempt = "";
    private static int probeDeaths = -1;
    private static Player? current;
    private Player? preparing;
    private bool teleportStarted, preparationFailed;
    private float prepareDeadline;
    private const string PreparedKey = "uberdungeon.qa.manual.prepared.v1";
    private float next;
    private static string Folder => Path.Combine(Paths.ConfigPath, "ForestCryptQA");
    internal static bool Target(Player? p) => p != null && ZNet.instance != null &&
        ZNet.instance.GetWorldName() == "ForestCryptQA" && !string.IsNullOrWhiteSpace(settings.PlayerName) &&
        p.GetPlayerName() == settings.PlayerName;
    private static bool Matches(Player? p) => settings.Enabled && Target(p);

    internal static void Install(GameObject host)
    {
        ReadSettings();
        ManualTelemetry.Install(host);
        var harmony = new Harmony(Plugin.Guid + ".manual-test");
        harmony.Patch(AccessTools.Method(typeof(Player), "OnSpawned"),
            prefix: new HarmonyMethod(typeof(ForestManual), nameof(Spawned)));
        harmony.Patch(AccessTools.Method(typeof(Character), "ApplyDamage"),
            prefix: new HarmonyMethod(typeof(ForestManual), nameof(BeforeDamage)),
            postfix: new HarmonyMethod(typeof(ForestManual), nameof(AfterDamage)));
        host.AddComponent<ForestManual>();
    }

    private static void ReadSettings()
    {
        string path = Path.Combine(Paths.ConfigPath, "forest-manual-test.json");
        settings = File.Exists(path)
            ? JsonConvert.DeserializeObject<Settings>(File.ReadAllText(path))! : new Settings();
    }

    private static void Spawned(Player __instance)
    {
        if (!Matches(__instance)) return;
        __instance.SetGodMode(true); __instance.SetGhostMode(false);
    }

    private void Update()
    {
        if (Time.realtimeSinceStartup < next) return;
        next = Time.realtimeSinceStartup + .5f;
        try
        {
            ReadSettings();
            var player = Player.m_localPlayer;
            if (!Matches(player))
            {
                current = null; attempt = ""; preparing = null; return;
            }
            Spawned(player!);
            if (!ManualTelemetry.Ready) return;
            if (!Prepare(player!)) return;
            if (!settings.Record) { attempt = ""; current = null; return; }
            const string freshStart = "uberdungeon.qa.manual.fresh-start.v1";
            if (settings.StartFullOnce && !player!.m_customData.ContainsKey(freshStart))
            {
                if (player.IsTeleporting() || !player.IsOnGround()) return;
                player.Heal(player.GetMaxHealth(), false);
                player.m_customData[freshStart] = DateTime.UtcNow.ToString("O");
            }
            if (current != player || attempt.Length == 0)
            {
                current = player; attempt = Guid.NewGuid().ToString("N"); sequence = 0;
                SendSample(player!, "start", player!.GetHealth(), player.GetHealth(), "", false);
                Game.instance.SavePlayerProfile(true);
            }
        }
        catch (Exception e) { Plugin.LogError("MANUAL_TEST " + e.Message); }
    }

    private bool Prepare(Player player)
    {
        if (!settings.AutoPrepareOnce || player.m_customData.ContainsKey(PreparedKey)) return true;
        if (player.InIntro()) return false;
        if (preparing != player)
        {
            preparing = player; teleportStarted = false; preparationFailed = false;
            prepareDeadline = Time.realtimeSinceStartup + 90;
        }
        if (preparationFailed) return false;
        try
        {
            if (Time.realtimeSinceStartup > prepareDeadline)
                throw new InvalidOperationException("Истёк срок подготовки у входа; требуется проверка позиции");
            var p = settings.StartPosition;
            if (p.Length != 3 || Array.Exists(p, v => float.IsNaN(v) || float.IsInfinity(v)))
                throw new InvalidOperationException("Не задана проверенная точка у входа");
            if (player.IsDead() || player.IsTeleporting() || !player.IsOnGround() || InventoryGui.IsVisible())
                return false;
            var destination = new Vector3(p[0], p[1], p[2]);
            if (!teleportStarted)
            {
                // Штатный переход после появления может уже закончиться, но cooldown ещё действует.
                if ((float)AccessTools.Field(typeof(Player), "m_teleportCooldown").GetValue(player) < 2f)
                    return false;
                ForestLoadout.Validate();
                if (!player.TeleportTo(destination, Quaternion.Euler(0, 180, 0), false))
                    throw new InvalidOperationException("Телепортация отклонена");
                teleportStarted = true;
                return false;
            }
            var offset = player.transform.position - destination;
            if (new Vector2(offset.x, offset.z).magnitude > 5 || Math.Abs(offset.y) > 12)
                throw new InvalidOperationException("Персонаж не достиг ожидаемой площадки у входа");
            if (!player.m_customData.ContainsKey(ForestLoadout.Key)) ForestLoadout.Grant(player);
            player.m_customData[PreparedKey] = DateTime.UtcNow.ToString("O");
            Game.instance.SavePlayerProfile(true);
            Record("preparation-complete", new { god = player.InGodMode(), ghost = player.InGhostMode(),
                position = new[] { player.transform.position.x, player.transform.position.y,
                    player.transform.position.z }, excluded_from_attempts = true });
            return true;
        }
        catch (Exception e)
        {
            preparationFailed = true;
            Plugin.LogError("MANUAL_PREPARATION " + e.Message);
            player.Message(MessageHud.MessageType.Center, "Подготовка теста остановлена: " + e.Message);
            return false;
        }
    }

    private static void BeforeDamage(Character __instance, out float __state)
    {
        __state = __instance == Player.m_localPlayer && Matches(Player.m_localPlayer) ? __instance.GetHealth() : -1;
    }

    private static void AfterDamage(Character __instance, HitData hit, float __state)
    {
        try
        {
            if (__state < 0) return;
            float after = __instance.GetHealth();
            if (probeDeaths >= 0)
            {
                if (ManualDeathCounter.ReachedFloor(__state, after)) probeDeaths++;
                SendSample(Player.m_localPlayer, "damage", __state, after, "controlled-probe", true);
                return;
            }
            if (!settings.Record || attempt.Length == 0 || __instance != current) return;
            if (after >= __state && after > 1.001f) return;
            var attacker = hit.GetAttacker();
            SendSample(current!, "damage", __state, after,
                attacker == null ? "environment" : Utils.GetPrefabName(attacker.gameObject), false);
        }
        catch (Exception e) { ManualTelemetry.ReportFailure(e); }
    }

    private static void SendSample(Player player, string kind, float before, float after, string attacker, bool probe)
    {
        var p = player.transform.position;
        ManualTelemetry.Enqueue(new ManualSample
        {
            Attempt = probe ? probeAttempt : attempt, Sequence = probe ? probeSequence++ : sequence++,
            Kind = kind, Character = player.GetPlayerName(), ClientUtc = DateTime.UtcNow.ToString("O"),
            Before = before, After = after, Position = new[] { p.x, p.y, p.z }, Attacker = attacker,
            God = player.InGodMode(), Ghost = player.InGhostMode(), Probe = probe
        });
    }

    private static void Record(string kind, object value)
    {
        Directory.CreateDirectory(Folder);
        File.AppendAllText(Path.Combine(Folder, "manual-attempts.jsonl"), JsonConvert.SerializeObject(new
            { observed_at_utc = DateTime.UtcNow.ToString("O"), character = settings.PlayerName,
                attempt, kind, value }) + Environment.NewLine);
    }

    internal static object Probe()
    {
        var player = Player.m_localPlayer;
        if (!Matches(player) || !ManualTelemetry.Ready || settings.Record || player!.IsDead() || player.IsTeleporting())
            throw new InvalidOperationException("Проба допустима только до начала ручной записи");
        float before = player.GetHealth();
        try
        {
            Spawned(player);
            probeDeaths = 0;
            player.SetHealth(10);
            probeAttempt = Guid.NewGuid().ToString("N"); probeSequence = 0;
            SendSample(player, "start", 10, 10, "controlled-probe", true);
            var hit = new HitData { m_point = player.transform.position };
            hit.m_damage.m_damage = 100000;
            player.ApplyDamage(hit, false, false);
            float after = player.GetHealth();
            bool passed = Math.Abs(after - 1) < .001f && !player.IsDead() &&
                player.InGodMode() && !player.InGhostMode();
            bool firstCounted = probeDeaths == 1;
            player.ApplyDamage(hit, false, false);
            bool repeatedAtFloor = probeDeaths != 1;
            player.SetHealth(10);
            player.ApplyDamage(hit, false, false);
            bool nextCounted = probeDeaths == 2;
            passed &= firstCounted && !repeatedAtFloor && nextCounted;
            if (!passed) throw new InvalidOperationException("Проверка god с порогом 1 HP не пройдена");
            return new { passed, damage_applied = true, health_at_floor = after,
                conditional_deaths = probeDeaths, repeated_at_floor_counted = repeatedAtFloor,
                god = player.InGodMode(), ghost = player.InGhostMode(), excluded_from_attempts = true };
        }
        finally { probeDeaths = -1; player.SetHealth(before); }
    }
}
