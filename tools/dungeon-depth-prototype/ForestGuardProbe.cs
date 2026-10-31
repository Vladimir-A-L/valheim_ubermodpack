using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DungeonDepthPrototype;

internal static class ForestGuardProbe
{
    private static readonly Dictionary<string, int> deaths = new Dictionary<string, int>();
    private static GameObject? obstruction;
    internal static object Run(JObject command)
    {
        if (!ZNet.instance.IsServer() || ZNet.instance.GetPeers().Any())
            throw new InvalidOperationException("Нужна отдельная копия без игроков");
        string phase = command.Value<string>("phase") ?? "status";
        if (phase == "visit")
        {
            var data = ForestFixture.AllZdos().Values.Single(z =>
                z.GetString(MiniBossConnection.LinkKey).Length > 0 && z.GetString(ForestRuntime.LayoutKey).Length > 0);
            var rooms = (JArray)JObject.Parse(data.GetString(ForestRuntime.LayoutKey))["rooms"]!;
            var room = command["room"] == null ? rooms.Single(r => r.Value<bool>("arena")) :
                rooms.Single(r => r.Value<int>("id") == command.Value<int>("room"));
            var p = ForestRuntime.V(ForestAtmosphere.Point(room["position"]!));
            MiniBossArenaProbe.Reference = p; ZNet.instance.SetReferencePosition(p);
            return new { visiting = room.Value<int>("id") };
        }
        var guards = Character.GetAllCharacters().Where(ForestGuardRecovery.Applies).ToArray();
        if (phase == "status") return guards.Select(c => {
            var data = MiniBossRuntime.Data(c)!;
            var space = ForestGuardSpace.Find(data.GetString(ForestRuntime.Tag));
            int room = -1; string reason = "not-loaded";
            bool valid = space != null && space.Ready &&
                space.Valid(c, c.transform.position, false, out room, out reason);
            return new { entity = data.GetString(CombatRecorder.EntityKey), prefab = Utils.GetPrefabName(c.gameObject),
                origin_room = data.GetInt(ForestRuntime.RoomKey), room, ready = space?.Ready == true, valid, reason,
                owner = c.GetComponent<ZNetView>().IsOwner(), position = ForestFixture.V(c.transform.position),
                hp = c.GetHealth(), mods = PovBridge.Read(c), state = data.GetString(ForestGuardRecovery.Key),
                controller = c.GetComponent<ForestGuardRecovery>() != null,
                capsule = new { radius = c.GetComponent<CapsuleCollider>().radius,
                    height = c.GetComponent<CapsuleCollider>().height },
                weapon = (c as Humanoid)?.GetCurrentWeapon()?.m_shared.m_name };
        }).ToArray();
        if (!Plugin.Instance.Config.Bind("ForestCrypt", "GuardRecoveryProbe", false,
                "Разрушающие пробы охраны только на отдельной диагностической копии.").Value)
            throw new InvalidOperationException("Проба изменения охраны выключена");
        if (phase == "deaths") return new { deaths, drops =
            UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None).Length };
        if (phase == "unblock")
        {
            if (obstruction != null) UnityEngine.Object.Destroy(obstruction);
            return new { removed = true };
        }
        if (phase == "generate")
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var obj = UnityEngine.Object.Instantiate(ZNetScene.instance.GetPrefab("DG_ForestCrypt"),
                MiniBossConnectionProbe.Entrance + Vector3.right * 1024,
                Quaternion.Euler(0, 90 * command.Value<int>("turn"), 0));
            var generator = obj.GetComponent<DungeonGenerator>();
            MiniBossRuntime.Data(generator)!.Set(MiniBossConnection.CandidateKey, true);
            generator.m_zoneSize = new Vector3(128, 64, 128);
            try
            {
                generator.Generate(command.Value<int?>("seed") ?? 13001, ZoneSystem.SpawnMode.Full);
                var space = obj.GetComponent<ForestGuardSpace>();
                var created = ForestRuntime.Spawned.Where(v => v != null).Select(v => v.GetComponent<Character>())
                    .Where(ForestGuardRecovery.Applies).ToArray();
                return new { passed = created.Length > 0 && created.All(c =>
                        space.Valid(c, c.transform.position, true, out _, out _)),
                    guards = created.Length, corrections = ForestGuardRecovery.LastSpawnCorrections,
                    duration_ms = watch.ElapsedMilliseconds };
            }
            finally
            {
                foreach (var view in ForestRuntime.Spawned.ToArray())
                    if (view != null && view.IsValid() && view.IsOwner())
                    { view.gameObject.SetActive(false); ZNetScene.instance.Destroy(view.gameObject); }
                generator.Clear(); ZNetScene.instance.Destroy(obj);
            }
        }
        if (phase == "freeze")
        {
            foreach (var c in guards)
            {
                c.GetComponent<BaseAI>().enabled = false;
                c.enabled = false;
                c.GetComponent<Rigidbody>().isKinematic = true;
            }
            return new { frozen = guards.Length };
        }
        string entity = command.Value<string>("entity") ?? "";
        var guard = guards.Single(c => MiniBossRuntime.Data(c)!.GetString(CombatRecorder.EntityKey) == entity);
        var zdo = MiniBossRuntime.Data(guard)!;
        if (!guard.GetComponent<ZNetView>().IsOwner()) throw new InvalidOperationException("Нужен владелец");
        if (phase == "spawn-control")
        {
            var clone = UnityEngine.Object.Instantiate(ZNetScene.instance.GetPrefab(
                command.Value<string>("prefab") ?? "Skeleton"), guard.transform.position, Quaternion.identity)
                .GetComponent<Character>();
            clone.GetComponent<BaseAI>().enabled = false; clone.GetComponent<Rigidbody>().isKinematic = true;
            clone.enabled = false;
            var data = MiniBossRuntime.Data(clone)!;
            data.Set(ForestRuntime.Tag, zdo.GetString(ForestRuntime.Tag));
            data.Set(ForestRuntime.KindKey, "guard");
            data.Set(ForestRuntime.RoomKey, command.Value<int?>("room") ?? zdo.GetInt(ForestRuntime.RoomKey));
            data.Set(CombatRecorder.EntityKey, Guid.NewGuid().ToString("N"));
            PovBridge.AssignIds(clone, PovBridge.Ids(PovBridge.Read(guard)));
            var space = ForestGuardSpace.Find(data.GetString(ForestRuntime.Tag))!;
            if (!space.FreePoint(clone, data.GetInt(ForestRuntime.RoomKey), out var p))
            { ZNetScene.instance.Destroy(clone.gameObject); throw new InvalidOperationException("Нет точки контроля"); }
            clone.transform.position = p; clone.GetComponent<Rigidbody>().position = p; data.SetPosition(p);
            ForestGuardRecovery.MarkSpawn(clone);
            string receipt = data.GetString(CombatRecorder.EntityKey);
            deaths[receipt] = 0; clone.m_onDeath += () => deaths[receipt]++;
            return new { entity = data.GetString(CombatRecorder.EntityKey), position = ForestFixture.V(p) };
        }
        if (phase == "block-room")
        {
            var generator = MiniBossConnection.Instance(MiniBossConnection.Generator(zdo))!;
            var room = generator.GetComponentsInChildren<Room>()
                .OrderBy(r => Vector3.Distance(r.transform.position, guard.transform.position)).First();
            if (obstruction != null) throw new InvalidOperationException("Преграда уже создана");
            obstruction = GameObject.CreatePrimitive(PrimitiveType.Cube);
            obstruction.name = "guard-recovery-probe-obstruction";
            obstruction.layer = LayerMask.NameToLayer("static_solid");
            obstruction.transform.position = room.transform.position;
            obstruction.transform.rotation = room.transform.rotation;
            obstruction.transform.localScale = (Vector3)room.m_size + Vector3.one;
            Physics.SyncTransforms(); return new { blocked = true };
        }
        if (phase == "geometry")
        {
            var space = ForestGuardSpace.Find(zdo.GetString(ForestRuntime.Tag))!;
            var data = MiniBossConnection.Generator(zdo)!;
            var rooms = (JArray)JObject.Parse(data.GetString(ForestRuntime.LayoutKey))["rooms"]!;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var samples = rooms.Where(r => !r.Value<bool>("endcap") && !r.Value<bool>("miniboss"))
                .Select(r => {
                    int id = r.Value<int>("id"); bool found = space.FreePoint(guard, id, out var p);
                    return new { id, prefab = r.Value<string>("prefab"), found, position = ForestFixture.V(p),
                        map = space.Inspect(guard, id) };
                }).ToArray();
            return new { samples, duration_ms = watch.ElapsedMilliseconds };
        }
        if (phase == "gate-path")
        {
            var space = ForestGuardSpace.Find(zdo.GetString(ForestRuntime.Tag))!;
            int a = command.Value<int>("from"), b = command.Value<int>("to");
            var door = UnityEngine.Object.FindObjectsByType<Door>(FindObjectsSortMode.None)
                .Single(d => MiniBossRuntime.Data(d)?.GetString(ForestRuntime.Tag) ==
                    zdo.GetString(ForestRuntime.Tag) &&
                    MiniBossRuntime.Data(d)!.GetInt(ForestGates.FromKey, -1) == a &&
                    MiniBossRuntime.Data(d)!.GetInt(ForestRuntime.RoomKey, -1) == b);
            var data = MiniBossRuntime.Data(door)!; int previous = data.GetInt(ZDOVars.s_state);
            bool closed, open, reclosed;
            try
            {
                data.Set(ZDOVars.s_state, 0); closed = space.CanReturn(a, b);
                data.Set(ZDOVars.s_state, 1); open = space.CanReturn(a, b);
                data.Set(ZDOVars.s_state, 0); reclosed = space.CanReturn(a, b);
            }
            finally { data.Set(ZDOVars.s_state, previous); }
            return new { passed = !closed && open && !reclosed, closed, open, reclosed };
        }
        if (phase == "move")
        {
            var p = command["position"]!.ToObject<float[]>()!;
            var target = MiniBossLink.V(p);
            guard.transform.position = target; guard.GetComponent<Rigidbody>().position = target;
            zdo.SetPosition(target); Physics.SyncTransforms();
            return new { moved = true };
        }
        if (phase == "health")
        {
            guard.SetHealth(command.Value<float>("hp"));
            var ward = PovBridge.Method("PassiveTree.RuneWard", "Of").Invoke(null, new object[] { guard });
            if (ward != null)
            {
                AccessTools.Field(ward.GetType(), "Current").SetValue(ward, 7f);
                AccessTools.Method(ward.GetType(), "Save").Invoke(ward, null);
            }
            return new { hp = guard.GetHealth(), ward = ward == null ? null :
                AccessTools.Field(ward.GetType(), "Current").GetValue(ward) };
        }
        if (phase == "death")
        {
            string token = zdo.GetString(CombatRecorder.EntityKey);
            guard.SetHealth(0); guard.OnDeath();
            return new { entity = token, death_called = true };
        }
        if (phase == "owner")
        {
            zdo.SetOwner(0); guard.GetComponent<ForestGuardRecovery>().Tick(.5f);
            zdo.SetOwner(ZDOMan.GetSessionID()); guard.GetComponent<ForestGuardRecovery>().Tick(.5f);
            return new { reset = true, scope = "native-owner-reset-not-two-clients" };
        }
        if (phase == "room-active")
        {
            var generator = MiniBossConnection.Instance(MiniBossConnection.Generator(zdo))!;
            var room = generator.GetComponentsInChildren<Room>(true)
                .OrderBy(r => Vector3.Distance(r.transform.position, guard.transform.position)).First();
            room.gameObject.SetActive(command.Value<bool>("active"));
            return new { active = room.gameObject.activeSelf };
        }
        if (phase == "safe-point")
        {
            var space = ForestGuardSpace.Find(zdo.GetString(ForestRuntime.Tag))!;
            bool found = space.FreePoint(guard, command.Value<int?>("room") ??
                zdo.GetInt(ForestRuntime.RoomKey), out var p);
            return new { found, position = ForestFixture.V(p) };
        }
        if (phase == "tick")
        {
            var ward = PovBridge.Method("PassiveTree.RuneWard", "Of").Invoke(null, new object[] { guard });
            object? Ward() => ward == null ? null : AccessTools.Field(ward.GetType(), "Current").GetValue(ward);
            float hp = guard.GetHealth(); object? shield = Ward(); int mods = PovBridge.Read(guard);
            int drops = UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None).Length;
            string token = zdo.GetString(CombatRecorder.EntityKey);
            for (int i = 0; i < command.Value<int>("count"); i++) guard.GetComponent<ForestGuardRecovery>().Tick(.5f);
            return new { state = zdo.GetString(ForestGuardRecovery.Key),
                position = ForestFixture.V(guard.transform.position),
                hp = guard.GetHealth(), mods = PovBridge.Read(guard),
                preserved = hp == guard.GetHealth() && Equals(shield, Ward()) && mods == PovBridge.Read(guard) &&
                    token == zdo.GetString(CombatRecorder.EntityKey),
                drops_unchanged = drops == UnityEngine.Object.FindObjectsByType<ItemDrop>(
                    FindObjectsSortMode.None).Length };
        }
        throw new ArgumentException("Неизвестная проба охраны");
    }
}
