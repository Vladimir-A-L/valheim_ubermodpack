using DungeonDepthPrototype;

internal static class GuardRecoveryChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var clock = new GuardRecoveryClock();
        for (int i = 0; i < 15; i++) check(!clock.Observe(.5f, true, false), "Загрузка и выдержка до возврата");
        check(clock.Observe(.5f, true, false), "Непрерывный дефект после выдержки");
        foreach (string discontinuity in new[] { "owner", "geometry", "lag" })
        {
            clock.Reset();
            for (int i = 0; i < 13; i++) clock.Observe(.5f, true, false);
            check(!clock.Observe(discontinuity == "lag" ? 2 : .5f, discontinuity == "lag", false),
                "Прерывистое наблюдение не подтверждает дефект: " + discontinuity);
            for (int i = 0; i < 15; i++) check(!clock.Observe(.5f, true, false), "Новая полная выдержка");
        }
        clock.Reset();
        for (int i = 0; i < 10; i++) clock.Observe(.5f, true, true);
        for (int cycle = 0; cycle < 100; cycle++)
        {
            for (int i = 0; i < 5; i++) check(!clock.Observe(.5f, true, false), "Краткий толчок/стык");
            check(!clock.Observe(.5f, true, true), "Вернулся в допустимое пространство сам");
        }
    }
}
