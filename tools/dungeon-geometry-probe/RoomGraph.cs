using System;
using System.Collections.Generic;
using System.Linq;

namespace DungeonGeometryProbe;

// Независимая от Unity часть: циклы учитываются через кратчайший путь, не порядок размещения.
public static class RoomGraph
{
    public static int[] Distances(int count, IEnumerable<(int A, int B)> edges, int entrance)
    {
        if (count <= 0 || entrance < 0 || entrance >= count) throw new ArgumentOutOfRangeException(nameof(entrance));
        var links = Enumerable.Range(0, count).Select(_ => new HashSet<int>()).ToArray();
        foreach (var (a, b) in edges)
        {
            if (a < 0 || b < 0 || a >= count || b >= count || a == b)
                throw new ArgumentException("Некорректное соединение комнат");
            links[a].Add(b);
            links[b].Add(a);
        }
        var depth = Enumerable.Repeat(-1, count).ToArray();
        var pending = new Queue<int>();
        depth[entrance] = 0;
        pending.Enqueue(entrance);
        while (pending.Count > 0)
        {
            int current = pending.Dequeue();
            foreach (int neighbour in links[current])
                if (depth[neighbour] < 0)
                {
                    depth[neighbour] = depth[current] + 1;
                    pending.Enqueue(neighbour);
                }
        }
        return depth;
    }
}
