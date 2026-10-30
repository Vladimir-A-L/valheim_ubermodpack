using DungeonDepthPrototype;
using Point = DungeonDepthPrototype.ForestPlan.Point;

internal static class ForestPlanChecks
{
    internal static void Run(Action<bool, string> check)
    {
        for (int depth = 0; depth <= 16; depth++)
            check(ForestPlan.ModifierCount(depth) == 0, "Начальный участок без модификаторов");
        for (int depth = 17; depth <= 64; depth++)
        {
            int count = ForestPlan.ModifierCount(depth);
            check(count >= 1 && count <= 10, "Диапазон глубокой охраны");
            check(count - ForestPlan.ModifierCount(depth - 1) is 0 or 1, "Постепенное усиление");
        }
        check(ForestPlan.ModifierCount(40) == 5, "Вдвое более медленный рост до глубины 40");
        check(ForestPlan.ModifierCount(59) == 9 && ForestPlan.ModifierCount(60) == 10 &&
            ForestPlan.ModifierCount(64) == 10, "Предельная ступень начинается на глубине 60");
        for (int count = 1; count <= 10; count++)
            check(Enumerable.Range(17, 48).Count(d => ForestPlan.ModifierCount(d) == count) is 4 or 5,
                "Каждая ступень занимает четыре-пять переходов");
        foreach (var (left, right) in new[] { (0, 1), (2, 3), (4, 5), (5, 6), (6, 7), (10, 11), (25, 26) })
            check(EncounterGates.Required(left, right), "Обязательное разделение ступеней");
        check(!EncounterGates.Required(1, 2) && !EncounterGates.Required(3, 4), "Младшие ступени попарно");
        check(EncounterGates.Required(10, 10, false, true), "Арена отделена даже при одинаковом усилении");
        var densityRules = new RewardRules();
        densityRules = System.Text.Json.JsonSerializer.Deserialize<RewardRules>(
            File.ReadAllText("configs/dungeon-rewards.json"),
            new System.Text.Json.JsonSerializerOptions { IncludeFields = true })!;
        decimal terminal = EncounterPlan.ForestTerminalBudget(densityRules);
        check(terminal >= 175 && terminal <= 200, "Конечный бюджет в согласованном диапазоне");
        check(densityRules.Budget(EncounterPlan.ForestSet(59), 0, false).Value < 175,
            "До последней ступени нижний порог ещё не достигнут");
        check(EncounterPlan.ForestTerminalBudget(densityRules, 16) == 0 &&
            EncounterPlan.ForestTerminalBudget(densityRules, 40) < terminal,
            "Короткий маршрут не поднимает бюджет до испытания 64");
        for (int seed = 0; seed < 30; seed++)
            check(EncounterPlan.Guards(densityRules, 0, true, false, seed, true) ==
                EncounterPlan.Guards(densityRules, 0, true, false, seed) + 2, "Ровно две добавки у начальных ядер");
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
            Room("forestcrypt_Stairs1", n, s), Room("forestcrypt_new_Burialchamber05", n, s),
            Room(ForestPlan.TrialArena, n, s) };
        string Fingerprint(ForestPlan.Layout layout) => string.Join(";", layout.Rooms.Select(r =>
            FormattableString.Invariant($"{r.Template.Name}:{r.Position.X},{r.Position.Y},{r.Position.Z}:{r.Turn}") +
            $":{r.ResourceCount}:{r.RareGuardCount}"));
        for (int seed = 0; seed < 100; seed++)
        {
            var trial = ForestPlan.CreateTrial(templates, new Point(0, 0, -1), 0,
                new Point(0, 0, 0), new Point(128, 64, 128), seed);
            check(trial.BranchCount == 1 && trial.Rooms.Count(r => r.Arena) == 1,
                "Одна ветвь с одним боссом");
            check(trial.Rooms.Single(r => r.Arena).Depth == 5 && trial.Rooms.Max(r => r.Depth) == 5,
                "Короткая проба явно имеет глубину 5");
            check(trial.Rooms.All(r => r.ResourceCount == 0 && r.ChestCount == 0 && r.RareGuardCount == 0),
                "Проба не наследует ресурсную охрану и сундуки");
            var depths = DungeonGeometryProbe.RoomGraph.Distances(trial.Rooms.Count, trial.Edges, 0);
            for (int i = 0; i < trial.Rooms.Count; i++)
            {
                var room = trial.Rooms[i];
                check(room.Depth == (room.Template.Endcap ? depths[room.Parent] : depths[i]),
                    "Кратчайшая глубина пробного склепа");
                for (int socket = 0; socket < room.Template.Sockets.Length; socket++)
                    if (!room.Template.Sockets[socket].Entrance)
                        check((room.InSocket == socket ? 1 : 0) +
                            trial.Rooms.Count(r => r.Parent == i && r.ParentSocket == socket) == 1,
                            "Каждый стык пробного склепа закрыт ровно один раз");
            }
            check(Fingerprint(trial) == Fingerprint(ForestPlan.CreateTrial(templates,
                new Point(0, 0, -1), 0, new Point(0, 0, 0), new Point(128, 64, 128), seed)),
                "Воспроизводимость короткого склепа");
        }
        foreach (int seed in new[] { 0, 1, 1001, 1002, 1003, 1004, 1005, 1006 })
        {
            var center = new Point(64, 5000, -128);
            var a = ForestPlan.Create(templates, center, seed % 4, center, new Point(128, 64, 128), seed);
            var b = ForestPlan.Create(templates, center, seed % 4, center, new Point(128, 64, 128), seed);
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
            var boss = a.Rooms.Single(r => r.MiniBoss);
            check(boss.CombatDepth >= 52 && boss.CombatDepth <= 64 && boss.Depth == boss.CombatDepth + 1,
                "Секция — один узел; профиль берётся перед переходом");
            check(boss.ResourceCount == 0 && boss.ChestCount == 0 && boss.RareGuardCount == 0,
                "Босс не наследует ресурсное наполнение");
            var gated = ForestPlan.Create(templates, center, seed % 4, center, new Point(128, 64, 128), seed, true);
            EncounterGates.Validate(gated);
            check(EncounterGates.Edges(gated).Any(e => gated.Rooms[e.B].Arena), "Вход арены закрыт");
            check(EncounterGates.Edges(gated).Any(e => gated.Rooms[e.A].Depth == 16),
                "Граница нулевых свойств закрыта");
            var blocked = EncounterGates.Edges(gated).First();
            var socket = gated.Rooms[blocked.A].Template.Sockets[gated.Rooms[blocked.B].ParentSocket];
            bool allowed = socket.AllowDoor; socket.AllowDoor = false;
            bool refusedDoor = false;
            try { EncounterGates.Validate(gated); }
            catch (InvalidOperationException) { refusedDoor = true; }
            socket.AllowDoor = allowed;
            check(refusedDoor, "Непригодный для двери обязательный стык отвергается");
            var empty = a.Rooms.First(r => !r.Template.Endcap && r.ResourceCount == 0);
            empty.RareGuardCount = 1;
            bool badRare = false;
            try { ForestPlan.Validate(a); }
            catch (InvalidOperationException) { badRare = true; }
            check(badRare, "Редкая добавка без ресурсной комнаты отвергается");
            empty.RareGuardCount = 0;
            var deep = a.Rooms.FindIndex(r => r.Depth == 64 && !r.Template.Endcap);
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
        foreach (int length in new[] { 16, 40, 63, 64, 65, 80 })
        foreach (int turn in Enumerable.Range(0, 4))
        {
            var plan = ForestPlan.Create(templates, new Point(0, 0, 0), turn,
                new Point(0, 0, 0), new Point(128, 64, 128), 730 + turn, true, length);
            var boss = plan.Rooms.Single(r => r.MiniBoss);
            var arena = plan.Rooms.Single(r => r.Arena);
            foreach (var special in new[] { boss, arena })
            {
                int route = plan.Rooms[special.RouteEnd].Depth;
                check(route == length && plan.Rooms[special.Attachment].Depth >=
                    ForestPlan.PlacementThreshold(route, special.MiniBoss ? 80 : 70),
                    "Порог считается от входа до конца выбранного маршрута");
            }
            check(plan.Rooms.Sum(r => r.Depth) > length && boss.CombatDepth <= length,
                "Сумма длин ветвей не является длиной выбранного пути");
            check(plan.SearchSteps <= ForestPlan.MaxAttempts * 20000, "Ограниченная стоимость поиска");
        }
        foreach (var row in new[] { (40, 32, 28), (64, 52, 45), (80, 64, 56), (41, 33, 29) })
            check(ForestPlan.PlacementThreshold(row.Item1, 80) == row.Item2 &&
                ForestPlan.PlacementThreshold(row.Item1, 70) == row.Item3, "Округление порогов вверх");
        foreach (int length in new[] { 40, 64, 80 })
        foreach (int unrelatedLength in new[] { 5, 100, 250 })
        {
            var graph = Enumerable.Range(0, length).Select(i => (i, i + 1)).ToList();
            int previous = 8;
            for (int i = 1; i <= unrelatedLength; i++)
            { graph.Add((previous, length + i)); previous = length + i; }
            var depths = DungeonGeometryProbe.RoomGraph.Distances(length + unrelatedLength + 1, graph, 0);
            check(depths[length] == length && ForestPlan.PlacementThreshold(depths[length], 80) ==
                ForestPlan.PlacementThreshold(length, 80), "Другая ветка не изменяет выбранный маршрут");
        }
        for (int seed = 0; seed < 10; seed++)
        {
            var shortPlan = ForestPlan.Create(templates, new Point(0, 0, 0), 0, new Point(0, 0, 0),
                new Point(128, 64, 128), seed, true, 16);
            check(shortPlan.BranchCount is >= 2 and <= 5, "Минимальный маршрут сохраняет квоту ветвей");
        }
        bool impossible = false;
        try { ForestPlan.Create(templates, new Point(0, 0, 0), 0, new Point(0, 0, 0),
            new Point(1, 1, 1), 42); }
        catch (InvalidOperationException) { impossible = true; }
        check(impossible, "Несовместимые размеры отвергаются без неполного данжа");
    }
}
