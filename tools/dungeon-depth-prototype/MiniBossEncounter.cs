using System;
using System.Linq;

namespace DungeonDepthPrototype;

// Снимок исходной встречи. Длина маршрута и внутренние метры секции не участвуют в боевом профиле.
internal sealed class MiniBossEncounter
{
    public int Schema = 1, Seed, Depth;
    public string Prefab = "", Name = "", RulesRevision = "";
    public string[] Modifiers = Array.Empty<string>(), Helpers = Array.Empty<string>();
    public decimal Budget;
    public RewardPlan Reward = new RewardPlan();
    public bool Trial = false, Archer = false;
    public int TrialIndex = -1;
    public RadialSelection? Radial;
    public decimal CombatBudget;
    public int Empowered;

    internal static MiniBossEncounter Create(int seed, int depth, RewardRules rules, string name = "",
        RadialSelection? radial = null)
    {
        if (depth < 0) throw new ArgumentOutOfRangeException(nameof(depth));
        var random = new Random(seed);
        var pool = new[] { "Skeleton", "Ghost" };
        var loadout = radial?.Loadout(depth, RadialRole.Boss);
        var modifiers = loadout?.Modifiers ?? EncounterPlan.ForestSet(depth);
        int stacks = loadout?.Empowered ?? 0;
        var result = rules.Budget(modifiers, 0, modifiers.Length == 0, stacks);
        decimal budget = result.Value;
        var reward = RewardModel.Plan(budget, rules, random.NextDouble);
        foreach (var group in reward.Groups) group.Size *= 2;
        if (reward.ItemCount > rules.MaxGeneratedItems)
            throw new InvalidOperationException("Удвоенная награда превышает предел профиля");
        reward.Coins = radial?.Schema == 2 ? EmpoweredRules.Coins(radial.Profile.Id, seed) :
            new Random(unchecked(seed ^ 0x27d4eb2d)).Next(300, 901);
        string prefab = pool[random.Next(pool.Length)];
        var helpers = Enumerable.Range(0, MiniBossRules.Helpers(random.Next(100)))
            .Select(_ => pool[random.Next(pool.Length)]).ToArray();
        return new MiniBossEncounter { Schema = radial?.Schema == 2 ? 2 : 1,
            Seed = seed, Depth = depth, Name = name, Prefab = prefab, Empowered = stacks,
            Modifiers = modifiers, Helpers = helpers, Budget = budget, Reward = reward,
            RulesRevision = rules.Revision, Radial = radial, CombatBudget = result.CombatValue };
    }
}
