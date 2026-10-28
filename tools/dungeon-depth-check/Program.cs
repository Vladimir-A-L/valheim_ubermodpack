using DungeonDepthPrototype;

int checks = 0;
void Check(bool ok, string name)
{
    if (!ok) throw new Exception(name);
    checks++;
}
void Refuses(Action action, string name)
{
    try { action(); }
    catch (ArgumentException) { checks++; return; }
    throw new Exception(name);
}

double[] input = { 80, 15, 5, 0 };
var unchanged = RewardProjection.BestOf(input, 1);
Check(unchanged.Zip(input).All(p => Math.Abs(p.First - p.Second / 100) < 1e-12), "Baseline");
// Точные вероятности максимума двух независимых категориальных бросков.
double[] pairExpected = { 0.64, 0.2625, 0.0975, 0 };
var pair = RewardProjection.BestOf(input, 2);
Check(pair.Zip(pairExpected).All(p => Math.Abs(p.First - p.Second) < 1e-12), "Two draws");
Check(RewardProjection.OpportunityFactor(10, 0.5, 1) == 6, "Normal PoV factor");
Check(RewardProjection.OpportunityFactor(10, 0.5, 0.4, 2) == 2, "Normalized PoV factor");
var floored = RewardProjection.WithMinimum(input, 1);
Check(floored[0] == 0 && Math.Abs(floored[1] - 0.95) < 1e-12 && Math.Abs(floored[2] - 0.05) < 1e-12,
    "Minimum quality preserves upper rarity probability and total mass");
Check(RewardProjection.BestOf(new double[] { 19, 3 }, 4.5)[1] < 0.5 &&
    RewardProjection.BestOf(new double[] { 19, 3 }, 5)[1] > 0.5, "First rare floor at eight default modifiers");
double previousRare = 0;
for (int modifiers = 0; modifiers <= 26; modifiers++)
{
    var distribution = RewardProjection.BestOf(input, RewardProjection.OpportunityFactor(modifiers, 0.5, 1));
    Check(Math.Abs(distribution.Sum() - 1) < 1e-12, "Normalization");
    Check(distribution.All(p => p >= 0) && distribution[3] == 0, "Support preservation");
    Check(distribution[2] >= previousRare, "Monotone rarity");
    previousRare = distribution[2];
}
Check(RewardProjection.Roll(new double[] { 0, 0, 1, 0 }, 0) == 2, "Leading zero weights");
Check(RewardProjection.Roll(new double[] { 0, 0, 1, 0 }, 0.9999999) == 2, "Trailing zero weights");
Refuses(() => RewardProjection.BestOf(new double[] { 0, 0 }, 1), "Empty mass");
Refuses(() => RewardProjection.BestOf(new double[] { -1, 2 }, 1), "Negative weight");
Refuses(() => RewardProjection.BestOf(new double[] { double.NaN }, 1), "NaN weight");
Refuses(() => RewardProjection.BestOf(input, double.PositiveInfinity), "Infinite opportunities");
Refuses(() => RewardProjection.OpportunityFactor(-1, 0.5, 1), "Negative modifiers");
Refuses(() => RewardProjection.OpportunityFactor(10, 0.5, 2), "Invalid normalization");
Refuses(() => RewardProjection.Roll(input, 1), "Upper RNG boundary");
Refuses(() => RewardProjection.WithMinimum(input, 4), "Invalid minimum quality");
Console.WriteLine($"Проверки распределения наград: {checks} OK");
