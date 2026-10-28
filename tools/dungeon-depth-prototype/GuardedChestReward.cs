using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace DungeonDepthPrototype;

public sealed class RoomRewardContext
{
    public string DungeonId = "";
    public string RoomId = "";
    public int Depth;
    public bool ProtectedProgressionZone;
    public decimal LocationBonus;
    public int Seed;
}

public sealed class ChestRewardState
{
    public int Schema = 2;
    public string GuardZdoAtBinding = "", GuardToken = "", ChestToken = "";
    public string GuardPrefab = "", Source = "", RulesHash = "";
    public int GuardMask, GuardLevel, Seed;
    public string[] GuardModifiers = Array.Empty<string>();
    public float[] GuardOrigin = Array.Empty<float>();
    public RoomRewardContext Room = new RoomRewardContext();
    public RewardRules Rules = new RewardRules();
    public BudgetResult Budget = new BudgetResult();
    public string BoundAtUtc = "", GeneratedAtUtc = "";
    public string[] WorldKeys = Array.Empty<string>();
    public RewardPlan? Plan;
    public List<string> Items = new List<string>();
    public int Delivered;
    public RewardJournal? Journal;
    public string Error = "";
}

public static class GuardedChestReward
{
    internal const string StateKey = "uberdungeon.reward.v2";
    internal const string IdentityKey = "uberdungeon.reward.identity";
    private const string AssignedChestKey = "uberdungeon.reward.chest";
    private static readonly HashSet<Container> Busy = new HashSet<Container>();
    private static readonly HashSet<string> LoadingReported = new HashSet<string>();
    internal static Action<string>? Fault;
    internal static RewardRules Rules = null!;

    internal static Inventory Inventory(Container chest) =>
        (Inventory)AccessTools.Field(typeof(Container), "m_inventory").GetValue(chest);

    internal static string Pack(Inventory inventory)
    {
        var package = new ZPackage();
        inventory.Save(package);
        return Convert.ToBase64String(package.GetArray());
    }

    internal static Inventory Unpack(string value, int width, int height)
    {
        var inventory = new Inventory("Dungeon reward", null, width, height);
        inventory.Load(new ZPackage(Convert.FromBase64String(value)));
        return inventory;
    }

    internal static string StoredInventory(Container chest)
    {
        var bytes = chest.GetComponent<ZNetView>().GetZDO().GetByteArray(ZDOVars.s_items);
        if (bytes == null) throw new InvalidOperationException("Нет сохранённого inventory сундука");
        return Convert.ToBase64String(bytes);
    }

    private static Inventory Copy(Inventory source)
    {
        var copy = new Inventory("Dungeon transaction", null, source.GetWidth(), source.GetHeight());
        copy.GetAllItems().AddRange(source.GetAllItems().Select(item => item.Clone()));
        return copy;
    }

    private static void SetRevision(Container chest) => AccessTools.Field(typeof(Container), "m_lastRevision")
        .SetValue(chest, chest.GetComponent<ZNetView>().GetZDO().DataRevision);

    internal static string PackItem(ItemDrop.ItemData item)
    {
        var inventory = new Inventory("Dungeon item", null, 1, 1);
        if (!inventory.AddItem(item)) throw new InvalidOperationException("Не удалось сериализовать предмет");
        return Pack(inventory);
    }

    public static ChestRewardState? Read(Container chest)
    {
        var view = chest.GetComponent<ZNetView>();
        if (view == null || !view.IsValid()) return null;
        string value = view.GetZDO().GetString(StateKey, "");
        if (value.Length == 0) return null;
        var state = JsonConvert.DeserializeObject<ChestRewardState>(value)!;
        if (state.Schema != 2) throw new InvalidOperationException("Неизвестная схема награды");
        return state;
    }

    private static void Write(ZDO zdo, ChestRewardState state) =>
        zdo.Set(StateKey, JsonConvert.SerializeObject(state));

    private static string Identity(ZDO zdo)
    {
        string token = zdo.GetString(IdentityKey, "");
        if (token.Length > 0) return token;
        token = System.Guid.NewGuid().ToString("N");
        zdo.Set(IdentityKey, token);
        return token;
    }

    // Точка подключения будущего генератора комнат. Не ищем ближайшего врага и не проверяем его смерть.
    public static void Bind(Container chest, Character guard, RoomRewardContext context)
    {
        var view = chest.GetComponent<ZNetView>();
        var guardView = guard.GetComponent<ZNetView>();
        if (view == null || guardView == null || !view.IsValid() || !guardView.IsValid() ||
            !view.IsOwner() || !guardView.IsOwner())
            throw new InvalidOperationException("Привязка требует владельца обоих ZDO");
        if (context.Depth < 0 || string.IsNullOrWhiteSpace(context.DungeonId) ||
            string.IsNullOrWhiteSpace(context.RoomId) || chest.m_autoDestroyEmpty)
            throw new ArgumentException("Нужен постоянный сундук и контекст комнаты");
        // Native ZDO ID перенумеровывается при Load в 1.0.20. Постоянная связь использует собственные токены.
        string chestId = Identity(view.GetZDO()), guardId = Identity(guardView.GetZDO());
        var existing = Read(chest);
        if (existing != null)
        {
            if (existing.GuardToken != guardId)
                throw new InvalidOperationException("Сундук уже привязан к другой охране");
            return;
        }
        string assigned = guardView.GetZDO().GetString(AssignedChestKey, "");
        if (assigned.Length > 0 && assigned != chestId)
            throw new InvalidOperationException("Бюджет этой охраны уже закреплён за другим сундуком");
        int mask = PovBridge.Read(guard);
        string[] ids = PovBridge.Ids(mask);
        if (ids.Length != PovBridge.Count(mask)) throw new InvalidOperationException("Неизвестные биты PoV");
        string rulesJson = JsonConvert.SerializeObject(Rules);
        var state = new ChestRewardState
        {
            GuardToken = guardId, ChestToken = chestId, GuardZdoAtBinding = guardView.GetZDO().m_uid.ToString(),
            GuardPrefab = Utils.GetPrefabName(guard.gameObject),
            GuardMask = mask, GuardLevel = guard.GetLevel(), GuardModifiers = ids,
            GuardOrigin = new[] { guard.transform.position.x, guard.transform.position.y, guard.transform.position.z },
            Source = Utils.GetPrefabName(chest.gameObject), Seed = context.Seed,
            Room = context, Rules = JsonConvert.DeserializeObject<RewardRules>(rulesJson)!,
            RulesHash = EpicLootBridge.Hash(Encoding.UTF8.GetBytes(rulesJson)),
            Budget = Rules.Budget(ids, context.LocationBonus, context.ProtectedProgressionZone),
            BoundAtUtc = DateTime.UtcNow.ToString("O")
        };
        RewardModel.Plan(state.Budget.Value, state.Rules, () => 0);
        guardView.GetZDO().Set(AssignedChestKey, chestId);
        Write(view.GetZDO(), state);
        SetRevision(chest); // Изменилась только наша метаинформация, inventory уже актуален у владельца.
    }

    private static void Generate(ChestRewardState state)
    {
        var randomState = UnityEngine.Random.state;
        try
        {
            UnityEngine.Random.InitState(state.Seed);
            var rng = new System.Random(state.Seed);
            var plan = RewardModel.Plan(state.Budget.Value, state.Rules, rng.NextDouble);
            var items = new List<string>();
            foreach (var group in plan.Groups)
            {
                var seen = new HashSet<string>();
                for (int i = 0; i < group.Size; i++)
                {
                    ItemDrop.ItemData? item = null;
                    for (int attempt = 0; attempt < 64; attempt++)
                    {
                        var candidate = EpicLootBridge.Generate(state.Source, 1, group.Quality);
                        if (!seen.Add(EpicLootBridge.Identity(candidate))) continue;
                        item = candidate;
                        break;
                    }
                    if (item == null) throw new InvalidOperationException("Недостаточно разных предметов в группе");
                    items.Add(PackItem(item));
                }
            }
            if (plan.Coins > 0)
            {
                var prefab = ObjectDB.instance.GetItemPrefab("Coins");
                var coins = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
                coins.m_dropPrefab = prefab;
                coins.m_stack = plan.Coins;
                items.Add(PackItem(coins));
            }
            state.Items = items;
            state.Plan = plan;
            state.WorldKeys = ZoneSystem.instance.GetGlobalKeys().OrderBy(k => k).ToArray();
            state.GeneratedAtUtc = DateTime.UtcNow.ToString("O");
        }
        finally { UnityEngine.Random.state = randomState; }
    }

    public static int Process(Container chest)
    {
        var view = chest.GetComponent<ZNetView>();
        if (view == null || !view.IsValid() || !view.IsOwner() || Busy.Contains(chest) || Loading(chest)) return 0;
        var state = Read(chest);
        if (state == null || state.Error.Length > 0) return 0;
        Busy.Add(chest);
        UnityEngine.Random.State? ownRandom = null;
        try
        {
            AccessTools.Method(typeof(Container), "Load").Invoke(chest, null);
            var inventory = Inventory(chest);
            var recoveryRandom = UnityEngine.Random.state;
            try { Recover(chest, state); }
            finally { UnityEngine.Random.state = recoveryRandom; }
            EpicLootBridge.FlushNative(chest);
            ownRandom = UnityEngine.Random.state;
            if (state.Plan == null)
            {
                try { Generate(state); }
                catch (Exception error)
                {
                    // Отказ всей добавки до выдачи. Повторное открытие не перебрасывает неподдержанный пул.
                    state.Error = error.Message;
                    Write(view.GetZDO(), state);
                    throw;
                }
                Write(view.GetZDO(), state);
            }
            int before = state.Delivered;
            while (state.Delivered < state.Items.Count)
            {
                string current = StoredInventory(chest);
                var copy = Copy(inventory);
                var item = Unpack(state.Items[state.Delivered], 1, 1).GetAllItems().Single();
                // AddItem может частично добавить стопку и вернуть false. Пробуем на отдельной копии.
                if (!copy.AddItem(item)) break;
                state.Journal = new RewardJournal { Before = current, After = Pack(copy), Next = state.Delivered + 1 };
                Write(view.GetZDO(), state);
                Fault?.Invoke("prepared");
                Recover(chest, state, copy);
            }
            SetRevision(chest);
            return state.Delivered - before;
        }
        finally
        {
            if (ownRandom.HasValue) UnityEngine.Random.state = ownRandom.Value;
            Busy.Remove(chest);
        }
    }

    private static void Recover(Container chest, ChestRewardState state, Inventory? prepared = null)
    {
        if (state.Journal == null) return;
        var view = chest.GetComponent<ZNetView>();
        var inventory = Inventory(chest);
        if (!view.IsOwner()) throw new InvalidOperationException("Потеря владельца при выдаче");
        if (state.Journal.NeedsApply(StoredInventory(chest)))
        {
            // Сравниваем исходные байты ZDO. Vanilla Save/Load округляет durability до сотых и
            // иногда теряет ещё 0,01 из-за float: повторная сериализация не является точным сравнением.
            var copy = prepared ?? Unpack(state.Journal.After, inventory.GetWidth(), inventory.GetHeight());
            view.GetZDO().Set(ZDOVars.s_items, Convert.FromBase64String(state.Journal.After));
            inventory.GetAllItems().Clear();
            inventory.GetAllItems().AddRange(copy.GetAllItems());
            AccessTools.Method(typeof(Inventory), "UpdateTotalWeight").Invoke(inventory, null);
        }
        Fault?.Invoke("inventory-saved");
        state.Delivered = state.Journal.Next;
        state.Journal = null;
        Write(view.GetZDO(), state);
        SetRevision(chest);
    }

    internal static void Install(Harmony harmony)
    {
        foreach (string method in new[] { "RPC_RequestOpen", "RPC_RequestTakeAll" })
            harmony.Patch(AccessTools.Method(typeof(Container), method),
                prefix: new HarmonyMethod(typeof(GuardedChestReward), nameof(OnOpen)) { priority = Priority.First });
        harmony.Patch(AccessTools.Method(typeof(Container), "GetInventory"),
            prefix: new HarmonyMethod(typeof(GuardedChestReward), nameof(OnAccess)) { priority = Priority.First });
        harmony.Patch(AccessTools.Method(typeof(Container), "CheckForChanges"),
            postfix: new HarmonyMethod(typeof(GuardedChestReward), nameof(OnUpdate)));
    }

    private static void OnOpen(Container __instance) => SafeProcess(__instance, false);
    // Фоновый скан inventory другими модами не должен формировать закрытые награды раньше открытия.
    private static void OnAccess(Container __instance) => SafeProcess(__instance, true);
    private static void OnUpdate(Container __instance) => SafeProcess(__instance, true);

    private static bool Loading(Container chest) =>
        (bool)AccessTools.Field(typeof(Container), "m_loading").GetValue(chest);

    private static void SafeProcess(Container chest, bool pendingOnly)
    {
        // После освобождения места очередь продолжится при закрытии/следующем открытии.
        // Не заменяем ItemData под курсором уже открытого InventoryGui.
        if (Busy.Contains(chest) || chest.IsInUse()) return;
        try
        {
            var state = Read(chest);
            if (state == null || pendingOnly && state.Plan == null) return;
            if (pendingOnly && state.Delivered == state.Items.Count && state.Journal == null) return;
            // Inventory.Load добавляет вещи по одной. Патчи других модов могут вызвать GetInventory
            // посреди Load; его m_lastRevision уже обновлён, поэтому повторный Load не защитит нас.
            if (Loading(chest))
            {
                if (LoadingReported.Add(state.ChestToken))
                    Plugin.LogInfo("DUNGEON_REWARD_DEFER_LOADING: " + state.Room.RoomId);
                return;
            }
            Process(chest);
        }
        catch (Exception error) { Plugin.LogError("Ошибка бонуса сундука: " + error); }
    }
}
