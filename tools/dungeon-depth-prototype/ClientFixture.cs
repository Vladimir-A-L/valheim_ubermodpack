using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DungeonDepthPrototype;

// Явно включаемая подготовка отдельного мира. Открытие и перенос наград выполняются через игровой UI.
internal sealed class ClientFixture : MonoBehaviour
{
    internal const string Tag = "uberdungeon.client.fixture";
    private const string LayoutKey = Tag + ".layout";
    private const int LayoutVersion = 2;
    private static readonly string[] Names = { "zero", "coins", "single", "groups", "overflow" };
    private static readonly decimal[] Budgets = { 0, 1, 120, 240, 432 };
    private readonly Dictionary<string, string> last = new Dictionary<string, string>();
    private float next;
    private string DirectoryPath => Path.Combine(Paths.ConfigPath, "DungeonClientFixture");
    private bool CorrectWorld => ZNet.instance != null && ZNetScene.instance != null &&
        ZNet.instance.GetWorldName() == "DungeonClientRewards";
    private static ZDO Zdo(Component obj) => obj.GetComponent<ZNetView>().GetZDO();
    private static Inventory Inv(Container chest) => GuardedChestReward.Inventory(chest);

    private void Update()
    {
        if (!CorrectWorld || Time.realtimeSinceStartup < next) return;
        next = Time.realtimeSinceStartup + 0.5f;
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            // Dedicated-сервер может хранить ZDO без загруженного Unity Container.
            if (ZNet.instance.IsServer())
            foreach (var zdo in AllZdos().Values.Where(z => Names.Contains(z.GetString(Tag))))
            {
                string name = zdo.GetString(Tag);
                string value = JsonConvert.SerializeObject(new
                {
                    owner = zdo.GetOwner(), local = ZNet.GetUID(),
                    reward = EpicLootBridge.Hash(System.Text.Encoding.UTF8.GetBytes(
                        zdo.GetString(GuardedChestReward.StateKey))),
                    inventory = EpicLootBridge.Hash(zdo.GetByteArray(ZDOVars.s_items) ?? Array.Empty<byte>())
                });
                if (!last.TryGetValue("zdo-" + name, out string previous) || previous != value)
                {
                    last["zdo-" + name] = value;
                    Record("zdo-transition", new { chest = name, value });
                }
            }
            foreach (var chest in Object.FindObjectsByType<Container>(FindObjectsSortMode.None))
            {
                var state = GuardedChestReward.Read(chest);
                if (state == null) continue;
                chest.m_name = "QA " + state.Room.RoomId;
                var zdo = Zdo(chest);
                string value = JsonConvert.SerializeObject(new
                {
                    owner = zdo.GetOwner(), local = ZNet.GetUID(), in_use = chest.IsInUse(),
                    generated = state.Plan != null, state.Delivered, total = state.Items.Count,
                    journal = state.Journal != null, state.Error,
                    inventory = EpicLootBridge.Hash(zdo.GetByteArray(ZDOVars.s_items) ?? Array.Empty<byte>())
                });
                if (!last.TryGetValue(state.ChestToken, out string previous) || previous != value)
                {
                    last[state.ChestToken] = value;
                    Record("transition", new { chest = state.Room.RoomId, state.ChestToken, value });
                }
            }
            // Охрана жива, но не атакует: это проверка контейнера, не боевого баланса.
            foreach (var character in Character.GetAllCharacters())
            {
                var view = character.GetComponent<ZNetView>();
                if (view == null || !view.IsValid() || !Zdo(character).GetString(Tag).StartsWith("guard-")) continue;
                var ai = character.GetComponent<BaseAI>();
                if (ai != null) ai.enabled = false;
            }
            foreach (var wear in Object.FindObjectsByType<WearNTear>(FindObjectsSortMode.None))
            {
                var view = wear.GetComponent<ZNetView>();
                if (view != null && view.IsValid() && Zdo(wear).GetString(Tag).Length > 0)
                {
                    wear.m_noSupportWear = false;
                    wear.m_noRoofWear = false;
                }
            }
            string request = Path.Combine(DirectoryPath, "request.json");
            if (!File.Exists(request)) return;
            var command = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(request))!;
            string id = command["id"];
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-zA-Z0-9_-]{1,64}$"))
                throw new ArgumentException("Недопустимый id запроса");
            string receipt = Path.Combine(DirectoryPath, id + ".json");
            if (File.Exists(receipt)) throw new InvalidOperationException("Повторный id запроса");
            File.Move(request, Path.Combine(DirectoryPath, id + ".request.json"));
            object result;
            try { result = new { ok = true, result = Execute(command) }; }
            catch (Exception error) { result = new { ok = false, error = error.ToString() }; }
            File.WriteAllText(receipt, JsonConvert.SerializeObject(new
            {
                observed_at_utc = DateTime.UtcNow.ToString("O"), result
            }, Formatting.Indented));
        }
        catch (Exception error) { Plugin.LogError("Client fixture: " + error); }
    }

    private void Record(string kind, object value) => File.AppendAllText(Path.Combine(DirectoryPath, "events.jsonl"),
        JsonConvert.SerializeObject(new { observed_at_utc = DateTime.UtcNow.ToString("O"),
            server = ZNet.instance.IsServer(), kind, value }) + "\n");

    private static Dictionary<ZDOID, ZDO> AllZdos() =>
        (Dictionary<ZDOID, ZDO>)AccessTools.Field(typeof(ZDOMan), "m_objectsByID").GetValue(ZDOMan.instance);

    private object Execute(Dictionary<string, string> command)
    {
        string action = command["action"];
        if (action == "snapshot") return Snapshot();
        if (action == "create")
        {
            if (!ZNet.instance.IsServer()) throw new InvalidOperationException("Нужен сервер");
            var all = AllZdos();
            if (all.Values.Any(z => z.GetString(Tag).Length > 0))
                throw new InvalidOperationException("Площадка уже существует");
            for (int x = -4; x <= 20; x += 2)
            for (int z = -4; z <= 6; z += 2)
                Spawn("stone_floor_2x2", new Vector3(x, 200, z), "floor");
            for (int i = 0; i < Names.Length; i++)
            {
                var guard = Spawn("Skeleton", new Vector3(i * 4, 201, 4), "guard-" + Names[i])
                    .GetComponent<Character>();
                guard.GetComponent<BaseAI>().enabled = false;
                PovBridge.AssignIds(guard);
                // При горизонтальном взгляде сундук должен попадать в прицел без отдельного наклона.
                Spawn("stone_floor_2x2", new Vector3(i * 4, 201.5f, 0), "pedestal-" + Names[i]);
                var chest = Spawn("TreasureChest_forestcrypt", new Vector3(i * 4, 202.5f, 0), Names[i])
                    .GetComponent<Container>();
                Zdo(chest).Set(LayoutKey, LayoutVersion);
                // Штатное содержимое и pending EpicLoot сохраняются. Наполнитель помечен отдельно.
                if (Names[i] == "overflow")
                    while (Inv(chest).HaveEmptySlot()) Add(Inv(chest), "Wood", "qa-filler");
                GuardedChestReward.Bind(chest, guard, new RoomRewardContext
                {
                    DungeonId = "client-fixture", RoomId = Names[i], Depth = 20,
                    LocationBonus = Budgets[i], Seed = 8701 + i
                });
            }
            return Snapshot();
        }
        var player = Player.m_localPlayer;
        if (player == null || player.GetPlayerName() != "RewardQA")
            throw new InvalidOperationException("Нужен отдельный персонаж RewardQA");
        if (InventoryGui.IsVisible()) throw new InvalidOperationException("Сначала закрыть UI инвентаря");
        if (action == "position-status")
        {
            if (!Names.Contains(command["chest"])) throw new ArgumentException("Неизвестный сундук");
            var hovered = player.GetHoverObject();
            var chest = hovered == null ? null : hovered.GetComponentInParent<Container>();
            string? name = chest == null ? null : Zdo(chest)?.GetString(Tag);
            return new
            {
                ready_to_open = !player.IsTeleporting() && name == command["chest"],
                teleporting = player.IsTeleporting(), hovered_chest = name
            };
        }
        if (action == "position")
        {
            int index = Array.IndexOf(Names, command["chest"]);
            if (index < 0) throw new ArgumentException("Неизвестный сундук");
            var chest = AllZdos().Values.Single(z => z.GetString(Tag) == Names[index]);
            if (chest.GetInt(LayoutKey) != LayoutVersion)
                throw new InvalidOperationException("Нужна новая площадка layout 2; прежние сундуки не перемещаются");
            if (player.IsTeleporting()) throw new InvalidOperationException("Дождаться текущей телепортации");
            player.SetGodMode(true);
            player.SetGhostMode(true);
            bool started = player.TeleportTo(new Vector3(index * 4, 202, -1.7f), Quaternion.identity, false);
            if (started) AccessTools.Field(typeof(Player), "m_lookPitch").SetValue(player, 0f);
            return new { teleport_started = started, layout_version = LayoutVersion };
        }
        if (action == "prepare-overflow")
        {
            var chest = Object.FindObjectsByType<Container>(FindObjectsSortMode.None)
                .Single(c => GuardedChestReward.Read(c)?.Room.RoomId == "overflow");
            if (!chest.IsOwner() || GuardedChestReward.Read(chest)!.Plan != null)
                throw new InvalidOperationException("Нужен владелец ещё не открытого сундука");
            EpicLootBridge.FlushNative(chest);
            while (Inv(chest).HaveEmptySlot()) Add(Inv(chest), "Wood", "qa-filler");
            return Snapshot();
        }
        if (action == "fill-player")
        {
            while (player.GetInventory().HaveEmptySlot()) Add(player.GetInventory(), "Wood", "qa-player-filler");
            return Snapshot();
        }
        if (action == "clear-player-filler")
        {
            foreach (var item in player.GetInventory().GetAllItems().ToArray())
                if (item.m_customData.ContainsKey("qa-player-filler")) player.GetInventory().RemoveItem(item);
            return Snapshot();
        }
        throw new ArgumentException("Неизвестное действие подготовки");
    }

    private static GameObject Spawn(string name, Vector3 position, string tag)
    {
        var obj = Object.Instantiate(ZNetScene.instance.GetPrefab(name), position, Quaternion.identity);
        Zdo(obj.transform).Set(Tag, tag);
        var wear = obj.GetComponent<WearNTear>();
        if (wear != null) { wear.m_noSupportWear = false; wear.m_noRoofWear = false; }
        return obj;
    }

    private static void Add(Inventory inventory, string name, string tag)
    {
        var prefab = ObjectDB.instance.GetItemPrefab(name);
        var item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
        item.m_dropPrefab = prefab;
        item.m_stack = item.m_shared.m_maxStackSize;
        item.m_customData[tag] = "1";
        if (!inventory.AddItem(item)) throw new InvalidOperationException("Не удалось добавить наполнитель");
    }

    private static object Items(Inventory inventory) => inventory.GetAllItems().Select(item => new
    {
        identity = EpicLootBridge.Identity(item), prefab = item.m_dropPrefab.name,
        quantity = item.m_stack, item.m_quality, item.m_variant, item.m_durability, item.m_gridPos,
        custom = item.m_customData
    }).ToArray();

    private static object Snapshot() => new
    {
        server = ZNet.instance.IsServer(), local = ZNet.GetUID(), world_keys = ZoneSystem.instance.GetGlobalKeys(),
        network = AllZdos().Values.Where(z => Names.Contains(z.GetString(Tag))).Select(z => new
        {
            chest = z.GetString(Tag), layout_version = z.GetInt(LayoutKey), owner = z.GetOwner(),
            state = JsonConvert.DeserializeObject<ChestRewardState>(z.GetString(GuardedChestReward.StateKey)),
            inventory = EpicLootBridge.Hash(z.GetByteArray(ZDOVars.s_items) ?? Array.Empty<byte>())
        }).ToArray(),
        chests = Object.FindObjectsByType<Container>(FindObjectsSortMode.None)
            .Where(c => GuardedChestReward.Read(c) != null).Select(c => new
        {
            state = GuardedChestReward.Read(c), owner = Zdo(c).GetOwner(), in_use = c.IsInUse(),
            width = Inv(c).GetWidth(), height = Inv(c).GetHeight(), position = Position(c),
            reward_items = GuardedChestReward.Read(c)!.Items.Select(blob =>
                Items(GuardedChestReward.Unpack(blob, 1, 1))).ToArray(),
            items = Items(Inv(c))
        }).ToArray(),
        guards = Character.GetAllCharacters().Where(c => c.GetComponent<ZNetView>() != null &&
            c.GetComponent<ZNetView>().IsValid() && Zdo(c).GetString(Tag).StartsWith("guard-")).Select(c => new
        {
            tag = Zdo(c).GetString(Tag), alive = !c.IsDead(), health = c.GetHealth(), owner = Zdo(c).GetOwner(),
            position = Position(c)
        }).ToArray(),
        player_name = Player.m_localPlayer == null ? null : Player.m_localPlayer.GetPlayerName(),
        player = Player.m_localPlayer == null ? null : Items(Player.m_localPlayer.GetInventory())
    };

    private static float[] Position(Component obj) => new[]
        { obj.transform.position.x, obj.transform.position.y, obj.transform.position.z };
}
