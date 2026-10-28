using DungeonDepthPrototype;

internal static class ManualLedgerChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var stored = new List<ManualReceipt>();
        var ledger = new ManualLedger();
        const string actor = "test-account", actor2 = "other-account";
        string attempt = Guid.NewGuid().ToString("N");
        ManualSample Sample(int sequence, float before, float after, bool probe = false) => new()
        {
            Attempt = attempt, Sequence = sequence, Character = "TestQA", Kind = sequence == 0 ? "start" : "damage",
            Before = before, After = after, God = true, Ghost = false, Position = new float[3], Probe = probe,
            ClientUtc = DateTime.UtcNow.ToString("O")
        };
        string Fingerprint(ManualSample sample) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(sample,
                new System.Text.Json.JsonSerializerOptions { IncludeFields = true })))).ToLowerInvariant();
        ManualReceipt Add(ManualSample sample) => ledger.Accept(actor, sample, Fingerprint(sample), stored.Add, out _);
        Add(Sample(0, 100, 100));
        var first = Sample(1, 100, 1);
        Add(first);
        Add(Sample(2, 1, 1));
        var second = Sample(3, 8, 1);
        Add(second);
        check(ledger.Total(actor, "TestQA") == 2, "Сервер считает новые переходы к 1 HP");
        ledger.Accept(actor, second, Fingerprint(second), stored.Add, out bool duplicate);
        check(duplicate && stored.Count == 4, "Потерянный ответ не дублирует запись");
        var restored = new ManualLedger();
        foreach (var row in stored) restored.Restore(row);
        restored.Accept(actor, second, Fingerprint(second), _ => throw new Exception("Повтор записан"), out duplicate);
        check(duplicate && restored.Total(actor, "TestQA") == 2, "Повтор после рестарта сохраняет счётчик");
        var next = Sample(4, 4, 1);
        try { ledger.Accept(actor, next, Fingerprint(next), _ => throw new IOException("disk failure"), out _); }
        catch (IOException) { }
        check(ledger.Total(actor, "TestQA") == 2, "Ошибка диска не меняет подтверждённый итог");
        Add(next);
        check(ledger.Total(actor, "TestQA") == 3, "Повтор после ошибки записи учитывается один раз");
        bool rejected = false;
        try { Add(Sample(8, 5, 1)); } catch (InvalidOperationException) { rejected = true; }
        check(rejected, "Пропуск последовательности отклонён");
        rejected = false;
        try { Add(Sample(1, 30, 1)); } catch (InvalidOperationException) { rejected = true; }
        check(rejected, "Подмена уже записанного события отклонена");
        var start = Sample(0, 20, 20);
        ledger.Accept(actor2, start, Fingerprint(start), stored.Add, out _);
        check(ledger.Total(actor2, "TestQA") == 0, "Одинаковые имена разных аккаунтов разделены");
        Add(Sample(0, 10, 10, true));
        Add(Sample(1, 10, 1, true));
        check(ledger.Total(actor, "TestQA", true) == 1 && ledger.Total(actor, "TestQA") == 3,
            "Контрольные удары исключены из игровых попыток");
        var invalid = Sample(5, float.NaN, 1);
        rejected = false;
        try { Add(invalid); } catch (ArgumentException) { rejected = true; }
        check(rejected, "Нечисловое HP не принимается");
    }
}
