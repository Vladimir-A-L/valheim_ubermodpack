using DungeonDepthPrototype;
using Point = DungeonDepthPrototype.ForestPlan.Point;
using Volume = DungeonDepthPrototype.ForestPlan.Volume;

internal static class InteriorCoverageChecks
{
    internal static void Run(Action<bool, string> check)
    {
        for (int turn = 0; turn < 4; turn++)
        {
            var origin = new Point(2000, 4000, 3000);
            var section = new ForestPlan.Placement { Template = ForestPlan.BossTemplate("test"),
                Position = origin, Turn = turn };
            Point At(float x, float y, float z) => origin + new Point(x, y, z).Rotate(turn);
            var parent = new Volume { Center = At(0, 2, -32), Size = new Point(8, 8, 8) };
            var volumes = section.Bounds.Concat(new[] { parent }).ToArray();
            foreach (float z in new[] { -32, -28.1f, -28, -27.9f, -24, -12, 0, 4 })
                check(InteriorCoverage.Contains(volumes, At(0, 1, z)), "Нет разрыва окружения на стыках");
            foreach (var p in MiniBossArenaPlan.Route)
                check(InteriorCoverage.Contains(volumes, At(p.X, 1.6f, p.Z)), "Охват всего подхода и зала");
            check(!InteriorCoverage.Contains(volumes, At(-10, 1, 20)), "Пустота вне Г-образной секции исключена");
            check(!InteriorCoverage.Contains(volumes, At(0, 30, -12)), "Нет переноса окружения на другой этаж");
            check(!InteriorCoverage.Contains(volumes, new Point(2048, 4000, 2048)), "Хаб вне окружения данжа");
            check(!InteriorCoverage.Contains(Array.Empty<Volume>(), At(0, 1, 0)), "Выгруженная область не действует");
        }
    }
}
