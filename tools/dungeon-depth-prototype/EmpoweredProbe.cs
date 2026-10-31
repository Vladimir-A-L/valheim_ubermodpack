using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DungeonDepthPrototype;

// Проба реальных RPC закреплённой игры и PoV. Только одноразовая копия без игроков.
internal static class EmpoweredProbe
{
    internal static object Run()
    {
        if (!ZNet.instance.IsServer() || ZNet.instance.GetPeers().Any() ||
            !Plugin.Instance.Config.Bind("ForestCrypt", "RadialDiagnostics", false).Value)
            throw new InvalidOperationException("Нужен отдельный радиальный стенд без игроков");
        var actors = new List<Character>();
        var oldDrops = Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None).ToHashSet();
        var rows = new List<object>();
        var rpc = AccessTools.Method(typeof(Character), "RPC_Damage");
        var cfg = RadialRuntime.Configuration!;
        var choice = cfg.Types.Single(t => t.Type == "ForestCrypt").Probe("far", 17, cfg.Rewards);
        Character Spawn(float x)
        {
            var c = Object.Instantiate(ZNetScene.instance.GetPrefab("Skeleton"),
                new Vector3(x, 4200, 0), Quaternion.identity).GetComponent<Character>();
            actors.Add(c); c.enabled = false; c.GetComponent<BaseAI>().enabled = false;
            c.GetComponent<Rigidbody>().isKinematic = true;
            PovBridge.AssignIds(c); c.SetHealth(c.GetMaxHealth());
            return c;
        }
        void Mark(Character c, RadialRole role, int depth)
        {
            ForestRuntime.Zdo(c).Set(ForestRuntime.Tag, "empowered-native-probe");
            RadialRuntime.Mark(c, choice, role == RadialRole.Boss, depth, role);
            c.SetHealth(c.GetMaxHealth());
        }
        void Require(bool condition, string label, object detail)
        {
            rows.Add(new { check = label, passed = condition, detail });
            if (!condition) throw new InvalidOperationException("Empowered: " + label + ": " +
                Newtonsoft.Json.JsonConvert.SerializeObject(detail));
        }
        bool Close(float a, float b) => Math.Abs(a - b) < .002f;
        HitData Hit(Character? attacker, bool ranged = false)
        {
            var h = new HitData { m_ranged = ranged, m_dodgeable = false, m_blockable = false };
            h.m_damage.m_damage = 1;
            if (attacker != null) h.SetAttacker(attacker);
            return h;
        }
        try
        {
            foreach (var role in new[] { RadialRole.Guard, RadialRole.Boss, RadialRole.Helper })
            foreach (int depth in role == RadialRole.Guard ? new[] { 15, 16, 18, 40 } : new[] { 29, 30, 32, 40 })
            {
                var c = Spawn(100 + actors.Count * 5); float hp = c.GetMaxHealth();
                Mark(c, role, depth); int n = choice.Loadout(depth, role).Empowered;
                float expected = hp * choice.Factors(role == RadialRole.Boss).Health * EmpoweredRules.Health(n);
                Require(Close(c.GetMaxHealth() / expected, 1), "native-max-health",
                    new { role, depth, stacks = n, expected, actual = c.GetMaxHealth() });
                PovBridge.AssignIds(c, "warded");
                var data = ForestRuntime.Zdo(c);
                c.SetHealth(c.GetMaxHealth() * .4f);
                var ward = PovBridge.Method("PassiveTree.RuneWard", "Of").Invoke(null, new object[] { c });
                AccessTools.Field(ward.GetType(), "Current").SetValue(ward, 7f);
                AccessTools.Method(ward.GetType(), "Save").Invoke(ward, null);
                float maximum = c.GetMaxHealth(), current = c.GetHealth();
                data.SetOwner(0); data.SetOwner(ZDOMan.GetSessionID());
                for (int i = 0; i < 3; i++)
                    PovBridge.Method("PassiveTree.EnemyDifficulty", "Ensure").Invoke(null, new object[] { c });
                Require(Close(c.GetMaxHealth(), maximum) && Close(c.GetHealth(), current) &&
                    Close(data.GetFloat("pt.ward.cur"), 7) && data.GetInt(RadialRuntime.EmpoweredKey) == n,
                    "wounded-owner-reset-ensure", new { role, depth, current, maximum, stacks = n });
            }
            var source = Spawn(0); var target = Spawn(4); var partner = Spawn(8);
            Mark(source, RadialRole.Guard, 40); Mark(target, RadialRole.Boss, 40);
            float Loss(Character c, Action action)
            {
                c.SetHealth(c.GetMaxHealth()); float hp = c.GetHealth(); action(); return hp - c.GetHealth();
            }
            foreach (string mode in new[] { "direct", "armored", "bound", "mirror" })
            {
                PovBridge.AssignIds(source);
                PovBridge.AssignIds(target, mode == "direct" ? Array.Empty<string>() : new[] { mode });
                PovBridge.AssignIds(partner);
                // Удаляем неоднозначность выбора соседа: Bound связывается с контролем, а не атакующим.
                if (mode == "bound")
                {
                    var habits = PovBridge.Method("PassiveTree.RarityMods2+Habits", "Ensure")
                        .Invoke(null, new object[] { target });
                    AccessTools.Field(habits.GetType(), "partner").SetValue(habits, partner);
                }
                if (mode == "mirror") RadialAffixProbe.Tick(target);
                float[] loss = new float[2];
                for (int i = 0; i < 2; i++)
                {
                    ForestRuntime.Zdo(source).Set(RadialRuntime.EmpoweredKey, i == 0 ? 0 : 13);
                    ForestRuntime.Zdo(target).Set(RadialRuntime.EmpoweredKey, i == 0 ? 0 : 6);
                    target.SetHealth(target.GetMaxHealth()); source.SetHealth(source.GetMaxHealth());
                    partner.SetHealth(partner.GetMaxHealth());
                    var measured = mode == "mirror" ? source : mode == "bound" ? partner : target;
                    loss[i] = Loss(measured, () => rpc.Invoke(target, new object[] { 0L,
                        Hit(source, mode == "mirror") }));
                }
                Require(loss[0] > 0 && Close(loss[1] / loss[0], 2.3f), "damage-" + mode,
                    new { baseline = loss[0], empowered = loss[1], ratio = loss[1] / loss[0] });
            }
            // Руна/лужа берут уже усиленный AttackPower и передают HitData без атакующего.
            PovBridge.AssignIds(source);
            var habitsSource = PovBridge.Method("PassiveTree.RarityMods2+Habits", "Ensure")
                .Invoke(null, new object[] { source });
            var lastAttack = AccessTools.Field(habitsSource.GetType(), "<LastAttack>k__BackingField");
            float[] powers = new float[2];
            for (int i = 0; i < 2; i++)
            {
                ForestRuntime.Zdo(source).Set(RadialRuntime.EmpoweredKey, i == 0 ? 0 : 13);
                lastAttack.SetValue(habitsSource, 0f);
                powers[i] = (float)PovBridge.Method("PassiveTree.RarityMods2", "AttackPower")
                    .Invoke(null, new object[] { source });
            }
            Require(powers[0] > 0 && Close(powers[1] / powers[0], 2.3f), "area-fallback-power", powers);
            PovBridge.AssignIds(target);
            target.SetHealth(target.GetMaxHealth());
            rpc.Invoke(target, new object[] { 0L, Hit(source) });
            float last = (float)lastAttack.GetValue(habitsSource);
            float cached = (float)PovBridge.Method("PassiveTree.RarityMods2", "AttackPower")
                .Invoke(null, new object[] { source });
            Require(last > 0 && Close(last, cached), "area-cached-power-not-scaled-twice", new { last, cached });
            var inherited = Hit(source); inherited.m_hitType = EmpoweredDamage.Inherited;
            var package = new ZPackage(); inherited.Serialize(ref package);
            var incoming = new ZPackage(package.GetArray()); var decoded = new HitData();
            decoded.Deserialize(ref incoming);
            Require(decoded.m_hitType == EmpoweredDamage.Inherited && decoded.GetAttacker() == source,
                "derived-marker-network-roundtrip", new { type = (byte)decoded.m_hitType });
            foreach (string profile in new[] { "near", "middle", "far" })
            {
                var selected = cfg.Types.Single(t => t.Type == "ForestCrypt").Probe(profile, 17, cfg.Rewards);
                var encounter = MiniBossEncounter.Create(73, 30, cfg.Rewards.AtPrice(selected.Profile.ModifierPrice),
                    radial: selected);
                var boss = Spawn(300 + actors.Count * 5); var data = ForestRuntime.Zdo(boss);
                data.Set(MiniBossRuntime.BossKey, true); data.Set(MiniBossRuntime.SeedKey, encounter.Seed);
                data.Set(MiniBossConnection.EncounterKey, JsonConvert.SerializeObject(encounter));
                MiniBossRuntime.PrepareReward(boss);
                var first = MiniBossRuntime.DropReward(boss);
                string saved = data.GetString(MiniBossRuntime.RewardKey);
                int drops = Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None).Length;
                var second = MiniBossRuntime.DropReward(boss);
                int afterDrops = Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None).Length;
                // Inventory.Load создаёт временные объекты: считаем именно повторную выдачу до распаковки.
                int coins = first.Items.Select(item => GuardedChestReward.Unpack(item, 1, 1).GetAllItems().Single())
                    .Where(item => item.m_shared.m_name == "$item_coins").Sum(item => item.m_stack);
                Require(first.Gold == encounter.Reward.Coins && coins == first.Gold &&
                    second.Delivered == first.Items.Count && saved == data.GetString(MiniBossRuntime.RewardKey) &&
                    drops == afterDrops,
                    "boss-native-coins-repeat", new { profile, expected = encounter.Reward.Coins, coins, drops,
                        first.Gold, second.Delivered, count = first.Items.Count,
                        after_drops = afterDrops,
                        saved_equal = saved == data.GetString(MiniBossRuntime.RewardKey),
                        semantic_equal = Newtonsoft.Json.Linq.JToken.DeepEquals(
                            Newtonsoft.Json.Linq.JToken.Parse(saved),
                            Newtonsoft.Json.Linq.JToken.Parse(data.GetString(MiniBossRuntime.RewardKey))) });
            }
            return new { passed = true, checks = rows,
                scope = "native-rpc-owner-reset; process-restart-and-two-client-transfer-separate" };
        }
        finally
        {
            foreach (var c in actors)
                if (c != null && c.GetComponent<ZNetView>().IsValid()) ZNetScene.instance.Destroy(c.gameObject);
            foreach (var drop in Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None).Except(oldDrops))
                if (drop != null && drop.GetComponent<ZNetView>().IsValid())
                    ZNetScene.instance.Destroy(drop.gameObject);
        }
    }
}
