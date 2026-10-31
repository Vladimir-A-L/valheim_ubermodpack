using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DungeonDepthPrototype;

// Кэш связного пространства комнаты, построенный по коллайдерам. Двери проверяются отдельно при возврате.
internal sealed class ForestGuardWalkmap
{
    private readonly float step;
    internal static bool CaptureDebug;
    private readonly List<Vector3> reachable = new List<Vector3>();
    private readonly List<object> nodes = new List<object>();
    internal object Debug => new { summary = Diagnostics, nodes };
    internal int Cells, Anchors;
    internal float ClosestSocket = float.MaxValue;
    internal readonly Dictionary<string, int> Rejected = new Dictionary<string, int>();
    internal object Diagnostics => new { cells = Cells, anchors = Anchors, reachable = reachable.Count,
        resolution = step, closest_socket = ClosestSocket, rejected = Rejected };
    internal IEnumerable<Vector3> Points => reachable;
    internal bool Ready => reachable.Count > 0;
    internal bool NeedsRefinement => !Ready || reachable.Count * 4 < Cells;
    internal ForestGuardWalkmap(ForestGuardSpace space, Room room, Character guard, int id, float resolution = .4f)
    {
        step = resolution;
        var cells = new Dictionary<(int, int, int), Vector3>();
        var columns = new Dictionary<(int, int), List<(int, int, int)>>();
        int nx = Mathf.CeilToInt(room.m_size.x / step), nz = Mathf.CeilToInt(room.m_size.z / step);
        if (nx * nz > 20000) return; // Ошибочный размер не должен блокировать сервер неограниченным поиском.
        for (int x = 0; x <= nx; x++)
        for (int z = 0; z <= nz; z++)
        {
            var p = room.transform.TransformPoint(new Vector3(-room.m_size.x / 2 + x * step,
                room.m_size.y / 2 + 1, -room.m_size.z / 2 + z * step));
            var floors = Physics.RaycastAll(p, Vector3.down, room.m_size.y + 2, ForestGuardSpace.Solid,
                QueryTriggerInteraction.Ignore).Where(h => h.normal.y >= .65f && space.RoomOf(h.collider) == id)
                .OrderByDescending(h => h.point.y);
            foreach (var floor in floors)
            {
                if (!columns.TryGetValue((x, z), out var layers))
                    columns[(x, z)] = layers = new List<(int, int, int)>();
                if (layers.Count >= 8) break;
                // На ступеньке корпус опирается также на соседнюю, более высокую грань.
                foreach (float lift in new[] { .08f, .2f, .35f, .55f })
                {
                    p = floor.point + Vector3.up * lift;
                    if (!space.Surface(guard, p, true, true, out int surfaceRoom, out string reason) ||
                        surfaceRoom != id)
                    {
                        if (surfaceRoom != id) reason = "other-room";
                        Rejected[reason] = Rejected.TryGetValue(reason, out int count) ? count + 1 : 1;
                        continue;
                    }
                    if (!layers.Any(key => Math.Abs(cells[key].y - p.y) < .1f))
                    {
                        var key = (x, z, layers.Count); cells[key] = p; layers.Add(key);
                    }
                    break;
                }
            }
        }
        // Не начинать обход с заглушённых сокетов: за заглушкой бывает собственный участок пола.
        var sockets = space.Openings(room, id);
        Cells = cells.Count;
        ClosestSocket = cells.Values.SelectMany(p => sockets.Select(s => Vector3.Distance(p, s)))
            .DefaultIfEmpty(float.MaxValue).Min();
        var visited = new HashSet<(int, int, int)>(); var queue = new Queue<(int, int, int)>();
        foreach (var cell in cells)
            if (sockets.Any(s => Math.Abs(s.y - cell.Value.y) < 1 &&
                    new Vector2(s.x - cell.Value.x, s.z - cell.Value.z).magnitude < 1.1f &&
                    space.ClearSegment(guard, cell.Value, new Vector3(s.x, cell.Value.y, s.z))))
            { visited.Add(cell.Key); queue.Enqueue(cell.Key); }
        Anchors = queue.Count;
        while (queue.Count > 0)
        {
            var cell = queue.Dequeue(); reachable.Add(cells[cell]);
            // Диагональ допустима только при полном проходе капсулы, без срезания угла стены.
            foreach (var offset in new[] { (-1, 0), (1, 0), (0, -1), (0, 1),
                         (-1, -1), (-1, 1), (1, -1), (1, 1) })
            {
                var column = (cell.Item1 + offset.Item1, cell.Item2 + offset.Item2);
                if (!columns.TryGetValue(column, out var layers)) continue;
                foreach (var other in layers)
                {
                    var p = cells[other];
                    if (visited.Contains(other) || Math.Abs(p.y - cells[cell].y) > .5f ||
                        !space.ClearSegment(guard, cells[cell], p)) continue;
                    visited.Add(other); queue.Enqueue(other);
                }
            }
        }
        if (CaptureDebug)
            nodes.AddRange(cells.Select(c => (object)new { x = c.Key.Item1, z = c.Key.Item2, layer = c.Key.Item3,
                position = ForestFixture.V(room.transform.InverseTransformPoint(c.Value)),
                reachable = visited.Contains(c.Key) }));
    }
    internal bool Contains(ForestGuardSpace space, Character guard, Vector3 p) => reachable.Any(q =>
        new Vector2(p.x - q.x, p.z - q.z).sqrMagnitude <= .65f * .65f &&
        Math.Abs(p.y - q.y) < 1.3f && space.ClearSegment(guard, p, q));
}
