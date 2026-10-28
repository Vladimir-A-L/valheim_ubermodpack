using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using Object = UnityEngine.Object;
using Point = DungeonDepthPrototype.ForestPlan.Point;

namespace DungeonDepthPrototype;

internal static class ForestRuntime
{
    internal const string Tag = "uberdungeon.forest.v1";
    internal const string RoomKey = Tag + ".room", DepthKey = Tag + ".depth", KindKey = Tag + ".kind";
    internal const string LayoutKey = Tag + ".layout";
    private static readonly string[] Modifiers = { "keen", "warded", "furious", "swift", "hardy",
        "steadfast", "greedy", "armored", "piercing", "unbreakable" };
    internal static readonly List<ZNetView> Spawned = new List<ZNetView>();
    internal static DungeonGenerator? Active;
    internal static ForestPlan.Layout? LastPlan;
    private static readonly MethodInfo Place = AccessTools.GetDeclaredMethods(typeof(DungeonGenerator))
        .Single(m => m.Name == "PlaceRoom" && m.ReturnType == typeof(Room));
    private static string ExpectedWorld = "";
    private static bool CorrectWorld => ZNet.instance != null && ZNet.instance.GetWorldName() == ExpectedWorld;

    internal static void InstallTransforms(Harmony harmony, string world)
    {
        ExpectedWorld = world;
        harmony.Patch(AccessTools.Method(typeof(ZoneSystem), "SpawnLocation"),
            prefix: new HarmonyMethod(typeof(ForestRuntime), nameof(BeforeLocation)),
            finalizer: new HarmonyMethod(typeof(ForestRuntime), nameof(AfterLocation)));
        harmony.Patch(AccessTools.Method(typeof(DungeonGenerator), "Awake"),
            prefix: new HarmonyMethod(typeof(ForestRuntime), nameof(BeforeAwake)));
    }

    private sealed class TransformState
    {
        internal ZoneSystem.ZoneLocation Source = null!;
        internal Location Location = null!;
        internal DungeonGenerator Generator = null!;
        internal DungeonGenerator? OriginalGenerator;
        internal Transform? OriginalInterior;
        internal bool LocationFlag, GeneratorFlag;
    }

    private static void BeforeLocation(ZoneSystem.ZoneLocation location, out TransformState? __state)
    {
        __state = null;
        if (!CorrectWorld) return;
        location.m_prefab.Load();
        var value = location.m_prefab.Asset.GetComponent<Location>();
        var generators = value == null ? Array.Empty<DungeonGenerator>() :
            value.GetComponentsInChildren<DungeonGenerator>(true)
                .Where(g => Utils.GetPrefabName(g.gameObject) == "DG_ForestCrypt").ToArray();
        if (generators.Length > 0)
        {
            if (generators.Length != 1) throw new InvalidOperationException("Неоднозначный лесной интерьер");
            var generator = generators[0];
            var interior = generator.transform.parent;
            if (interior == null || interior == value!.transform || interior.localPosition.y < 1000)
                throw new InvalidOperationException("Не найдена отдельная группа лесного интерьера");
            __state = new TransformState { Source = location, Location = value,
                Generator = generator, OriginalGenerator = value.m_generator,
                OriginalInterior = value.m_interiorTransform,
                LocationFlag = value.m_useCustomInteriorTransform,
                GeneratorFlag = generator.m_useCustomInteriorTransform };
            // Штатный механизм одновременно переносит интерьер и внутреннюю точку входного перехода.
            value.m_generator = generator;
            value.m_interiorTransform = interior;
            value.m_useCustomInteriorTransform = true;
            generator.m_useCustomInteriorTransform = true;
            return; // Удерживаем prefab до восстановления исходных ссылок в finalizer.
        }
        location.m_prefab.Release();
    }

    private static void AfterLocation(TransformState? __state)
    {
        if (__state == null) return;
        __state.Location.m_useCustomInteriorTransform = __state.LocationFlag;
        __state.Generator.m_useCustomInteriorTransform = __state.GeneratorFlag;
        __state.Location.m_generator = __state.OriginalGenerator!;
        __state.Location.m_interiorTransform = __state.OriginalInterior!;
        __state.Source.m_prefab.Release();
    }

    private static void BeforeAwake(DungeonGenerator __instance)
    {
        if (CorrectWorld && Utils.GetPrefabName(__instance.gameObject) == "DG_ForestCrypt")
            __instance.m_useCustomInteriorTransform = true;
    }

    internal static Point P(Vector3 p) => new Point(p.x, p.y, p.z);
    internal static Vector3 V(Point p) => new Vector3(p.X, p.Y, p.Z);
    internal static ZDO Zdo(Component obj) => obj.GetComponent<ZNetView>().GetZDO();
    internal static int Turn(Quaternion q)
    {
        int turn = Mathf.RoundToInt(q.eulerAngles.y / 90) % 4;
        if (Quaternion.Angle(q, Quaternion.Euler(0, turn * 90, 0)) > .1f)
            throw new InvalidOperationException("Неподдержанный поворот лесного интерьера");
        return turn;
    }

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(DungeonGenerator), "GenerateDungeon"),
            prefix: new HarmonyMethod(typeof(ForestRuntime), nameof(Generate)));
        harmony.Patch(AccessTools.Method(typeof(ZNetView), "Awake"),
            postfix: new HarmonyMethod(typeof(ForestRuntime), nameof(Created)));
    }

    private static void Created(ZNetView __instance)
    {
        if (Active != null && __instance.IsValid()) Spawned.Add(__instance);
    }

    internal static ForestPlan.Template[] Catalog(IEnumerable<DungeonDB.RoomData> data) => data.Select(d =>
    {
        var room = d.RoomInPrefab;
        return new ForestPlan.Template { Name = d.m_prefab.Name, Size = P(room.m_size),
            Entrance = room.m_entrance, Endcap = room.m_endCap,
            Sockets = room.GetConnections().Select(c => new ForestPlan.Socket
            {
                Position = P(c.transform.localPosition), Turn = Turn(c.transform.localRotation),
                Entrance = c.m_entrance, Type = c.m_type
            }).ToArray() };
    }).ToArray();

    private static bool Generate(DungeonGenerator __instance, ZoneSystem.SpawnMode mode)
    {
        if (Utils.GetPrefabName(__instance.gameObject) != "DG_ForestCrypt") return true;
        if (!ZNet.instance.IsServer() || mode == ZoneSystem.SpawnMode.Client)
            throw new InvalidOperationException("Генерация полного лесного профиля требует сервера");
        var data = ((List<DungeonDB.RoomData>)AccessTools.Field(typeof(DungeonGenerator), "m_availableRooms")
            .GetValue(null)).ToDictionary(d => d.m_prefab.Name);
        var plan = ForestPlan.Create(Catalog(data.Values), P(__instance.transform.position),
            Turn(__instance.transform.rotation), P(__instance.m_zoneCenter), P(__instance.m_zoneSize),
            __instance.m_generatedSeed);
        string token = Guid.NewGuid().ToString("N");
        Active = __instance; Spawned.Clear(); LastPlan = null;
        try
        {
            var placed = new List<Room>();
            for (int i = 0; i < plan.Rooms.Count; i++)
            {
                var r = plan.Rooms[i];
                var source = data[r.Template.Name];
                var position = V(r.Position); var rotation = Quaternion.Euler(0, r.Turn * 90, 0);
                if (r.Template.Size.X > 0 && r.Template.Size.Z > 0 &&
                    (bool)AccessTools.Method(typeof(DungeonGenerator), "TestCollision").Invoke(__instance,
                        new object[] { source.RoomInPrefab, position, rotation }))
                    throw new InvalidOperationException("Штатная проверка коллизий отклонила комнату " + i);
                RoomConnection? from = r.Parent < 0 ? null : placed[r.Parent].GetConnections()[r.ParentSocket];
                int first = Spawned.Count;
                var room = (Room)Place.Invoke(__instance, new object?[] { source, position, rotation, from, mode });
                placed.Add(room);
                var objects = Spawned.Skip(first).Where(v => v != null).ToList();
                Populate(source.RoomInPrefab, r, token, i, plan.Seed, objects, mode);
            }
            var zdo = Zdo(__instance);
            zdo.Set(Tag, token);
            zdo.Set(LayoutKey, JsonConvert.SerializeObject(new
            {
                schema = 1, seed = plan.Seed, attempts = plan.Attempts,
                rooms = plan.Rooms.Select((r, i) => new { id = i, prefab = r.Template.Name,
                    position = new[] { r.Position.X, r.Position.Y, r.Position.Z }, turn = r.Turn,
                    depth = r.Depth, parent = r.Parent, endcap = r.Template.Endcap,
                    cores = r.GuaranteedCores,
                    sockets = Enumerable.Range(0, r.Template.Sockets.Length).Select(s => new
                    {
                        position = new[] { r.SocketPosition(s).X, r.SocketPosition(s).Y, r.SocketPosition(s).Z },
                        entrance = r.Template.Sockets[s].Entrance
                    }) }),
                edges = plan.Edges.Select(e => new[] { e.A, e.B })
            }));
            LastPlan = plan;
            Plugin.LogInfo($"FOREST_GENERATED seed={plan.Seed} rooms={plan.Rooms.Count} depth=40");
        }
        catch
        {
            foreach (var view in Spawned)
                if (view != null && view.IsValid() && view.IsOwner()) ZNetScene.instance.Destroy(view.gameObject);
            __instance.Clear();
            throw;
        }
        finally { Active = null; }
        return false;
    }

    private static void TagObject(ZNetView view, string token, int index, int depth, string kind)
    {
        var zdo = view.GetZDO();
        zdo.Set(Tag, token); zdo.Set(RoomKey, index); zdo.Set(DepthKey, depth); zdo.Set(KindKey, kind);
    }

    private static void Populate(Room template, ForestPlan.Placement room, string token, int index, int seed,
        List<ZNetView> objects, ZoneSystem.SpawnMode mode)
    {
        var rotation = Quaternion.Euler(0, room.Turn * 90, 0);
        Vector3 Position(Component c) => V(room.Position) + rotation * template.transform.InverseTransformPoint(
            c.transform.position);
        ZNetView Ensure(Component source)
        {
            Vector3 pos = Position(source);
            var existing = objects.FirstOrDefault(v => Vector3.Distance(v.transform.position, pos) < .03f &&
                Utils.GetPrefabName(v.gameObject) == Utils.GetPrefabName(source.gameObject));
            if (existing != null) return existing;
            var prefab = ZNetScene.instance.GetPrefab(Utils.GetPrefabName(source.gameObject));
            if (prefab == null) throw new InvalidOperationException("Не найден prefab обязательного объекта");
            var view = Object.Instantiate(prefab, pos,
                rotation * Quaternion.Inverse(template.transform.rotation) * source.transform.rotation)
                .GetComponent<ZNetView>();
            objects.Add(view);
            return view;
        }
        if (room.GuaranteedCores)
            foreach (var core in template.GetComponentsInChildren<Pickable>(true)
                         .Where(p => p.m_itemPrefab != null && p.m_itemPrefab.name == "SurtlingCore"))
                Ensure(core);
        // Каждая выбранная комната награды сохраняет штатный сундук и хотя бы одного штатного охранника.
        foreach (var chest in template.GetComponentsInChildren<Container>(true)) Ensure(chest);
        bool hasChest = objects.Any(v => v.GetComponent<Container>() != null);
        if (hasChest && !objects.Any(v => v.GetComponent<CreatureSpawner>() != null))
        {
            var spawner = template.GetComponentsInChildren<CreatureSpawner>(true).First();
            Ensure(spawner);
        }
        var guards = new List<Character>();
        foreach (var view in objects.ToArray())
        {
            var spawner = view.GetComponent<CreatureSpawner>();
            if (spawner == null) continue;
            // Однократная исходная охрана. Штатная точка сохраняется; AI, преследование и лут не ограничиваются.
            var guard = Object.Instantiate(spawner.m_creaturePrefab,
                spawner.transform.position + Vector3.up * .15f, spawner.transform.rotation).GetComponent<Character>();
            guard.SetLevel(spawner.m_minLevel);
            PovBridge.AssignIds(guard, Modifiers.Take(ForestPlan.ModifierCount(room.Depth)).ToArray());
            TagObject(guard.GetComponent<ZNetView>(), token, index, room.Depth, "guard");
            guards.Add(guard);
            ZNetScene.instance.Destroy(view.gameObject);
            objects.Remove(view);
        }
        int guardIndex = 0;
        foreach (var view in objects)
        {
            var chest = view.GetComponent<Container>();
            var pickable = view.GetComponent<Pickable>();
            string kind = chest != null ? "chest" : pickable != null && pickable.m_itemPrefab.name == "SurtlingCore"
                ? "core" : "object";
            TagObject(view, token, index, room.Depth, kind);
            if (chest == null) continue;
            if (guardIndex >= guards.Count) throw new InvalidOperationException("Недостаточно охраны для сундуков");
            GuardedChestReward.Bind(chest, guards[guardIndex++], new RoomRewardContext
            {
                DungeonId = token, RoomId = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Depth = room.Depth, ProtectedProgressionZone = room.Depth <= ForestPlan.SafeDepth,
                Seed = unchecked(seed * 397 ^ index)
            });
        }
        if (room.GuaranteedCores && !objects.Any(v => v.GetComponent<Pickable>()?.m_itemPrefab.name == "SurtlingCore"))
            throw new InvalidOperationException("В ресурсной комнате не появились ядра");
        if (mode == ZoneSystem.SpawnMode.Ghost)
            foreach (var view in objects.Concat(guards.Select(g => g.GetComponent<ZNetView>())))
                Object.Destroy(view.gameObject);
    }
}
