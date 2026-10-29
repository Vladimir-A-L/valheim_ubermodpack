using System;
using System.Linq;

namespace DungeonDepthPrototype;

// Общая политика наполнения; адаптер каждого типа отвечает за физически допустимые точки.
public static class EncounterPlan
{
    public static readonly string[] ForestModifiers = { "keen", "warded", "furious", "swift", "hardy",
        "steadfast", "greedy", "armored", "piercing", "unbreakable" };

    public static int Guards(RewardRules rules, decimal budget, bool resources, bool arena, int seed)
    {
        var band = rules.Band(budget); // Полный бюджет: после 175 плотность не сбрасывается.
        int minimum = Math.Max(resources ? 5 : 2, band.MinGuards);
        int maximum = Math.Max(minimum, band.MaxGuards);
        int count = new Random(seed).Next(minimum, maximum + 1);
        return arena ? 3 * count : count;
    }

    public static string[] ForestSet(int depth) => ForestModifiers.Take(ForestPlan.ModifierCount(depth)).ToArray();

    // Редкая охрана добавляется после основного расчёта, включая утроение арены.
    public static int ResourceReinforcements(bool resources, int seed) =>
        resources ? new Random(unchecked(seed ^ 0x4f1bbcdc)).Next(1, 4) : 0;
}
