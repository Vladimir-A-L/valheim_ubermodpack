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

var jsonOptions = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
MiniBossArenaChecks.Run(Check);
GuardRecoveryChecks.Run(Check);
GuardShapeChecks.Run(Check);
EmpoweredChecks.Run(Check);
RadialChecks.Run(Check);
RadialConfigChecks.Run(Check);
MiniBossEncounterChecks.Run(Check);
string rulesPath = args.Length > 0 ? args[0] : "configs/history/dungeon-rewards-20261028.json";
var rules = System.Text.Json.JsonSerializer.Deserialize<RewardRules>(File.ReadAllText(rulesPath), jsonOptions)!;
rules.Validate();
RewardPlan Plan(decimal b, double draw = 0) => RewardModel.Plan(b, rules, () => draw);
decimal scale = rules.QualityKnots[6], minimum = rules.QualityKnots[0];
for (int n = 0; n <= 12; n++)
{
    decimal b = n * scale;
    int ancient = n * (n + 1) / 2;
    var exact = Plan(b);
    Check(exact.ItemCount == ancient && exact.Coins == 0, "Exact complete scale");
    Check(exact.Groups.All(g => g.Quality == RewardQuality.Ancient), "Completed groups retained");
    foreach (decimal small in new[] { 0.001m, minimum - 0.001m })
    {
        var below = Plan(b + small);
        Check(below.ItemCount == ancient && below.Coins == 50, "One compensation at positive remainder");
    }
    var common = Plan(b + minimum);
    Check(common.ItemCount == ancient + n + 1 && common.Coins == 0, "Next group threshold");
    Check(common.Groups.Last().Quality == RewardQuality.Common, "Common is not Magic");
    for (int k = 1; k < 6; k++)
    {
        var plan = Plan(b + rules.QualityKnots[k]);
        Check(plan.Groups.Last().Size == n + 1 && plan.Groups.Last().Quality == (RewardQuality)k,
            "Budget not divided by group size");
    }
    if (n > 0)
    {
        var below = Plan(b - 0.001m, 0.999999);
        Check(below.ItemCount == ancient && below.Groups.Count == n, "Early Ancient does not open next group");
    }
}
Check(Plan(3 * scale + rules.QualityKnots[3]).ItemCount == 10, "Six Ancient plus four Epic");
var previous = new decimal[7];
for (decimal score = minimum; score <= scale; score += 0.25m)
{
    var p = RewardModel.Distribution(score, rules.QualityKnots);
    Check(Math.Abs(p.Sum() - 1) < 0.00000000001m && p.All(x => x >= 0 && x <= 1), "Probability mass");
    for (int k = 1; k < 7; k++)
    {
        decimal tail = p.Skip(k).Sum();
        Check(tail >= previous[k], "Stochastic quality monotonicity");
        previous[k] = tail;
    }
}
var rng = new Random(20261028);
int upper = 0;
for (int i = 0; i < 20000; i++)
    if (Plan((rules.QualityKnots[2] + rules.QualityKnots[3]) / 2, rng.NextDouble())
            .Groups.Single().Quality == RewardQuality.Epic) upper++;
Check(Math.Abs(upper / 20000.0 - 0.5) < 0.015, "Adjacent quality sample");
var ids = rules.Weights.Keys.ToArray();
for (int mask = 0; mask < (1 << ids.Length); mask++)
{
    var chosen = ids.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
    var b = rules.Budget(chosen, 0, false);
    Check(b.Points == chosen.Sum(id => rules.Weights[id]), "Actual modifier identities");
    Check(b.Value == rules.Budget(chosen.Reverse(), 0, false).Value, "Order independence");
    Check(rules.Budget(chosen, 20, false).Value - b.Value == 20, "Global weights across biomes");
    foreach (string id in ids.Except(chosen))
        Check(rules.Budget(chosen.Append(id), 0, false).Value >= b.Value, "Adding an affix never reduces budget");
}
Check(rules.Budget(Array.Empty<string>(), 40, true).Value == 0, "Protected zone cancels location bonus");
Refuses(() => rules.Budget(new[] { "unknown" }, 0, false), "No invented affix cost");
Refuses(() => rules.Budget(new[] { "swift", "swift" }, 0, false), "No duplicate affix");
Refuses(() => rules.Budget(new[] { "swift" }, 0, true), "No guarded progression zone");
Refuses(() => Plan(-1), "Negative budget");
Refuses(() => Plan(1000000), "Excessive reward refuses before partial generation");
Refuses(() => Plan(minimum, double.NaN), "NaN RNG");
Refuses(() => Plan(minimum, 1), "Exclusive upper RNG bound");
var journal = new RewardJournal { Before = "ordinary", After = "ordinary+bonus", Next = 1 };
Check(journal.NeedsApply("ordinary"), "Prepared transaction resumes");
Check(!journal.NeedsApply("ordinary+bonus"), "Written transaction does not duplicate");
try { journal.NeedsApply("different ordinary content"); throw new Exception("Conflict accepted"); }
catch (InvalidOperationException) { checks++; }
var chain = DungeonGeometryProbe.RoomGraph.Distances(4, new[] { (0, 1), (1, 2), (2, 3) }, 0);
Check(chain.SequenceEqual(new[] { 0, 1, 2, 3 }), "Room chain depth");
var shortcut = DungeonGeometryProbe.RoomGraph.Distances(4, new[] { (0, 1), (1, 2), (2, 3), (3, 0) }, 0);
Check(shortcut.SequenceEqual(new[] { 0, 1, 2, 1 }), "Cycle changes shortest path");
var disconnected = DungeonGeometryProbe.RoomGraph.Distances(4, new[] { (0, 1), (2, 3) }, 0);
Check(disconnected.SequenceEqual(new[] { 0, 1, -1, -1 }), "Disconnected rooms stay unreachable");
var branch = DungeonGeometryProbe.RoomGraph.Distances(4, new[] { (0, 1), (0, 2), (0, 3) }, 0);
Check(branch.Max() == 1, "Branching alone does not add depth");
Refuses(() => DungeonGeometryProbe.RoomGraph.Distances(2, new[] { (0, 2) }, 0), "Invalid room id");
Refuses(() => DungeonGeometryProbe.RoomGraph.Distances(2, new[] { (0, 1) }, -1), "Missing entrance");
ForestPlanChecks.Run(Check);
InteriorCoverageChecks.Run(Check);
IntervalChecks.Run(Check);
ManualLedgerChecks.Run(Check);
var helperOutcomes = Enumerable.Range(0, 100).Select(MiniBossRules.Helpers).ToArray();
Check(helperOutcomes.Count(n => n == 0) == 85 && helperOutcomes.Count(n => n == 1) == 15,
    "Мини-босс: 85% без помощника, 15% с одним; два помощника исключены");
Check(Enumerable.Range(0, MiniBossRules.TrialCount).Select(MiniBossRules.TrialHelpers)
    .SequenceEqual(new[] { 0, 0, 1, 1 }), "Четыре пробы покрывают оба вида и оба числа помощников");
Refuses(() => MiniBossRules.Helpers(-1), "Отрицательный бросок помощника");
Refuses(() => MiniBossRules.Helpers(100), "Верхняя граница броска помощника исключена");
Refuses(() => MiniBossRules.TrialHelpers(4), "Пятая проба с двумя помощниками исключена");
Check(ManualDeathCounter.ReachedFloor(100, 1), "First conditional death");
Check(!ManualDeathCounter.ReachedFloor(1, 1), "Hits at floor do not add deaths");
Check(!ManualDeathCounter.ReachedFloor(1, 8), "Healing is not a death");
Check(ManualDeathCounter.ReachedFloor(8, 1), "Another fall after healing is counted");
Check(!ManualDeathCounter.ReachedFloor(50, 20), "Nonlethal damage is not a death");
Check(!ManualDeathCounter.ReachedFloor(1.0001f, 1), "Float tolerance at floor");
Console.WriteLine($"Проверки групп, весов, журнала, графа и лесного планировщика: {checks} OK");
