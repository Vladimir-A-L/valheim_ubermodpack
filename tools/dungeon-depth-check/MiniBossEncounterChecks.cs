using DungeonDepthPrototype;

internal static class MiniBossEncounterChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var rules = System.Text.Json.JsonSerializer.Deserialize<RewardRules>(
            File.ReadAllText("configs/dungeon-rewards.json"),
            new System.Text.Json.JsonSerializerOptions { IncludeFields = true })!;
        foreach (int d in new[] { 0, 16, 17, 20, 21, 25, 26, 35, 36, 40, 41, 59, 60, 64, 80 })
        {
            var encounter = MiniBossEncounter.Create(13001, d, rules);
            var budget = rules.Budget(EncounterPlan.ForestSet(d), 0, d <= 16).Value;
            var chest = RewardModel.Plan(budget, rules, () => 0);
            check(encounter.Modifiers.SequenceEqual(EncounterPlan.ForestSet(d)) && encounter.Depth == d,
                "Боевой профиль обычной охраны до перехода в секцию");
            check(encounter.Budget == budget && encounter.Reward.ItemCount == chest.ItemCount * 2,
                "Удвоено количество предметов, а не бюджет или качество");
            check(encounter.Reward.Groups.Select(g => g.Quality).SequenceEqual(chest.Groups.Select(g => g.Quality)),
                "Качество бюджетного сундука сохраняется");
            check(encounter.Reward.Coins is >= 300 and <= 900 && !encounter.Trial && !encounter.Archer,
                "Обычная встреча не наследует принудительную пробу и множитель ресурсов");
            check(d > 16 || encounter.Reward.ItemCount == 0, "Нулевая глубина не получает фиксированный Mythic");
        }
        int skeletons = 0, helpers = 0;
        for (int seed = 0; seed < 10000; seed++)
        {
            var encounter = MiniBossEncounter.Create(seed, 40, rules);
            if (encounter.Prefab == "Skeleton") skeletons++;
            helpers += encounter.Helpers.Length;
            check(encounter.Helpers.Length is 0 or 1 && encounter.Helpers.All(p => p is "Skeleton" or "Ghost"),
                "Допустимый пул помощников");
        }
        check(skeletons is > 4800 and < 5200 && helpers is > 1350 and < 1650,
            "Равные веса видов и 15 процентов помощников");
    }
}
