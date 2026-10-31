using System;
using System.Collections.Generic;
using System.Linq;

namespace DungeonDepthPrototype;

// Чистый выбор до поиска геометрии. Сохранённый снимок не обращается к текущему конфигу.
public sealed class RadialProfile
{
    public string Id = "";
    public int MinimumDepth, MaximumDepth, SafeDepth = 16, InitialModifiers, ModifierStep = 4, ModifierLimit = 20;
    public int TargetBasisPoints;
    public RewardQuality TargetQuality;
    public decimal ModifierPrice = 10;
    public float GuardHealth = 1, GuardDamage = 1, BossHealth = 12, BossDamage = 3;

    public int Count(int depth)
    {
        if (depth < 0) throw new ArgumentOutOfRangeException(nameof(depth));
        return Math.Min(ModifierLimit, InitialModifiers > 0 ? InitialModifiers + depth / ModifierStep :
            depth <= SafeDepth ? 0 : (depth - SafeDepth + ModifierStep - 1) / ModifierStep);
    }

    public void Validate(int available)
    {
        if (string.IsNullOrWhiteSpace(Id) || MinimumDepth < 20 || MaximumDepth < MinimumDepth ||
            MaximumDepth > 160 || SafeDepth < 0 || InitialModifiers < 0 || ModifierStep < 1 ||
            ModifierLimit < 1 || ModifierLimit > available || InitialModifiers > ModifierLimit ||
            TargetBasisPoints < 0 || TargetBasisPoints > 10000 ||
            (ModifierPrice != 5 && ModifierPrice != 10) ||
            new[] { GuardHealth, GuardDamage, BossHealth, BossDamage }.Any(f =>
                float.IsNaN(f) || float.IsInfinity(f) || f <= 0))
            throw new ArgumentException("Некорректный профиль радиального данжа");
    }

    internal RadialProfile Copy() => (RadialProfile)MemberwiseClone();
}

public sealed class RadialTypeRules
{
    public string Type = "ForestCrypt", Revision = "forest-radial-1";
    public bool Enabled, RadiiAccepted;
    public double NearRadius, MiddleRadius;
    public string[] Modifiers = Array.Empty<string>();
    public RadialProfile Near = new RadialProfile { Id = "near", MinimumDepth = 26, MaximumDepth = 52,
        TargetBasisPoints = 3000, TargetQuality = RewardQuality.Legendary, BossHealth = 8, BossDamage = 2 };
    public RadialProfile Middle = new RadialProfile { Id = "middle", MinimumDepth = 40, MaximumDepth = 64,
        TargetBasisPoints = 2500, TargetQuality = RewardQuality.Ancient };
    public RadialProfile Far = new RadialProfile { Id = "far", MinimumDepth = 20, MaximumDepth = 40,
        SafeDepth = 0, InitialModifiers = 6, ModifierStep = 2, ModifierPrice = 5,
        GuardHealth = 2, GuardDamage = 2, BossHealth = 24, BossDamage = 6 };

    public void Validate(RewardRules rewards, bool outside)
    {
        rewards.Validate();
        if (string.IsNullOrWhiteSpace(Type) || string.IsNullOrWhiteSpace(Revision) || rewards.Schema != 3 ||
            Modifiers.Length != Modifiers.Distinct().Count() || Modifiers.Length > rewards.MaxModifiers ||
            Modifiers.Count(id => new[] { "burning", "chilling", "shocking", "venomous" }.Contains(id)) > 1 ||
            Modifiers.Any(id => !rewards.Weights.ContainsKey(id)))
            throw new ArgumentException("Неполные или повторяющиеся ID радиального типа");
        foreach (var p in new[] { Near, Middle, Far }) p.Validate(Modifiers.Length);
        if (outside && (!Enabled || !RadiiAccepted || double.IsNaN(NearRadius) ||
            double.IsNaN(MiddleRadius) || double.IsInfinity(MiddleRadius) ||
            NearRadius <= 0 || MiddleRadius <= NearRadius))
            throw new InvalidOperationException("Радиусы типа не приняты или его генерация выключена");
    }

    public RadialSelection Select(double outsideX, double outsideZ, int seed, RewardRules rewards)
    {
        Validate(rewards, true);
        double radius = Radius(outsideX, outsideZ);
        return Choose(radius < NearRadius ? Near : radius < MiddleRadius ? Middle : Far,
            outsideX, outsideZ, seed, rewards, false);
    }

    public RadialSelection Probe(string profile, int seed, RewardRules rewards)
    {
        Validate(rewards, false);
        var selected = new[] { Near, Middle, Far }.Single(p => p.Id == profile);
        return Choose(selected, 0, 0, seed, rewards, true);
    }

    public RadialSelection ProbeEntrance(double x, double z, int seed, RewardRules rewards)
    {
        Validate(rewards, false);
        if (double.IsNaN(NearRadius) || double.IsNaN(MiddleRadius) || double.IsInfinity(MiddleRadius) ||
            NearRadius <= 0 || MiddleRadius <= NearRadius)
            throw new InvalidOperationException("Не заданы пробные радиусы");
        double radius = Radius(x, z);
        return Choose(radius < NearRadius ? Near : radius < MiddleRadius ? Middle : Far,
            x, z, seed, rewards, true);
    }

    private static double Radius(double x, double z)
    {
        double radius = Math.Sqrt(x * x + z * z);
        if (double.IsNaN(radius) || double.IsInfinity(radius))
            throw new ArgumentException("Некорректная наружная точка");
        return radius;
    }

    public int TargetStart(RadialProfile profile, RewardRules rewards)
    {
        if (profile.TargetBasisPoints == 0) return -1;
        var priced = rewards.AtPrice(profile.ModifierPrice);
        foreach (int depth in Enumerable.Range(profile.MinimumDepth,
                     profile.MaximumDepth - profile.MinimumDepth + 1))
        {
            decimal budget = priced.Budget(Modifiers.Take(profile.Count(depth)), 0, false).Value;
            var reward = RewardModel.Plan(budget, priced, () => 0);
            if (reward.Groups.Any(g => g.Quality >= profile.TargetQuality)) return depth;
        }
        throw new InvalidOperationException("Недостижимая категория " + profile.Id +
            ": D=" + profile.MaximumDepth + ", свойств=" + profile.Count(profile.MaximumDepth) +
            ", B_loot=" + priced.Budget(Modifiers.Take(profile.Count(profile.MaximumDepth)), 0, false).Value);
    }

    private RadialSelection Choose(RadialProfile p, double x, double z, int seed, RewardRules rewards, bool forced)
    {
        var random = new RadialRandom(seed, Type + "/" + p.Id);
        int targetStart = TargetStart(p, rewards), low = p.MinimumDepth, high = p.MaximumDepth;
        string category = "all";
        if (p.TargetBasisPoints > 0)
        {
            if (targetStart <= low && p.TargetBasisPoints < 10000)
                throw new InvalidOperationException("Нет длин ниже целевой ступени: " + p.Id);
            bool target = random.Next(10000) < p.TargetBasisPoints;
            category = target ? "target" : "ordinary";
            if (target) low = targetStart; else high = targetStart - 1;
        }
        return new RadialSelection { Type = Type, Revision = Revision, Seed = seed, OutsideX = x, OutsideZ = z,
            Radius = Radius(x, z), Category = category, MaximumDepth = low + random.Next(high - low + 1),
            Profile = p.Copy(), Modifiers = (string[])Modifiers.Clone(), ForcedProbe = forced,
            RewardRevision = rewards.Revision };
    }
}

public sealed class RadialSelection
{
    public int Schema = 1, Seed, MaximumDepth;
    public string Type = "", Revision = "", RewardRevision = "", Category = "";
    public double OutsideX, OutsideZ, Radius;
    public bool ForcedProbe;
    public RadialProfile Profile = new RadialProfile();
    public string[] Modifiers = Array.Empty<string>();
    public string[] AtDepth(int depth) => Modifiers.Take(Profile.Count(depth)).ToArray();
    public (float Health, float Damage) Factors(bool boss) => boss ?
        (Profile.BossHealth, Profile.BossDamage) : (Profile.GuardHealth, Profile.GuardDamage);
}

// Одинаковая последовательность в CLR модели и Mono игры; не зависит от string.GetHashCode.
internal sealed class RadialRandom
{
    private uint state;
    internal RadialRandom(int seed, string type)
    {
        state = unchecked((uint)seed) ^ 2166136261u;
        foreach (char c in type) state = unchecked((state ^ c) * 16777619u);
    }
    private uint Draw()
    {
        state = unchecked(state + 0x9e3779b9u);
        uint z = state;
        z = unchecked((z ^ (z >> 16)) * 0x21f0aaadu);
        z = unchecked((z ^ (z >> 15)) * 0x735a2d97u);
        return z ^ (z >> 15);
    }
    internal int Next(int maximum)
    {
        if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum));
        uint limit = unchecked(0u - (uint)maximum) % (uint)maximum, draw;
        do { draw = Draw(); } while (draw < limit);
        return (int)(draw % (uint)maximum);
    }
}
