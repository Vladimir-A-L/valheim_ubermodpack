using System;
using System.Collections.Generic;
using System.Linq;

namespace DungeonDepthPrototype;

// Номер сектора не ограничивает допустимое число свойств десятью.
public static class EncounterGates
{
    public static int Band(int modifiers)
    {
        if (modifiers < 0) throw new ArgumentOutOfRangeException(nameof(modifiers));
        return modifiers == 0 ? 0 : modifiers <= 4 ? (modifiers + 1) / 2 : modifiers - 2;
    }

    public static bool Required(int a, int b, bool arenaA = false, bool arenaB = false) =>
        arenaA != arenaB || Band(a) != Band(b);

    public static bool Required(ForestPlan.Placement a, ForestPlan.Placement b) =>
        !a.Template.Endcap && !b.Template.Endcap && !a.MiniBoss && !b.MiniBoss &&
        Required(a.ModifierCount, b.ModifierCount, a.Arena, b.Arena);

    public static bool Fits(ForestPlan.Placement parent, int socket, ForestPlan.Placement child) =>
        parent.Template.Sockets[socket].AllowDoor &&
        (!parent.Template.Sockets[socket].DoorRequiresBoth || child.Template.Sockets[child.InSocket].AllowDoor);

    public static (int A, int B)[] Edges(ForestPlan.Layout layout) => layout.Edges
        .Where(e => Required(layout.Rooms[e.A], layout.Rooms[e.B])).ToArray();

    public static void Validate(ForestPlan.Layout layout)
    {
        var barriers = new HashSet<(int, int)>(Edges(layout));
        var bossEdges = layout.Edges.Where(e => layout.Rooms[e.B].MiniBoss).ToArray();
        foreach (var (a, b) in barriers)
        {
            var child = layout.Rooms[b];
            if (child.Parent != a || !Fits(layout.Rooms[a], child.ParentSocket, child))
                throw new InvalidOperationException("Порог сложности не допускает штатную дверь");
        }
        barriers.UnionWith(bossEdges); // Настоящая дверь внутри составной секции.
        // Проверяем связные области, а не только выбранную главную ветвь.
        var seen = new HashSet<int>();
        for (int start = 0; start < layout.Rooms.Count; start++)
        {
            if (!seen.Add(start)) continue;
            var pending = new Queue<int>(); pending.Enqueue(start);
            int band = Band(layout.Rooms[start].ModifierCount);
            bool arena = layout.Rooms[start].Arena;
            while (pending.Count > 0)
            {
                int current = pending.Dequeue();
                var room = layout.Rooms[current];
                if (!room.Template.Endcap && (Band(room.ModifierCount) != band ||
                        room.Arena != arena))
                    throw new InvalidOperationException("Есть обход закрытого порога сложности");
                foreach (var edge in layout.Edges.Where(e => e.A == current || e.B == current))
                {
                    if (barriers.Contains(edge)) continue;
                    int other = edge.A == current ? edge.B : edge.A;
                    if (seen.Add(other)) pending.Enqueue(other);
                }
            }
        }
    }
}
