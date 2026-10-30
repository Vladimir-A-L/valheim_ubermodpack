using System;

namespace DungeonDepthPrototype;

internal static class MiniBossRules
{
    internal const float HealthFactor = 12, DamageFactor = 3;
    internal const int HelperChancePercent = 15, TrialCount = 4;

    // Равномерный бросок 0..99: один помощник в последних 15 исходах, иначе ни одного.
    internal static int Helpers(int roll)
    {
        if (roll < 0 || roll >= 100) throw new ArgumentOutOfRangeException(nameof(roll));
        return roll >= 100 - HelperChancePercent ? 1 : 0;
    }

    // Ручная площадка покрывает оба исхода для скелета и призрака без случайного пропуска варианта.
    internal static int TrialHelpers(int index)
    {
        if (index < 0 || index >= TrialCount) throw new ArgumentOutOfRangeException(nameof(index));
        return Helpers(index < 2 ? 0 : 99);
    }
}
