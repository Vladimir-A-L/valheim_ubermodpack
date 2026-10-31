using System;

namespace DungeonDepthPrototype;

// Только ключ кэша, не размеры физического тела. lossyScale меняется на несколько ULP при повороте.
public static class GuardShapeKey
{
    public const double Resolution = .0001; // 0,1 мм; физические проверки по-прежнему используют живую капсулу.
    public static float Canonical(float dimension)
    {
        if (float.IsNaN(dimension) || float.IsInfinity(dimension) || dimension < 0)
            throw new ArgumentOutOfRangeException(nameof(dimension));
        return (float)(Math.Round(dimension / Resolution, MidpointRounding.AwayFromZero) * Resolution);
    }
}
