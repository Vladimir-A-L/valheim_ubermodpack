using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DungeonDepthPrototype;

// Сообщение клиента содержит измерения, но не готовое число смертей.
internal sealed class ManualSample
{
    public int Protocol = 1, Sequence;
    public string Attempt = "", Character = "", Kind = "", ClientUtc = "", Attacker = "";
    public float Before, After;
    public float[] Position = Array.Empty<float>();
    public bool God, Ghost, Probe;
    public string Id => Attempt + ":" + Sequence;

    internal void Validate()
    {
        if (Protocol != 1 || !Regex.IsMatch(Attempt, "^[a-f0-9]{32}$") || Sequence < 0 || Sequence > 10000000 ||
            Character.Length < 1 || Character.Length > 64 || Attacker.Length > 128 ||
            (Kind != "start" && Kind != "damage") || (Kind == "start") != (Sequence == 0) ||
            !Finite(Before) || !Finite(After) || Before < 0 || After < 0 || Before > 1000000 || After > 1000000 ||
            Position.Length != 3 || Array.Exists(Position, p => !Finite(p)) || !God || Ghost ||
            !DateTimeOffset.TryParse(ClientUtc, out _))
            throw new ArgumentException("Некорректное измерение ручной проверки");
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}

internal sealed class ManualReceipt
{
    public string Actor = "", ServerUtc = "", Fingerprint = "";
    public ManualSample Sample = new ManualSample();
    public bool ConditionalDeath;
    public int AttemptDeaths, TotalDeaths;
}

internal sealed class ManualLedger
{
    private readonly Dictionary<string, ManualReceipt> events = new Dictionary<string, ManualReceipt>();
    private readonly Dictionary<string, int> totals = new Dictionary<string, int>();
    private readonly Dictionary<string, ManualReceipt> attempts = new Dictionary<string, ManualReceipt>();

    private static string Identity(string actor, ManualSample sample) =>
        actor + "\n" + sample.Character + (sample.Probe ? "\nprobe" : "\nmanual");

    internal int Total(string actor, string character, bool probe = false) =>
        totals.TryGetValue(Identity(actor, new ManualSample { Character = character, Probe = probe }), out int n)
            ? n : 0;

    internal ManualReceipt Accept(string actor, ManualSample sample, string fingerprint,
        Action<ManualReceipt> persist, out bool duplicate)
    {
        sample.Validate();
        if (string.IsNullOrWhiteSpace(actor) || fingerprint.Length != 64)
            throw new ArgumentException("Не определён подтверждённый отправитель или отпечаток события");
        string identity = Identity(actor, sample), key = identity + "\n" + sample.Id;
        duplicate = events.TryGetValue(key, out var previous);
        if (duplicate)
        {
            if (previous!.Fingerprint != fingerprint) throw new InvalidOperationException("Конфликт события");
            return previous;
        }
        string attemptKey = identity + "\n" + sample.Attempt;
        attempts.TryGetValue(attemptKey, out var last);
        if (sample.Sequence != (last == null ? 0 : last.Sample.Sequence + 1))
            throw new InvalidOperationException("Нарушен порядок событий попытки");
        bool death = sample.Kind == "damage" && ManualDeathCounter.ReachedFloor(sample.Before, sample.After);
        var receipt = new ManualReceipt
        {
            Actor = actor, ServerUtc = DateTime.UtcNow.ToString("O"), Sample = sample, Fingerprint = fingerprint,
            ConditionalDeath = death, AttemptDeaths = (last?.AttemptDeaths ?? 0) + (death ? 1 : 0),
            TotalDeaths = Total(actor, sample.Character, sample.Probe) + (death ? 1 : 0)
        };
        // Подтверждение допустимо только после успешной устойчивой записи.
        persist(receipt);
        Commit(receipt);
        return receipt;
    }

    internal void Restore(ManualReceipt receipt)
    {
        var expected = Accept(receipt.Actor, receipt.Sample, receipt.Fingerprint, _ => { }, out bool duplicate);
        if (duplicate || expected.TotalDeaths != receipt.TotalDeaths ||
            expected.AttemptDeaths != receipt.AttemptDeaths || expected.ConditionalDeath != receipt.ConditionalDeath)
            throw new InvalidOperationException("Журнал ручной проверки не согласован");
    }

    private void Commit(ManualReceipt receipt)
    {
        string identity = Identity(receipt.Actor, receipt.Sample);
        events.Add(identity + "\n" + receipt.Sample.Id, receipt);
        attempts[identity + "\n" + receipt.Sample.Attempt] = receipt;
        totals[identity] = receipt.TotalDeaths;
    }
}
