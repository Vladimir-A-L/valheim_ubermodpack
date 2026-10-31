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
    private const int MaxRadialObjects = 2048;
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
        RadialEntrance.Install(harmony);
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
        internal Vector3 InteriorPosition, GeneratorSize;
        internal RadialEntranceState? PreviousRadial;
    }

    private static void BeforeLocation(ZoneSystem.ZoneLocation location, int seed, Vector3 pos, Quaternion rot,
        ZoneSystem.SpawnMode mode, out TransformState? __state)
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
                GeneratorFlag = generator.m_useCustomInteriorTransform,
                InteriorPosition = interior.localPosition, GeneratorSize = generator.m_zoneSize,
                PreviousRadial = RadialEntrance.Current };
            RadialEntrance.Current = RadialEntrance.Choose(value, seed, pos, rot, mode);
            if (RadialEntrance.Current != null)
            {
                var p = interior.localPosition; p.y = RadialEntrance.Current.Height - pos.y;
                interior.localPosition = p; generator.m_zoneSize = RadialEntrance.Size;
            }
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
        __state.Generator.transform.parent.localPosition = __state.InteriorPosition;
        __state.Generator.m_zoneSize = __state.GeneratorSize;
        RadialEntrance.Current = __state.PreviousRadial;
        __state.Source.m_prefab.Release();
    }

    private static void BeforeAwake(DungeonGenerator __instance)
    {
        if (CorrectWorld && Utils.GetPrefabName(__instance.gameObject) == "DG_ForestCrypt")
        {
            __instance.m_useCustomInteriorTransform = true;
            if ((MiniBossRuntime.Data(__instance)?.GetString(RadialRuntime.Key).Length ?? 0) > 0)
                __instance.m_zoneSize = RadialEntrance.Size;
        }
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
        ForestGates.Install(harmony);
        ForestGuardRecovery.Install(harmony);
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
                Entrance = c.m_entrance, Type = c.m_type, AllowDoor = c.m_allowDoor,
                DoorRequiresBoth = c.m_doorOnlyIfOtherAlsoAllowsDoor
            }).ToArray() };
    }).ToArray();

    private static bool NativeCollision(DungeonGenerator generator, Room room, Vector3 position, Quaternion rotation)
    {
        var colliders = generator.GetComponents<Collider>();
        try
        {
            foreach (var collider in colliders) collider.enabled = true;
            return (bool)AccessTools.Method(typeof(DungeonGenerator), "TestCollision")
                .Invoke(generator, new object[] { room, position, rotation });
        }
        finally { foreach (var collider in colliders) collider.enabled = false; }
    }

    private static bool Generate(DungeonGenerator __instance, ZoneSystem.SpawnMode mode)
    {
        var entry = RadialEntrance.Current;
        bool ordinary = entry != null && Utils.GetPrefabName(__instance.gameObject) == "DG_ForestCrypt" &&
            mode != ZoneSystem.SpawnMode.Client;
        if (ordinary && Zdo(__instance).GetString(LayoutKey).Length > 0)
            throw new InvalidOperationException("Сохранённый данж нельзя перестраивать запасным генератором");
        if (ordinary && RadialRuntime.Read(Zdo(__instance)) == null)
        {
            entry!.Snapshot.Validate();
            Zdo(__instance).Set(RadialRuntime.Key, JsonConvert.SerializeObject(entry.Snapshot));
        }
        try
        {
            bool native = GenerateCore(__instance, mode);
            if (ordinary) entry!.Outcome = native ? "vanilla" : "radial";
            return native;
        }
        catch (Exception error) when (ordinary)
        {
            // GenerateCore удаляет частичное наполнение. Штатный генератор создаёт рабочий запасной склеп.
            entry!.Outcome = "fallback-vanilla"; entry.Failure = error.ToString();
            foreach (var collider in __instance.GetComponents<Collider>()) collider.enabled = true;
            foreach (string field in new[] { "m_placedRooms", "m_openConnections", "m_doorConnections" })
                ((System.Collections.IList)AccessTools.Field(typeof(DungeonGenerator), field)
                    .GetValue(__instance)).Clear();
            Zdo(__instance).Set(RadialEntrance.Key, JsonConvert.SerializeObject(entry));
            Plugin.LogError("RADIAL_FALLBACK " + error);
            return true;
        }
    }

    private static bool GenerateCore(DungeonGenerator __instance, ZoneSystem.SpawnMode mode)
    {
        if (Utils.GetPrefabName(__instance.gameObject) != "DG_ForestCrypt") return true;
        bool isolated = Zdo(__instance).GetBool(MiniBossConnection.CandidateKey);
        bool legacyTrial = Zdo(__instance).GetInt(MiniBossTrial.IndexKey, -1) >= 0 && MiniBossTrial.Enabled;
        if (!isolated && !legacyTrial && !Plugin.Instance.Config.Bind("ForestCrypt", "IntegratedEncounters", false,
                "Создание соединённых данжей вне явно вызванного кандидата.").Value) return true;
        if (!isolated && !legacyTrial && RadialEntrance.Current == null) return true;
        if (!ZNet.instance.IsServer() || mode == ZoneSystem.SpawnMode.Client)
            throw new InvalidOperationException("Генерация полного лесного профиля требует сервера");
        if (Zdo(__instance).GetString(LayoutKey).Length > 0)
            throw new InvalidOperationException("Сохранённый данж нельзя перестраивать генерацией");
        Spawned.Clear(); LastPlan = null;
        int routeLength = Plugin.Instance.Config.Bind("ForestCrypt", "RouteLength", ForestPlan.MaxDepth,
            "Длина основного маршрута нового кандидата, отдельно от шкалы сложности.").Value;
        var radial = RadialRuntime.Read(Zdo(__instance));
        var rules = radial?.Priced ?? GuardedChestReward.Rules;
        if (radial != null) routeLength = radial.Selection.MaximumDepth;
        decimal terminalBudget = radial == null ? EncounterPlan.ForestTerminalBudget(rules, routeLength) :
            rules.Budget(radial.Selection.AtDepth(routeLength), 0, false).Value;
        var data = ((List<DungeonDB.RoomData>)AccessTools.Field(typeof(DungeonGenerator), "m_availableRooms")
            .GetValue(null)).ToDictionary(d => d.m_prefab.Name);
        bool trial = MiniBossTrial.Enabled && Zdo(__instance).GetInt(MiniBossTrial.IndexKey, -1) >= 0;
        var plan = trial ? ForestPlan.CreateTrial(Catalog(data.Values), P(__instance.transform.position),
            Turn(__instance.transform.rotation), P(__instance.m_zoneCenter), P(__instance.m_zoneSize),
            __instance.m_generatedSeed) : ForestPlan.Create(Catalog(data.Values), P(__instance.transform.position),
            Turn(__instance.transform.rotation), P(__instance.m_zoneCenter), P(__instance.m_zoneSize),
            radial?.Selection.Seed ?? __instance.m_generatedSeed, ForestGates.Enabled, routeLength,
            radial?.Selection);
        string token = Guid.NewGuid().ToString("N");
        Active = __instance; Spawned.Clear(); LastPlan = null;
        // Служебные коробки сравнения объёмов стоят у входа. Они не являются стенами или опорой.
        foreach (var collider in __instance.GetComponents<Collider>()) collider.enabled = false;
        try
        {
            var placed = new List<Room?>();
            GameObject? section = null;
            for (int i = 0; i < plan.Rooms.Count; i++)
            {
                if (i == 4 && RadialEntrance.Current?.ForcedFailure == true)
                    throw new InvalidOperationException("Диагностический отказ после частичного размещения");
                var r = plan.Rooms[i];
                if (r.MiniBoss)
                {
                    int encounterSeed = unchecked(plan.Seed * 397 ^ i);
                    var encounter = MiniBossEncounter.Create(encounterSeed, r.CombatDepth,
                        rules, MiniBossTrial.Name(encounterSeed), radial?.Selection);
                    section = MiniBossArenaSection.Create(V(r.Position), Quaternion.Euler(0, r.Turn * 90, 0),
                        encounter, token, i);
                    if (radial != null && Spawned.Count > MaxRadialObjects)
                        throw new InvalidOperationException("Превышен предел сетевых объектов данжа");
                    var parent = plan.Rooms[r.Parent];
                    string link = JsonConvert.SerializeObject(new MiniBossLink {
                        Seed = encounterSeed, Depth = r.CombatDepth, Room = r.Parent, Socket = r.ParentSocket,
                        Turn = r.Turn, Token = token, ParentPrefab = parent.Template.Name,
                        ParentPosition = ForestFixture.V(V(parent.Position)),
                        Origin = ForestFixture.V(V(r.Position)) });
                    var rootData = Zdo(section.transform); var generatorData = Zdo(__instance);
                    rootData.Set(MiniBossConnection.LinkKey, link);
                    generatorData.Set(MiniBossConnection.LinkKey, link);
                    placed.Add(null); continue;
                }
                var source = data[r.Template.Name];
                var position = V(r.Position); var rotation = Quaternion.Euler(0, r.Turn * 90, 0);
                if (r.Template.Size.X > 0 && r.Template.Size.Z > 0 &&
                    NativeCollision(__instance, source.RoomInPrefab, position, rotation))
                    throw new InvalidOperationException("Штатная проверка коллизий отклонила комнату " + i);
                RoomConnection? from = r.Parent < 0 ? null : placed[r.Parent]!.GetConnections()[r.ParentSocket];
                int first = Spawned.Count;
                var room = (Room)Place.Invoke(__instance, new object?[] { source, position, rotation, from, mode });
                placed.Add(room);
                var objects = Spawned.Skip(first).Where(v => v != null).ToList();
                if (trial) MiniBossTrial.ClearNative(objects);
                else Populate(source.RoomInPrefab, r, token, i, plan.Seed, objects, mode, radial);
                if (radial != null && Spawned.Count > MaxRadialObjects)
                    throw new InvalidOperationException("Превышен предел сетевых объектов данжа");
            }
            if (trial) MiniBossTrial.Populate(__instance, plan, token);
            else ForestGates.Place(__instance, plan, token);
            if (radial != null && Spawned.Count > MaxRadialObjects)
                throw new InvalidOperationException("Превышен предел сетевых объектов данжа с дверями");
            var zdo = Zdo(__instance);
            zdo.Set(Tag, token);
            zdo.Set(LayoutKey, JsonConvert.SerializeObject(new
            {
                schema = radial == null ? 6 : 7, seed = plan.Seed, attempts = plan.Attempts,
                search_steps = plan.SearchSteps, radial = radial?.Selection, branches = plan.BranchCount,
                max_depth = plan.Rooms.Max(r => r.Depth), route_length = plan.RouteLength,
                safe_depth = trial ? 0 : radial?.Selection.Profile.SafeDepth ?? ForestPlan.SafeDepth,
                miniboss_trial = trial, combat_profile_depth = trial ? 64 : 0,
                terminal_budget = terminalBudget,
                stage_gates = plan.StageGates,
                gates = plan.StageGates ? EncounterGates.Edges(plan).Select(e => new[] { e.A, e.B }).ToArray() :
                    Array.Empty<int[]>(),
                rooms = plan.Rooms.Select((r, i) => new { id = i, prefab = r.Template.Name,
                    position = new[] { r.Position.X, r.Position.Y, r.Position.Z }, turn = r.Turn,
                    depth = r.Depth, combat_depth = r.ProfileDepth, parent = r.Parent,
                    modifier_count = r.ModifierCount,
                    parent_socket = r.ParentSocket, in_socket = r.InSocket, endcap = r.Template.Endcap,
                    cores = r.GuaranteedCores, resource_count = r.ResourceCount, chest_count = r.ChestCount,
                    rare_guard_count = r.RareGuardCount,
                    arena = r.Arena, miniboss = r.MiniBoss, branch = r.Branch,
                    attachment = r.Attachment, route_end = r.RouteEnd,
                    volumes = r.Bounds.Select(v => new { center = ForestFixture.V(V(v.Center)),
                        size = ForestFixture.V(V(v.Size)) }),
                    sockets = Enumerable.Range(0, r.Template.Sockets.Length).Select(s => new
                    {
                        position = new[] { r.SocketPosition(s).X, r.SocketPosition(s).Y, r.SocketPosition(s).Z },
                        entrance = r.Template.Sockets[s].Entrance
                    }) }),
                edges = plan.Edges.Select(e => new[] { e.A, e.B })
            }));
            if (section != null) Zdo(section.transform).Set(MiniBossConnection.CompleteKey, true);
            if (!trial) ForestGuardRecovery.ValidateSpawns(__instance, Spawned);
            if (mode == ZoneSystem.SpawnMode.Ghost)
                foreach (var view in Spawned.Where(v => v != null)) Object.Destroy(view.gameObject);
            LastPlan = plan;
            Plugin.LogInfo($"FOREST_GENERATED seed={plan.Seed} rooms={plan.Rooms.Count} " +
                $"depth={plan.Rooms.Max(r => r.Depth)} trial={trial}");
        }
        catch
        {
            Zdo(__instance).Set(Tag, "");
            Zdo(__instance).Set(MiniBossConnection.LinkKey, "");
            Zdo(__instance).Set(LayoutKey, "");
            foreach (var view in Spawned)
                if (view != null && view.IsValid() && view.IsOwner())
                {
                    view.gameObject.SetActive(false);
                    ZNetScene.instance.Destroy(view.gameObject);
                }
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
        List<ZNetView> objects, ZoneSystem.SpawnMode mode, RadialSnapshot? radial)
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
        // Штатные ядра вне выбранных 2–4 комнат тоже входят в лимит и удаляются при новой генерации.
        if (room.ResourceCount == 0)
            foreach (var core in objects.Where(v =>
                         v.GetComponent<Pickable>()?.m_itemPrefab.name == "SurtlingCore").ToArray())
            {
                core.gameObject.SetActive(false);
                ZNetScene.instance.Destroy(core.gameObject); objects.Remove(core);
            }
        if (room.ResourceCount > 0)
            foreach (var core in template.GetComponentsInChildren<Pickable>(true)
                         .Where(p => p.m_itemPrefab != null && p.m_itemPrefab.name == "SurtlingCore"))
                Ensure(core);
        // Штатные предметы сохраняются; количество охраны задано общим бюджетным профилем.
        foreach (var chest in template.GetComponentsInChildren<Container>(true)) Ensure(chest);
        bool hasChest = objects.Any(v => v.GetComponent<Container>() != null);
        var occupied = objects.Where(v => v.GetComponent<Container>() != null ||
            v.GetComponent<Pickable>()?.m_itemPrefab.name == "SurtlingCore").Select(v => v.transform.position).ToList();
        var random = new System.Random(unchecked(seed * 397 ^ index));
        var slots = new List<Vector3>();
        int scanned = 0, noFloor = 0, wrongFloor = 0, blockedBody = 0, nearSocket = 0;
        if (hasChest || room.ResourceCount > 0 || room.Arena)
        {
            // Проверяется настоящий пол и свободная капсула, а не только прямоугольник комнаты.
            Physics.SyncTransforms();
            int mask = LayerMask.GetMask("Default", "static_solid", "piece", "terrain");
            for (float x = -room.Template.Size.X / 2 + 1.2f; x <= room.Template.Size.X / 2 - 1.2f; x += 1.15f)
            for (float z = -room.Template.Size.Z / 2 + 1.2f; z <= room.Template.Size.Z / 2 - 1.2f; z += 1.15f)
            {
                scanned++;
                var p = V(room.Position) + rotation * new Vector3(x, 1, z);
                if (!Physics.Raycast(p, Vector3.down, out RaycastHit hit, 4, mask,
                        QueryTriggerInteraction.Ignore) || hit.normal.y < .85f) { noFloor++; continue; }
                if (Math.Abs(hit.point.y - (room.Position.Y - 1.5f)) > .35f) { wrongFloor++; continue; }
                p = hit.point + Vector3.up * .08f;
                if (Physics.CheckCapsule(p + Vector3.up * .4f, p + Vector3.up * 1.5f, .3f, mask,
                        QueryTriggerInteraction.Ignore)) { blockedBody++; continue; }
                if (Enumerable.Range(0, room.Template.Sockets.Length)
                    .Any(s => Vector3.Distance(p, V(room.SocketPosition(s))) < 1.4f)) { nearSocket++; continue; }
                slots.Add(p);
            }
            slots = slots.OrderBy(_ => random.Next()).ToList();
        }
        int totalSlots = slots.Count;
        Vector3 Slot(string kind)
        {
            int found = slots.FindIndex(p => occupied.All(q =>
                new Vector2(p.x - q.x, p.z - q.z).magnitude >= 1.05f));
            if (found < 0) throw new InvalidOperationException($"Нет безопасной точки в комнате {index}: " +
                JsonConvert.SerializeObject(new { prefab = room.Template.Name, kind, totalSlots, left = slots.Count,
                    occupied = occupied.Count, scanned, noFloor, wrongFloor, blockedBody, nearSocket }));
            var p = slots[found]; slots.RemoveAt(found); occupied.Add(p); return p;
        }
        ZNetView Add(string prefab)
        {
            var source = ZNetScene.instance.GetPrefab(prefab) ??
                throw new InvalidOperationException("Не найден prefab наполнения " + prefab);
            var view = Object.Instantiate(source, Slot(prefab), rotation).GetComponent<ZNetView>();
            objects.Add(view); return view;
        }
        while (objects.Count(v => v.GetComponent<Container>() != null) < room.ChestCount)
            Add("TreasureChest_forestcrypt");
        while (objects.Count(v => v.GetComponent<Pickable>()?.m_itemPrefab.name == "SurtlingCore") < room.ResourceCount)
            Add("Pickable_SurtlingCoreStand");
        var modifiers = radial?.Selection.AtDepth(room.Depth) ?? EncounterPlan.ForestSet(room.Depth);
        var rules = radial?.Priced ?? GuardedChestReward.Rules;
        var budget = rules.Budget(modifiers, 0, modifiers.Length == 0);
        bool planned = hasChest || room.ResourceCount > 0 || room.Arena;
        int desired = planned ? EncounterPlan.Guards(rules, budget.CombatValue,
            room.ResourceCount > 0, room.Arena, unchecked(seed * 397 ^ index), room.GuaranteedCores) : 0;
        var guards = new List<Character>();
        foreach (var view in objects.ToArray())
        {
            var spawner = view.GetComponent<CreatureSpawner>();
            // Бесконечные кучки костей не входят в согласованное разовое число охраны арены.
            if (room.Arena && view.GetComponent<SpawnArea>() != null)
            {
                ZNetScene.instance.Destroy(view.gameObject); objects.Remove(view); continue;
            }
            if (spawner == null) continue;
            if (!planned)
            {
                var guard = Object.Instantiate(spawner.m_creaturePrefab,
                    spawner.transform.position + Vector3.up * .15f, spawner.transform.rotation)
                    .GetComponent<Character>();
                guard.SetLevel(spawner.m_minLevel);
                guards.Add(guard);
            }
            ZNetScene.instance.Destroy(view.gameObject);
            objects.Remove(view);
        }
        while (guards.Count < desired)
            guards.Add(Object.Instantiate(ZNetScene.instance.GetPrefab("Skeleton"), Slot("Skeleton"), rotation)
                .GetComponent<Character>());
        for (int i = 0; i < room.RareGuardCount; i++)
        {
            var ghost = Object.Instantiate(ZNetScene.instance.GetPrefab("Ghost"), Slot("Ghost"), rotation)
                .GetComponent<Character>();
            ghost.SetLevel(1);
            guards.Add(ghost);
        }
        foreach (var guard in guards)
        {
            TagObject(guard.GetComponent<ZNetView>(), token, index, room.Depth, "guard");
            RadialRuntime.Mark(guard, radial?.Selection, false);
            PovBridge.AssignIds(guard, modifiers);
            Zdo(guard).Set(CombatRecorder.EntityKey, Guid.NewGuid().ToString("N"));
            ForestGuardRecovery.MarkSpawn(guard);
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
                Depth = room.Depth, ProtectedProgressionZone = modifiers.Length == 0,
                Seed = unchecked(seed * 397 ^ index * 31 ^ guardIndex)
            }, rules);
        }
        if (objects.Count(v => v.GetComponent<Pickable>()?.m_itemPrefab.name == "SurtlingCore") < room.ResourceCount)
            throw new InvalidOperationException("В ресурсной комнате не появились ядра");
        if (mode == ZoneSystem.SpawnMode.Ghost)
            foreach (var view in objects.Concat(guards.Select(g => g.GetComponent<ZNetView>())))
                Object.Destroy(view.gameObject);
    }
}
