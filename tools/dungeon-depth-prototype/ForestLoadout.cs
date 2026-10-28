using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using BepInEx;
using Newtonsoft.Json;

namespace DungeonDepthPrototype;

// Только подготовка выбранного тестового персонажа. Предметы не относятся к добыче склепа.
internal static class ForestLoadout
{
    internal const string Key = "uberdungeon.qa.bronze.v1";

    internal sealed class Specification
    {
        public string Prefab = "";
        public int Count = 1, Quality = 1;
    }

    internal static object Validate() => Build().Select(i => new
        { prefab = i.m_dropPrefab.name, count = i.m_stack, quality = i.m_quality }).ToArray();

    private static ItemDrop.ItemData[] Build()
    {
        var defaults = new[]
        {
            ("BowFineWood", 1, 2), ("MaceBronze", 1, 2), ("SwordBronze", 1, 2),
            ("ShieldBronzeBuckler", 1, 2), ("Torch", 1, 1),
            ("DeerStew", 20, 1), ("MinceMeatSauce", 20, 1), ("CarrotSoup", 20, 1),
            ("QueensJam", 20, 1), ("ArrowFlint", 100, 1),
            ("HelmetBronze", 1, 2), ("ArmorBronzeChest", 1, 2), ("ArmorBronzeLegs", 1, 2),
            ("CapeDeerHide", 1, 2)
        };
        var path = Path.Combine(Paths.ConfigPath, "forest-loadout.json");
        var specifications = File.Exists(path)
            ? JsonConvert.DeserializeObject<Specification[]>(File.ReadAllText(path))! :
            defaults.Select(s => new Specification { Prefab = s.Item1, Count = s.Item2, Quality = s.Item3 }).ToArray();
        if (specifications.Length == 0 || specifications.Length > 32)
            throw new InvalidOperationException("Недопустимый размер комплекта");
        return specifications.SelectMany(s =>
        {
            var prefab = ObjectDB.instance.GetItemPrefab(s.Prefab) ??
                throw new InvalidOperationException("Не найден предмет " + s.Prefab);
            var source = prefab.GetComponent<ItemDrop>().m_itemData;
            if (s.Count < 1 || s.Count > 1000 || s.Quality < 1 || source.m_shared.m_maxStackSize < 1 ||
                s.Quality > Math.Max(1, source.m_shared.m_maxQuality))
                throw new InvalidOperationException("Недопустимое качество или лимит стека " + s.Prefab);
            var stacks = new List<ItemDrop.ItemData>();
            for (int remaining = s.Count; remaining > 0;)
            {
                var item = source.Clone();
                item.m_dropPrefab = prefab; item.m_stack = Math.Min(remaining, source.m_shared.m_maxStackSize);
                remaining -= item.m_stack; item.m_quality = s.Quality;
                item.m_durability = item.GetMaxDurability(); item.m_cheated = true;
                item.m_customData[Key] = "manual-acceptance";
                stacks.Add(item);
            }
            return stacks;
        }).ToArray();
    }

    internal static object Grant(Player player, bool reset = false)
    {
        if (!ForestManual.Target(player) ||
            player.IsTeleporting() || player.IsDead() || InventoryGui.IsVisible())
            throw new InvalidOperationException("Нужен выбранный тестовый персонаж в отдельном мире");
        if (!reset && player.m_customData.ContainsKey(Key))
            throw new InvalidOperationException("Этот комплект уже выдан; повторная выдача заблокирована");
        var inventory = player.GetInventory();
        var items = Build();
        var previous = inventory.GetAllItems().ToArray();
        var equipped = previous.Where(i => i.m_equipped).ToArray();
        if (reset && items.Length > inventory.GetWidth() * inventory.GetHeight())
            throw new InvalidOperationException("Комплект не помещается в пустой инвентарь");
        if (reset)
        {
            player.UnequipAllItems();
            inventory.RemoveAll();
        }
        var free = new Queue<Vector2i>();
        for (int y = 0; y < inventory.GetHeight(); y++)
        for (int x = 0; x < inventory.GetWidth(); x++)
            if (!inventory.GetAllItems().Any(i => i.m_gridPos.x == x && i.m_gridPos.y == y))
                free.Enqueue(new Vector2i(x, y));
        if (free.Count < items.Length) throw new InvalidOperationException("Недостаточно свободных ячеек");
        var added = new List<ItemDrop.ItemData>();
        try
        {
            foreach (var item in items)
            {
                if (!inventory.AddItem(item, free.Dequeue())) throw new InvalidOperationException("Выдача отклонена");
                added.Add(item);
            }
        }
        catch
        {
            foreach (var item in added) inventory.RemoveItem(item);
            if (reset)
            {
                foreach (var item in previous) inventory.AddItem(item, item.m_gridPos);
                foreach (var item in equipped) player.EquipItem(item, false);
            }
            throw;
        }
        player.m_customData[Key] = DateTime.UtcNow.ToString("O");
        foreach (var item in items.Where(i => i.m_dropPrefab.name.StartsWith("Armor", StringComparison.Ordinal) ||
                     i.m_dropPrefab.name == "HelmetBronze" || i.m_dropPrefab.name == "CapeDeerHide"))
            player.EquipItem(item, false);
        if (reset) player.ClearFood();
        foreach (string food in new[] { "DeerStew", "MinceMeatSauce", "CarrotSoup" })
        {
            var item = items.First(i => i.m_dropPrefab.name == food);
            if (player.EatFood(item)) inventory.RemoveItem(item, 1);
        }
        player.Heal(player.GetMaxHealth(), false);
        player.SetGodMode(true); player.SetGhostMode(false);
        return new { granted = true, marker = Key, god = player.InGodMode(), ghost = player.InGhostMode(),
            health = player.GetHealth(), max_health = player.GetMaxHealth(),
            items = items.Select(i => new { prefab = i.m_dropPrefab.name, count = i.m_stack,
                quality = i.m_quality, equipped = i.m_equipped }).ToArray() };
    }
}
