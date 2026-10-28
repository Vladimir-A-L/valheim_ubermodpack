using System;

namespace DungeonDepthPrototype;

// Vanilla хранит inventory отдельно от курсора. Журнал сохраняется раньше inventory.
public sealed class RewardJournal
{
    public string Before = "";
    public string After = "";
    public int Next;

    public bool NeedsApply(string current)
    {
        if (current == After) return false;
        if (current == Before) return true;
        throw new InvalidOperationException("Конфликт журнала наград: содержимое сохранено, нужна диагностика");
    }
}
