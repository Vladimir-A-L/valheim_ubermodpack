using DungeonDepthPrototype;
using System.Text.Json;

internal static class MiniBossArenaChecks
{
    internal static void Run(Action<bool, string> check)
    {
        check(MiniBossArenaPlan.InFightZone(0, 0, -20), "Пятый сегмент включён в зону");
        check(!MiniBossArenaPlan.InFightZone(0, 0, -20.01f), "За пятым сегментом начинается возврат");
        check(!MiniBossArenaPlan.InFightZone(0, 10, -10), "Игрок с другого этажа не удерживает бой");
        check(!MiniBossArenaPlan.Inside(0, 0, -.01f), "Порог отделяет внутреннюю область");
        foreach (var p in MiniBossArenaPlan.Route)
            check(MiniBossArenaPlan.Inside(p.X, 0, p.Z), "Весь маршрут возврата внутри арены");
        check(MiniBossArenaPlan.FlightDistance == 40 && MiniBossArenaPlan.CorridorWidth == 4,
            "Дальность и полный радиус взрыва взяты из геометрии");
        var state = new MiniBossArenaState { Engaged = true };
        check(state.MayCast(false, false) && !state.MayCast(true, false), "Танк внутри запрещает новый бросок");
        state.Casting = true; state.Windup = .7f; state.CastSequence = 3;
        state.Observe(true, false);
        check(state.Casting && state.Returning, "Уход участников фиксирует возврат, сохраняя начатый бросок");
        check(!state.MayCast(false, true), "Подготовка исключает второй параллельный бросок");
        var options = new JsonSerializerOptions { IncludeFields = true };
        state = JsonSerializer.Deserialize<MiniBossArenaState>(JsonSerializer.Serialize(state, options), options)!;
        state.Observe(true, true);
        check(state.Returning && state.Casting && state.CastSequence == 3 && state.Windup == .7f,
            "Вход и передача сохранённого состояния не отменяют возврат/подготовку");
        state.Casting = false;
        check(!state.MayCast(false, false) && state.MayCast(true, true),
            "Возврат разрешает бросок только по преграде, даже при игроке внутри");
        state.Cooldown = 1;
        check(!state.MayCast(true, true), "Преграда не обходит отдельную паузу");
        check(state.Arrive(true) && state.Heals == 1, "Живое прибытие создаёт одну квитанцию лечения");
        check(!state.Arrive(true) && state.Heals == 1, "Стояние в центре не лечит повторно");
        state.Engaged = true; state.Returning = true; state.Casting = true;
        state.Observe(false, true);
        check(!state.Casting && !state.Returning && state.Mode == "dead" && !state.Arrive(false),
            "Смерть отменяет бросок, запрет стройки и лечение");
        state = new MiniBossArenaState { Engaged = true };
        state.Observe(true, true);
        check(!state.Returning, "Последний участник в зоне удерживает бой");
    }
}
