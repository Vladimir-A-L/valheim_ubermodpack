using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DungeonDepthPrototype;

internal static class ProbeSuite
{
    internal static void Run(Dictionary<string, object> report)
    {
        var rows = new List<object>();
        report["creatures"] = rows;
        foreach (string prefab in new[] { "Skeleton", "Greydwarf" })
            foreach (int count in new[] { 0, 1, 3, 5, 10 })
                rows.Add(CheckCreature(prefab, count));
        report["control"] = CheckControl();
        report["reward_projection"] = CheckRewards();
        report["epicloot"] = CheckEpicLoot();
    }

    private static Character Spawn(string prefab)
    {
        var template = ZNetScene.instance.GetPrefab(prefab);
        if (template == null) throw new InvalidOperationException("Не найден prefab " + prefab);
        var instance = Object.Instantiate(template, new Vector3(0, 5000, 0), Quaternion.identity);
        var character = instance.GetComponent<Character>();
        var view = instance.GetComponent<ZNetView>();
        if (character == null || view == null || !view.IsValid() || !view.IsOwner())
            throw new InvalidOperationException("Нет принадлежащего серверу ZDO для " + prefab);
        character.SetLevel(1);
        return character;
    }

    private static object CheckCreature(string prefab, int count)
    {
        Character character = Spawn(prefab);
        Character? restored = null;
        var randomState = UnityEngine.Random.state;
        try
        {
            UnityEngine.Random.InitState(5701 + count);
            int mask = PovBridge.Assign(character, count);
            for (int i = 0; i < 3; i++) PovBridge.Sync.Invoke(null, new object[] { character });
            Require(PovBridge.Read(character) == mask, "Sync изменил назначенный набор");
            character.transform.position += new Vector3(100, 0, 0);
            PovBridge.Sync.Invoke(null, new object[] { character });
            Require(PovBridge.Read(character) == mask, "Перемещение изменило набор");
            character.SetLevel(2);
            PovBridge.Sync.Invoke(null, new object[] { character });
            Require(PovBridge.Read(character) == mask, "Изменение звёзд заменило набор");
            character.SetLevel(1);

            // Реальный сетевой формат ZDO; это не испытание сохранения/загрузки всего мира.
            var package = new ZPackage();
            character.GetComponent<ZNetView>().GetZDO().Serialize(package);
            restored = Spawn(prefab);
            restored.GetComponent<ZNetView>().GetZDO().Deserialize(new ZPackage(package.GetArray()));
            PovBridge.Sync.Invoke(null, new object[] { restored });
            Require(PovBridge.Read(restored) == mask, "Сериализация ZDO потеряла набор");
            Require(restored.GetComponent<ZNetView>().GetZDO().GetInt(PovBridge.ProfileKey, 0) == 1, "Потерян профиль");
            return new { prefab, requested = count, actual = PovBridge.Count(mask), sync = true,
                moved = true, star_changed = true, zdo_roundtrip = true };
        }
        finally
        {
            UnityEngine.Random.state = randomState;
            if (restored != null) ZNetScene.instance.Destroy(restored.gameObject);
            ZNetScene.instance.Destroy(character.gameObject);
        }
    }

    private static object CheckControl()
    {
        var character = Spawn("Skeleton");
        try
        {
            PovBridge.Method("PassiveTree.RarityMods", "Fill").Invoke(null, new object[] { character, 3 });
            PovBridge.Sync.Invoke(null, new object[] { character });
            Require(PovBridge.Read(character) == 0, "Непомеченный контроль перестал следовать PoV");
            return new { unmarked_vanilla_sync = true };
        }
        finally { ZNetScene.instance.Destroy(character.gameObject); }
    }

    private static object CheckRewards()
    {
        double reward = PovBridge.ConfigFloat("PassiveTree.RarityRoll", "ModReward");
        var scaleProperty = AccessTools.Property(
            PovBridge.Assembly.GetType("PassiveTree.Loot.LootDifficulty"), "StarScale");
        double scale = Convert.ToDouble(scaleProperty.GetValue(null));
        var rarityAt = PovBridge.Method("PassiveTree.Loot.LootCatalog", "RarityAt");
        object[] args = { 19, 0f, 0f, 0f };
        rarityAt.Invoke(null, args);
        double common = Convert.ToDouble(args[1]), magic = Convert.ToDouble(args[2]), rare = Convert.ToDouble(args[3]);
        var rows = new List<object>();
        var rng = new System.Random(20261028);
        foreach (int count in new[] { 0, 1, 3, 5, 8, 10 })
        {
            double factor = RewardProjection.OpportunityFactor(count, reward, scale);
            double[] pov = RewardProjection.BestOf(new[] { magic, rare }, factor);
            // Исследованные веса EpicLoot forestcrypt; таблица прототипа, не изменение конфигов EpicLoot.
            double[] rawEpic = RewardProjection.BestOf(new double[] { 95, 4, 1, 0, 0, 0 }, factor);
            double[] epic = GuardedChestReward.Probabilities(factor);
            int[] sample = new int[epic.Length];
            for (int i = 0; i < 100000; i++) sample[RewardProjection.Roll(epic, rng.NextDouble())]++;
            Require(sample.Sum() == 100000 && sample.Skip(3).Sum() == 0, "Нарушен объём или потолок выборки");
            for (int i = 0; i < epic.Length; i++)
                Require(Math.Abs(sample[i] / 100000.0 - epic[i]) < 0.007, "Выборка расходится с распределением");
            rows.Add(new { modifiers = count, opportunities = factor, pov_rare = pov[1],
                raw_epic_probabilities = rawEpic,
                epic_probabilities = epic, rare_floor = pov[1] >= Plugin.RareFloorChance,
                sample, trials = 100000, items_per_trial = 1 });
        }
        return new { forest_level = 19, common, magic, rare, reward_per_modifier = reward, star_scale = scale,
            rare_floor_reference_chance = Plugin.RareFloorChance, rows };
    }

    private static object CheckEpicLoot()
    {
        var api = Type.GetType("EpicLoot.API, EpicLoot", true)!;
        var create = AccessTools.Method(api, "TryMakeMagicItem");
        var read = AccessTools.Method(api, "TryGetRarity");
        var source = ObjectDB.instance.GetItemPrefab("SwordBronze").GetComponent<ItemDrop>().m_itemData;
        var results = new List<object>();
        foreach (int rarity in new[] { 0, 1, 2 })
        {
            var item = source.Clone();
            bool ok = (bool)create.Invoke(null, new object?[] { item, rarity, 0f, null });
            Require(ok, "EpicLoot отказал в назначении редкости");
            object[] readArgs = { item, -1 };
            Require((bool)read.Invoke(null, readArgs), "EpicLoot не прочитал редкость");
            int actual = Convert.ToInt32(readArgs[1]);
            Require(actual == rarity, "EpicLoot вернул другую редкость");
            Require(item.m_stack == 1, "EpicLoot изменил количество");
            results.Add(new { prefab = "SwordBronze", requested = rarity, actual, quantity = item.m_stack });
        }
        var chestObject = Object.Instantiate(ZNetScene.instance.GetPrefab("TreasureChest_forestcrypt"),
            new Vector3(0, 5000, 0), Quaternion.identity);
        try
        {
            var chest = chestObject.GetComponent<Container>();
            int before = chest.GetInventory().GetAllItems().Count;
            Require(GuardedChestReward.TryAdd(chest, 10, 5701), "Награда не помещена в тестовый сундук");
            Require(chest.GetInventory().GetAllItems().Count == before + 1, "Награда должна занимать один предмет");
            int savedRarity = chest.GetComponent<ZNetView>().GetZDO().GetInt("uberdungeon.probe.reward-rarity", -1);
            Require(savedRarity >= 1, "Сильная охрана не дала минимальную редкость Rare");
            Require(!GuardedChestReward.TryAdd(chest, 1, 9999), "Повторный вызов повторил награду");
            Require(chest.GetInventory().GetAllItems().Count == before + 1, "Повтор изменил число предметов");
            results.Add(new { chest = "TreasureChest_forestcrypt", added = 1, rarity = savedRarity,
                duplicate_refused = true });
        }
        finally { ZNetScene.instance.Destroy(chestObject); }
        return results;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
