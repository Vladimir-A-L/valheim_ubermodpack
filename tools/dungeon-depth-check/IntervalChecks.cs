using DungeonDepthPrototype;

internal static class IntervalChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var rules = System.Text.Json.JsonSerializer.Deserialize<RewardRules>(
            File.ReadAllText("configs/dungeon-rewards.json"),
            new System.Text.Json.JsonSerializerOptions { IncludeFields = true })!;
        rules.Validate();
        RewardPlan Plan(decimal b) => RewardModel.Plan(b, rules, () => throw new Exception("Бросок не нужен"));
        check(Plan(0).ItemCount == 0 && Plan(0).Coins == 0, "Ноль без добавки");
        for (int n = 0; n < 10; n++)
        {
            decimal start = 175 * n;
            int completed = n * (n + 1) / 2;
            decimal lower = 0;
            foreach (var band in rules.Bands)
            {
                foreach (decimal b in new[] { lower + .0001m, band.Upper })
                {
                    var p = Plan(start + b);
                    check(p.Coins == band.Coins, "Граница суммы монет включительна сверху");
                    check(p.ItemCount == completed + (band.Coins > 0 ? 0 : n + 1), "Размер следующей группы");
                    if (band.Coins == 0) check(p.Groups.Last().Quality == band.Quality, "Точный цвет интервала");
                }
                lower = band.Upper;
            }
        }
        check(Plan(175).ItemCount == 1 && Plan(350).ItemCount == 3 && Plan(525).ItemCount == 6,
            "Треугольный рост завершённых групп");
        check(Plan(176).Coins == 50 && Plan(186).Coins == 100 && Plan(195).ItemCount == 1 &&
            Plan(195.001m).ItemCount == 3, "Переполнение не умножает золото и не создаёт обычных вещей");
        var ids = rules.Weights.Keys.ToArray();
        for (int mask = 0; mask < (1 << ids.Length); mask++)
        {
            var chosen = ids.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
            var b = rules.Budget(chosen, 0, false);
            check(b.Points == chosen.Length * 10 && b.Value == b.Points * b.Factor, "Формула 10*n*k");
            check(b.Value == rules.Budget(chosen.Reverse(), 0, false).Value, "Порядок свойств не влияет на k");
            foreach (var id in ids.Except(chosen))
                check(rules.Budget(chosen.Append(id), 0, false).Value >= b.Value, "Сила набора монотонна");
        }
        foreach (var band in rules.Bands)
        for (int seed = 0; seed < 100; seed++)
        {
            int normal = EncounterPlan.Guards(rules, band.Upper, false, false, seed);
            int resource = EncounterPlan.Guards(rules, band.Upper, true, false, seed);
            int arena = EncounterPlan.Guards(rules, band.Upper, true, true, seed);
            check(normal >= band.MinGuards && normal <= band.MaxGuards, "Плотность по интервалу");
            check(resource >= 5 && resource <= Math.Max(5, band.MaxGuards), "Минимум пять у ресурсов");
            check(arena == 3 * resource, "Арена втрое больше обычной комнаты этой ступени");
            check(EncounterPlan.Guards(rules, 176, false, false, seed) ==
                EncounterPlan.Guards(rules, 175, false, false, seed), "После шкалы плотность не сбрасывается");
        }
    }
}
