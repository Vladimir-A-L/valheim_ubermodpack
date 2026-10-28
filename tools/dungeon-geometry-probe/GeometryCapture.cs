using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace DungeonGeometryProbe;

internal static class GeometryCapture
{
    internal sealed class RoomRow
    {
        public int id;
        public string prefab = "";
        public bool entrance, endcap, divider;
        public float[] position = Array.Empty<float>();
        public int depth;
        public int original_order;
    }

    internal sealed class Snapshot
    {
        public List<RoomRow> rooms = new List<RoomRow>();
        public List<int[]> edges = new List<int[]>();
        public int entrance_count, disconnected, max_depth, branching_rooms, leaves;
        public int open_sockets, ambiguous_sockets, mismatched_contacts, overlap_pairs, outside_bounds;
        public int required_count, required_target;
        public bool required_satisfied;
        public int[] required_depths = Array.Empty<int>();
        public List<SpawnRow> spawns = new List<SpawnRow>();
        public string fingerprint = "";
    }

    internal sealed class SpawnRow
    {
        public string prefab = "";
        public string pickable_item = "";
        public int room = -1;
        public int depth = -1;
        public string association = "unknown";
    }

    internal static Snapshot Capture(DungeonGenerator generator, Room[] rooms, bool physicsAudit)
    {
        var result = new Snapshot();
        for (int i = 0; i < rooms.Length; i++)
        {
            var r = rooms[i];
            var p = r.transform.position - generator.transform.position;
            result.rooms.Add(new RoomRow { id = i, prefab = r.name, entrance = r.m_entrance,
                endcap = r.m_endCap, divider = r.m_divider, position = new[] { p.x, p.y, p.z },
                original_order = r.m_placeOrder });
        }
        var sockets = rooms.SelectMany((r, id) => r.GetConnections().Select(c => (room: id, socket: c))).ToArray();
        var edgeSet = new HashSet<(int, int)>();
        foreach (var a in sockets)
        {
            if (a.socket.m_entrance || rooms[a.room].m_divider) continue;
            int matches = 0;
            bool cappedByDivider = false;
            foreach (var b in sockets)
            {
                if (a.room == b.room || !a.socket.TestContact(b.socket)) continue;
                if (rooms[b.room].m_divider) { cappedByDivider = true; continue; }
                if (b.socket.m_entrance) continue;
                if (a.socket.m_type != b.socket.m_type)
                {
                    result.mismatched_contacts++;
                    continue;
                }
                if (Vector3.Dot(a.socket.transform.forward, b.socket.transform.forward) > -0.99f) continue;
                matches++;
                edgeSet.Add((Math.Min(a.room, b.room), Math.Max(a.room, b.room)));
            }
            if (matches == 0 && !cappedByDivider) result.open_sockets++;
            if (matches > 1) result.ambiguous_sockets++;
        }
        result.edges = edgeSet.OrderBy(e => e.Item1).ThenBy(e => e.Item2)
            .Select(e => new[] { e.Item1, e.Item2 }).ToList();
        int[] entrances = Enumerable.Range(0, rooms.Length).Where(i => rooms[i].m_entrance).ToArray();
        result.entrance_count = entrances.Length;
        if (entrances.Length != 1) throw new InvalidOperationException("Ожидался один вход в граф комнат");
        int[] depths = RoomGraph.Distances(rooms.Length, edgeSet, entrances[0]);
        var gameplay = Enumerable.Range(0, rooms.Length)
            .Where(i => !rooms[i].m_divider && !rooms[i].m_endCap).ToArray();
        foreach (var row in result.rooms) row.depth = depths[row.id];
        result.disconnected = gameplay.Count(i => depths[i] < 0);
        result.max_depth = gameplay.Select(i => depths[i]).DefaultIfEmpty(0).Max();
        var gameplaySet = new HashSet<int>(gameplay);
        var degree = new int[rooms.Length];
        foreach (var (a, b) in edgeSet)
            if (gameplaySet.Contains(a) && gameplaySet.Contains(b)) { degree[a]++; degree[b]++; }
        result.branching_rooms = gameplay.Count(i => degree[i] >= 3);
        result.leaves = gameplay.Count(i => !rooms[i].m_entrance && degree[i] == 1);
        result.required_count = rooms.Count(r => generator.m_requiredRooms.Contains(r.gameObject.name));
        result.required_target = generator.m_minRequiredRooms;
        result.required_satisfied = result.required_count >= result.required_target;
        result.required_depths = result.rooms.Where(r => generator.m_requiredRooms.Contains(r.prefab))
            .Select(r => r.depth).OrderBy(d => d).ToArray();
        if (physicsAudit) AuditBoundsAndOverlap(generator, rooms, gameplay, result);
        if (physicsAudit)
            foreach (var view in GenerationHooks.Spawned.Distinct())
            {
                if (view == null) continue;
                var row = new SpawnRow { prefab = Utils.GetPrefabName(view.gameObject) };
                var pickable = view.GetComponent<Pickable>();
                if (pickable != null)
                {
                    var item = AccessTools.Field(typeof(Pickable), "m_itemPrefab").GetValue(pickable) as GameObject;
                    if (item != null) row.pickable_item = item.name;
                }
                if (GenerationHooks.Origins.TryGetValue(view, out var origin))
                {
                    row.room = Array.IndexOf(rooms, origin);
                    if (row.room >= 0) { row.depth = depths[row.room]; row.association = "spawn-origin"; }
                }
                if (row.room < 0)
                {
                    var enclosing = gameplay.Where(i => Contains(rooms[i], view.transform.position)).ToArray();
                    if (enclosing.Length == 1)
                    {
                        row.room = enclosing[0]; row.depth = depths[row.room]; row.association = "bounds-fallback";
                    }
                }
                result.spawns.Add(row);
            }
        string geometry = string.Join(";", rooms.Select(r => r.name + ":" +
            Vector(r.transform.position - generator.transform.position) + ":" +
            Vector(r.transform.eulerAngles)).OrderBy(x => x, StringComparer.Ordinal));
        using var sha = SHA256.Create();
        result.fingerprint = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(geometry)))
            .Replace("-", "").ToLowerInvariant();
        return result;
    }

    private static bool Contains(Room room, Vector3 point)
    {
        Vector3 local = room.transform.InverseTransformPoint(point);
        Vector3 half = (Vector3)room.m_size * 0.5f;
        return half.x > 0 && half.y > 0 && half.z > 0 &&
            Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.y) <= half.y && Mathf.Abs(local.z) <= half.z;
    }

    private static string Vector(Vector3 v) => string.Join(",", new[] { v.x, v.y, v.z }
        .Select(n => n.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)));

    private static void AuditBoundsAndOverlap(DungeonGenerator generator, Room[] rooms, int[] ids, Snapshot result)
    {
        var a = (BoxCollider)AccessTools.Field(typeof(DungeonGenerator), "m_colliderA").GetValue(generator);
        var b = (BoxCollider)AccessTools.Field(typeof(DungeonGenerator), "m_colliderB").GetValue(generator);
        var inside = AccessTools.Method(typeof(DungeonGenerator), "IsInsideDungeon");
        for (int i = 0; i < ids.Length; i++)
        {
            var room = rooms[ids[i]];
            if (room.m_size.x <= 0 || room.m_size.y <= 0 || room.m_size.z <= 0) continue;
            if (!(bool)inside.Invoke(generator,
                new object[] { room, room.transform.position, room.transform.rotation }))
                result.outside_bounds++;
            a.size = (Vector3)room.m_size - Vector3.one * 0.1f;
            for (int j = 0; j < i; j++)
            {
                var other = rooms[ids[j]];
                if (other.m_size.x <= 0 || other.m_size.y <= 0 || other.m_size.z <= 0) continue;
                b.size = other.m_size;
                if (Physics.ComputePenetration(a, room.transform.position, room.transform.rotation,
                    b, other.transform.position, other.transform.rotation, out _, out _)) result.overlap_pairs++;
            }
        }
    }
}
