using System;
using System.Linq;

namespace DungeonDepthPrototype;

// Собственная модель выбора качества одного предмета, не копия генератора PoV.
public static class RewardProjection
{
    public static double OpportunityFactor(int modifiers, double rewardPerModifier, double starScale,
        double baseQuantity = 1)
    {
        if (modifiers < 0 || !Finite(rewardPerModifier) || rewardPerModifier < 0 ||
            !Finite(starScale) || starScale < 0 || starScale > 1 || !Finite(baseQuantity) || baseQuantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(modifiers));
        return 1 + modifiers * rewardPerModifier * starScale / baseQuantity;
    }

    // Для целого k это распределение лучшего из k независимых вариантов; дробное k — интерполяция CDF.
    public static double[] BestOf(double[] weights, double opportunities)
    {
        if (weights.Length == 0 || weights.Any(w => !Finite(w) || w < 0) ||
            !Finite(weights.Sum()) || weights.Sum() <= 0 || !Finite(opportunities) || opportunities < 1)
            throw new ArgumentException("Некорректное распределение или число возможностей");
        double total = weights.Sum(), cumulative = 0, previous = 0;
        var result = new double[weights.Length];
        for (int i = 0; i < weights.Length; i++)
        {
            cumulative += weights[i] / total;
            double next = Math.Pow(Math.Min(1, cumulative), opportunities);
            result[i] = Math.Max(0, next - previous);
            previous = next;
        }
        return result;
    }

    public static int Roll(double[] distribution, double uniform)
    {
        if (!Finite(uniform) || uniform < 0 || uniform >= 1) throw new ArgumentOutOfRangeException(nameof(uniform));
        // Одновременно проверяем и нормализуем входное распределение.
        var normalized = BestOf(distribution, 1);
        double cumulative = 0;
        for (int i = 0; i < normalized.Length; i++)
        {
            cumulative += normalized[i];
            if (uniform < cumulative) return i;
        }
        for (int i = normalized.Length - 1; i >= 0; i--)
            if (normalized[i] > 0) return i;
        throw new InvalidOperationException("Пустое распределение");
    }

    public static double[] WithMinimum(double[] distribution, int minimum)
    {
        var result = BestOf(distribution, 1);
        if (minimum < 0 || minimum >= result.Length) throw new ArgumentOutOfRangeException(nameof(minimum));
        for (int i = 0; i < minimum; i++)
        {
            result[minimum] += result[i];
            result[i] = 0;
        }
        return result;
    }

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
