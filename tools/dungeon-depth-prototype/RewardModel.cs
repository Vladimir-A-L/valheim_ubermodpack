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

public sealed class RewardBand
{
    public decimal Upper;
    public int Coins, MinGuards, MaxGuards;
    public RewardQuality Quality;
}

public sealed class RewardRules
{
    public int Schema = 1;
    public string Revision = "forest-provisional-1";
    public Dictionary<string, decimal> Weights = new Dictionary<string, decimal>();
    public SynergyRule[] Synergies = Array.Empty<SynergyRule>();
    public decimal[] QualityKnots = Array.Empty<decimal>();
    public int MaxGeneratedItems = 256, MaxModifiers = 10;
    public decimal ModifierPrice = 10, GrowthDivisor = 200;
    public RewardBand[] Bands = Array.Empty<RewardBand>();

    public void Validate()
    {
        if ((Schema < 1 || Schema > 4) || string.IsNullOrWhiteSpace(Revision) || Weights.Count == 0 ||
            Weights.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value <= 0) ||
            MaxGeneratedItems < 1 || MaxGeneratedItems > 4096 || MaxModifiers < 1 || MaxModifiers > 26)
            throw new ArgumentException("Некорректная глобальная таблица наград");
        if (Schema == 1 && (QualityKnots.Length != 7 || QualityKnots[0] <= 0 ||
            QualityKnots.Zip(QualityKnots.Skip(1), (a, b) => a < b).Any(ok => !ok)))
            throw new ArgumentException("Некорректная прежняя шкала качества");
        if (Schema >= 2 && ((Schema == 2 ? ModifierPrice != 10 :
                ModifierPrice != 10 && ModifierPrice != 5 && !(Schema == 4 && ModifierPrice == 7)) ||
            GrowthDivisor <= 0 || Bands.Length != 8 ||
            Bands.Any(b => b.Upper <= 0 || b.Coins < 0 || b.MinGuards < 2 || b.MaxGuards < b.MinGuards ||
                (b.Coins > 0 ? b.Quality != RewardQuality.Common : b.Quality == RewardQuality.Common)) ||
            Bands.Zip(Bands.Skip(1), (a, b) => a.Upper < b.Upper && a.MinGuards <= b.MinGuards &&
                a.MaxGuards <= b.MaxGuards).Any(ok => !ok) ||
            Bands[7].Quality != RewardQuality.Ancient))
            throw new ArgumentException("Некорректные интервалы наград и охраны");
        if (Synergies.Select(s => s.Id).Distinct().Count() != Synergies.Length ||
            Synergies.Any(s => string.IsNullOrWhiteSpace(s.Id) || s.Factor < 1 ||
                s.Requires.Length < 2 || s.Requires.Distinct().Count() != s.Requires.Length ||
                s.Requires.Any(id => !Weights.ContainsKey(id))))
            throw new ArgumentException("Некорректные правила сочетаний");
    }

    public BudgetResult Budget(IEnumerable<string> modifiers, decimal locationBonus, bool protectedZone,
        int empowered = 0)
    {
        Validate();
        var ids = modifiers.ToArray();
        if (locationBonus < 0 || empowered < 0 || empowered > 1000 || Schema < 4 && empowered != 0 ||
            ids.Length != ids.Distinct().Count() ||
            ids.Length > (Schema < 3 ? 10 : MaxModifiers) ||
            ids.Any(id => !Weights.ContainsKey(id)))
            throw new ArgumentException("Неподдержанный набор охраны или надбавка");
        if (protectedZone && (ids.Length > 0 || empowered > 0))
            throw new ArgumentException("В гарантированной части усиленная охрана запрещена");
        var matches = Synergies.Where(s => s.Requires.All(ids.Contains)).OrderBy(s => s.Id).ToArray();
        decimal sum = ids.Sum(id => Weights[id]);
        decimal factor = matches.Aggregate(1m, (n, s) => n * s.Factor);
        decimal growth = 1;
        if (Schema >= 2)
        {
            // Порядок канонический: десятичное округление не зависит от порядка ID в ZDO.
            growth = ids.OrderBy(id => id, StringComparer.Ordinal)
                .Aggregate(1m, (n, id) => n * (1 + Weights[id] / GrowthDivisor));
            // В schema 3 нормировка всегда по цене 10: цена 5 уменьшает только добычу.
            factor = ids.Length == 0 ? 1 : sum / (ids.Length * 10) * growth * factor;
            sum = ids.Length * ModifierPrice;
        }
        decimal lootFactor = Schema >= 3 ? ModifierPrice / 10 : 1;
        decimal combat = protectedZone ? 0 : locationBonus + (Schema >= 3 ? ids.Length * 10 : sum) * factor;
        return new BudgetResult { Points = sum, Factor = factor,
            Growth = growth, CombatValue = combat, LootFactor = lootFactor,
            Rules = matches.Select(s => s.Id).ToArray(), Empowered = empowered,
            EmpoweredPoints = ModifierPrice * empowered, Value = combat * lootFactor + ModifierPrice * empowered };
    }

    public RewardBand Band(decimal budget)
    {
        Validate();
        if (Schema < 2 || budget < 0) throw new ArgumentException("Нужна интервальная шкала");
        return Bands.FirstOrDefault(b => budget <= b.Upper) ?? Bands[Bands.Length - 1];
    }

    public RewardRules AtPrice(decimal price)
    {
        if (Schema < 3) throw new InvalidOperationException("Радиальные цены требуют schema 3 или 4");
        var copy = (RewardRules)MemberwiseClone();
        copy.ModifierPrice = price;
        copy.Validate();
        return copy;
    }
}

public sealed class BudgetResult
{
    public int Empowered;
    public decimal EmpoweredPoints;
    public decimal Points, Factor, Value, CombatValue;
    public decimal LootFactor = 1;
    public decimal Growth = 1;
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
        if (rules.Schema >= 2) return IntervalPlan(budget, rules);
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

    private static RewardPlan IntervalPlan(decimal budget, RewardRules rules)
    {
        decimal scale = rules.Bands[rules.Bands.Length - 1].Upper;
        decimal completed = decimal.Floor(budget / scale), remainder = budget % scale;
        var band = rules.Band(remainder);
        decimal active = remainder > 0 && band.Coins == 0 ? completed + 1 : 0;
        if (completed * (completed + 1) / 2 + active > rules.MaxGeneratedItems)
            throw new ArgumentException("Награда превышает технический предел профиля");
        var result = new RewardPlan();
        for (int size = 1; size <= (int)completed; size++)
            result.Groups.Add(new RewardGroup { Size = size, Quality = RewardQuality.Ancient });
        if (active > 0) result.Groups.Add(new RewardGroup { Size = (int)active, Quality = band.Quality });
        else if (remainder > 0) result.Coins = band.Coins;
        return result;
    }
}
