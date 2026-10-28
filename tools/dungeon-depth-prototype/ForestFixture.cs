using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DungeonDepthPrototype;

// Диагностика не открывает сундуки; отдельная команда выдаёт помеченный комплект ручной проверки.
internal sealed class ForestFixture : MonoBehaviour
{
    private float next;
    private string Folder => Path.Combine(Paths.ConfigPath, "ForestCryptQA");
    internal static Dictionary<ZDOID, ZDO> AllZdos() =>
        (Dictionary<ZDOID, ZDO>)AccessTools.Field(typeof(ZDOMan), "m_objectsByID").GetValue(ZDOMan.instance);

    private void Update()
    {
        if (ZNet.instance == null || ZNet.instance.GetWorldName() != "ForestCryptQA" ||
            Time.realtimeSinceStartup < next) return;
        next = Time.realtimeSinceStartup + .2f;
        Directory.CreateDirectory(Folder);
        string request = Path.Combine(Folder, "request.json");
        if (!File.Exists(request)) return;
        try
        {
            var command = JObject.Parse(File.ReadAllText(request));
            string id = command.Value<string>("id") ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-zA-Z0-9_-]{1,64}$"))
                throw new ArgumentException("Недопустимый id");
            string target = Path.Combine(Folder, id + ".json");
            if (File.Exists(target)) throw new InvalidOperationException("Повторная команда");
            File.Move(request, Path.Combine(Folder, id + ".request.json"));
            object result;
            try { result = new { ok = true, value = Execute(command) }; }
            catch (Exception e) { result = new { ok = false, error = e.ToString() }; }
            File.WriteAllText(target, JsonConvert.SerializeObject(new
                { observed_at_utc = DateTime.UtcNow.ToString("O"), result }, Formatting.Indented));
        }
        catch (Exception e) { Plugin.LogError("FOREST_DIAGNOSTICS " + e); }
    }

    private object Execute(JObject command)
    {
        string action = command.Value<string>("action") ?? "";
        if (action == "loadout-check") return ForestLoadout.Validate();
        if (action == "manual-god-probe") return ForestManual.Probe();
        if (action == "save-player")
        {
            if (Player.m_localPlayer?.GetPlayerName() != "RewardQA")
                throw new InvalidOperationException("Нужен тестовый персонаж");
            Game.instance.SavePlayerProfile(true);
            return PlayerState();
        }
        if (action == "snapshot") return Snapshot();
        if (action == "probe")
        {
            if (!ZNet.instance.IsServer()) throw new InvalidOperationException("Нужен сервер");
            return Probe();
        }
        if (action == "locations")
            return ZoneSystem.instance.m_locationInstances.Values
                .Where(l => l.m_location.m_prefab.Name == "Crypt2")
                .OrderBy(l => l.m_position.sqrMagnitude).Take(5)
                .Select(l => new { position = V(l.m_position), placed = l.m_placed }).ToArray();
        var player = Player.m_localPlayer;
        if (player == null || player.GetPlayerName() != "RewardQA")
            throw new InvalidOperationException("Нужен отдельный персонаж RewardQA");
        if (action == "prepare")
        {
            var p = command["position"]!.ToObject<float[]>()!;
            player.SetGodMode(true); player.SetGhostMode(true);
            return new { teleport_started = player.TeleportTo(new Vector3(p[0], p[1], p[2]),
                Quaternion.identity, false) };
        }
        if (action == "aim")
        {
            if (player.IsTeleporting() || InventoryGui.IsVisible())
                throw new InvalidOperationException("Ввод заблокирован состоянием UI");
            float yaw = command.Value<float>("yaw"), pitch = command.Value<float>("pitch");
            AccessTools.Field(typeof(Character), "m_lookYaw").SetValue(player, Quaternion.Euler(0, yaw, 0));
            AccessTools.Field(typeof(Player), "m_lookPitch").SetValue(player, Mathf.Clamp(pitch, -80, 80));
            AccessTools.Method(typeof(Player), "UpdateEyeRotation").Invoke(player, null);
            return new { aimed = true };
        }
        if (action == "status") return PlayerState();
        if (action == "bronze-loadout") return ForestLoadout.Grant(player);
        if (action == "manual-ready")
        {
            if (player.IsDead() || player.IsTeleporting())
                throw new InvalidOperationException("Персонаж ещё не готов");
            player.SetGodMode(true); player.SetGhostMode(false);
            return PlayerState();
        }
        throw new ArgumentException("Неизвестная команда");
    }

    private static float[] V(Vector3 p) => new[] { p.x, p.y, p.z };

    private static object PlayerState()
    {
        var player = Player.m_localPlayer;
        if (player == null) return new { exists = false };
        var hover = player.GetHoverObject();
        var view = hover == null ? null : hover.GetComponentInParent<ZNetView>();
        return new
        {
            exists = true, position = V(player.transform.position), eye = V(player.GetEyePoint()),
            yaw = player.GetLookYaw().eulerAngles.y, on_ground = player.IsOnGround(),
            teleporting = player.IsTeleporting(), inventory_open = InventoryGui.IsVisible(),
            dead = player.IsDead(), cores = player.GetInventory().CountItems("$item_surtlingcore"),
            god = player.InGodMode(), ghost = player.InGhostMode(),
            health = player.GetHealth(), max_health = player.GetMaxHealth(),
            inventory = player.GetInventory().GetAllItems().Select(i => new
            {
                prefab = i.m_dropPrefab.name, count = i.m_stack, quality = i.m_quality,
                equipped = i.m_equipped, loadout = i.m_customData.ContainsKey(ForestLoadout.Key)
            }).ToArray(),
            hovered = view == null ? "" : Utils.GetPrefabName(view.gameObject),
            hover_room = view == null ? -1 : view.GetZDO().GetInt(ForestRuntime.RoomKey, -1),
            ground = Physics.Raycast(player.transform.position + Vector3.up * .3f, Vector3.down,
                out RaycastHit hit, 2f, LayerMask.GetMask("Default", "static_solid", "piece", "terrain"))
                ? V(hit.point) : Array.Empty<float>()
        };
    }

    private static object Snapshot() => new
    {
        player = PlayerState(),
        dungeons = AllZdos().Values.Where(z => z.GetString(ForestRuntime.LayoutKey).Length > 0)
            .Select(z => new { token = z.GetString(ForestRuntime.Tag), layout =
                JObject.Parse(z.GetString(ForestRuntime.LayoutKey)), position = V(z.GetPosition()) }).ToArray(),
        objects = AllZdos().Values.Where(z => z.GetString(ForestRuntime.KindKey).Length > 0)
            .Select(z => new
            {
                token = z.GetString(ForestRuntime.Tag), kind = z.GetString(ForestRuntime.KindKey),
                room = z.GetInt(ForestRuntime.RoomKey), depth = z.GetInt(ForestRuntime.DepthKey),
                position = V(z.GetPosition()), prefab = z.GetPrefab(), picked = z.GetBool(ZDOVars.s_picked),
                mods = z.GetInt(PovBridge.ModsKey), reward = z.GetString(GuardedChestReward.StateKey),
                inventory = Convert.ToBase64String(z.GetByteArray(ZDOVars.s_items) ?? Array.Empty<byte>())
            }).ToArray()
    };

    private object Probe()
    {
        var results = new List<object>();
        var fingerprints = new Dictionary<int, string>();
        var template = ZNetScene.instance.GetPrefab("DG_ForestCrypt");
        foreach (int seed in new[] { 1001, 1002, 1003, 1004, 1005, 1006, 1001, 1002, 1003, 1004, 1005, 1006 })
        {
            var generator = Object.Instantiate(template, new Vector3(0, 5000, -1), Quaternion.identity)
                .GetComponent<DungeonGenerator>();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                generator.Generate(seed, ZoneSystem.SpawnMode.Full);
                var layout = ForestRuntime.LastPlan ?? throw new InvalidOperationException("Нет плана");
                var rooms = generator.GetComponentsInChildren<Room>();
                var sockets = rooms.SelectMany((r, i) => r.GetConnections().Select(s => (room: i, s))).ToArray();
                var edges = new HashSet<(int, int)>();
                int open = 0;
                foreach (var a in sockets.Where(s => !s.s.m_entrance))
                {
                    var matches = sockets.Where(b => b.room != a.room && !b.s.m_entrance &&
                        a.s.TestContact(b.s) && a.s.m_type == b.s.m_type &&
                        Vector3.Dot(a.s.transform.forward, b.s.transform.forward) < -.99f).ToArray();
                    if (matches.Length != 1) open++;
                    foreach (var b in matches) edges.Add((Math.Min(a.room, b.room), Math.Max(a.room, b.room)));
                }
                var depths = DungeonGeometryProbe.RoomGraph.Distances(rooms.Length, edges, 0);
                var views = ForestRuntime.Spawned.Where(v => v != null && v.IsValid()).ToArray();
                var cores = views.Where(v => v.GetComponent<Pickable>()?.m_itemPrefab.name == "SurtlingCore").ToArray();
                var guards = views.Select(v => v.GetComponent<Character>()).Where(c => c != null).ToArray();
                var chests = views.Select(v => v.GetComponent<Container>()).Where(c => c != null).ToArray();
                bool depthMatch = Enumerable.Range(0, rooms.Length).All(i => rooms[i].m_endCap ||
                    depths[i] == layout.Rooms[i].Depth);
                bool guardMatch = guards.All(c => PovBridge.Count(PovBridge.Read(c)) ==
                    ForestPlan.ModifierCount(ForestRuntime.Zdo(c).GetInt(ForestRuntime.DepthKey)));
                bool rewardMatch = chests.All(c => GuardedChestReward.Read(c) is ChestRewardState state &&
                    state.GuardModifiers.Length == ForestPlan.ModifierCount(state.Room.Depth) &&
                    (state.Room.Depth > 16 || state.Budget.Value == 0));
                int earlyCores = cores.Count(v => v.GetZDO().GetInt(ForestRuntime.DepthKey) <= 16);
                string fingerprint = EpicLootBridge.Hash(ForestRuntime.Zdo(generator).GetByteArray(ZDOVars.s_roomData));
                bool repeat = !fingerprints.TryGetValue(seed, out string previous) || previous == fingerprint;
                fingerprints[seed] = fingerprint;
                bool passed = open == 0 && depthMatch && guardMatch && rewardMatch && earlyCores >= 5 && repeat;
                results.Add(new { seed, passed, rooms = rooms.Length, open_sockets = open,
                    max_depth = layout.Rooms.Max(r => r.Depth), early_core_pickables = earlyCores,
                    guards = guards.Length, chests = chests.Length, depth_match = depthMatch,
                    guard_match = guardMatch, reward_match = rewardMatch, repeat_matches = repeat,
                    fingerprint, duration_ms = watch.ElapsedMilliseconds });
            }
            catch (Exception e) { results.Add(new { seed, passed = false, error = e.ToString() }); }
            finally
            {
                foreach (var view in ForestRuntime.Spawned.ToArray())
                    if (view != null && view.IsValid() && view.IsOwner()) ZNetScene.instance.Destroy(view.gameObject);
                ForestRuntime.Spawned.Clear();
                ZNetScene.instance.Destroy(generator.gameObject);
            }
        }
        return results;
    }
}
