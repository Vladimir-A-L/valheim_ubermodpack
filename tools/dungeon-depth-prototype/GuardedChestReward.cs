using System;
using HarmonyLib;

namespace DungeonDepthPrototype;

// Только явно выбранные сундуки прототипа; обычные выдачи PoV/EpicLoot не перехватываются.
internal static class GuardedChestReward
{
    private const string IssuedKey = "uberdungeon.probe.reward-issued";

    internal static bool TryAdd(Container chest, int guardModifiers, int seed)
    {
        if (guardModifiers < 0 || guardModifiers > 26) throw new ArgumentOutOfRangeException(nameof(guardModifiers));
        var view = chest.GetComponent<ZNetView>();
        if (view == null || !view.IsValid() || !view.IsOwner()) return false;
        var zdo = view.GetZDO();
        if (zdo.GetBool(IssuedKey, false)) return false;
        // Связь с охраной и seed сохраняются до броска; поздние вызовы не перебрасывают качество.
        const string guardKey = "uberdungeon.probe.guard-modifiers";
        const string seedKey = "uberdungeon.probe.reward-seed";
        int savedCount = zdo.GetInt(guardKey, -1);
        if (savedCount < 0)
        {
            zdo.Set(guardKey, guardModifiers);
            zdo.Set(seedKey, seed);
            savedCount = guardModifiers;
        }
        double perMod = PovBridge.ConfigFloat("PassiveTree.RarityRoll", "ModReward");
        var scaleProperty = AccessTools.Property(
            PovBridge.Assembly.GetType("PassiveTree.Loot.LootDifficulty"), "StarScale");
        double scale = Convert.ToDouble(scaleProperty.GetValue(null));
        double factor = RewardProjection.OpportunityFactor(savedCount, perMod, scale);
        var probabilities = Probabilities(factor);
        const string rarityKey = "uberdungeon.probe.reward-rarity";
        int rarity = zdo.GetInt(rarityKey, -1);
        if (rarity < 0)
        {
            rarity = RewardProjection.Roll(probabilities, new Random(zdo.GetInt(seedKey, seed)).NextDouble());
            zdo.Set(rarityKey, rarity);
        }
        var item = ObjectDB.instance.GetItemPrefab("SwordBronze").GetComponent<ItemDrop>().m_itemData.Clone();
        item.m_stack = 1;
        var api = Type.GetType("EpicLoot.API, EpicLoot", true)!;
        var create = AccessTools.Method(api, "TryMakeMagicItem");
        if (!(bool)create.Invoke(null, new object?[] { item, rarity, 0f, null })) return false;
        if (!chest.GetInventory().AddItem(item)) return false;
        zdo.Set(IssuedKey, true);
        AccessTools.Method(typeof(Container), "Save").Invoke(chest, null);
        return true;
    }

    internal static double[] Probabilities(double factor)
    {
        var probabilities = RewardProjection.BestOf(new double[] { 95, 4, 1, 0, 0, 0 }, factor);
        object[] weights = { 19, 0f, 0f, 0f };
        PovBridge.Method("PassiveTree.Loot.LootCatalog", "RarityAt").Invoke(null, weights);
        double magic = Convert.ToDouble(weights[2]), rare = Convert.ToDouble(weights[3]);
        double referenceChance = RewardProjection.BestOf(new[] { magic, rare }, factor)[1];
        // Выбранное правило качества прототипа; это не встроенная гарантия PoV.
        return referenceChance >= Plugin.RareFloorChance
            ? RewardProjection.WithMinimum(probabilities, 1) : probabilities;
    }
}
