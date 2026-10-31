using System.Text.Json;
using DungeonDepthPrototype;

internal static class RadialChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var json = new JsonSerializerOptions { IncludeFields = true };
        var legacy = JsonSerializer.Deserialize<RewardRules>(
            File.ReadAllText("configs/crypt-atmosphere/dungeon-rewards.json"), json)!;
        var rewards = JsonSerializer.Deserialize<RewardRules>(JsonSerializer.Serialize(legacy, json), json)!;
        rewards.Schema = 3; rewards.MaxModifiers = 20; rewards.Revision = "synthetic-radial-check";
        // Синтетические ID/веса проверяют модель. Это не утверждённый набор PoV для поставки.
        var extra = new[] { 8m, 8m, 3m, 4m, 7m, 10m, 2m, 12m, 6m, 5m };
        foreach (int i in Enumerable.Range(0, extra.Length)) rewards.Weights.Add("fixture" + i, extra[i]);
        var type = new RadialTypeRules { Modifiers = EncounterPlan.ForestModifiers
            .Concat(Enumerable.Range(0, extra.Length).Select(i => "fixture" + i)).ToArray() };
        check(type.TargetStart(type.Near, rewards) == 49, "Стартовая целевая длина начинается с 49");
        check(type.TargetStart(type.Middle, rewards) == 61, "Средняя граница вычисляется из весов");
        var near = type.Probe("near", 57, rewards);
        check(near.ForcedProbe, "Явная проба не выдаётся за наружную генерацию");
        for (int d = 0; d < 150; d++)
        {
            check(type.Near.Count(d) == type.Middle.Count(d), "Плавный рост общий для старта и середины");
            if (d <= 16) check(type.Near.Count(d) == 0, "Первые шестнадцать переходов без свойств");
            else check(type.Near.Count(d) == Math.Min(20, (d - 17) / 4 + 1), "Один ID за четыре перехода");
            check(type.Far.Count(d) == Math.Min(20, 6 + d / 2), "Дальний рост и предел двадцать");
            if (d > 0) foreach (var p in new[] { type.Near, type.Middle, type.Far })
                check(p.Count(d) - p.Count(d - 1) is 0 or 1, "Нет скачка на два между соседями");
        }
        for (int n = 0; n <= 20; n++)
        {
            var ids = type.Modifiers.Take(n).ToArray();
            var full = rewards.Budget(ids, 0, false);
            var half = rewards.AtPrice(5).Budget(ids, 0, false);
            check(half.CombatValue == full.CombatValue && half.Value == full.Value * .5m,
                "Цена пять уменьшает только бюджет добычи вдвое");
            if (n <= 10) check(full.Value == legacy.Budget(ids, 0, false).Value,
                "Первые десять сохраняют прежнюю калибровку");
            check(full.Value == rewards.Budget(ids.Reverse(), 0, false).Value, "Наследуемый набор каноничен");
            for (int seed = 0; seed < 40; seed++)
                check(EncounterPlan.Guards(rewards, full.CombatValue, true, true, seed) ==
                    EncounterPlan.Guards(rewards, half.CombatValue, true, true, seed),
                    "Уменьшенная цена добычи не уменьшает охрану арены");
            var loot = RewardModel.Plan(half.Value, rewards, () => throw new Exception("Бросок цвета"));
            check(n == 0 ? loot.ItemCount == 0 && loot.Coins == 0 : loot.ItemCount > 0 || loot.Coins > 0,
                "При любом положительном бюджете добавка гарантирована");
        }
        check(type.Probe("near", 1, rewards).Factors(true) == (8, 2), "Босс старта 8/2");
        check(type.Probe("middle", 1, rewards).Factors(true) == (12, 3), "Средний босс 12/3");
        var far = type.Probe("far", 1, rewards);
        check(far.Factors(true) == (24, 6) && far.Factors(false) == (2, 2),
            "Дальний множитель применяется один раз; помощник не получает множитель босса");
        bool refused = false;
        try { type.Select(0, 0, 1, rewards); } catch (InvalidOperationException) { refused = true; }
        check(refused, "Обычная генерация требует включённого типа и принятых радиусов");
        type.Enabled = type.RadiiAccepted = true; type.NearRadius = 1000; type.MiddleRadius = 3000;
        foreach (var (radius, profile) in new[] { (0d, "near"), (999.99, "near"), (1000, "middle"),
                     (2999.99, "middle"), (3000, "far"), (9000, "far") })
        {
            var s = type.Select(radius, 0, 1, rewards);
            check(s.Profile.Id == profile && s.Radius == radius && !s.ForcedProbe, "Граница кольца по X/Z");
        }
        var diagonal = type.Select(600, 800, 1, rewards);
        check(diagonal.Radius == 1000 && diagonal.Profile.Id == "middle", "Радиус — расстояние наружного входа");
        string saved = JsonSerializer.Serialize(diagonal, json);
        type.NearRadius = 2000; type.Middle.ModifierStep = 7; type.Modifiers[0] = "fixture0";
        var restored = JsonSerializer.Deserialize<RadialSelection>(saved, json)!;
        check(restored.Profile.Id == "middle" && restored.Profile.ModifierStep == 4 &&
            restored.Modifiers[0] == "keen" && restored.MaximumDepth == diagonal.MaximumDepth,
            "Сохранённый выбор не пересчитывается при смене конфига");
        type.Middle.ModifierStep = 4; type.Modifiers[0] = "keen";
        foreach (var profile in new[] { type.Near, type.Middle, type.Far })
        {
            const int samples = 10000;
            int target = 0;
            for (int seed = 0; seed < samples; seed++)
            {
                var selection = type.Probe(profile.Id, seed, rewards);
                check(selection.MaximumDepth >= profile.MinimumDepth &&
                    selection.MaximumDepth <= profile.MaximumDepth, "D внутри профиля");
                if (selection.Category == "target") target++;
                if (profile.TargetBasisPoints > 0)
                {
                    decimal budget = rewards.Budget(selection.AtDepth(selection.MaximumDepth), 0, false).Value;
                    bool reached = RewardModel.Plan(budget, rewards, () => 0).Groups
                        .Any(g => g.Quality >= profile.TargetQuality);
                    check(reached == (selection.Category == "target"), "Категория следует из длины и бюджета");
                }
            }
            if (profile.TargetBasisPoints > 0)
                check(Math.Abs(target / (double)samples - profile.TargetBasisPoints / 10000d) < .015,
                    "Частота категорий на серии seed около целевой доли");
            Console.WriteLine($"Радиальная модель {profile.Id}: seed={samples}, целевая категория={target}");
        }
        var weak = rewards.AtPrice(10);
        weak.Weights = new Dictionary<string, decimal>(rewards.Weights);
        weak.Weights["fixture0"] = weak.Weights["fixture1"] = 1;
        refused = false;
        try { type.Probe("middle", 1, weak); } catch (InvalidOperationException) { refused = true; }
        check(refused, "Недостижимая красная категория явно отклоняется, без подгонки или броска цвета");
    }
}
