using System.Text.Json;
using DungeonDepthPrototype;

internal static class RadialConfigChecks
{
    internal static void Run(Action<bool, string> check)
    {
        for (int x = -4; x <= 4; x++)
        for (int z = -4; z <= 4; z++)
        for (int dx = -2; dx <= 2; dx++)
        for (int dz = -2; dz <= 2; dz++)
        {
            if (dx == 0 && dz == 0) continue;
            int vertical = Math.Abs(RadialAllocation.Altitude(x, z) - RadialAllocation.Altitude(x + dx, z + dz));
            check(Math.Abs(dx) * RadialAllocation.ZoneSize >= RadialAllocation.Width ||
                Math.Abs(dz) * RadialAllocation.ZoneSize >= RadialAllocation.Width ||
                vertical >= RadialAllocation.Height, "Соседние радиальные объёмы не пересекаются");
        }
        var options = new JsonSerializerOptions { IncludeFields = true };
        using var document = JsonDocument.Parse(File.ReadAllText("configs/radial-dungeons/radial-dungeons.json"));
        var root = document.RootElement;
        var rewards = root.GetProperty("Rewards").Deserialize<RewardRules>(options)!;
        var type = root.GetProperty("Types")[0].Deserialize<RadialTypeRules>(options)!;
        type.Validate(rewards, false);
        check(!type.Enabled,
            "Обычная генерация в поставляемом конфиге закрыта до приёмки");
        check(type.RadiiAccepted && root.GetProperty("Fallback").GetString() == "vanilla",
            "Радиусы и ванильный запасной вариант согласованы отдельно от включения");
        type.Enabled = true;
        foreach (var (radius, expected) in new[] { (0d, "near"), (1999.99, "near"), (2000d, "middle"),
                     (4000d, "middle"), (4499.99, "middle"), (4500d, "far") })
            check(type.Select(-radius, 0, 13001, rewards).Profile.Id == expected,
                "Рабочие границы 2000/4500 м: точная граница относится к следующему кольцу");
        type.Enabled = false;
        check(type.Modifiers.Take(10).SequenceEqual(EncounterPlan.ForestModifiers),
            "Первые десять ID сохраняют порядок");
        check(type.Modifiers.Length == 20 && type.Modifiers.Distinct().Count() == 20,
            "Двадцать уникальных свойств");
        check(type.Modifiers.Intersect(new[] { "burning", "chilling", "shocking", "venomous" }).Count() == 1,
            "Взаимоисключающие стихийные механизмы не считаются разными действующими свойствами");
        var legacy = JsonSerializer.Deserialize<RewardRules>(
            File.ReadAllText("configs/guard-recovery/dungeon-rewards.json"), options)!;
        for (int n = 0; n <= 10; n++)
            check(rewards.Budget(type.Modifiers.Take(n), 0, false).Value ==
                legacy.Budget(type.Modifiers.Take(n), 0, false).Value, "Старая калибровка сохранена");
        check(type.TargetStart(type.Near, rewards) == 49 && type.TargetStart(type.Middle, rewards) == 61,
            "Реальная таблица сохраняет границы 49 и 61");
        var far = rewards.AtPrice(5);
        var ancientDepths = Enumerable.Range(20, 21).Where(d => RewardModel.Plan(
            far.Budget(type.Modifiers.Take(type.Far.Count(d)), 0, false).Value, far, () => 0)
            .Groups.Any(g => g.Quality == RewardQuality.Ancient)).ToArray();
        check(ancientDepths.SequenceEqual(Enumerable.Range(28, 13)), "Красная дальняя ступень начинается с D=28");
        foreach (var profile in new[] { type.Near, type.Middle, type.Far })
        foreach (int d in Enumerable.Range(profile.MinimumDepth, profile.MaximumDepth - profile.MinimumDepth + 1))
        {
            var priced = rewards.AtPrice(profile.ModifierPrice);
            var budget = priced.Budget(type.Modifiers.Take(profile.Count(d)), 0, false);
            var plan = RewardModel.Plan(budget.Value, priced, () => throw new Exception("Скрытый бросок цвета"));
            check(budget.Value > 0 && (plan.Coins > 0 || plan.ItemCount > 0), "Конечная награда непустая");
            if (profile.Id == "near") check(plan.Groups.All(g => g.Quality <= RewardQuality.Legendary),
                "Начальный профиль не превышает Legendary");
        }
        Console.WriteLine("Рабочие радиальные бюджеты: " + string.Join("; ", new[] { 9, 10, 12, 16, 19, 20 }
            .Select(n => $"n={n}: combat={rewards.Budget(type.Modifiers.Take(n), 0, false).Value:F6}, " +
                $"far={far.Budget(type.Modifiers.Take(n), 0, false).Value:F6}")));
    }
}
