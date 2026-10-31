using System;
using System.Collections.Generic;
using System.Linq;

namespace DungeonDepthPrototype;

// Планирование без создания игровых объектов. Координаты берутся из загруженных штатных prefab.
public static class ForestPlan
{
    public sealed class PlacementFailure : InvalidOperationException
    {
        public int Attempts { get; }
        public int SearchSteps { get; }
        internal PlacementFailure(int attempts, int steps, int depth) : base(
            $"Не удалось разместить обе встречи за {attempts} попыток; L={depth}, проверено кандидатов={steps}")
        { Attempts = attempts; SearchSteps = steps; }
    }
    public const int SafeDepth = 16, MaxDepth = 64;
    public const string TrialArena = "forestcrypt_new_BurialChamber02";
    private const int GrowthDepth = MaxDepth - SafeDepth;
    public const int MaxAttempts = 256;
    public const int MaxRadialSearchSteps = 250000, MaxRadialRooms = 384, MaxRadialAttempts = 2048;
    public static int ModifierCount(int depth) => depth <= SafeDepth ? 0 :
        depth >= MaxDepth ? 10 : (10 * (depth - SafeDepth) + GrowthDepth - 1) / GrowthDepth;

    // Удлинение пути не удваивает частоту наград: прежний шаг 8 растянут после нулевого участка.
    private static bool RewardDepth(int depth) => depth <= SafeDepth ? depth % 8 == 0 :
        (depth - SafeDepth) % 16 == 0;

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
        public bool AllowDoor = true, DoorRequiresBoth;
        public string Type = "";
    }

    public sealed class Volume
    {
        public Point Center, Size;
    }

    public sealed class Template
    {
        public string Name = "";
        public Point Size;
        public Socket[] Sockets = Array.Empty<Socket>();
        public bool Entrance, Endcap;
        public Volume[] Volumes = Array.Empty<Volume>();
    }

    public sealed class Placement
    {
        public Template Template = null!;
        public Point Position;
        public int Turn, Parent = -1, ParentSocket = -1, InSocket = -1, Depth;
        public bool GuaranteedCores;
        public int ResourceCount, RareGuardCount, ChestCount, Branch;
        public bool Arena, MiniBoss;
        public int CombatDepth, RouteEnd = -1, Attachment = -1;
        public int Modifiers = -1;
        public int ProfileDepth => MiniBoss ? CombatDepth : Depth;
        public int ModifierCount => Modifiers >= 0 ? Modifiers : ForestPlan.ModifierCount(ProfileDepth);
        public IEnumerable<Volume> Bounds => (Template.Volumes.Length > 0 ? Template.Volumes :
            new[] { new Volume { Size = Template.Size } }).Select(v => new Volume {
                Center = Position + v.Center.Rotate(Turn), Size = Abs(v.Size.Rotate(Turn)) });
        private static Point Abs(Point p) => new Point(Math.Abs(p.X), Math.Abs(p.Y), Math.Abs(p.Z));
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
        public int BranchCount, RouteLength = MaxDepth, SearchSteps;
        public bool StageGates;
        public RadialSelection? Radial;
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
        internal readonly bool StageGates;
        internal readonly RadialSelection? Radial;
        private readonly int workBudget;
        private int work;
        internal int Steps => Radial == null ? Tries : work;
        private bool Exhausted => Tries >= Limit || Radial != null && work >= workBudget;
        internal const int Limit = 20000;

        internal Search(Template[] templates, int seed, Point center, Point size, bool stageGates,
            string arena = "forestcrypt_new_Burialchamber05", RadialSelection? radial = null,
            int remainingWork = MaxRadialSearchSteps)
        {
            Random = new Random(seed); Center = center; Size = size;
            StageGates = stageGates; Radial = radial; workBudget = remainingWork;
            Template Get(string name) => templates.Single(t => t.Name == name);
            Reward = Get("forestcrypt_room1");
            Arena = Get(arena);
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
                Depth = from.Depth + (template.Endcap ? 0 : 1),
                Modifiers = Radial?.Profile.Count(from.Depth + (template.Endcap ? 0 : 1)) ?? -1 };
        }

        internal bool Fits(Placement candidate)
        {
            if (Radial != null && (work >= workBudget || ++work > workBudget || Rooms.Count >= MaxRadialRooms))
                return false;
            foreach (var volume in candidate.Bounds)
            {
                var p = volume.Center - Center; var size = volume.Size;
                if (Math.Abs(p.X) + size.X / 2 > Size.X / 2 + .01f ||
                    Math.Abs(p.Y) + size.Y / 2 > Size.Y / 2 + .01f ||
                    Math.Abs(p.Z) + size.Z / 2 > Size.Z / 2 + .01f) return false;
            }
            if (Rooms.Any(r => Overlaps(candidate, r))) return false;
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
            if (Exhausted || Radial != null && targetDepth > Radial.MaximumDepth) return false;
            int next = Rooms[parent].Depth + 1;
            var pool = arena && next == targetDepth ? new[] { Arena } :
                RewardDepth(next) || next == targetDepth || (branch == 0 && next == 12) ?
                    new[] { Reward } : Through;
            var candidates = (from s in FreeSockets(parent)
                from t in pool from entry in Enumerable.Range(0, t.Sockets.Length)
                where t.Sockets[entry].Type == Rooms[parent].Template.Sockets[s].Type
                select Attach(parent, s, t, entry)).OrderBy(_ => Random.Next()).ToArray();
            foreach (var c in candidates)
            {
                if (++Tries >= Limit || Exhausted) return false;
                if (!Fits(c)) continue;
                c.Branch = branch;
                c.Arena = arena && next == targetDepth;
                if (StageGates && EncounterGates.Required(Rooms[parent], c) &&
                    !EncounterGates.Fits(Rooms[parent], c.ParentSocket, c)) continue;
                c.ChestCount = c.Arena ? 3 : c.Template == Reward ? 1 : 0;
                int index = Rooms.Count;
                Rooms.Add(c);
                if (Extend(index, targetDepth, branch, arena)) return true;
                Rooms.RemoveRange(index, Rooms.Count - index);
            }
            return false;
        }
    }

    public static int PlacementThreshold(int length, int percent)
    {
        if (length < 0 || percent < 0 || percent > 100) throw new ArgumentOutOfRangeException();
        return checked((int)(((long)length * percent + 99) / 100));
    }

    public static bool Overlaps(Placement a, Placement b) => a.Bounds.Any(v => b.Bounds.Any(w =>
        v.Size.X > 0 && v.Size.Y > 0 && v.Size.Z > 0 && w.Size.X > 0 && w.Size.Y > 0 && w.Size.Z > 0 &&
        Math.Abs(v.Center.X - w.Center.X) < (v.Size.X + w.Size.X) / 2 - .05f &&
        Math.Abs(v.Center.Y - w.Center.Y) < (v.Size.Y + w.Size.Y) / 2 - .05f &&
        Math.Abs(v.Center.Z - w.Center.Z) < (v.Size.Z + w.Size.Z) / 2 - .05f));

    public static Template BossTemplate(string connection) => new Template {
        Name = MiniBossArenaPlan.Prefab,
        Sockets = new[] { new Socket { Position = new Point(0, 0, MiniBossArenaPlan.ConnectionZ),
            Turn = 0, Type = connection, AllowDoor = false } },
        // Объём оболочки смещён относительно порога. У самого стыка допускается штатное перекрытие кладки.
        Volumes = MiniBossArenaPlan.ShellFloor.Select(r => {
            float bottom = r.Z0 == MiniBossArenaPlan.ConnectionZ ? -1.1f : -.6f;
            float z0 = r.Z0 == MiniBossArenaPlan.ConnectionZ ? r.Z0 : r.Z0 - .4f;
            return new Volume { Center = new Point((r.X0 + r.X1) / 2f, (bottom + 6.6f) / 2,
                (z0 + r.Z1 + .4f) / 2), Size = new Point(r.X1 - r.X0 + .8f, 6.6f - bottom,
                    r.Z1 + .4f - z0) };
        }).ToArray()
    };

    public static Layout Create(Template[] templates, Point entrance, int entranceTurn,
        Point center, Point size, int seed, bool stageGates = false, int routeLength = MaxDepth,
        RadialSelection? radial = null, int searchBudget = MaxRadialSearchSteps)
    {
        if (radial != null)
        {
            if (searchBudget < 1 || searchBudget > MaxRadialSearchSteps)
                throw new ArgumentOutOfRangeException(nameof(searchBudget));
            routeLength = radial.MaximumDepth;
        }
        if (routeLength < SafeDepth || routeLength > 160)
            throw new ArgumentOutOfRangeException(nameof(routeLength), "Допустимы маршруты от 16 до 160 переходов");
        int totalSteps = 0, attempts = 0;
        int attemptLimit = radial == null ? MaxAttempts : MaxRadialAttempts;
        for (int attempt = 0; attempt < attemptLimit; attempt++)
        {
            if (radial != null && totalSteps >= searchBudget) break;
            attempts++;
            var search = new Search(templates, unchecked(seed + attempt * 104729), center, size, stageGates,
                radial: radial, remainingWork: searchBudget - totalSteps);
            var start = templates.Single(t => t.Entrance);
            int entry = Array.FindIndex(start.Sockets, s => s.Entrance);
            int turn = (entranceTurn + 4 - start.Sockets[entry].Turn) % 4;
            var first = new Placement { Template = start, Turn = turn, InSocket = entry,
                Position = entrance - start.Sockets[entry].Position.Rotate(turn),
                Modifiers = radial?.Profile.Count(0) ?? -1 };
            if (!search.Fits(first)) { totalSteps += search.Steps; continue; }
            search.Rooms.Add(first);
            int bossDepth = search.Random.Next(PlacementThreshold(routeLength, 80), routeLength + 1);
            // Сначала зарезервировать весь объём секции, затем достраивать остальные части плана.
            if (!search.Extend(0, bossDepth, 0)) { totalSteps += search.Steps; continue; }
            int bossParent = search.Rooms.FindIndex(r => r.Branch == 0 && r.Depth == bossDepth);
            Placement? boss = null;
            foreach (int socket in search.FreeSockets(bossParent).OrderBy(_ => search.Random.Next()))
            {
                var candidate = search.Attach(bossParent, socket,
                    BossTemplate(search.Rooms[bossParent].Template.Sockets[socket].Type), 0);
                if (!search.Fits(candidate)) continue;
                boss = candidate; break;
            }
            if (boss == null) { totalSteps += search.Steps; continue; }
            boss.MiniBoss = true; boss.CombatDepth = bossDepth; boss.Branch = 2; boss.Attachment = bossParent;
            boss.Modifiers = radial?.Profile.Count(bossDepth) ?? -1;
            search.Rooms.Add(boss);
            if (!search.Extend(bossParent, routeLength, 0)) { totalSteps += search.Steps; continue; }
            int routeEnd = search.Rooms.FindIndex(r => r.Branch == 0 && r.Depth == routeLength);
            boss.RouteEnd = routeEnd;
            bool arenaPlaced = false;
            var joins = search.Rooms.Select((r, i) => (r, i)).Where(x => x.r.Branch == 0 &&
                x.r.Depth >= PlacementThreshold(routeLength, 70) &&
                (radial == null || x.r.Depth <= routeLength - 5)).OrderBy(_ => search.Random.Next()).ToArray();
            foreach (var join in joins)
            {
                int before = search.Rooms.Count;
                int length = search.Random.Next(5, radial == null ? 10 : Math.Min(9, routeLength - join.r.Depth) + 1);
                if (search.Extend(join.i, join.r.Depth + length, 1, true))
                {
                    var arena = search.Rooms.Single(r => r.Arena);
                    arena.RouteEnd = routeEnd; arena.Attachment = join.i; arenaPlaced = true; break;
                }
                search.Rooms.RemoveRange(before, search.Rooms.Count - before);
            }
            if (!arenaPlaced) { totalSteps += search.Steps; continue; }
            int branchCount = new Random(seed).Next(2, 6), branch = 3;
            bool branches = true;
            var branchDepths = routeLength == SafeDepth ? new[] { 4, 8, 12 } :
                new[] { 8, 12, 16, routeLength / 2 };
            foreach (int depth in branchDepths.Distinct()
                         .Where(d => d < routeLength && (radial == null || d <= routeLength - 5))
                         .OrderBy(_ => search.Random.Next()).Take(branchCount - 2))
            {
                int parent = search.Rooms.FindIndex(r => r.Branch == 0 && r.Depth == depth);
                int length = radial == null ? search.Random.Next(5, 9) * (depth >= SafeDepth ? 2 : 1) :
                    search.Random.Next(3) == 0 ? routeLength - depth : search.Random.Next(5, routeLength - depth + 1);
                if (!search.Extend(parent, depth + length, branch++)) { branches = false; break; }
            }
            if (!branches) { totalSteps += search.Steps; continue; }
            for (int i = 0; i < search.Rooms.Count && branches; i++)
                foreach (int socket in search.FreeSockets(i).ToArray())
                {
                    var cap = search.Attach(i, socket, search.Wall, 0);
                    if (!search.Fits(cap)) { branches = false; break; }
                    search.Rooms.Add(cap);
                }
            totalSteps += search.Steps;
            if (!branches) continue;
            var result = new Layout { Seed = seed, Attempts = attempt + 1, Rooms = search.Rooms,
                BranchCount = branchCount, StageGates = stageGates, RouteLength = routeLength,
                SearchSteps = totalSteps, Radial = radial };
            AssignResources(result);
            result.Edges = result.Rooms.Select((r, i) => (r, i)).Where(x => x.r.Parent >= 0)
                .Select(x => (x.r.Parent, x.i)).ToList();
            Validate(result);
            return result;
        }
        throw new PlacementFailure(attempts, totalSteps, routeLength);
    }

    // Короткий стенд боя: один основной тупик и ровно одно ответвление к залу босса.
    // Физическая глубина не выдаётся за глубину боевого профиля испытания.
    public static Layout CreateTrial(Template[] templates, Point entrance, int entranceTurn,
        Point center, Point size, int seed)
    {
        for (int attempt = 0; attempt < 256; attempt++)
        {
            var search = new Search(templates, unchecked(seed + attempt * 104729), center, size, false, TrialArena);
            var start = templates.Single(t => t.Entrance);
            int entry = Array.FindIndex(start.Sockets, s => s.Entrance);
            int turn = (entranceTurn + 4 - start.Sockets[entry].Turn) % 4;
            var first = new Placement { Template = start, Turn = turn, InSocket = entry,
                Position = entrance - start.Sockets[entry].Position.Rotate(turn) };
            if (!search.Fits(first)) continue;
            search.Rooms.Add(first);
            if (!search.Extend(0, 2, 0)) continue;
            int junction = search.Rooms.FindIndex(r => r.Depth == 2);
            if (!search.Extend(junction, 5, 1, true) || !search.Extend(junction, 3, 0)) continue;
            bool fits = true;
            for (int i = 0; i < search.Rooms.Count; i++)
                foreach (int socket in search.FreeSockets(i).ToArray())
                {
                    var cap = search.Attach(i, socket, search.Wall, 0);
                    if (!search.Fits(cap)) { fits = false; break; }
                    search.Rooms.Add(cap);
                }
            if (!fits) continue;
            foreach (var room in search.Rooms) room.ChestCount = 0;
            var layout = new Layout { Seed = seed, Attempts = attempt + 1, BranchCount = 1,
                Rooms = search.Rooms };
            layout.Edges = layout.Rooms.Select((r, i) => (r, i)).Where(x => x.r.Parent >= 0)
                .Select(x => (x.r.Parent, x.i)).ToList();
            return layout;
        }
        throw new InvalidOperationException("Не удалось разместить склеп испытания");
    }

    private static void AssignResources(Layout layout)
    {
        // Отдельный поток случайности сохраняет уже проверенную геометрию для того же seed.
        var random = new Random(unchecked(layout.Seed ^ 0x27d4eb2d));
        int quota = random.Next(2, 5);
        var early = layout.Rooms.Single(r => r.Branch == 0 && r.Depth == 8 && !r.Template.Endcap);
        early.ResourceCount = 5; early.GuaranteedCores = true;
        layout.Rooms.Single(r => r.Arena).ResourceCount = 6;
        var extra = layout.Rooms.Where(r => !r.Template.Endcap && !r.Arena &&
            r.ChestCount > 0 && !r.GuaranteedCores).OrderBy(_ => random.Next()).Take(quota - 2).ToArray();
        if (extra.Length != quota - 2) throw new InvalidOperationException("Нет места для ресурсной квоты");
        foreach (var room in extra) room.ResourceCount = 3;
        for (int i = 0; i < layout.Rooms.Count; i++)
            layout.Rooms[i].RareGuardCount = EncounterPlan.ResourceReinforcements(
                layout.Rooms[i].ResourceCount > 0, unchecked(layout.Seed * 397 ^ i));
    }

    public static void Validate(Layout layout)
    {
        var rooms = layout.Rooms;
        if (rooms.Count == 0 || !rooms[0].Template.Entrance) throw new InvalidOperationException("Нет входа");
        if (layout.Radial != null && (rooms.Count > MaxRadialRooms ||
            layout.SearchSteps > MaxRadialSearchSteps || rooms.Any(r => r.ProfileDepth > layout.RouteLength) ||
            layout.RouteLength != layout.Radial.MaximumDepth))
            throw new InvalidOperationException("Превышен общий предел глубины, комнат или поиска");
        var depths = DungeonGeometryProbe.RoomGraph.Distances(rooms.Count, layout.Edges, 0);
        for (int i = 0; i < rooms.Count; i++)
        {
            var r = rooms[i];
            int expected = r.Template.Endcap ? depths[r.Parent] : depths[i];
            if (expected != r.Depth) throw new InvalidOperationException("Неверная глубина");
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
        if (!rooms.Any(r => r.Branch == 0 && !r.Template.Endcap && r.Depth == layout.RouteLength) ||
            rooms.Where(r => r.Depth <= SafeDepth).Sum(r => r.ResourceCount) < 5)
            throw new InvalidOperationException("Нет обязательного маршрута или ресурсов");
        var branchIds = rooms.Where(r => !r.Template.Endcap && r.Branch > 0).Select(r => r.Branch).Distinct();
        if (layout.BranchCount < 2 || layout.BranchCount > 5 || branchIds.Count() != layout.BranchCount)
            throw new InvalidOperationException("Неверное число ответвлений");
        var arena = rooms.Where(r => r.Arena).ToArray();
        if (arena.Length != 1 || arena[0].ChestCount != 3 || arena[0].ResourceCount < 1 ||
            arena[0].MiniBoss)
            throw new InvalidOperationException("Нет полной арены");
        var firstArenaBranch = rooms.First(r => r.Branch == arena[0].Branch && !r.Template.Endcap);
        if (firstArenaBranch.Parent != arena[0].Attachment ||
            arena[0].Depth - rooms[firstArenaBranch.Parent].Depth < 5 ||
            arena[0].Depth - rooms[firstArenaBranch.Parent].Depth > 9)
            throw new InvalidOperationException("Арена должна быть в конце длинного позднего ответвления");
        var bosses = rooms.Where(r => r.MiniBoss).ToArray();
        if (bosses.Length != 1) throw new InvalidOperationException("Нужна ровно одна секция мини-босса");
        var boss = bosses[0];
        if (boss.CombatDepth != depths[boss.Parent] || boss.Attachment != boss.Parent ||
            boss.Template.Sockets.Length != 1 || rooms.Any(r => r.Parent == rooms.IndexOf(boss)) ||
            boss.ChestCount != 0 || boss.ResourceCount != 0 || boss.RareGuardCount != 0)
            throw new InvalidOperationException("Неверный профиль тупиковой секции мини-босса");
        foreach (var special in new[] { boss, arena[0] })
        {
            int end = special.RouteEnd, attachment = special.Attachment;
            if (end < 0 || end >= rooms.Count || attachment < 0 || attachment >= rooms.Count)
                throw new InvalidOperationException("Нет выбранного маршрута встречи");
            int cursor = end;
            while (cursor >= 0 && cursor != attachment) cursor = rooms[cursor].Parent;
            if (cursor != attachment || rooms[end].MiniBoss || rooms[end].Arena ||
                depths[attachment] < PlacementThreshold(depths[end], special.MiniBoss ? 80 : 70))
                throw new InvalidOperationException("Стык вне допустимого участка выбранного маршрута");
        }
        for (int i = 0; i < rooms.Count; i++)
        for (int j = i + 1; j < rooms.Count; j++)
            if (Overlaps(rooms[i], rooms[j])) throw new InvalidOperationException("Пересечение объёмов комнат");
        int resourceRooms = rooms.Count(r => r.ResourceCount > 0);
        if (resourceRooms < 2 || resourceRooms > 4 || rooms.Any(r => r.ResourceCount > 0 &&
                (r.Template.Endcap || r.RareGuardCount < 1 || r.RareGuardCount > 3)) ||
            rooms.Any(r => r.ResourceCount == 0 && r.RareGuardCount != 0))
            throw new InvalidOperationException("Нарушена общая квота ресурсов или редкой охраны");
        foreach (var (a, b) in layout.Edges)
            if (Math.Abs(rooms[a].ModifierCount - rooms[b].ModifierCount) > 1)
                throw new InvalidOperationException("Скачок числа модификаторов");
        if (layout.StageGates) EncounterGates.Validate(layout);
    }
}
