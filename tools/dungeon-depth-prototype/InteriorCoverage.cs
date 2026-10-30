using System;
using System.Collections.Generic;
using System.Linq;

namespace DungeonDepthPrototype;

// Объединение сохранённых объёмов. Межкомнатный шов не является выходом из интерьера.
internal static class InteriorCoverage
{
    internal const float Margin = .10f;
    internal static bool Contains(IEnumerable<ForestPlan.Volume> volumes, ForestPlan.Point point) =>
        volumes.Any(v => Math.Abs(point.X - v.Center.X) <= v.Size.X / 2 + Margin &&
            Math.Abs(point.Y - v.Center.Y) <= v.Size.Y / 2 + Margin &&
            Math.Abs(point.Z - v.Center.Z) <= v.Size.Z / 2 + Margin);
}
