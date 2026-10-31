using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DungeonDepthPrototype;

// Объёмы плана лишь выбирают комнату. Допуск требует настоящей опоры, потолка и свободного корпуса.
internal sealed class ForestGuardSpace : MonoBehaviour
{
    private static readonly HashSet<ForestGuardSpace> active = new HashSet<ForestGuardSpace>();
    private string token = "", saved = "";
    private JArray rooms = new JArray(), edges = new JArray();
    private readonly Dictionary<int, Room> loaded = new Dictionary<int, Room>();
    private readonly Dictionary<Room, int> roomIds = new Dictionary<Room, int>();
    private readonly Dictionary<int, Bounds[]> bounds = new Dictionary<int, Bounds[]>();
    private readonly Dictionary<(Room, float, float), ForestGuardWalkmap> maps =
        new Dictionary<(Room, float, float), ForestGuardWalkmap>();
    private MiniBossArena? arena;
    private int arenaRoom = -1;
    private float refreshAt;
    private bool ready;
    internal static int Solid => LayerMask.GetMask("Default", "static_solid", "piece", "terrain", "blocker");
    private void OnEnable() => active.Add(this);
    private void OnDisable() { active.Remove(this); ready = false; }
    private void OnDestroy() => active.Remove(this);
    internal static void Attach(DungeonGenerator __instance)
    {
        if (__instance.GetComponent<ForestGuardSpace>() == null)
            __instance.gameObject.AddComponent<ForestGuardSpace>();
    }
    internal static ForestGuardSpace? Find(string tag)
    {
        foreach (var space in active)
        {
            if (space == null || !space.isActiveAndEnabled) continue;
            space.Refresh();
            if (space.token == tag && tag.Length > 0) return space;
        }
        return null;
    }
    internal bool Ready
    {
        get
        {
            Refresh();
            return ready && loaded.Values.All(r => r != null && r.gameObject.activeInHierarchy);
        }
    }
    internal bool ReadyAt(Vector3 position) => Ready && (NearestRoom(position) != arenaRoom ||
        arena != null && arena.gameObject.activeInHierarchy && arena.transform.Find("crypt-architecture") != null);
    internal void RebuildNow() { refreshAt = 0; maps.Clear(); Refresh(); }
    private void Refresh()
    {
        if (Time.time < refreshAt) return;
        refreshAt = Time.time + 2;
        var data = MiniBossRuntime.Data(this);
        token = data?.GetString(ForestRuntime.Tag) ?? "";
        string layout = data?.GetString(ForestRuntime.LayoutKey) ?? "";
        if (layout.Length == 0) { ready = false; return; }
        if (saved != layout)
        {
            var plan = JObject.Parse(layout); rooms = (JArray)plan["rooms"]!; edges = (JArray)plan["edges"]!;
            saved = layout;
        }
        var children = GetComponentsInChildren<Room>();
        loaded.Clear(); roomIds.Clear(); bounds.Clear(); arenaRoom = -1;
        foreach (var room in rooms)
        {
            int id = room.Value<int>("id");
            if (room["volumes"] is JArray volumes)
                bounds[id] = volumes.Select(v => new Bounds(ForestRuntime.V(ForestAtmosphere.Point(v["center"]!)),
                    ForestRuntime.V(ForestAtmosphere.Point(v["size"]!)))).ToArray();
            if (room.Value<bool>("miniboss")) { arenaRoom = id; continue; }
            var p = ForestRuntime.V(ForestAtmosphere.Point(room["position"]!));
            var match = children.FirstOrDefault(r => r.name == room.Value<string>("prefab") &&
                Vector3.Distance(r.transform.position, p) < .03f);
            if (match != null && match.GetComponentsInChildren<Collider>().Any(c => c.enabled && !c.isTrigger))
            {
                loaded[id] = match; roomIds[match] = id;
                if (!bounds.ContainsKey(id))
                {
                    var size = match.transform.TransformVector(match.m_size);
                    bounds[id] = new[] { new Bounds(p, new Vector3(Math.Abs(size.x), Math.Abs(size.y),
                        Math.Abs(size.z))) };
                }
            }
        }
        arena = arenaRoom < 0 ? null : UnityEngine.Object.FindObjectsByType<MiniBossArena>(FindObjectsSortMode.None)
            .FirstOrDefault(a => MiniBossRuntime.Data(a)?.GetString(ForestRuntime.Tag) == token);
        ready = loaded.Count == rooms.Count - (arenaRoom < 0 ? 0 : 1);
    }
    internal int RoomOf(Collider c)
    {
        var room = c.GetComponentInParent<Room>();
        if (room != null && roomIds.TryGetValue(room, out int id)) return id;
        return arena != null && c.GetComponentInParent<MiniBossArena>() == arena ? arenaRoom : -1;
    }
    internal static void Capsule(Character c, Vector3 position, out Vector3 bottom, out Vector3 top,
        out float radius, float skin = .04f)
    {
        var capsule = c.GetComponent<CapsuleCollider>();
        var scale = c.transform.lossyScale;
        radius = Math.Max(.1f, capsule.radius * Math.Max(Math.Abs(scale.x), Math.Abs(scale.z)) - skin);
        float half = Math.Max(radius, capsule.height * Math.Abs(scale.y) / 2 - skin);
        var center = position + c.transform.TransformVector(capsule.center);
        bottom = center - Vector3.up * (half - radius);
        top = center + Vector3.up * (half - radius);
    }
    internal static bool BodyClear(Character c, Vector3 position, bool creatures)
    {
        Capsule(c, position, out var bottom, out var top, out float radius);
        int mask = creatures ? Solid | LayerMask.GetMask("character", "character_net", "character_ghost") : Solid;
        return !Physics.OverlapCapsule(bottom, top, radius, mask, QueryTriggerInteraction.Ignore)
            .Any(hit => hit.GetComponentInParent<Character>() != c);
    }
    internal bool Valid(Character c, Vector3 position, bool target, out int room, out string reason)
    {
        room = -1; reason = "geometry-unavailable";
        if (!ReadyAt(position)) return false;
        if (!Surface(c, position, target, false, out room, out reason)) return false;
        if (room == arenaRoom) return true; // Принятая открытая секция; её оболочка проверена отдельно.
        if (!loaded.TryGetValue(room, out var actual)) { reason = "walkmap-unavailable"; return false; }
        var map = Map(c, actual, room);
        if (!map.Ready) { reason = "walkmap-unavailable"; return false; }
        if (!map.Contains(this, c, position)) { reason = "unreachable-pocket"; return false; }
        return true;
    }
    private ForestGuardWalkmap Map(Character c, Room room, int id)
    {
        Capsule(c, Vector3.zero, out var bottom, out var top, out float radius);
        var key = (room, radius, Vector3.Distance(bottom, top));
        if (!maps.TryGetValue(key, out var map))
        {
            map = new ForestGuardWalkmap(this, room, c, id);
            if (map.NeedsRefinement) map = new ForestGuardWalkmap(this, room, c, id, .2f);
            maps[key] = map;
        }
        return map;
    }
    internal object Inspect(Character c, int id) => loaded.TryGetValue(id, out var room) ?
        Map(c, room, id).Diagnostics : new { missing_room = id };
    internal object Debug(Character c, int id) => !loaded.ContainsKey(id) ? Inspect(c, id) : new {
        room = loaded[id].name,
        size = ForestFixture.V(loaded[id].m_size), map = Map(c, loaded[id], id).Debug,
        point = ForestFixture.V(loaded[id].transform.InverseTransformPoint(c.transform.position)),
        passages = Openings(loaded[id], id).Select(s => {
            var socket = loaded[id].GetConnections().First(t => Vector3.Distance(t.transform.position, s) < .01f);
            return Enumerable.Range(1, 8).Select(i => {
                var p = s + socket.transform.forward * (i * .5f) + Vector3.up * .08f;
                Capsule(c, p, out var bottom, out var top, out float radius);
                return new { local = ForestFixture.V(loaded[id].transform.InverseTransformPoint(p)),
                    floors = Physics.RaycastAll(p + Vector3.up * 4, Vector3.down, 6, Solid,
                        QueryTriggerInteraction.Ignore).Select(h => new { room = RoomOf(h.collider),
                            point = ForestFixture.V(loaded[id].transform.InverseTransformPoint(h.point)),
                            normal = ForestFixture.V(h.normal),
                            path = string.Join("/", h.collider.GetComponentsInParent<Transform>()
                                .Reverse().Select(t => t.name)) }).ToArray(),
                    levels = new[] { 0f, .12f, .27f, .47f }.Select(lift => {
                        bool valid = Surface(c, p + Vector3.up * lift, true, true, out int found,
                            out string reason);
                        return new { lift, valid, room = found, reason };
                    }).ToArray(),
                    blocked = Physics.OverlapCapsule(bottom, top, radius, Solid, QueryTriggerInteraction.Ignore)
                        .Where(hit => RoomOf(hit) >= 0).Select(hit => new { room = RoomOf(hit),
                            path = string.Join("/", hit.GetComponentsInParent<Transform>()
                                .Reverse().Select(t => t.name)),
                            center = ForestFixture.V(loaded[id].transform.InverseTransformPoint(hit.bounds.center)),
                            size = ForestFixture.V(hit.bounds.size) }).ToArray() };
            }).ToArray();
        }).ToArray(),
        sockets = Openings(loaded[id], id).Select(p =>
            ForestFixture.V(loaded[id].transform.InverseTransformPoint(p))),
        capsule = new { radius = c.GetComponent<CapsuleCollider>().radius,
            height = c.GetComponent<CapsuleCollider>().height } };
    internal Vector3[] Openings(Room room, int id)
    {
        var plan = rooms.Single(r => r.Value<int>("id") == id);
        var indices = new HashSet<int>();
        int parent = plan.Value<int>("parent");
        if (parent >= 0) indices.Add(plan.Value<int>("in_socket"));
        foreach (var child in rooms.Where(r => r.Value<int>("parent") == id && !r.Value<bool>("endcap")))
            indices.Add(child.Value<int>("parent_socket"));
        return room.GetConnections().Where((_, i) => indices.Contains(i)).Select(s => s.transform.position).ToArray();
    }
    internal bool ClearSegment(Character c, Vector3 from, Vector3 to)
    {
        bool Sweep(Vector3 a, Vector3 b)
        {
            Capsule(c, a, out var bottom, out var top, out float radius);
            var delta = b - a;
            return delta.sqrMagnitude < .0001f || !Physics.CapsuleCastAll(bottom, top, radius,
                delta.normalized, delta.magnitude, Solid, QueryTriggerInteraction.Ignore)
                .Any(h => RoomOf(h.collider) >= 0 && h.normal.y < .65f);
        }
        if (Sweep(from, to)) return true;
        return false;
    }
    internal bool Surface(Character c, Vector3 position, bool target, bool geometryOnly,
        out int room, out string reason)
    {
        room = -1; reason = "no-support";
        // Небольшой зазор допускает штатное зависание призрака и ступеньки, но не землю под интерьером.
        Capsule(c, position, out var bottom, out var top, out float radius);
        float down = target ? .65f : 1.25f;
        var support = Physics.CapsuleCastAll(bottom + Vector3.up * .08f, top + Vector3.up * .08f,
            radius, Vector3.down, down + .08f, Solid, QueryTriggerInteraction.Ignore)
            .Where(h => h.normal.y >= .65f).OrderBy(h => h.distance).FirstOrDefault();
        if (support.collider == null) return false;
        room = RoomOf(support.collider);
        if (room < 0) { reason = "foreign-support"; return false; }
        // Верх внешней оболочки и пол за стеной не считаются внутренним помещением.
        if (!Physics.Raycast(position + Vector3.up * .3f, Vector3.up, out var roof, 9,
                Solid, QueryTriggerInteraction.Ignore) || RoomOf(roof.collider) < 0)
        { reason = "outside-shell"; return false; }
        bool blocked = geometryOnly ? Physics.OverlapCapsule(bottom, top, radius, Solid,
            QueryTriggerInteraction.Ignore).Any(hit => RoomOf(hit) >= 0) : !BodyClear(c, position, target);
        if (blocked) { reason = "body-blocked"; return false; }
        reason = "valid"; return true;
    }
    internal int NearestRoom(Vector3 p)
    {
        // При провале сохраняется X/Z помещения; расстояние до объёма лучше расстояния до центра.
        int nearest = -1; float distance = float.MaxValue;
        foreach (var pair in bounds)
        foreach (var volume in pair.Value)
        {
            float current = volume.SqrDistance(p);
            if (current < distance) { distance = current; nearest = pair.Key; }
        }
        return nearest;
    }
    private Door[] Doors() => UnityEngine.Object.FindObjectsByType<Door>(FindObjectsSortMode.None)
        .Where(d => MiniBossRuntime.Data(d)?.GetString(ForestRuntime.Tag) == token).ToArray();
    internal bool CanReturn(int from, int to)
    {
        if (!Ready || from < 0 || to < 0) return false;
        if (from == to) return true;
        // Межкомнатный возврат разрешён только по загруженному графу с доказанно открытыми дверями.
        var doors = Doors();
        var queue = new Queue<int>(); var seen = new HashSet<int> { from }; queue.Enqueue(from);
        var declared = JObject.Parse(saved)["gates"] as JArray ?? new JArray();
        while (queue.Count > 0)
        {
            int here = queue.Dequeue();
            foreach (var edge in edges)
            {
                int a = edge[0]!.Value<int>(), b = edge[1]!.Value<int>();
                int other = a == here ? b : b == here ? a : -1;
                if (other < 0 || seen.Contains(other) || other == arenaRoom || here == arenaRoom) continue;
                if (declared.Any(e => e[0]!.Value<int>() == a && e[1]!.Value<int>() == b ||
                    e[0]!.Value<int>() == b && e[1]!.Value<int>() == a))
                {
                    var door = doors.FirstOrDefault(d => {
                        var z = MiniBossRuntime.Data(d)!;
                        return z.GetInt(ForestGates.FromKey, -1) == a && z.GetInt(ForestRuntime.RoomKey, -1) == b ||
                            z.GetInt(ForestGates.FromKey, -1) == b && z.GetInt(ForestRuntime.RoomKey, -1) == a;
                    });
                    // Отсутствующая дверь неоднозначна (разрушение/выгрузка); безопаснее остаться в своей комнате.
                    if (door == null || MiniBossRuntime.Data(door)!.GetInt(ZDOVars.s_state) == 0) continue;
                }
                if (other == to) return true;
                seen.Add(other); queue.Enqueue(other);
            }
        }
        return false;
    }
    internal bool NoClosedDoorBetween(Vector3 from, Vector3 to)
    {
        var direction = to - from;
        if (direction.sqrMagnitude < .01f) return true;
        var ray = new Ray(from + Vector3.up, direction.normalized);
        return !Doors().Where(d => MiniBossRuntime.Data(d)!.GetInt(ZDOVars.s_state) == 0)
            .SelectMany(d => d.GetComponentsInChildren<Collider>()).Any(c => c.enabled && !c.isTrigger &&
                c.Raycast(ray, out _, direction.magnitude));
    }
    internal bool FreePoint(Character c, int id, out Vector3 point, Func<Vector3, bool>? allowed = null)
    {
        point = default;
        if (id == arenaRoom && arena != null && ReadyAt(arena.transform.position))
        {
            var frame = new ArenaFrame(arena.transform.position, arena.transform.rotation);
            for (float x = 8; x <= 30; x++)
            for (float z = 12; z <= 30; z++)
            {
                var p = frame.Point(new Vector3(x, .08f, z));
                if (!Surface(c, p, true, false, out int found, out _) || found != id ||
                    allowed != null && !allowed(p)) continue;
                point = p; return true;
            }
            return false;
        }
        if (!loaded.TryGetValue(id, out var room) || !room.gameObject.activeInHierarchy) return false;
        var sockets = room.GetConnections().Select(s => s.transform.position).ToArray();
        foreach (var p in Map(c, room, id).Points.OrderByDescending(p =>
                     sockets.Select(s => Vector3.Distance(p, s)).DefaultIfEmpty(0).Min()))
        {
            if (!Surface(c, p, true, false, out int actual, out _) || actual != id) continue;
            if (allowed != null && !allowed(p)) continue;
            point = p; return true;
        }
        return false;
    }
}
