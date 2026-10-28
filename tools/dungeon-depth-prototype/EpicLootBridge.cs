using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using EpicLoot;
using EpicLoot.GatedItemType;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DungeonDepthPrototype;

internal static class EpicLootBridge
{
    private static bool gateScope;
    internal const string Guid = "randyknapp.mods.epicloot";
    internal const string ExpectedHash = "ef0f11fb3248a7ab4a49da1b9c8f96827dec008fbc31569d90bf1bab52a1c9ef";

    internal static string Hash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    internal static void Verify()
    {
        var info = BepInEx.Bootstrap.Chainloader.PluginInfos[Guid];
        if (info.Metadata.Version.ToString() != "0.15.3" ||
            Hash(File.ReadAllBytes(typeof(API).Assembly.Location)) != ExpectedHash)
            throw new InvalidOperationException("Нужна закреплённая DLL EpicLoot 0.15.3");
        if (EpicLoot.EpicLoot.GetGatedItemTypeMode() != GatedItemTypeMode.BossKillUnlocksCurrentBiomeItems)
            throw new InvalidOperationException("Нужен BossKillUnlocksCurrentBiomeItems");
    }

    internal static void Install(Harmony harmony)
    {
        var method = AccessTools.Method(typeof(GatedItemTypeHelper), "CheckIfItemNeedsGate",
            new[] { typeof(GatedItemTypeMode), typeof(string) });
        harmony.Patch(method, prefix: new HarmonyMethod(typeof(EpicLootBridge), nameof(BossGateOnly)));
    }

    private static bool BossGateOnly(GatedItemTypeMode mode, string itemName, ref bool __result)
    {
        if (!gateScope || mode != GatedItemTypeMode.BossKillUnlocksCurrentBiomeItems) return true;
        // В 0.15.3 общий фильтр сначала требует Player.m_localPlayer даже для мировых ключей.
        // Только внутри нашего синхронного выбора вызываем его же проверку босса без этого раннего выхода.
        __result = !AvailableByBoss(itemName);
        return false;
    }

    internal static bool AvailableByBoss(string name) => !(bool)AccessTools.Method(
        typeof(GatedItemTypeHelper), "CheckGateBossKill").Invoke(null,
            new object[] { name, GatedItemTypeMode.BossKillUnlocksCurrentBiomeItems });

    // ItemFallback разрешается штатным GetGatedItemFromType даже без ключей (например, рваная одежда).
    internal static bool AllowedResult(string name) => AvailableByBoss(name) ||
        GatedItemTypeHelper.FallbackByType.Values.Any(f => f.Item == name);

    private static string SelectBase(string entry)
    {
        bool previous = gateScope;
        gateScope = true;
        try
        {
            return GatedItemTypeHelper.GetGatedItemNameFromItemOrType(
                entry, GatedItemTypeMode.BossKillUnlocksCurrentBiomeItems);
        }
        finally { gateScope = previous; }
    }

    internal static void FlushNative(Container chest)
    {
        // Завершить только уже ожидающий штатный бросок. TryRoll проверяет собственный pending-флаг.
        var type = typeof(API).Assembly.GetType("EpicLoot.PendingChestLoot", true);
        AccessTools.Method(type, "TryRoll").Invoke(null, new object[] { chest });
    }

    internal static ItemDrop.ItemData Generate(string source, int level, RewardQuality quality)
    {
        if (EpicLoot.EpicLoot.GetGatedItemTypeMode() != GatedItemTypeMode.BossKillUnlocksCurrentBiomeItems)
            throw new InvalidOperationException("Режим технологического ограничения изменён");
        var tables = LootRoller.GetLootTable(source);
        if (tables.Count == 0) throw new InvalidOperationException("Нет таблицы EpicLoot: " + source);
        // Drops, DropRate, удача и Prosperity не определяют нашу выдачу.
        var planMethod = AccessTools.Method(typeof(LootRoller), "PlanRoll");
        for (int attempt = 0; attempt < 256; attempt++)
        {
            var table = tables[UnityEngine.Random.Range(0, tables.Count)];
            object plan = planMethod.Invoke(null, new object[] { table, level, source });
            object? anchor = AccessTools.Field(plan.GetType(), "LootAnchor").GetValue(plan);
            if (anchor == null) continue;
            var drops = ((LeveledLootDef)anchor).Loot;
            var scaling = (LootScalingContext)AccessTools.Field(plan.GetType(), "Scaling").GetValue(plan);
            var weights = drops.Select(d => (double)LootScalingMath.ScaledWeight(d, scaling.LootSteps)).ToArray();
            double sum = weights.Sum();
            if (sum <= 0 || weights.Any(w => w < 0 || double.IsNaN(w) || double.IsInfinity(w))) continue;
            double draw = UnityEngine.Random.value * sum;
            int index = 0;
            while (index < drops.Length - 1 && draw >= weights[index]) draw -= weights[index++];
            var leaf = LootRoller.ResolveLootDrop(drops[index], 0f, false, scaling);
            string name = SelectBase(leaf.Item);
            if (string.IsNullOrEmpty(name)) continue;
            var prefab = ObjectDB.instance.GetItemPrefab(name);
            if (prefab == null) continue;
            var drop = prefab.GetComponent<ItemDrop>();
            if (drop == null || !API.CanBeMagicItem(drop.m_itemData)) continue;
            if (!AllowedResult(name))
                throw new InvalidOperationException("EpicLoot вернул закрытую основу " + name);
            var item = drop.m_itemData.Clone();
            item.m_dropPrefab = prefab;
            item.m_stack = 1;
            item.m_quality = 1;
            if (quality != RewardQuality.Common && !API.TryMakeMagicItem(item, (int)quality - 1, 0f, null))
                continue;
            int rarity = -1;
            bool magic = API.TryGetRarity(item, ref rarity);
            if (magic != (quality != RewardQuality.Common) || magic && rarity != (int)quality - 1)
                throw new InvalidOperationException("EpicLoot изменил выбранную редкость");
            return item;
        }
        throw new InvalidOperationException("Нет допустимой основы за 256 попыток: " + source);
    }

    internal static string Identity(ItemDrop.ItemData item)
    {
        string json = API.GetMagicItemJson(item);
        // Порядок эффектов не превращает полную копию в другой предмет.
        if (!string.IsNullOrEmpty(json))
        {
            var magic = JObject.Parse(json);
            if (magic["Effects"] is JArray effects)
                magic["Effects"] = new JArray(effects.OrderBy(e => e.ToString(Newtonsoft.Json.Formatting.None)));
            json = magic.ToString(Newtonsoft.Json.Formatting.None);
        }
        return item.m_dropPrefab.name + ":" + item.m_quality + ":" + item.m_variant + ":" + json;
    }
}
