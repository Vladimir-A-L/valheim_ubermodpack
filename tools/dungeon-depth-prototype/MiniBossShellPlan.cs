using System;
using System.Collections.Generic;
using System.Linq;

namespace DungeonDepthPrototype;

// Замкнутая подложка за декоративным камнем. Габариты коллайдеров и игровая площадь не меняются.
internal static class MiniBossShellPlan
{
    internal const float Padding = .4f, Bottom = -.6f, Top = 6.6f;
    internal readonly struct Point
    {
        internal readonly float X, Y, Z;
        internal Point(float x, float y, float z) { X = x; Y = y; Z = z; }
    }
    internal readonly struct Face
    {
        internal readonly Point[] Points;
        internal readonly bool Cap;
        internal Face(bool cap, params Point[] points) { Points = points; Cap = cap; }
    }
    internal static IEnumerable<Face> Faces()
    {
        var rects = MiniBossArenaPlan.ShellFloor.ToArray();
        var xs = rects.SelectMany(r => new[] { r.X0 - Padding, r.X1 + Padding })
            .Concat(new[] { -1f, 1f }).Distinct().OrderBy(x => x).ToArray();
        var zs = rects.SelectMany(r => new[] { r.Z0 - Padding, r.Z1 + Padding })
            .Distinct().OrderBy(z => z).ToArray();
        var ys = new[] { Bottom, 0, MiniBossArenaPlan.ConnectionHeight, Top };
        bool Filled(int x, int z) => x >= 0 && z >= 0 && x < xs.Length - 1 && z < zs.Length - 1 &&
            rects.Any(r => (xs[x] + xs[x + 1]) / 2 > r.X0 - Padding &&
                (xs[x] + xs[x + 1]) / 2 < r.X1 + Padding &&
                (zs[z] + zs[z + 1]) / 2 > r.Z0 - Padding && (zs[z] + zs[z + 1]) / 2 < r.Z1 + Padding);
        for (int x = 0; x < xs.Length - 1; x++)
        for (int z = 0; z < zs.Length - 1; z++)
        {
            if (!Filled(x, z)) continue;
            float x0 = xs[x], x1 = xs[x + 1], z0 = zs[z], z1 = zs[z + 1];
            // Нормали внутрь: подложка видна из прохода при штатном backface culling.
            yield return new Face(false, new Point(x0, Bottom, z0), new Point(x0, Bottom, z1),
                new Point(x1, Bottom, z1), new Point(x1, Bottom, z0));
            yield return new Face(false, new Point(x1, Top, z0), new Point(x1, Top, z1),
                new Point(x0, Top, z1), new Point(x0, Top, z0));
            for (int y = 0; y < ys.Length - 1; y++)
            {
                float b = ys[y], t = ys[y + 1];
                if (!Filled(x - 1, z))
                    yield return new Face(false, new Point(x0, b, z0), new Point(x0, t, z0),
                        new Point(x0, t, z1), new Point(x0, b, z1));
                if (!Filled(x + 1, z))
                    yield return new Face(false, new Point(x1, b, z1), new Point(x1, t, z1),
                        new Point(x1, t, z0), new Point(x1, b, z0));
                if (!Filled(x, z - 1))
                    yield return new Face(z0 == MiniBossArenaPlan.ConnectionZ - Padding &&
                        x0 >= -1 && x1 <= 1 && b == 0 && t == MiniBossArenaPlan.ConnectionHeight,
                        new Point(x1, b, z0), new Point(x1, t, z0), new Point(x0, t, z0), new Point(x0, b, z0));
                if (!Filled(x, z + 1))
                    yield return new Face(false, new Point(x0, b, z1), new Point(x0, t, z1),
                        new Point(x1, t, z1), new Point(x1, b, z1));
            }
        }
    }
}
