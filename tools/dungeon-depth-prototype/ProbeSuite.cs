using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EpicLoot;
using BepInEx.Configuration;
using EpicLoot.GatedItemType;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DungeonDepthPrototype;

// Только явно включённый одноразовый сервер. Подготовленные сундуки/удары не считаются игровой приёмкой.
internal static class ProbeSuite
{
    private const string Tag = "uberdungeon.reward.test";
    private static readonly List<GameObject> Temporary = new List<GameObject>();
    private static readonly string[] Bosses = { "defeated_eikthyr", "defeated_gdking", "defeated_bonemass" };
    private static string CheckpointPath => Path.Combine(BepInEx.Paths.ConfigPath,
        "DungeonDepthPrototype", "reward-checkpoint.json");
    private static int checks;
    private static void Require(bool ok, string text)
    {
        if (!ok) throw new InvalidOperationException(text);
        checks++;
    }

    private static void Refuses(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { checks++; return; }
        catch (ArgumentException) { checks++; return; }
        throw new InvalidOperationException("Ожидался отказ");
    }

    internal static void Run(Dictionary<string, object> report, string phase)
    {
        var randomState = UnityEngine.Random.state;
        var keys = ZoneSystem.instance.GetGlobalKeys().ToArray();
        try
        {
            if (phase == "create")
            {
                Require(!File.Exists(CheckpointPath), "Checkpoint уже существует: нужен новый мир/каталог");
                report["mechanics"] = Mechanics();
                Plugin.LogInfo("REWARD_CHECK: mechanics");
                report["open_rpc"] = OpenHook();
                report["native_independence"] = Independence();
                report["deferred_native"] = DeferredNative();
                Plugin.LogInfo("REWARD_CHECK: native independence");
                report["boundaries"] = Boundaries();
                Plugin.LogInfo("REWARD_CHECK: boundaries");
                report["journal"] = Journal();
                report["technology"] = Technology();
                Plugin.LogInfo("REWARD_CHECK: technology");
                report["persistence"] = PreparePersistence();
            }
            else if (phase == "resume" || phase == "verify")
                report["persistence"] = RestorePersistence(phase);
            else throw new ArgumentException("Неизвестная фаза");
            report["assertions"] = checks;
        }
        finally
        {
            GuardedChestReward.Fault = null;
            foreach (string key in Bosses)
            {
                if (keys.Contains(key)) ZoneSystem.instance.SetGlobalKey(key);
                else ZoneSystem.instance.RemoveGlobalKey(key);
            }
            UnityEngine.Random.state = randomState;
            foreach (var obj in Temporary)
                if (obj != null) ZNetScene.instance.Destroy(obj);
            Temporary.Clear();
        }
    }

    private static Character Guard(params string[] ids) => SpawnGuard("Skeleton", ids);

    private static Character SpawnGuard(string prefab, params string[] ids)
    {
        var obj = Object.Instantiate(ZNetScene.instance.GetPrefab(prefab), new Vector3(0, 5000, 0),
            Quaternion.identity);
        Temporary.Add(obj);
        var guard = obj.GetComponent<Character>();
        guard.GetComponent<BaseAI>().enabled = false;
        guard.SetLevel(1);
        PovBridge.AssignIds(guard, ids);
        return guard;
    }

    private static Container Chest(string prefab = "TreasureChest_forestcrypt")
    {
        var obj = Object.Instantiate(ZNetScene.instance.GetPrefab(prefab), new Vector3(0, 5000, 0),
            Quaternion.identity);
        Temporary.Add(obj);
        return obj.GetComponent<Container>();
    }

    private static RoomRewardContext Context(decimal bonus, int seed = 5701) => new RoomRewardContext
    {
        DungeonId = "isolated-fixture", RoomId = "reward-room", Depth = 20, Seed = seed, LocationBonus = bonus
    };

    private static Inventory Inv(Container chest) => GuardedChestReward.Inventory(chest);
    private static string Snapshot(Container chest) => GuardedChestReward.Pack(Inv(chest));
    private static ChestRewardState State(Container chest) => GuardedChestReward.Read(chest)!;

    private static string Contents(Container chest) => JsonConvert.SerializeObject(Inv(chest).GetAllItems()
        .Select(i => new { id = EpicLootBridge.Identity(i), i.m_stack, i.m_durability,
            x = i.m_gridPos.x, y = i.m_gridPos.y }));

    private static string Totals(IEnumerable<ItemDrop.ItemData> items) => JsonConvert.SerializeObject(
        items.GroupBy(EpicLootBridge.Identity).OrderBy(g => g.Key)
            .Select(g => new { id = g.Key, quantity = g.Sum(i => i.m_stack) }));

    private static readonly Type PendingType = typeof(API).Assembly.GetType("EpicLoot.PendingChestLoot", true);
    private static int NativePendingKey => (int)AccessTools.Field(PendingType, "PendingKeyHash").GetValue(null);

    private static void NativeFixture(Container chest)
    {
        var zdo = chest.GetComponent<ZNetView>().GetZDO();
        if (!zdo.GetBool(NativePendingKey)) return;
        // TryRoll требует локального игрока. В headless fixture имитируем только доставку триггера:
        // тот же pending-переход и настоящий штатный Roll, без подмены его таблиц/шансов/предметов.
        zdo.Set(NativePendingKey, false);
        ((HashSet<Container>)AccessTools.Field(PendingType, "Pending").GetValue(null)).Remove(chest);
        string source = Utils.GetPrefabName(chest.gameObject);
        AccessTools.Method(PendingType, "Roll").Invoke(null,
            new object[] { chest, source, LootRoller.GetLootTable(source) });
    }

    private static object DeferredNative()
    {
        var chest = Chest();
        Require(chest.GetComponent<ZNetView>().GetZDO().GetBool(NativePendingKey), "Нет отложенного броска");
        GuardedChestReward.Bind(chest, Guard(), Context(120));
        GuardedChestReward.Process(chest);
        var state = State(chest);
        Require(chest.GetComponent<ZNetView>().GetZDO().GetBool(NativePendingKey),
            "Серверный бонус отменил будущий штатный бросок клиента");
        var identities = Inv(chest).GetAllItems().Select(EpicLootBridge.Identity).ToArray();
        NativeFixture(chest);
        Require(identities.All(id => Inv(chest).GetAllItems().Any(i => EpicLootBridge.Identity(i) == id)),
            "Штатный бросок удалил существовавшие вещи");
        GuardedChestReward.Process(chest);
        Require(State(chest).Items.SequenceEqual(state.Items) && State(chest).Delivered == state.Delivered,
            "Поздний штатный бросок пересоздал бонус");
        return new { pending_preserved_until_native_trigger = true, bonus_unchanged = true,
            trigger = "headless-direct-native-Roll-not-client-interaction" };
    }

    private static object OpenHook()
    {
        var chest = Chest();
        GuardedChestReward.Bind(chest, Guard(), Context(120));
        _ = chest.GetInventory();
        Require(State(chest).Plan == null, "Фоновое чтение сформировало закрытую награду");
        var open = AccessTools.Method(typeof(Container), "RPC_RequestOpen");
        open.Invoke(chest, new object[] { ZNet.GetUID(), 0L });
        Require(State(chest).Plan != null && State(chest).Delivered == 1, "RPC открытия не выдал бонус");
        string before = Totals(Inv(chest).GetAllItems());
        string generated = JsonConvert.SerializeObject(State(chest).Items);
        open.Invoke(chest, new object[] { ZNet.GetUID(), 0L });
        Require(before == Totals(Inv(chest).GetAllItems()) && State(chest).Delivered == 1 &&
            generated == JsonConvert.SerializeObject(State(chest).Items), "Повторный RPC повторил бонус");
        return new { inventory_scan_does_not_generate = true, real_server_open_rpc = true,
            repeated_rpc_no_duplicate = true, client_ui_tested = false };
    }

    private static object Boundaries()
    {
        var rows = new List<object>();
        foreach (decimal budget in new[] { 0m, 1, 11.999m, 12, 24, 48, 72, 96, 108, 119.999m,
                     120, 121, 132, 192, 240, 300, 360, 361, 372, 432, 480 })
        {
            var chest = Chest();
            NativeFixture(chest);
            var before = Inv(chest).GetAllItems().Select(i => (EpicLootBridge.Identity(i), i.m_stack)).ToArray();
            GuardedChestReward.Bind(chest, Guard(), Context(budget));
            GuardedChestReward.Process(chest);
            var state = State(chest);
            Require(state.Error.Length == 0 && state.Plan != null, "Не сформирован план");
            Require(before.All(x => Inv(chest).GetAllItems().Any(i => EpicLootBridge.Identity(i) == x.Item1 &&
                i.m_stack >= x.m_stack)), "Потеря обычного содержимого");
            var expected = RewardModel.Plan(budget, state.Rules, new System.Random(state.Seed).NextDouble);
            Require(state.Items.Count == expected.ItemCount + (expected.Coins > 0 ? 1 : 0), "Размер награды");
            int offset = 0;
            foreach (var group in state.Plan!.Groups)
            {
                var identities = new HashSet<string>();
                for (int i = 0; i < group.Size; i++)
                {
                    var item = GuardedChestReward.Unpack(state.Items[offset++], 1, 1).GetAllItems().Single();
                    int rarity = -1;
                    bool magic = API.TryGetRarity(item, ref rarity);
                    Require(magic == (group.Quality != RewardQuality.Common) &&
                        (!magic || rarity == (int)group.Quality - 1), "Общая редкость группы");
                    Require(identities.Add(EpicLootBridge.Identity(item)), "Полная копия внутри группы");
                }
            }
            if (expected.Coins > 0)
            {
                var coins = GuardedChestReward.Unpack(state.Items.Last(), 1, 1).GetAllItems().Single();
                Require(coins.m_dropPrefab.name == "Coins" && coins.m_stack == 50, "Компенсация умножена");
            }
            string snapshot = Snapshot(chest), generated = JsonConvert.SerializeObject(state.Items);
            GuardedChestReward.Process(chest);
            Require(snapshot == Snapshot(chest) && generated == JsonConvert.SerializeObject(State(chest).Items),
                "Повторное открытие изменило предметы");
            rows.Add(new { budget, items = state.Plan.ItemCount, coins = state.Plan.Coins,
                delivered = state.Delivered, pending = state.Items.Count - state.Delivered });
        }
        var live = Guard(GuardedChestReward.Rules.Weights.Keys.ToArray());
        var bound = Chest();
        GuardedChestReward.Bind(bound, live, Context(0));
        var saved = State(bound);
        Refuses(() => GuardedChestReward.Bind(Chest(), live, Context(0)));
        Refuses(() => GuardedChestReward.Bind(bound, Guard(), Context(0)));
        live.transform.position += new Vector3(100, 0, 100);
        PovBridge.Sync.Invoke(null, new object[] { live });
        Require(PovBridge.Read(live) == saved.GuardMask, "Перемещение изменило охрану");
        PovBridge.AssignIds(live, "swift");
        GuardedChestReward.Bind(bound, live, Context(1000, 123));
        GuardedChestReward.Process(bound);
        Require(!live.IsDead() && State(bound).Budget.Value == saved.Budget.Value &&
            State(bound).GuardMask == saved.GuardMask && State(bound).Seed == saved.Seed,
            "Привязка зависит от смерти, нового набора или повторного назначения");
        rows.Add(new { saved_modifier_count = saved.GuardModifiers.Length, saved_budget = saved.Budget.Value,
            moved_alive_open = true, duplicate_guard_refused = true, rebind_unchanged = true });
        return rows;
    }

    private static ConfigEntry<float> NativeRate => (ConfigEntry<float>)AccessTools.Field(
        typeof(API).Assembly.GetType("EpicLoot.Config.ELConfig", true), "GlobalDropRateModifier").GetValue(null);

    private static object Independence()
    {
        float oldRate = NativeRate.Value;
        try
        {
            var rows = new List<object>();
            foreach (decimal budget in new[] { 0m, 120m })
            {
                var results = new List<ChestRewardState>();
                foreach (float rate in new[] { 0f, 4f })
                {
                    NativeRate.Value = rate;
                    UnityEngine.Random.InitState(881);
                    var chest = Chest();
                    NativeFixture(chest);
                    int native = Inv(chest).GetAllItems().Count(i => API.IsMagicItem(i));
                    if (rate == 0) Require(native == 0, "Штатный контроль пустого магического дропа");
                    else Require(native > 0, "Нужен непустой штатный магический контроль");
                    GuardedChestReward.Bind(chest, Guard(), Context(budget));
                    var before = UnityEngine.Random.state;
                    float expectedNext = UnityEngine.Random.value;
                    UnityEngine.Random.state = before;
                    GuardedChestReward.Process(chest);
                    Require(UnityEngine.Random.value == expectedNext, "Наш бонус расходует чужой RNG");
                    results.Add(State(chest));
                    rows.Add(new { budget, native_rate_for_fixture = rate, native_magic_items = native,
                        bonus_items = State(chest).Items.Count });
                }
                Require(results[0].Items.SequenceEqual(results[1].Items), "Чужой дроп изменил наш бонус");
            }
            return rows;
        }
        finally { NativeRate.Value = oldRate; }
    }

    private static object Technology()
    {
        var rows = new List<object>();
        foreach (string key in Bosses) ZoneSystem.instance.RemoveGlobalKey(key);
        for (int stage = 0; stage < 4; stage++)
        {
            if (stage > 0) ZoneSystem.instance.SetGlobalKey(Bosses[stage - 1]);
            foreach (string source in new[] { "TreasureChest_forestcrypt", "TreasureChest_sunkencrypt" })
            {
                var names = new Dictionary<string, int>();
                UnityEngine.Random.InitState(700 + stage);
                for (int i = 0; i < 120; i++)
                {
                    var item = EpicLootBridge.Generate(source, 1, (RewardQuality)(i % 7));
                    string name = item.m_dropPrefab.name;
                    Require(EpicLootBridge.AllowedResult(name), "Закрытая технология без штатного fallback");
                    names[name] = names.TryGetValue(name, out int count) ? count + 1 : 1;
                }
                rows.Add(new { stage, source, samples = 120, bases = names });
                if (source == "TreasureChest_sunkencrypt" && stage >= 2)
                    Require(names.Keys.Any(n => n.Contains(stage == 2 ? "Bronze" : "Iron")),
                        "Открытая технология не появилась в исходном пуле");
            }
            Require(EpicLootBridge.AvailableByBoss("PickaxeBronze") == (stage >= 2), "Ключ бронзы");
            Require(EpicLootBridge.AvailableByBoss("PickaxeIron") == (stage >= 3), "Ключ железа");
        }
        foreach (string key in Bosses) ZoneSystem.instance.RemoveGlobalKey(key);
        return rows;
    }

    private static object Mechanics()
    {
        var type = PovBridge.Assembly.GetType("PassiveTree.RarityMods", true);
        var constants = new Dictionary<string, float>();
        foreach (string name in new[] { "SwiftSpeed", "FuriousSpeed", "ArmoredTaken", "ArmoredSlow",
                     "WardedShare", "HardyPerSecond", "GreedyLeech", "KeenSight", "SteadfastStagger",
                     "PiercingArmor", "PiercingResist", "UnbreakableCap" })
            constants[name] = Convert.ToSingle(AccessTools.Property(type, name).GetValue(null));
        var rows = new List<object>();
        foreach (string prefab in new[] { "Skeleton", "Greydwarf" })
        foreach (float raw in new[] { 8f, 16f, 38.4f })
        foreach (string[] ids in new[] { Array.Empty<string>(), new[] { "armored" }, new[] { "unbreakable" },
                     new[] { "armored", "unbreakable" }, new[] { "warded" } })
        {
            var target = SpawnGuard(prefab, ids);
            float maximum = target.GetMaxHealth(), before = target.GetHealth();
            var hit = new HitData();
            hit.m_damage.m_slash = raw;
            hit.m_point = target.transform.position;
            hit.m_dodgeable = false;
            hit.m_blockable = false;
            AccessTools.Method(typeof(Character), "RPC_Damage").Invoke(target, new object[] { 0L, hit });
            float loss = before - target.GetHealth();
            Require(loss >= 0 && !float.IsNaN(loss), "Некорректный урон");
            rows.Add(new { prefab, modifiers = ids, maximum, hit_slash = raw, health_loss = loss });
        }
        return new { constants, real_rpc_damage_samples = rows,
            limitation = "single-native-hits-not-human-combat-or-sustained-DPS" };
    }

    private static void Fill(Container chest)
    {
        var inventory = Inv(chest);
        while (inventory.HaveEmptySlot())
        {
            var prefab = ObjectDB.instance.GetItemPrefab("Club");
            var item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
            item.m_dropPrefab = prefab;
            item.m_stack = 1;
            Require(inventory.AddItem(item), "Заполнение обычными предметами");
        }
    }

    private static object Journal()
    {
        var rows = new List<object>();
        foreach (string fault in new[] { "prepared", "inventory-saved" })
        {
            var chest = Chest();
            NativeFixture(chest);
            int count = Inv(chest).NrOfItems();
            GuardedChestReward.Bind(chest, Guard(), Context(120));
            GuardedChestReward.Fault = point =>
            {
                if (point == fault) throw new InvalidOperationException("injected");
            };
            Refuses(() => GuardedChestReward.Process(chest));
            Require(State(chest).Journal != null, "Не сохранён журнал до сбоя");
            GuardedChestReward.Fault = null;
            GuardedChestReward.Process(chest);
            Require(State(chest).Delivered == 1 && Inv(chest).NrOfItems() == count + 1, "Сбой потерял/повторил вещь");
            GuardedChestReward.Process(chest);
            Require(Inv(chest).NrOfItems() == count + 1, "Повтор после сбоя");
            rows.Add(new { interruption = fault, recovered = true });
        }
        // Только 10 монет места в существующей стопке: AddItem возвращает false после частичного изменения.
        var coinsChest = Chest();
        NativeFixture(coinsChest);
        Inv(coinsChest).RemoveAll(); // Подготовка одноразового fixture, не код выдачи.
        var prefab = ObjectDB.instance.GetItemPrefab("Coins");
        var coins = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
        coins.m_dropPrefab = prefab;
        coins.m_stack = coins.m_shared.m_maxStackSize - 10;
        Inv(coinsChest).AddItem(coins);
        Fill(coinsChest);
        // Clone EpicLoot добавляет пустой служебный компонент. Сравниваем предметы, количество,
        // прочность, магические свойства и позиции, а не отсутствие пустой метаинформации.
        string before = Contents(coinsChest);
        GuardedChestReward.Bind(coinsChest, Guard(), Context(1));
        GuardedChestReward.Process(coinsChest);
        Require(Contents(coinsChest) == before && State(coinsChest).Delivered == 0, "Частичная утечка монет");
        Inv(coinsChest).RemoveItem(Inv(coinsChest).GetAllItems().Last());
        GuardedChestReward.Process(coinsChest);
        Require(Inv(coinsChest).CountItems("$item_coins") == coins.m_shared.m_maxStackSize + 40,
            "Компенсация потеряна или повторена");
        rows.Add(new { partial_coin_stack_atomic = true });
        return rows;
    }

    private sealed class SavedCase
    {
        public string Name = "", Inventory = "", Reward = "";
    }

    private static object PreparePersistence()
    {
        var saves = new List<SavedCase>();
        foreach (string name in new[] { "complete", "overflow", "unformed" })
        {
            var chest = Chest();
            var guard = Guard("armored", "furious", "greedy");
            NativeFixture(chest);
            if (name == "overflow") Fill(chest);
            GuardedChestReward.Bind(chest, guard, Context(name == "overflow" ? 360 : 120));
            if (name != "unformed") GuardedChestReward.Process(chest);
            if (name == "overflow")
            {
                Require(State(chest).Delivered == 0, "Полный сундук должен сохранить очередь");
                Inv(chest).RemoveItem(Inv(chest).GetAllItems().Last());
                Inv(chest).RemoveItem(Inv(chest).GetAllItems().Last());
                GuardedChestReward.Process(chest);
                Require(State(chest).Delivered == 2 && State(chest).Items.Count > 2, "Частичная выдача");
            }
            chest.GetComponent<ZNetView>().GetZDO().Set(Tag, name);
            guard.GetComponent<ZNetView>().GetZDO().Set(Tag, "guard-" + name);
            Temporary.Remove(chest.gameObject);
            Temporary.Remove(guard.gameObject);
            saves.Add(new SavedCase { Name = name, Inventory = GuardedChestReward.StoredInventory(chest),
                Reward = JsonConvert.SerializeObject(State(chest)) });
        }
        Directory.CreateDirectory(Path.GetDirectoryName(CheckpointPath)!);
        File.WriteAllText(CheckpointPath, JsonConvert.SerializeObject(saves));
        return new { persistent_cases = saves.Select(s => s.Name).ToArray(), restart_checked = false };
    }

    private static object RestorePersistence(string phase)
    {
        var saves = JsonConvert.DeserializeObject<List<SavedCase>>(File.ReadAllText(CheckpointPath))!;
        var all = (Dictionary<ZDOID, ZDO>)AccessTools.Field(typeof(ZDOMan), "m_objectsByID").GetValue(ZDOMan.instance);
        var rows = new List<object>();
        foreach (var saved in saves)
        {
            ZDO zdo = all.Values.Single(z => z.GetString(Tag, "") == saved.Name);
            var obj = ZNetScene.instance.FindInstance(zdo.m_uid) ??
                (GameObject)AccessTools.Method(typeof(ZNetScene), "CreateObject").Invoke(ZNetScene.instance,
                    new object[] { zdo });
            var chest = obj.GetComponent<Container>();
            // Штатное получение свободного владения сервером после завершения прежней сессии.
            chest.GetComponent<ZNetView>().ClaimOwnership();
            Require(GuardedChestReward.StoredInventory(chest) == saved.Inventory,
                "Мир потерял содержимое: " + saved.Name);
            Require(JsonConvert.SerializeObject(State(chest)) == saved.Reward, "Мир потерял награду: " + saved.Name);
            var old = State(chest);
            var guardZdo = all.Values.Single(z => z.GetString(Tag, "") == "guard-" + saved.Name);
            Require(guardZdo.GetString(GuardedChestReward.IdentityKey) == old.GuardToken &&
                guardZdo.GetInt(PovBridge.ModsKey) == old.GuardMask,
                "Потерян сохранённый профиль охраны");
            Require(zdo.GetString(GuardedChestReward.IdentityKey) == old.ChestToken,
                "Потерян постоянный идентификатор сундука");
            ZoneSystem.instance.SetGlobalKey("defeated_gdking");
            GuardedChestReward.Process(chest);
            if (old.Plan != null)
                Require(old.Items.SequenceEqual(State(chest).Items), "Новый босс пересоздал готовую награду");
            else Require(State(chest).WorldKeys.Contains("defeated_gdking"), "Ключи проверены до формирования");
            if (phase == "resume" && saved.Name == "overflow")
            {
                string expected = Totals(Inv(chest).GetAllItems().Concat(State(chest).Items
                    .Skip(State(chest).Delivered).Select(s =>
                        GuardedChestReward.Unpack(s, 1, 1).GetAllItems().Single())));
                var taken = new List<ItemDrop.ItemData>();
                int removed = 0;
                while (State(chest).Delivered < State(chest).Items.Count)
                {
                    var item = Inv(chest).GetAllItems().First();
                    taken.Add(item);
                    Inv(chest).RemoveItem(item);
                    removed++;
                    GuardedChestReward.Process(chest);
                    Require(removed <= 30, "Очередь не продвигается");
                }
                Require(State(chest).Delivered == State(chest).Items.Count, "Очередь не завершена");
                Require(Totals(Inv(chest).GetAllItems().Concat(taken)) == expected,
                    "Частичная выдача потеряла или повторила обычные/бонусные вещи");
            }
            string complete = Snapshot(chest);
            GuardedChestReward.Process(chest);
            Require(complete == Snapshot(chest), "Повторное открытие после перезапуска");
            rows.Add(new { name = saved.Name, saved_inventory_matches = true, saved_reward_matches = true,
                delivered = State(chest).Delivered, total = State(chest).Items.Count,
                stable_guard_binding = true,
                new_boss_did_not_reroll = old.Plan != null });
            saved.Inventory = GuardedChestReward.StoredInventory(chest);
            saved.Reward = JsonConvert.SerializeObject(State(chest));
        }
        File.WriteAllText(CheckpointPath, JsonConvert.SerializeObject(saves));
        return rows;
    }
}
