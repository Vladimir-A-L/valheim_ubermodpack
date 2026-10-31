using System;
using System.Linq;

namespace DungeonDepthPrototype;

public enum RadialRole { Guard, Boss, Helper }

public sealed class RadialLoadout
{
    public string[] Modifiers = Array.Empty<string>();
    public int Requested, Empowered;
}

// Число стаков отделено от маски PoV. Ни цена добычи, ни число стаков не добавляют охранников.
public static class EmpoweredRules
{
    public static readonly string[] OrdinaryModifiers = { "keen", "warded", "furious", "swift", "hardy",
        "steadfast", "greedy", "armored", "piercing", "unbreakable", "burning", "bound", "headsman" };

    public static RadialLoadout Allocate(RadialProfile profile, int depth, string[] pool)
    {
        if (depth < 0 || pool.Length == 0 || pool.Distinct().Count() != pool.Length || profile.ModifierStep < 1)
            throw new ArgumentException("Нужны глубина и непустой уникальный набор роли");
        int raw = profile.InitialModifiers > 0 ? checked(profile.InitialModifiers + depth / profile.ModifierStep) :
            depth <= profile.SafeDepth ? 0 : checked((depth - profile.SafeDepth + profile.ModifierStep - 1) /
                profile.ModifierStep);
        return new RadialLoadout { Requested = raw, Modifiers = pool.Take(raw).ToArray(),
            Empowered = Math.Max(0, raw - pool.Length) };
    }

    public static float Health(int stacks) => Factor(stacks, .15f);
    public static float Damage(int stacks) => Factor(stacks, .10f);
    private static float Factor(int stacks, float step)
    {
        if (stacks < 0 || stacks > 1000) throw new ArgumentOutOfRangeException(nameof(stacks));
        return 1 + step * stacks;
    }

    public static int Coins(string profile, int seed)
    {
        var limits = profile switch { "near" => (50, 201), "middle" => (150, 501), "far" => (300, 901),
            _ => throw new ArgumentException("Неизвестный профиль монет босса") };
        return new Random(unchecked(seed ^ 0x27d4eb2d)).Next(limits.Item1, limits.Item2);
    }
}
