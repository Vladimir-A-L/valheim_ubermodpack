using DungeonDepthPrototype;

internal static class GuardShapeChecks
{
    internal static void Run(Action<bool, string> check)
    {
        // Наблюдённые на исходной DLL ключи одной капсулы при обычном движении/поворотах.
        var radii = new[] { .3099999f, .309999943f, .309999973f, .31f, .310000032f,
            .3100001f, .310000122f, .310000151f };
        var heights = new[] { 1.14999962f, 1.14999974f, 1.14999986f, 1.15f, 1.1500001f };
        check(radii.Select(GuardShapeKey.Canonical).Distinct().Count() == 1,
            "Observed yaw noise shares one radius key");
        check(heights.Select(GuardShapeKey.Canonical).Distinct().Count() == 1,
            "Observed yaw noise shares one height key");
        check((from r in radii from h in heights select (GuardShapeKey.Canonical(r),
            GuardShapeKey.Canonical(h))).Distinct().Count() == 1, "Rotation does not rebuild walkmap");
        foreach (float size in new[] { 0f, .1f, .31f, .36f, 1.05f, 1.15f, 1.8f, 3.6f })
        {
            check(Math.Abs(GuardShapeKey.Canonical(size) - size) <= .000051,
                "Canonical key stays within half a cell");
            check(GuardShapeKey.Canonical(size + .001f) != GuardShapeKey.Canonical(size),
                "Real capsule growth changes the key");
            check(GuardShapeKey.Canonical(GuardShapeKey.Canonical(size)) == GuardShapeKey.Canonical(size),
                "Key is idempotent");
        }
        foreach (float invalid in new[] { -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            bool refused = false;
            try { GuardShapeKey.Canonical(invalid); }
            catch (ArgumentOutOfRangeException) { refused = true; }
            check(refused, "Invalid dimensions fail closed");
        }
    }
}
