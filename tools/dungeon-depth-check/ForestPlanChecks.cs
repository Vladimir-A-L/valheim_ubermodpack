using DungeonDepthPrototype;
using Point = DungeonDepthPrototype.ForestPlan.Point;

internal static class ForestPlanChecks
{
    internal static void Run(Action<bool, string> check)
    {
        for (int depth = 0; depth <= 16; depth++)
            check(ForestPlan.ModifierCount(depth) == 0, "Начальный участок без модификаторов");
        for (int depth = 17; depth <= 40; depth++)
        {
            int count = ForestPlan.ModifierCount(depth);
            check(count >= 1 && count <= 10, "Диапазон глубокой охраны");
            check(count - ForestPlan.ModifierCount(depth - 1) is 0 or 1, "Постепенное усиление");
        }
        check(ForestPlan.ModifierCount(40) == 10, "Десять модификаторов на глубине 40");
        // Искусственные комнаты для проверки алгоритма; это не выгрузка игровых ресурсов.
        ForestPlan.Socket Socket(float x, float z, int turn, bool entrance = false) =>
            new() { Position = new Point(x, 0, z), Turn = turn, Entrance = entrance };
        var n = Socket(0, 1, 2); var s = Socket(0, -1, 0);
        var w = Socket(-1, 0, 1); var e = Socket(1, 0, 3);
        ForestPlan.Template Room(string name, params ForestPlan.Socket[] sockets) =>
            new() { Name = name, Size = new Point(2, 2, 2), Sockets = sockets };
        var entrance = Room("entrance", n, Socket(0, -1, 0, true), w, e);
        entrance.Entrance = true;
        var wall = Room("forestcrypt_EndCap", Socket(0, 0, 2));
        wall.Endcap = true; wall.Size = new Point(2, 2, 0);
        var core = Room("forestcrypt_EndCap3", Socket(0, 0, 2));
        core.Endcap = true; core.Size = new Point(2, 2, 0);
        var templates = new[] { entrance, wall, core,
            Room("forestcrypt_new_BurialChamber01", n), Room("forestcrypt_room1", n, s, w, e),
            Room("forestcrypt_Corridor1", n, s, w), Room("forestcrypt_Corridor2", n, s, e),
            Room("forestcrypt_Bend1", n, w), Room("forestcrypt_Bend2", n, e),
            Room("forestcrypt_Stairs1", n, s), Room("forestcrypt_new_Burialchamber05", n, s) };
        string Fingerprint(ForestPlan.Layout layout) => string.Join(";", layout.Rooms.Select(r =>
            FormattableString.Invariant($"{r.Template.Name}:{r.Position.X},{r.Position.Y},{r.Position.Z}:{r.Turn}") +
            $":{r.ResourceCount}:{r.RareGuardCount}"));
        foreach (int seed in new[] { 0, 1, 1001, 1002, 1003, 1004, 1005, 1006 })
        {
            var center = new Point(64, 5000, -128);
            var a = ForestPlan.Create(templates, center, seed % 4, center, new Point(64, 64, 64), seed);
            var b = ForestPlan.Create(templates, center, seed % 4, center, new Point(64, 64, 64), seed);
            check(Fingerprint(a) == Fingerprint(b), "Повторяемость полного плана");
            check(a.BranchCount >= 2 && a.BranchCount <= 5, "От двух до пяти ветвей");
            check(a.Rooms.Single(r => r.Arena).ChestCount == 3, "Три сундука на арене");
            check(a.Rooms.Count(r => r.ResourceCount > 0) is >= 2 and <= 4,
                "Общий лимит ресурсных комнат включает арену");
            check(a.Rooms.Where(r => r.ResourceCount > 0).All(r => r.RareGuardCount is >= 1 and <= 3),
                "В каждой ресурсной комнате добавлены 1–3 редких охранника");
            check(a.Rooms.Where(r => r.ResourceCount == 0).All(r => r.RareGuardCount == 0),
                "Редкая добавка только у ресурсов");
            check(a.Rooms.Where(r => r.GuaranteedCores).All(r => r.Depth <= 16), "Начальные ресурсы без усиления");
            check(a.Rooms.Count(r => r.Depth == 40 && !r.Template.Endcap) == 2, "Основной конец и арена на глубине 40");
            var empty = a.Rooms.First(r => !r.Template.Endcap && r.ResourceCount == 0);
            empty.RareGuardCount = 1;
            bool badRare = false;
            try { ForestPlan.Validate(a); }
            catch (InvalidOperationException) { badRare = true; }
            check(badRare, "Редкая добавка без ресурсной комнаты отвергается");
            empty.RareGuardCount = 0;
            var deep = a.Rooms.FindIndex(r => r.Depth == 40 && !r.Template.Endcap);
            a.Edges.Add((0, deep));
            bool rejected = false;
            try { ForestPlan.Validate(a); }
            catch (InvalidOperationException) { rejected = true; }
            check(rejected, "Короткий проход к глубокой охране отвергается");
            a.Edges.RemoveAt(a.Edges.Count - 1);
            a.Rooms[deep].Position += new Point(.5f, 0, 0);
            rejected = false;
            try { ForestPlan.Validate(a); }
            catch (InvalidOperationException) { rejected = true; }
            check(rejected, "Разрыв реального стыка отвергается");
        }
    }
}
