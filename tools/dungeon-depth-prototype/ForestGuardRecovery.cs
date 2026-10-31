using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DungeonDepthPrototype;

// Только живые помеченные экземпляры; никакого вызова смерти, повторного AssignIds или лечения.
internal sealed class ForestGuardRecovery : MonoBehaviour
{
    internal const string Key = "uberdungeon.guard-recovery.v1";
    internal static int LastSpawnCorrections;
    private readonly GuardRecoveryClock clock = new GuardRecoveryClock();
    private Character guard = null!;
    private ZNetView view = null!;
    private GuardRecoveryState state = new GuardRecoveryState();
    private bool owned;
    private float next, previous, noSafeSince = -1, savedAt;
    internal static bool Applies(Character c) => c != null && !c.IsPlayer() && !MiniBossRuntime.IsBoss(c) &&
        MiniBossRuntime.Data(c)?.GetString(ForestRuntime.KindKey) == "guard" &&
        MiniBossRuntime.Data(c)?.GetString(ForestRuntime.Tag).Length > 0;

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(Character), "Awake"),
            postfix: new HarmonyMethod(typeof(ForestGuardRecovery), nameof(Attach)));
        harmony.Patch(AccessTools.Method(typeof(DungeonGenerator), "Awake"),
            postfix: new HarmonyMethod(typeof(ForestGuardSpace), nameof(ForestGuardSpace.Attach)));
        foreach (var generator in Object.FindObjectsByType<DungeonGenerator>(FindObjectsSortMode.None))
            ForestGuardSpace.Attach(generator);
        foreach (var c in Character.GetAllCharacters()) Attach(c);
    }
    internal static void Attach(Character __instance)
    {
        if (Applies(__instance) && __instance.GetComponent<ForestGuardRecovery>() == null)
            __instance.gameObject.AddComponent<ForestGuardRecovery>();
    }
    private void Awake() { guard = GetComponent<Character>(); view = GetComponent<ZNetView>(); }
    private void OnDisable() { clock.Reset(); owned = false; noSafeSince = -1; }
    internal static void MarkSpawn(Character c)
    {
        var initial = new GuardRecoveryState { InitialKnown = true, Initial = ForestFixture.V(c.transform.position) };
        MiniBossRuntime.Data(c)!.Set(Key, JsonConvert.SerializeObject(initial));
        Attach(c);
    }
    internal static void ValidateSpawns(DungeonGenerator generator, IEnumerable<ZNetView> created)
    {
        // Временные BoxCollider штатной проверки объёмов ещё не удалены внешним Generate.
        var probes = generator.GetComponents<Collider>(); var flags = probes.Select(c => c.enabled).ToArray();
        try
        {
            foreach (var probe in probes) probe.enabled = false;
            ValidateCore(generator, created);
        }
        finally { for (int i = 0; i < probes.Length; i++) if (probes[i] != null) probes[i].enabled = flags[i]; }
    }
    private static void ValidateCore(DungeonGenerator generator, IEnumerable<ZNetView> created)
    {
        LastSpawnCorrections = 0;
        ForestGuardSpace.Attach(generator);
        var space = generator.GetComponent<ForestGuardSpace>(); space.RebuildNow();
        Physics.SyncTransforms();
        if (!space.Ready) throw new InvalidOperationException("Геометрия охраны не готова после генерации");
        foreach (var c in created.Where(v => v != null).Select(v => v.GetComponent<Character>()).Where(Applies))
        {
            if (!space.Valid(c, c.transform.position, true, out _, out string reason))
            {
                if (!space.FreePoint(c, MiniBossRuntime.Data(c)!.GetInt(ForestRuntime.RoomKey), out var p))
                {
                    if (Plugin.Instance.Config.Bind("ForestCrypt", "RadialDiagnostics", false).Value)
                        System.IO.File.WriteAllText(System.IO.Path.Combine(BepInEx.Paths.ConfigPath,
                            "ForestCryptQA", "guard-spawn-debug.json"), JsonConvert.SerializeObject(space.Debug(c,
                                MiniBossRuntime.Data(c)!.GetInt(ForestRuntime.RoomKey))));
                    throw new InvalidOperationException("Нет достижимой точки появления охраны: " + reason +
                        "; room=" + MiniBossRuntime.Data(c)!.GetInt(ForestRuntime.RoomKey) + "; " +
                        JsonConvert.SerializeObject(space.Inspect(c,
                            MiniBossRuntime.Data(c)!.GetInt(ForestRuntime.RoomKey))));
                }
                var initial = ForestFixture.V(c.transform.position);
                LastSpawnCorrections++;
                c.transform.position = p; c.GetComponent<Rigidbody>().position = p;
                MiniBossRuntime.Data(c)!.SetPosition(p); Physics.SyncTransforms();
                Plugin.LogInfo("GUARD_SPAWN_CORRECTED " + JsonConvert.SerializeObject(new {
                    observed_at_utc = DateTime.UtcNow.ToString("O"), reason, initial, target = ForestFixture.V(p),
                    room = MiniBossRuntime.Data(c)!.GetInt(ForestRuntime.RoomKey) }));
            }
            var data = MiniBossRuntime.Data(c)!;
            if (data.GetString(Key).Length == 0) MarkSpawn(c);
            var spawnState = JsonConvert.DeserializeObject<GuardRecoveryState>(data.GetString(Key))!;
            // Сохраняем исходную попытку появления, даже если проверка переставила новую охрану.
            spawnState.HasSafe = true; spawnState.Safe = ForestFixture.V(c.transform.position);
            spawnState.LastRoom = data.GetInt(ForestRuntime.RoomKey);
            spawnState.Reason = "validated-spawn"; spawnState.ObservedAtUtc = DateTime.UtcNow.ToString("O");
            data.Set(Key, JsonConvert.SerializeObject(spawnState)); Attach(c);
        }
    }
    private void Save() { view.GetZDO().Set(Key, JsonConvert.SerializeObject(state)); savedAt = Time.time; }
    private void Update()
    {
        if (Time.time < next) return;
        float dt = previous == 0 ? .5f : Time.time - previous;
        previous = Time.time; next = Time.time + .5f;
        Tick(dt);
    }
    internal void Tick(float dt)
    {
        if (!view.IsValid() || !view.IsOwner() || !Applies(guard))
        { owned = false; clock.Reset(); noSafeSince = -1; return; }
        if (!owned)
        {
            state = JsonConvert.DeserializeObject<GuardRecoveryState>(view.GetZDO().GetString(Key, "{}"))!;
            owned = true; clock.Reset(); noSafeSince = -1;
            if (state.Initial.Length == 0) state.Initial = ForestFixture.V(transform.position);
        }
        if (guard.IsDead() || guard.GetHealth() <= 0) { clock.Reset(); return; }
        if (state.Retired) { ZNetScene.instance.Destroy(gameObject); return; }
        var space = ForestGuardSpace.Find(view.GetZDO().GetString(ForestRuntime.Tag));
        if (space == null || !space.ReadyAt(transform.position))
        { clock.Reset(); noSafeSince = -1; return; }
        bool valid = space.Valid(guard, transform.position, false, out int room, out string reason);
        if (reason == "walkmap-unavailable" || reason == "geometry-unavailable")
        { clock.Reset(); noSafeSince = -1; return; }
        if (valid)
        {
            bool changed = !state.HasSafe || room != state.LastRoom ||
                Vector3.Distance(MiniBossLink.V(state.Safe), transform.position) > .75f;
            state.HasSafe = true; state.LastRoom = room; state.Safe = ForestFixture.V(transform.position);
            noSafeSince = -1;
            if (changed || Time.time - savedAt > 5) Save();
        }
        if (!clock.Observe(dt, true, valid)) return;
        if (state.Returns >= GuardRecoveryClock.Limit) { Retire("return-limit:" + reason); return; }
        // Текущая комната определяется независимо от исходной: выманивание остаётся свободным.
        int from = room >= 0 ? room : space.NearestRoom(transform.position);
        Vector3 target = default;
        bool found = state.HasSafe && space.CanReturn(from, state.LastRoom) &&
            space.Valid(guard, MiniBossLink.V(state.Safe), true, out _, out _) &&
            space.NoClosedDoorBetween(transform.position, MiniBossLink.V(state.Safe));
        if (found) target = MiniBossLink.V(state.Safe);
        else
        {
            int origin = view.GetZDO().GetInt(ForestRuntime.RoomKey, -1);
            found = space.CanReturn(from, origin) && space.FreePoint(guard, origin, out target,
                p => space.NoClosedDoorBetween(transform.position, p));
        }
        if (!found)
        {
            if (noSafeSince < 0) { noSafeSince = Time.time; Record("waiting-safe:" + reason, null); }
            if (Time.time - noSafeSince >= GuardRecoveryClock.Retry) Retire("no-safe-point:" + reason);
            return;
        }
        // Сохраняем квитанцию до перемещения; тот же ZDO, HP, щит, свойства и награды остаются на месте.
        state.Returns++; state.Reason = reason; state.ObservedAtUtc = DateTime.UtcNow.ToString("O");
        Save(); Record("return:" + reason, target);
        var body = GetComponent<Rigidbody>();
        transform.position = target;
        body.position = target;
        if (!body.isKinematic) { body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
        AccessTools.Field(typeof(Character), "m_pushForce").SetValue(guard, Vector3.zero);
        AccessTools.Field(typeof(Character), "m_currentVel").SetValue(guard, Vector3.zero);
        guard.GetComponent<BaseAI>()?.StopMoving();
        view.GetZDO().SetPosition(target);
        view.GetZDO().Set(ZDOVars.s_bodyVelHash, Vector3.zero);
        view.GetZDO().Set(ZDOVars.s_bodyAVelHash, Vector3.zero);
        Physics.SyncTransforms(); clock.Reset(); noSafeSince = -1;
    }
    private void Retire(string reason)
    {
        state.Retired = true; state.Reason = reason; state.ObservedAtUtc = DateTime.UtcNow.ToString("O");
        Save(); Record("despawn:" + reason, null);
        ZNetScene.instance.Destroy(gameObject);
    }
    private void Record(string reason, Vector3? target) => Plugin.LogInfo("GUARD_RECOVERY " +
        JsonConvert.SerializeObject(new { observed_at_utc = DateTime.UtcNow.ToString("O"), reason,
            entity = view.GetZDO().GetString(CombatRecorder.EntityKey), owner = view.GetZDO().GetOwner(),
            room = view.GetZDO().GetInt(ForestRuntime.RoomKey), last_room = state.LastRoom,
            initial_known = state.InitialKnown, initial = state.Initial, safe = state.Safe,
            position = ForestFixture.V(transform.position),
            target = target.HasValue ? ForestFixture.V(target.Value) : null,
            mods = view.GetZDO().GetInt(PovBridge.ModsKey), hp = guard.GetHealth(), returns = state.Returns }));
}
