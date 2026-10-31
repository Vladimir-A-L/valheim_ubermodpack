using System;
using System.Collections.Generic;
using System.Linq;
using EpicLoot;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DungeonDepthPrototype;

internal static class RadialDungeonProbe
{
    private static object TerminalReward(Container chest)
    {
        GuardedChestReward.Process(chest);
        var state = GuardedChestReward.Read(chest)!;
        if (state.Plan == null || state.Error.Length > 0)
            throw new InvalidOperationException("Конечная награда не сформирована: " + state.Error);
        int offset = 0;
        foreach (var group in state.Plan.Groups)
        foreach (int _ in Enumerable.Range(0, group.Size))
        {
            var item = GuardedChestReward.Unpack(state.Items[offset++], 1, 1).GetAllItems().Single();
            int rarity = -1; bool magic = API.TryGetRarity(item, ref rarity);
            if (magic != (group.Quality != RewardQuality.Common) ||
                magic && rarity != (int)group.Quality - 1)
                throw new InvalidOperationException("Редкость реального предмета не совпала с планом");
        }
        string items = JsonConvert.SerializeObject(state.Items);
        string inventory = GuardedChestReward.StoredInventory(chest);
        GuardedChestReward.Process(chest);
        if (items != JsonConvert.SerializeObject(GuardedChestReward.Read(chest)!.Items) ||
            inventory != GuardedChestReward.StoredInventory(chest))
            throw new InvalidOperationException("Повторный доступ изменил награду");
        return new { passed = true, state.Room.Depth, state.Budget.Value, state.Plan.Groups,
            state.Plan.Coins, state.Delivered, generated_items = state.Items.Count, repeat_unchanged = true };
    }
    private sealed class Cleanup
    {
        public int OldMissing, Pending;
        public string[] Tokens = Array.Empty<string>();
    }
    private static (float Health, float Damage) NativeFactors(Character creature)
    {
        var data = ForestRuntime.Zdo(creature);
        var desired = PovBridge.Method("PassiveTree.EnemyDifficulty", "Desired");
        float Read(bool hp) => (float)desired.Invoke(null, new object[] { creature, hp });
        float health = Read(true), damage = Read(false);
        string profile = data.GetString(RadialRuntime.ProfileKey); bool boss = data.GetBool(MiniBossRuntime.BossKey);
        try
        {
            // Только новая пробная сущность без игроков; Ensure и лечение в этом интервале не вызываются.
            data.Set(RadialRuntime.ProfileKey, ""); data.Set(MiniBossRuntime.BossKey, false);
            return (health / Read(true), damage / Read(false));
        }
        finally { data.Set(RadialRuntime.ProfileKey, profile); data.Set(MiniBossRuntime.BossKey, boss); }
    }
    internal static object Run(JObject command)
    {
        if (!ZNet.instance.IsServer() || ZNet.instance.GetPeers().Any() ||
            !Plugin.Instance.Config.Bind("ForestCrypt", "RadialDiagnostics", false).Value)
            throw new InvalidOperationException("Нужен отдельный радиальный стенд");
        var config = RadialRuntime.Configuration ?? throw new InvalidOperationException("Нет радиального конфига");
        var type = config.Types.Single(t => t.Type == "ForestCrypt");
        string phase = command.Value<string>("phase") ?? "plans";
        int first = command.Value<int?>("seed") ?? 21001;
        string profile = command.Value<string>("profile") ?? "near";
        if (phase == "plans")
        {
            int count = Math.Max(1, Math.Min(32, command.Value<int?>("count") ?? 8));
            var rooms = DungeonDB.GetRooms().Where(r => (r.m_theme & Room.Theme.ForestCrypt) != 0).ToArray();
            foreach (var room in rooms) room.m_prefab.Load();
            try
            {
                var catalog = ForestRuntime.Catalog(rooms); var results = new List<object>();
                for (int seed = first; seed < first + count; seed++)
                {
                    var choice = type.Probe(profile, seed, config.Rewards);
                    string before = JsonConvert.SerializeObject(choice);
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var plan = ForestPlan.Create(catalog, new ForestPlan.Point(0, 0, -1), seed % 4,
                            new ForestPlan.Point(0, 0, 0), ForestRuntime.P(RadialEntrance.Size), seed,
                            true, radial: choice);
                        results.Add(new { seed, choice.Category, choice.MaximumDepth, passed = true,
                            ms = watch.ElapsedMilliseconds, plan.Attempts, plan.SearchSteps,
                            rooms = plan.Rooms.Count, branches = plan.BranchCount,
                            selected_unchanged = before == JsonConvert.SerializeObject(choice) });
                    }
                    catch (Exception error) { results.Add(new { seed, choice.Category, choice.MaximumDepth,
                        passed = false, error = error.Message, ms = watch.ElapsedMilliseconds,
                        attempts = (error as ForestPlan.PlacementFailure)?.Attempts,
                        search_steps = (error as ForestPlan.PlacementFailure)?.SearchSteps,
                        selected_unchanged = before == JsonConvert.SerializeObject(choice) }); }
                }
                return results;
            }
            finally { foreach (var room in rooms) room.m_prefab.Release(); }
        }
        if (phase != "generate") throw new ArgumentException("Неизвестная проба радиального данжа");
        var beforeData = ForestFixture.AllZdos().Values.Where(z => z.GetString(ForestRuntime.Tag).Length > 0)
            .Select(z => z.m_uid).ToHashSet();
        bool keep = command.Value<bool>("keep"), retained = false;
        var position = command["position"] == null ? new Vector3(4096, 3500, 4095) :
            MiniBossLink.V(command["position"]!.ToObject<float[]>()!);
        if (keep && (position.y < 3000 || ForestFixture.AllZdos().Values.Any(z =>
                z.GetString(ForestRuntime.LayoutKey).Length > 0 &&
                Vector3.Distance(z.GetPosition(), position) < 600)))
            throw new InvalidOperationException("Для новой QA-копии нужна отдельная свободная область");
        var obj = Object.Instantiate(ZNetScene.instance.GetPrefab("DG_ForestCrypt"), position,
            Quaternion.Euler(0, (command.Value<int?>("turn") ?? 0) * 90, 0));
        var generator = obj.GetComponent<DungeonGenerator>();
        var data = ForestRuntime.Zdo(generator); data.Set(MiniBossConnection.CandidateKey, true);
        var snapshot = RadialRuntime.Probe(data, profile, first);
        string original = data.GetString(RadialRuntime.Key);
        generator.m_zoneSize = RadialEntrance.Size;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var cleanup = new Cleanup();
        ForestGuardWalkmap.CaptureDebug = command.Value<bool>("map_debug");
        try
        {
            generator.Generate(first, ZoneSystem.SpawnMode.Full);
            long generationMs = timer.ElapsedMilliseconds;
            var plan = ForestRuntime.LastPlan!;
            var creatures = ForestRuntime.Spawned.Where(v => v != null).Select(v => v.GetComponent<Character>())
                .Where(c => c != null).ToArray();
            var guards = creatures.Where(ForestGuardRecovery.Applies).ToArray();
            var space = generator.GetComponent<ForestGuardSpace>();
            bool positions = guards.All(c => space.Valid(c, c.transform.position, true, out _, out _));
            var checks = creatures.Select(c => {
                var d = ForestRuntime.Zdo(c); bool boss = MiniBossRuntime.IsBoss(c);
                var factors = snapshot.Selection.Factors(boss);
                int depth = d.GetInt(ForestRuntime.DepthKey);
                var role = boss ? RadialRole.Boss : d.GetString(ForestRuntime.KindKey) == "helper" ?
                    RadialRole.Helper : RadialRole.Guard;
                var loadout = snapshot.Selection.Loadout(depth, role);
                float expectedHealth = factors.Health * EmpoweredRules.Health(loadout.Empowered);
                float expectedDamage = factors.Damage * EmpoweredRules.Damage(loadout.Empowered);
                var actual = NativeFactors(c);
                float hp = c.GetMaxHealth();
                PovBridge.Method("PassiveTree.EnemyDifficulty", "Ensure").Invoke(null, new object[] { c });
                return new { boss, role = d.GetString(ForestRuntime.KindKey),
                    depth, mods = PovBridge.Count(PovBridge.Read(c)), empowered = d.GetInt(RadialRuntime.EmpoweredKey),
                    hp_factor = d.GetFloat(RadialRuntime.HealthKey),
                    damage_factor = d.GetFloat(RadialRuntime.DamageKey),
                    actual_health_factor = actual.Health, actual_damage_factor = actual.Damage,
                    passed = d.GetFloat(RadialRuntime.HealthKey) == expectedHealth &&
                        d.GetFloat(RadialRuntime.DamageKey) == factors.Damage && c.GetMaxHealth() == hp &&
                        Math.Abs(actual.Health - expectedHealth) < .001f &&
                        Math.Abs(actual.Damage - expectedDamage) < .001f &&
                        d.GetInt(RadialRuntime.EmpoweredKey) == loadout.Empowered &&
                        PovBridge.Ids(PovBridge.Read(c)).OrderBy(x => x)
                            .SequenceEqual(loadout.Modifiers.OrderBy(x => x)) };
            }).ToArray();
            var chestStates = ForestRuntime.Spawned.Where(v => v != null)
                .Select(v => v.GetComponent<Container>()).Where(c => c != null)
                .Select(GuardedChestReward.Read).Where(s => s != null).ToArray();
            decimal price = snapshot.Selection.Profile.ModifierPrice;
            bool rewards = chestStates.Length > 0 && chestStates.All(s =>
                s!.Rules.Schema == snapshot.Rewards.Schema &&
                s.Budget.Value == s.Budget.CombatValue * price / 10 + price * s.GuardEmpowered);
            bool terminal = chestStates.Any(s => s!.Room.Depth == snapshot.Selection.MaximumDepth);
            var delivery = command.Value<bool>("reward") ? TerminalReward(ForestRuntime.Spawned
                .Where(v => v != null).Select(v => v.GetComponent<Container>()).First(c => c != null &&
                    GuardedChestReward.Read(c)?.Room.Depth == snapshot.Selection.MaximumDepth)) : null;
            var effects = command.Value<bool>("effects") ? RadialRoomEffects.Run(space, guards) : (true, (object?)null);
            bool passed = positions && rewards && terminal && checks.All(c => c.passed) &&
                original == data.GetString(RadialRuntime.Key) && effects.Item1;
            retained = keep && passed;
            return new { passed, retained, position = ForestFixture.V(position),
                snapshot.Selection, rooms = plan.Rooms.Count,
                plan.Attempts, plan.SearchSteps, ms = timer.ElapsedMilliseconds, generation_ms = generationMs,
                objects = ForestRuntime.Spawned.Count(v => v != null), guards = guards.Length,
                positions, rewards, terminal, corrections = ForestGuardRecovery.LastSpawnCorrections,
                chests = chestStates.Length, creatures = checks, effects = effects.Item2, delivery, cleanup };
        }
        catch (Exception error)
        {
            return new { passed = false, snapshot.Selection, ms = timer.ElapsedMilliseconds,
                error = error.ToString(), attempts = (error as ForestPlan.PlacementFailure)?.Attempts,
                search_steps = (error as ForestPlan.PlacementFailure)?.SearchSteps,
                selected_unchanged = original == data.GetString(RadialRuntime.Key), cleanup };
        }
        finally
        {
            ForestGuardWalkmap.CaptureDebug = false;
            if (!retained)
            {
                foreach (var view in ForestRuntime.Spawned.ToArray())
                    if (view != null && view.IsValid() && view.IsOwner() && !beforeData.Contains(view.GetZDO().m_uid))
                    { view.gameObject.SetActive(false); ZNetScene.instance.Destroy(view.gameObject); }
                generator.Clear(); ZNetScene.instance.Destroy(obj);
                var after = ForestFixture.AllZdos().Values.Where(z => z.GetString(ForestRuntime.Tag).Length > 0)
                    .Select(z => z.m_uid).ToHashSet();
                cleanup.OldMissing = beforeData.Except(after).Count();
                var pending = ForestFixture.AllZdos().Values.Where(z =>
                    z.GetString(ForestRuntime.Tag).Length > 0 && !beforeData.Contains(z.m_uid)).ToArray();
                cleanup.Pending = pending.Length;
                cleanup.Tokens = pending.Select(z => z.GetString(ForestRuntime.Tag)).Distinct().ToArray();
                if (cleanup.OldMissing > 0)
                    throw new InvalidOperationException("После пробы исчезли прежние помеченные ZDO: " +
                        cleanup.OldMissing);
            }
        }
    }
}
