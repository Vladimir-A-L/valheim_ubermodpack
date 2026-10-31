using System.Text.Json;
using DungeonDepthPrototype;

internal static class EmpoweredChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var options = new JsonSerializerOptions { IncludeFields = true };
        using var document = JsonDocument.Parse(File.ReadAllText("configs/radial-empowered/radial-dungeons.json"));
        var root = document.RootElement;
        var type = root.GetProperty("Types")[0].Deserialize<RadialTypeRules>(options)!;
        var rewards = root.GetProperty("Rewards").Deserialize<RewardRules>(options)!;
        type.Validate(rewards, false);
        check(!type.Enabled && type.OrdinaryModifiers.SequenceEqual(EmpoweredRules.OrdinaryModifiers),
            "Accepted role order is explicit; ordinary generation stays disabled");
        check(type.TargetStart(type.Near, rewards) == 49 && type.TargetStart(type.Middle, rewards) == 61,
            "Same Near/Middle length category boundaries without a colour roll");
        type.Enabled = true;
        foreach (var (radius, profile) in new[] { (1999.999, "near"), (2000d, "middle"),
                     (4499.999, "middle"), (4500d, "far") })
            check(type.Select(radius, 0, 17, rewards).Profile.Id == profile, "Accepted radial boundaries");
        var far = type.Probe("far", 17, rewards);
        check(far.Schema == 2 && far.OrdinaryModifiers.Length == 13 && far.Modifiers.Length == 20,
            "New snapshot stores both role pools");
        foreach (var role in Enum.GetValues<RadialRole>())
        {
            int first = role == RadialRole.Guard ? 16 : 30;
            check(far.Loadout(first - 1, role).Empowered == 0, "No stack at the last unique slot");
            check(far.Loadout(first, role).Empowered == 1, "First stack at the role boundary");
            check(far.Loadout(first + 2, role).Empowered == 2, "Second stack follows the profile step");
            check(far.Loadout(40, role).Empowered == (role == RadialRole.Guard ? 13 : 6),
                "Maximum Far stack count is calculated before the old cap");
        }
        check(Math.Abs(EmpoweredRules.Health(13) - 2.95f) < .00001f &&
            Math.Abs(EmpoweredRules.Damage(13) - 2.3f) < .00001f, "Arithmetic growth, no exponentiation");
        foreach (var name in new[] { "near", "middle" })
        {
            var selection = type.Probe(name, 17, rewards);
            for (int depth = 0; depth <= 16; depth++)
                foreach (var role in Enum.GetValues<RadialRole>())
                    check(selection.Loadout(depth, role).Requested == 0, "Safe first sixteen transitions");
            check(selection.Loadout(17).Requested == 1, "First non-safe transition");
            check(selection.Loadout(selection.Profile.MaximumDepth).Empowered == 0,
                "Current Near/Middle lengths do not exhaust thirteen modifiers");
        }
        var priced = rewards.AtPrice(7);
        foreach (int depth in Enumerable.Range(0, 41))
        {
            var loadout = far.Loadout(depth);
            var budget = priced.Budget(loadout.Modifiers, 0, false, loadout.Empowered);
            var ten = rewards.AtPrice(10).Budget(loadout.Modifiers, 0, false, loadout.Empowered);
            check(budget.Value == budget.CombatValue * .7m + 7 * loadout.Empowered,
                "Each stack adds seven points exactly once");
            check(ten.Value == ten.CombatValue + 10 * loadout.Empowered,
                "Near/Middle stack price remains ten");
            check(budget.CombatValue == ten.CombatValue, "Loot price does not change density budget");
            for (int seed = 0; seed < 20; seed++)
                check(EncounterPlan.Guards(priced, budget.CombatValue, true, true, seed) ==
                    EncounterPlan.Guards(rewards, ten.CombatValue, true, true, seed),
                    "Changing seven to ten cannot change the resource arena population");
            RewardModel.Plan(budget.Value, priced, () => throw new Exception("Unexpected colour draw"));
        }
        decimal middle = rewards.Budget(type.Probe("middle", 17, rewards).AtDepth(64), 0, false).Value;
        check(Math.Abs(middle - 271.9431516352539618384383640m) < .000000001m,
            "Bound before Headsman preserves Ancient reachability at the accepted weights");
        foreach (var (name, minimum, maximum) in new[] { ("near", 50, 200), ("middle", 150, 500),
                     ("far", 300, 900) })
        {
            var selection = type.Probe(name, 17, rewards);
            var seen = new HashSet<int>();
            for (int seed = 0; seed < 10000; seed++)
            {
                int coins = EmpoweredRules.Coins(name, seed); seen.Add(coins);
                check(coins >= minimum && coins <= maximum, "Inclusive boss coin interval");
                check(coins == EmpoweredRules.Coins(name, seed), "Boss coin roll is deterministic");
            }
            check(seen.Min() == minimum && seen.Max() == maximum, "Both coin endpoints are reachable");
            int depth = name == "far" ? 30 : 40;
            var encounter = MiniBossEncounter.Create(73, depth, rewards.AtPrice(selection.Profile.ModifierPrice),
                radial: selection);
            check(encounter.Depth == depth &&
                encounter.Empowered == selection.Loadout(depth, RadialRole.Boss).Empowered,
                "Boss uses encounter depth, not the dungeon's maximum D");
            var saved = JsonSerializer.Deserialize<MiniBossEncounter>(
                JsonSerializer.Serialize(encounter, options), options)!;
            check(saved.Reward.Coins == encounter.Reward.Coins && saved.Empowered == encounter.Empowered &&
                saved.Modifiers.SequenceEqual(encounter.Modifiers), "Encounter snapshot preserves coins and stacks");
        }
        var legacy = new RadialSelection { Profile = type.Far, Modifiers = type.Modifiers };
        check(legacy.Loadout(40).Modifiers.Length == 20 && legacy.Loadout(40).Empowered == 0,
            "Old selection remains capped at twenty and receives no stacks");
        var oldEncounter = MiniBossEncounter.Create(73, 40, rewards, radial: null);
        check(oldEncounter.Schema == 1 && oldEncounter.Empowered == 0 &&
            oldEncounter.Reward.Coins == new Random(unchecked(73 ^ 0x27d4eb2d)).Next(300, 901),
            "Non-radial legacy encounter retains its old coin roll");
    }
}
