using System;
using System.Collections.Generic;
using System.Linq;

namespace DungeonDepthPrototype;

// Планирование без создания игровых объектов. Координаты берутся из загруженных штатных prefab.
public static class ForestPlan
{
    public const int SafeDepth = 16, MaxDepth = 40;
    private const int MaxAttempts = 4096;
    public static int ModifierCount(int depth) => depth <= SafeDepth ? 0 :
        depth >= MaxDepth ? 10 : (10 * (depth - SafeDepth) + 23) / 24;

    public readonly struct Point
    {
        public readonly float X, Y, Z;
        public Point(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static Point operator +(Point a, Point b) => new Point(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Point operator -(Point a, Point b) => new Point(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public Point Rotate(int turn) => ((turn % 4 + 4) % 4) switch
        {
            0 => this, 1 => new Point(Z, Y, -X), 2 => new Point(-X, Y, -Z), _ => new Point(-Z, Y, X)
        };
        public float Distance(Point b) => (float)Math.Sqrt(
            (X - b.X) * (X - b.X) + (Y - b.Y) * (Y - b.Y) + (Z - b.Z) * (Z - b.Z));
    }

    public sealed class Socket
    {
        public Point Position;
        public int Turn;
        public bool Entrance;
        public string Type = "";
    }

    public sealed class Template
    {
        public string Name = "";
        public Point Size;
        public Socket[] Sockets = Array.Empty<Socket>();
        public bool Entrance, Endcap;
    }

    public sealed class Placement
    {
        public Template Template = null!;
        public Point Position;
        public int Turn, Parent = -1, ParentSocket = -1, InSocket = -1, Depth;
        public bool GuaranteedCores;
        public int ResourceCount, ChestCount, Branch;
        public bool Arena;
        public Point SocketPosition(int index) => Position + Template.Sockets[index].Position.Rotate(Turn);
        public int SocketTurn(int index) => (Turn + Template.Sockets[index].Turn) % 4;
        public Point Extents
        {
            get
            {
                var s = Template.Size.Rotate(Turn);
                return new Point(Math.Abs(s.X) / 2, Math.Abs(s.Y) / 2, Math.Abs(s.Z) / 2);
            }
        }
    }

    public sealed class Layout
    {
        public List<Placement> Rooms = new List<Placement>();
        public int Seed, Attempts;
        public int BranchCount;
        public List<(int A, int B)> Edges = new List<(int, int)>();
    }

    private sealed class Search
    {
        internal readonly List<Placement> Rooms = new List<Placement>();
        internal readonly Random Random;
        internal readonly Template[] Through;
        internal readonly Template Reward, Arena, Wall;
        internal readonly Point Center, Size;
        internal int Tries;
        internal const int Limit = 100000;

        internal Search(Template[] templates, int seed, Point center, Point size)
        {
            Random = new Random(seed); Center = center; Size = size;
            Template Get(string name) => templates.Single(t => t.Name == name);
            Reward = Get("forestcrypt_room1");
            Arena = Get("forestcrypt_new_Burialchamber05");
            Wall = Get("forestcrypt_EndCap");
            var names = new HashSet<string> { "forestcrypt_Corridor1", "forestcrypt_Corridor2",
                "forestcrypt_Bend1", "forestcrypt_Bend2", "forestcrypt_Stairs1" };
            Through = templates.Where(t => names.Contains(t.Name))
                .OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
            if (Through.Length != names.Count) throw new InvalidOperationException("Неполный набор лесных комнат");
        }

        internal Placement Attach(int parent, int socket, Template template, int entry)
        {
            var from = Rooms[parent];
            int turn = (from.SocketTurn(socket) + 6 - template.Sockets[entry].Turn) % 4;
            return new Placement { Template = template, Turn = turn,
                Position = from.SocketPosition(socket) - template.Sockets[entry].Position.Rotate(turn),
                Parent = parent, ParentSocket = socket, InSocket = entry,
                Depth = from.Depth + (template.Endcap ? 0 : 1) };
        }

        internal bool Fits(Placement candidate)
        {
            var p = candidate.Position - Center; var e = candidate.Extents;
            if (Math.Abs(p.X) + e.X > Size.X / 2 + .01f || Math.Abs(p.Y) + e.Y > Size.Y / 2 + .01f ||
                Math.Abs(p.Z) + e.Z > Size.Z / 2 + .01f) return false;
            foreach (var r in Rooms)
            {
                var q = candidate.Position - r.Position; var f = r.Extents;
                if (e.X > 0 && e.Y > 0 && e.Z > 0 && f.X > 0 && f.Y > 0 && f.Z > 0 &&
                    Math.Abs(q.X) < e.X + f.X - .05f && Math.Abs(q.Y) < e.Y + f.Y - .05f &&
                    Math.Abs(q.Z) < e.Z + f.Z - .05f) return false;
            }
            // Незаявленные совпадения стыков могли бы сократить путь к глубокой охране.
            for (int a = 0; a < candidate.Template.Sockets.Length; a++)
                for (int i = 0; i < Rooms.Count; i++)
                    for (int b = 0; b < Rooms[i].Template.Sockets.Length; b++)
                    {
                        if (i == candidate.Parent && b == candidate.ParentSocket && a == candidate.InSocket) continue;
                        if (candidate.SocketPosition(a).Distance(Rooms[i].SocketPosition(b)) < .1f) return false;
                    }
            return true;
        }

        internal IEnumerable<int> FreeSockets(int room) => Enumerable.Range(0, Rooms[room].Template.Sockets.Length)
            .Where(s => !Rooms[room].Template.Sockets[s].Entrance && s != Rooms[room].InSocket &&
                !Rooms.Any(r => r.Parent == room && r.ParentSocket == s));

        internal bool Extend(int parent, int targetDepth, int branch, bool arena = false)
        {
            if (Rooms[parent].Depth == targetDepth) return true;
            if (Tries >= Limit) return false;
            int next = Rooms[parent].Depth + 1;
            var pool = arena && next == targetDepth ? new[] { Arena } :
                next % 8 == 0 || next == targetDepth || (branch == 0 && next == 12) ?
                    new[] { Reward } : Through;
            var candidates = (from s in FreeSockets(parent)
                from t in pool from entry in Enumerable.Range(0, t.Sockets.Length)
                where t.Sockets[entry].Type == Rooms[parent].Template.Sockets[s].Type
                select Attach(parent, s, t, entry)).OrderBy(_ => Random.Next()).ToArray();
            foreach (var c in candidates)
            {
                if (++Tries >= Limit) return false;
                if (!Fits(c)) continue;
                c.Branch = branch;
                c.Arena = arena && next == targetDepth;
                c.ResourceCount = c.Arena ? 6 : next % 8 == 0 ? (next == 8 ? 5 : 3) : 0;
                c.GuaranteedCores = c.ResourceCount > 0 && next <= SafeDepth;
                c.ChestCount = c.Arena ? 3 : c.Template == Reward ? 1 : 0;
                int index = Rooms.Count;
                Rooms.Add(c);
                if (Extend(index, targetDepth, branch, arena)) return true;
                Rooms.RemoveRange(index, Rooms.Count - index);
            }
            return false;
        }
    }

    public static Layout Create(Template[] templates, Point entrance, int entranceTurn,
        Point center, Point size, int seed)
    {
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var search = new Search(templates, unchecked(seed + attempt * 104729), center, size);
            var start = templates.Single(t => t.Entrance);
            int entry = Array.FindIndex(start.Sockets, s => s.Entrance);
            int turn = (entranceTurn + 4 - start.Sockets[entry].Turn) % 4;
            var first = new Placement { Template = start, Turn = turn, InSocket = entry,
                Position = entrance - start.Sockets[entry].Position.Rotate(turn) };
            if (!search.Fits(first)) continue;
            search.Rooms.Add(first);
            // Сначала резервируем крупный зал, затем свободный конец основной ветви.
            if (!search.Extend(0, 32, 0)) continue;
            int branchCount = new Random(seed).Next(2, 6);
            // Поздний выход с основного пути: 32/40 = 80%, затем ещё восемь переходов до арены.
            bool branches = true;
            int arenaParent = search.Rooms.FindIndex(r => r.Branch == 0 && r.Depth == 32);
            if (!search.Extend(arenaParent, MaxDepth, 1, true)) continue;
            if (!search.Extend(arenaParent, MaxDepth, 0)) continue;
            int branch = 2;
            foreach (int depth in new[] { 8, 12, 16, 24 }.OrderBy(_ => search.Random.Next()).Take(branchCount - 1))
            {
                int parent = search.Rooms.FindIndex(r => r.Branch == 0 && r.Depth == depth);
                int length = search.Random.Next(5, 9);
                if (!search.Extend(parent, depth + length, branch++)) { branches = false; break; }
            }
            if (!branches) continue;
            for (int i = 0; i < search.Rooms.Count; i++)
                foreach (int socket in search.FreeSockets(i).ToArray())
                {
                    var cap = search.Attach(i, socket, search.Wall, 0);
                    if (!search.Fits(cap)) { branches = false; break; }
                    search.Rooms.Add(cap);
                }
            if (!branches) continue;
            var result = new Layout { Seed = seed, Attempts = attempt + 1, Rooms = search.Rooms,
                BranchCount = branchCount };
            result.Edges = result.Rooms.Select((r, i) => (r, i)).Where(x => x.r.Parent >= 0)
                .Select(x => (x.r.Parent, x.i)).ToList();
            Validate(result);
            return result;
        }
        throw new InvalidOperationException($"Не удалось разместить полный лесной профиль за {MaxAttempts} попыток");
    }

    public static void Validate(Layout layout)
    {
        var rooms = layout.Rooms;
        if (rooms.Count == 0 || !rooms[0].Template.Entrance) throw new InvalidOperationException("Нет входа");
        var depths = DungeonGeometryProbe.RoomGraph.Distances(rooms.Count, layout.Edges, 0);
        for (int i = 0; i < rooms.Count; i++)
        {
            var r = rooms[i];
            int expected = r.Template.Endcap ? depths[r.Parent] : depths[i];
            if (expected != r.Depth || r.Depth > MaxDepth) throw new InvalidOperationException("Неверная глубина");
            for (int s = 0; s < r.Template.Sockets.Length; s++)
            {
                if (r.Template.Sockets[s].Entrance) continue;
                int links = (s == r.InSocket ? 1 : 0) + rooms.Count(p => p.Parent == i && p.ParentSocket == s);
                if (links != 1) throw new InvalidOperationException("Незакрытый или неоднозначный стык");
            }
            if (r.Parent >= 0 && (r.SocketPosition(r.InSocket).Distance(rooms[r.Parent]
                    .SocketPosition(r.ParentSocket)) > .01f ||
                (r.SocketTurn(r.InSocket) + 2) % 4 != rooms[r.Parent].SocketTurn(r.ParentSocket)))
                throw new InvalidOperationException("Разрыв стыка");
        }
        if (rooms.Max(r => r.Depth) != MaxDepth ||
            rooms.Where(r => r.Depth <= SafeDepth).Sum(r => r.ResourceCount) < 5)
            throw new InvalidOperationException("Нет обязательного маршрута или ресурсов");
        var branchIds = rooms.Where(r => !r.Template.Endcap && r.Branch > 0).Select(r => r.Branch).Distinct();
        if (layout.BranchCount < 2 || layout.BranchCount > 5 || branchIds.Count() != layout.BranchCount)
            throw new InvalidOperationException("Неверное число ответвлений");
        var arena = rooms.Where(r => r.Arena).ToArray();
        if (arena.Length != 1 || arena[0].ChestCount != 3 || arena[0].ResourceCount < 1)
            throw new InvalidOperationException("Нет полной арены");
        var firstArenaBranch = rooms.First(r => r.Branch == arena[0].Branch && !r.Template.Endcap);
        if (rooms[firstArenaBranch.Parent].Depth < MaxDepth * .7 ||
            arena[0].Depth - rooms[firstArenaBranch.Parent].Depth < 5)
            throw new InvalidOperationException("Арена должна быть в конце длинного позднего ответвления");
        foreach (var r in rooms.Where(r => !r.Template.Endcap))
        {
            int parent = r.Parent;
            while (parent > 0 && rooms[parent].ResourceCount == 0) parent = rooms[parent].Parent;
            int last = parent < 0 ? 0 : rooms[parent].Depth;
            int gap = r.Depth - last;
            if (gap > 10 || (r.ResourceCount > 0 && gap < 5))
                throw new InvalidOperationException("Нарушен шаг ресурсных комнат 5–10 переходов");
        }
        foreach (var (a, b) in layout.Edges)
            if (Math.Abs(ModifierCount(rooms[a].Depth) - ModifierCount(rooms[b].Depth)) > 1)
                throw new InvalidOperationException("Скачок числа модификаторов");
    }
}
