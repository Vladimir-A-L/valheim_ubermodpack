using System;
using System.Collections.Generic;
using System.Linq;

namespace DungeonDepthPrototype;

// Common не является значением EpicLoot.ItemRarity; остальные значения сдвинуты на единицу.
public enum RewardQuality { Common, Magic, Rare, Epic, Legendary, Mythic, Ancient }

public sealed class SynergyRule
{
    public string Id = "";
    public string[] Requires = Array.Empty<string>();
    public decimal Factor = 1;
}

public sealed class RewardRules
{
    public int Schema = 1;
    public string Revision = "forest-provisional-1";
    public Dictionary<string, decimal> Weights = new Dictionary<string, decimal>();
    public SynergyRule[] Synergies = Array.Empty<SynergyRule>();
    public decimal[] QualityKnots = Array.Empty<decimal>();
    public int MaxGeneratedItems = 256;

    public void Validate()
    {
        if (Schema != 1 || string.IsNullOrWhiteSpace(Revision) || Weights.Count == 0 ||
            Weights.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value <= 0) ||
            QualityKnots.Length != 7 || QualityKnots[0] <= 0 ||
            QualityKnots.Zip(QualityKnots.Skip(1), (a, b) => a < b).Any(ok => !ok) ||
            MaxGeneratedItems < 1 || MaxGeneratedItems > 4096)
            throw new ArgumentException("Некорректная глобальная таблица наград");
        if (Synergies.Select(s => s.Id).Distinct().Count() != Synergies.Length ||
            Synergies.Any(s => string.IsNullOrWhiteSpace(s.Id) || s.Factor < 1 ||
                s.Requires.Length < 2 || s.Requires.Distinct().Count() != s.Requires.Length ||
                s.Requires.Any(id => !Weights.ContainsKey(id))))
            throw new ArgumentException("Некорректные правила сочетаний");
    }

    public BudgetResult Budget(IEnumerable<string> modifiers, decimal locationBonus, bool protectedZone)
    {
        Validate();
        var ids = modifiers.ToArray();
        if (locationBonus < 0 || ids.Length != ids.Distinct().Count() || ids.Length > 10 ||
            ids.Any(id => !Weights.ContainsKey(id)))
            throw new ArgumentException("Неподдержанный набор охраны или надбавка");
        if (protectedZone && ids.Length > 0)
            throw new ArgumentException("В гарантированной части усиленная охрана запрещена");
        var matches = Synergies.Where(s => s.Requires.All(ids.Contains)).OrderBy(s => s.Id).ToArray();
        decimal sum = ids.Sum(id => Weights[id]);
        decimal factor = matches.Aggregate(1m, (n, s) => n * s.Factor);
        return new BudgetResult { Points = sum, Factor = factor,
            Rules = matches.Select(s => s.Id).ToArray(), Value = protectedZone ? 0 : locationBonus + sum * factor };
    }
}

public sealed class BudgetResult
{
    public decimal Points, Factor, Value;
    public string[] Rules = Array.Empty<string>();
}

public sealed class RewardGroup
{
    public int Size;
    public RewardQuality Quality;
}

public sealed class RewardPlan
{
    public List<RewardGroup> Groups = new List<RewardGroup>();
    public int Coins;
    public int ItemCount => Groups.Sum(g => g.Size);
}

public static class RewardModel
{
    public static decimal[] Distribution(decimal score, decimal[] knots)
    {
        var result = new decimal[7];
        if (knots.Length != 7 || score < knots[0] || score > knots[6])
            throw new ArgumentOutOfRangeException(nameof(score));
        for (int i = 0; i < 6; i++)
        {
            if (score > knots[i + 1]) continue;
            decimal upper = (score - knots[i]) / (knots[i + 1] - knots[i]);
            result[i] = 1 - upper;
            result[i + 1] = upper;
            return result;
        }
        throw new ArgumentOutOfRangeException(nameof(score));
    }

    public static RewardPlan Plan(decimal budget, RewardRules rules, Func<double> next)
    {
        rules.Validate();
        if (budget < 0) throw new ArgumentOutOfRangeException(nameof(budget));
        decimal scale = rules.QualityKnots[6];
        decimal n = decimal.Floor(budget / scale);
        decimal remainder = budget % scale;
        decimal active = remainder >= rules.QualityKnots[0] ? n + 1 : 0;
        // Отказ до любых записей/выдачи, а не молчаливое усечение дорогой награды.
        if (n * (n + 1) / 2 + active > rules.MaxGeneratedItems)
            throw new ArgumentException("Награда превышает технический предел профиля");
        var result = new RewardPlan();
        for (int size = 1; size <= (int)n; size++)
            result.Groups.Add(new RewardGroup { Size = size, Quality = RewardQuality.Ancient });
        if (active > 0)
        {
            double draw = next();
            if (double.IsNaN(draw) || draw < 0 || draw >= 1) throw new ArgumentException("Неверный бросок");
            var probabilities = Distribution(remainder, rules.QualityKnots);
            decimal cumulative = 0;
            for (int i = 0; i < probabilities.Length; i++)
            {
                cumulative += probabilities[i];
                if ((decimal)draw >= cumulative) continue;
                result.Groups.Add(new RewardGroup { Size = (int)active, Quality = (RewardQuality)i });
                break;
            }
        }
        else if (remainder > 0) result.Coins = 50;
        return result;
    }
}
