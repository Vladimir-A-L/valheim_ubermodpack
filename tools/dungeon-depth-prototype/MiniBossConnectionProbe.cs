using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DungeonDepthPrototype;

internal static class MiniBossConnectionProbe
{
    internal static readonly Vector3 Entrance = new Vector3(2048, 4000, 3072);
    internal static object Run(JObject command)
    {
        if (!ZNet.instance.IsServer() || ZNet.instance.GetPeers().Any(p => p.IsReady()))
            throw new InvalidOperationException("Нужен отдельный диагностический сервер без игроков");
        string phase = command.Value<string>("phase") ?? "status";
        if (phase == "plan") return Plans();
        if (phase == "visit")
        {
            var data = ForestFixture.AllZdos().Values.FirstOrDefault(z =>
                z.GetPrefab() == MiniBossArenaPlan.Prefab.GetStableHashCode() &&
                z.GetString(MiniBossConnection.LinkKey).Length > 0);
            var position = data == null ? Entrance : data.GetPosition();
            MiniBossArenaProbe.Reference = position;
            ZNet.instance.SetReferencePosition(position);
            return new { reference_set = true };
        }
        if (phase == "release") { MiniBossArenaProbe.Reference = null; return new { released = true }; }
        if (phase == "create")
        {
            if (ForestFixture.AllZdos().Values.Any(z => z.GetString(MiniBossConnection.LinkKey).Length > 0))
                throw new InvalidOperationException("Соединённый кандидат уже существует");
            var obj = Object.Instantiate(ZNetScene.instance.GetPrefab("DG_ForestCrypt"), Entrance,
                Quaternion.identity);
            var generator = obj.GetComponent<DungeonGenerator>();
            MiniBossRuntime.Data(generator)!.Set(MiniBossConnection.CandidateKey, true);
            generator.m_zoneSize = new Vector3(128, 64, 128);
            try { generator.Generate(command.Value<int?>("seed") ?? 13001, ZoneSystem.SpawnMode.Full); }
            catch { ZNetScene.instance.Destroy(obj); throw; }
            var plan = ForestRuntime.LastPlan!;
            return new { created = true, seed = plan.Seed, attempts = plan.Attempts, steps = plan.SearchSteps,
                rooms = plan.Rooms.Count, boss_depth = plan.Rooms.Single(r => r.MiniBoss).CombatDepth };
        }
        if (phase == "content")
        {
            var generator = ForestFixture.AllZdos().Values.Single(z =>
                z.GetString(MiniBossConnection.LinkKey).Length > 0 && z.GetString(ForestRuntime.LayoutKey).Length > 0);
            var plan = JObject.Parse(generator.GetString(ForestRuntime.LayoutKey));
            var rooms = (JArray)plan["rooms"]!;
            var all = ForestFixture.AllZdos().Values.Where(z =>
                z.GetString(ForestRuntime.Tag) == generator.GetString(ForestRuntime.Tag)).ToArray();
            bool density = rooms.All(r => {
                if (r.Value<bool>("miniboss") || r.Value<bool>("endcap")) return true;
                int resources = r.Value<int>("resource_count"), chests = r.Value<int>("chest_count");
                if (resources == 0 && chests == 0 && !r.Value<bool>("arena")) return true;
                int depth = r.Value<int>("depth"), index = r.Value<int>("id");
                decimal budget = EncounterPlan.ForestTerminalBudget(GuardedChestReward.Rules, depth);
                int guards = EncounterPlan.Guards(GuardedChestReward.Rules, budget, resources > 0,
                    r.Value<bool>("arena"), unchecked(plan.Value<int>("seed") * 397 ^ index),
                    r.Value<bool>("cores")) + r.Value<int>("rare_guard_count");
                var local = all.Where(z => z.GetInt(ForestRuntime.RoomKey, -1) == index).ToArray();
                return local.Count(z => z.GetString(ForestRuntime.KindKey) == "guard") == guards &&
                    local.Count(z => z.GetString(ForestRuntime.KindKey) == "core") >= resources &&
                    local.Count(z => z.GetString(ForestRuntime.KindKey) == "chest") == chests;
            });
            int gates = all.Count(z => z.GetBool(ForestGates.GateKey));
            int quota = all.Where(z => z.GetString(ForestRuntime.KindKey) == "core")
                .Select(z => z.GetInt(ForestRuntime.RoomKey)).Distinct().Count();
            bool profiles = all.Where(z => z.GetString(ForestRuntime.KindKey) == "guard").All(z =>
                PovBridge.Count(z.GetInt(PovBridge.ModsKey)) == ForestPlan.ModifierCount(
                    z.GetInt(ForestRuntime.DepthKey)));
            return new { passed = density && profiles && quota >= 2 && quota <= 4 &&
                    gates == ((JArray)plan["gates"]!).Count + 1,
                density, profiles, resource_rooms = quota, gates, graph_nodes = rooms.Count,
                branches = plan.Value<int>("branches"), resource_arena = rooms.Single(r => r.Value<bool>("arena")),
                miniboss = rooms.Single(r => r.Value<bool>("miniboss")) };
        }
        var roots = Object.FindObjectsByType<MiniBossArena>(FindObjectsSortMode.None)
            .Where(a => MiniBossRuntime.Data(a)?.GetString(MiniBossConnection.LinkKey).Length > 0).ToArray();
        if (phase == "access") return MiniBossConnectionAccess.Create(roots.Single());
        if (phase == "unload")
        {
            MiniBossArenaProbe.Reference = Entrance + Vector3.right * 1024;
            ZNet.instance.SetReferencePosition(MiniBossArenaProbe.Reference.Value);
            return new { unloading = true };
        }
        if (phase == "load-order")
        {
            var root = roots.Single(); var data = MiniBossRuntime.Data(root)!;
            var generator = MiniBossConnection.Instance(MiniBossConnection.Generator(data))!
                .GetComponent<DungeonGenerator>();
            var link = generator.GetComponent<MiniBossRoomLink>();
            root.gameObject.SetActive(false); link.Refresh(); bool arenaMissing = !link.Open;
            root.gameObject.SetActive(true); link.Refresh(); bool restored = link.Open;
            var spec = JsonConvert.DeserializeObject<MiniBossLink>(data.GetString(MiniBossConnection.LinkKey))!;
            var room = generator.GetComponentsInChildren<Room>().Single(r => r.name == spec.ParentPrefab &&
                Vector3.Distance(r.transform.position, MiniBossLink.V(spec.ParentPosition)) < .02f);
            room.gameObject.SetActive(false); link.Refresh(); bool roomMissing = !link.Open;
            room.gameObject.SetActive(true); link.Refresh();
            return new { passed = arenaMissing && roomMissing && restored && link.Open,
                arena_missing_closed = arenaMissing, room_missing_closed = roomMissing, restored = link.Open };
        }
        if (phase == "owner")
        {
            var root = roots.Single(); var data = MiniBossRuntime.Data(root)!;
            var token = data.GetString(ForestRuntime.Tag);
            var actors = Character.GetAllCharacters().Where(c =>
                MiniBossRuntime.Data(c)?.GetString(ForestRuntime.Tag) == token).ToArray();
            var before = actors.Select(c => MiniBossRuntime.Data(c)!.GetString(MiniBossRuntime.RewardKey) +
                MiniBossRuntime.Data(c)!.GetInt(ForestRuntime.DepthKey)).ToArray();
            foreach (var actor in actors)
            {
                var zdo = MiniBossRuntime.Data(actor)!; zdo.SetOwner(0); zdo.SetOwner(ZDOMan.GetSessionID());
            }
            var after = actors.Select(c => MiniBossRuntime.Data(c)!.GetString(MiniBossRuntime.RewardKey) +
                MiniBossRuntime.Data(c)!.GetInt(ForestRuntime.DepthKey)).ToArray();
            return new { passed = before.SequenceEqual(after), actors = actors.Length,
                scope = "native-owner-reset-without-second-client" };
        }
        if (phase == "consume")
        {
            var root = roots.Single(); var data = MiniBossRuntime.Data(root)!;
            var token = data.GetString(ForestRuntime.Tag);
            var boss = Character.GetAllCharacters().Single(c => MiniBossArena.Applies(c) &&
                MiniBossRuntime.Data(c)!.GetString(ForestRuntime.Tag) == token);
            var reward = MiniBossRuntime.DropReward(boss);
            int before = ForestFixture.AllZdos().Values.Count(z => z.GetString(MiniBossRuntime.DropKey)
                .StartsWith(reward.Token + ":", StringComparison.Ordinal));
            MiniBossRuntime.DropReward(boss);
            int after = ForestFixture.AllZdos().Values.Count(z => z.GetString(MiniBossRuntime.DropKey)
                .StartsWith(reward.Token + ":", StringComparison.Ordinal));
            var picked = MiniBossValueProbe.Pick(root);
            var door = MiniBossConnection.Instance(MiniBossConnection.Gate(data))!;
            AccessTools.Method(typeof(WearNTear), "Destroy").Invoke(door.GetComponent<WearNTear>(),
                new object?[] { null, true });
            string bossId = boss.GetZDOID().ToString();
            AccessTools.Method(typeof(Character), "OnDeath").Invoke(boss, null);
            return new { boss = bossId, picked, reward, exact_once = before == after && after == reward.Items.Count };
        }
        if (phase == "shell")
        {
            var root = roots.Single(); var data = MiniBossRuntime.Data(root)!;
            var generator = MiniBossConnection.Instance(MiniBossConnection.Generator(data))!
                .GetComponent<DungeonGenerator>();
            var link = JsonConvert.DeserializeObject<MiniBossLink>(data.GetString(MiniBossConnection.LinkKey))!;
            var parent = generator.GetComponentsInChildren<Room>().Single(r => r.name == link.ParentPrefab &&
                Vector3.Distance(r.transform.position, MiniBossLink.V(link.ParentPosition)) < .02f);
            return MiniBossShellProbe.Connection(root, parent);
        }
        if (phase == "geometry")
        {
            var root = roots.Single(); var data = MiniBossRuntime.Data(root)!;
            var generator = MiniBossConnection.Instance(MiniBossConnection.Generator(data))!
                .GetComponent<DungeonGenerator>();
            var connection = generator.GetComponent<MiniBossRoomLink>(); connection.Refresh();
            MiniBossRoomLink.MapRooms(generator);
            var frame = new ArenaFrame(root.transform.position, root.transform.rotation);
            int mask = LayerMask.GetMask("static_solid", "Default", "terrain", "piece");
            var samples = (from z in new[] { -29f, -28.5f, -28f, -27.5f, -27f, -25f, -23f }
                from x in new[] { -.5f, 0, .5f }
                let p = frame.Point(new Vector3(x, .08f, z))
                select new { x, z, floor = Physics.Raycast(p + Vector3.up, Vector3.down, out var hit, 1.2f,
                    mask, QueryTriggerInteraction.Ignore) && Math.Abs(hit.point.y - (p.y - .08f)) < .04f,
                    clear = !Physics.CheckCapsule(p + Vector3.up * .4f, p + Vector3.up * 1.5f, .3f, mask,
                        QueryTriggerInteraction.Ignore) }).ToArray();
            var boss = Character.GetAllCharacters().SingleOrDefault(c => MiniBossArena.Applies(c) &&
                MiniBossRuntime.Data(c)!.GetString(ForestRuntime.Tag) == data.GetString(ForestRuntime.Tag));
            var snapshot = JsonConvert.DeserializeObject<MiniBossEncounter>(
                data.GetString(MiniBossConnection.EncounterKey))!;
            int gold = boss == null ? snapshot.Reward.Coins :
                JsonConvert.DeserializeObject<MiniBossRuntime.RewardState>(
                    MiniBossRuntime.Data(boss)!.GetString(MiniBossRuntime.RewardKey))!.Gold;
            var path = new List<Vector3>();
            var agent = ZNetScene.instance.GetPrefab("Skeleton").GetComponent<BaseAI>().m_pathAgentType;
            bool nav = Pathfinding.instance.GetPath(frame.Point(new Vector3(0, .08f, -29)),
                frame.Point(new Vector3(0, .08f, -24)), path, agent);
            return new { passed = connection.Open && samples.All(s => s.floor && s.clear),
                open = connection.Open, samples, path = nav, map_regions =
                    generator.transform.Find("miniboss-map").GetComponentsInChildren<Room>().Length,
                depth = snapshot.Depth, modifiers = snapshot.Modifiers, budget = snapshot.Budget,
                reward_items = snapshot.Reward.ItemCount, gold,
                forced = snapshot.Trial, helpers = snapshot.Helpers, prefab = snapshot.Prefab,
                boss_hp = boss == null ? 0 : boss.GetMaxHealth(),
                test_chests = root.GetComponentsInChildren<Container>().Length };
        }
        return new { loaded = roots.Length, links = ForestFixture.AllZdos().Values
            .Where(z => z.GetString(MiniBossConnection.LinkKey).Length > 0)
            .Select(z => new { id = z.m_uid.ToString(), prefab = z.GetPrefab(),
                link = z.GetString(MiniBossConnection.LinkKey), spec = z.GetString(MiniBossTrial.SpecKey),
                encounter = z.GetString(MiniBossConnection.EncounterKey) }).ToArray(),
            objects = ForestFixture.AllZdos().Values.Where(z => z.GetString(ForestRuntime.Tag).Length > 0)
                .Select(z => new { id = z.m_uid.ToString(), prefab = z.GetPrefab(), persistent = z.Persistent,
                    kind = z.GetString(ForestRuntime.KindKey), token = z.GetString(ForestRuntime.Tag),
                    depth = z.GetInt(ForestRuntime.DepthKey),
                    reward = z.GetString(MiniBossRuntime.RewardKey), role = z.GetString(MiniBossTrial.ActorKey),
                    gate = z.GetZDOID(MiniBossConnection.GateKey).ToString(),
                    value = z.GetBool(MiniBossArena.ValueKey), picked = z.GetBool(ZDOVars.s_picked) }).ToArray() };
    }

    private static object Plans()
    {
        var rooms = DungeonDB.GetRooms().Where(r => (r.m_theme & Room.Theme.ForestCrypt) != 0).ToArray();
        foreach (var room in rooms) room.m_prefab.Load();
        try
        {
            var templates = ForestRuntime.Catalog(rooms);
            var results = new List<object>();
            foreach (int length in new[] { 40, 64, 80 })
            foreach (int turn in Enumerable.Range(0, 4))
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var plan = ForestPlan.Create(templates, new ForestPlan.Point(0, 0, 0), turn,
                        new ForestPlan.Point(0, 0, 0), new ForestPlan.Point(128, 64, 128), 13001 + turn,
                        true, length);
                    results.Add(new { seed = 13001 + turn, length, turn, passed = true, plan.Attempts, plan.SearchSteps,
                        ms = watch.ElapsedMilliseconds, boss_depth = plan.Rooms.Single(r => r.MiniBoss).CombatDepth });
                }
                catch (Exception e) { results.Add(new { seed = 13001 + turn, length, turn, passed = false,
                    error = e.Message,
                    ms = watch.ElapsedMilliseconds }); }
            }
            return results;
        }
        finally { foreach (var room in rooms) room.m_prefab.Release(); }
    }
}
