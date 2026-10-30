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

    internal static MiniBossEncounter Create(int seed, int depth, RewardRules rules, string name = "")
    {
        if (depth < 0) throw new ArgumentOutOfRangeException(nameof(depth));
        var random = new Random(seed);
        var pool = new[] { "Skeleton", "Ghost" };
        var modifiers = EncounterPlan.ForestSet(depth);
        decimal budget = rules.Budget(modifiers, 0, depth <= ForestPlan.SafeDepth).Value;
        var reward = RewardModel.Plan(budget, rules, random.NextDouble);
        foreach (var group in reward.Groups) group.Size *= 2;
        if (reward.ItemCount > rules.MaxGeneratedItems)
            throw new InvalidOperationException("Удвоенная награда превышает предел профиля");
        reward.Coins = new Random(unchecked(seed ^ 0x27d4eb2d)).Next(300, 901);
        string prefab = pool[random.Next(pool.Length)];
        var helpers = Enumerable.Range(0, MiniBossRules.Helpers(random.Next(100)))
            .Select(_ => pool[random.Next(pool.Length)]).ToArray();
        return new MiniBossEncounter { Seed = seed, Depth = depth, Name = name, Prefab = prefab,
            Modifiers = modifiers, Helpers = helpers, Budget = budget, Reward = reward,
            RulesRevision = rules.Revision };
    }
}
