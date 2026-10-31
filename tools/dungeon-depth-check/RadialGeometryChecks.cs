using DungeonDepthPrototype;
using Point = DungeonDepthPrototype.ForestPlan.Point;

internal static class RadialGeometryChecks
{
    internal static void Run(ForestPlan.Template[] templates, Action<bool, string> check)
    {
        var type = new RadialTypeRules();
        bool multipleEnds = false;
        int maximumRooms = 0, maximumSteps = 0, succeeded = 0;
        foreach (var (profile, depth) in new[] { (type.Far, 20), (type.Far, 40), (type.Near, 26),
                     (type.Near, 49), (type.Near, 52), (type.Middle, 40), (type.Middle, 61), (type.Middle, 64) })
        for (int seed = 0; seed < 4; seed++)
        {
            var selected = new RadialSelection { Type = "ForestCrypt", Revision = "geometry-fixture",
                Profile = profile, MaximumDepth = depth, Seed = seed, ForcedProbe = true };
            var layout = ForestPlan.Create(templates, new Point(0, 0, -1), seed,
                new Point(0, 0, 0), new Point(128, 64, 128), seed, true, radial: selected);
            succeeded++; maximumRooms = Math.Max(maximumRooms, layout.Rooms.Count);
            maximumSteps = Math.Max(maximumSteps, layout.SearchSteps);
            check(layout.Rooms.All(r => r.ProfileDepth <= depth), "Общий D, включая ресурсную арену");
            check(layout.Rooms.Where(r => !r.Template.Endcap).All(r =>
                r.ModifierCount == profile.Count(r.ProfileDepth)), "Профиль всех ветвей зафиксирован при создании");
            var arena = layout.Rooms.Single(r => r.Arena);
            int approach = arena.Depth - layout.Rooms[arena.Attachment].Depth;
            check(approach is >= 5 and <= 9 && arena.Depth <= depth && arena.ChestCount == 3,
                "Три сундука и подход 5–9 внутри общего потолка");
            check(layout.Rooms[arena.Attachment].Depth >= ForestPlan.PlacementThreshold(depth, 70),
                "Стык арены не раньше 70 процентов");
            check(layout.Rooms.Single(r => r.MiniBoss).CombatDepth >=
                ForestPlan.PlacementThreshold(depth, 80), "Стык босса не раньше 80 процентов");
            check(layout.Rooms.Count(r => r.ResourceCount > 0) is >= 2 and <= 4 &&
                layout.BranchCount is >= 2 and <= 5, "Квоты сохранены для коротких и длинных профилей");
            var terminalRewards = layout.Rooms.Where(r => r.Depth == depth && r.ChestCount > 0).ToArray();
            check(terminalRewards.Length > 0, "На D есть награда, а не только пустой сегмент");
            multipleEnds |= terminalRewards.Select(r => r.Branch).Distinct().Count() > 1;
            check(layout.SearchSteps <= ForestPlan.MaxRadialSearchSteps &&
                layout.Rooms.Count <= ForestPlan.MaxRadialRooms, "Общая работа и число комнат ограничены");
        }
        check(multipleEnds, "Несколько ветвей могут дать награды на максимальной глубине");
        var failedChoice = new RadialSelection { Profile = type.Middle, MaximumDepth = 64, Seed = 731,
            Category = "target", ForcedProbe = true };
        bool failed = false;
        try
        {
            ForestPlan.Create(templates, new Point(0, 0, -1), 0, new Point(0, 0, 0),
                new Point(2, 2, 2), 731, true, radial: failedChoice, searchBudget: 8);
        }
        catch (InvalidOperationException) { failed = true; }
        check(failed && failedChoice.MaximumDepth == 64 && failedChoice.Category == "target",
            "Отказ геометрии не перебрасывает категорию или D");
        Console.WriteLine($"Радиальная геометрия: {succeeded} планов; максимум комнат {maximumRooms}, " +
            $"проверок размещения {maximumSteps}");
    }
}
