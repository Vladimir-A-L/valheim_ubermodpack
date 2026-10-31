using System;

namespace DungeonDepthPrototype;

// Чистая модель таймеров: отсутствие владельца/геометрии разрывает наблюдение, а не подтверждает дефект.
internal sealed class GuardRecoveryClock
{
    internal const float Warmup = 5, Grace = 3, Retry = 10;
    internal const int Limit = 3;
    private float ready, invalid;
    internal void Reset() { ready = 0; invalid = 0; }
    internal bool Observe(float dt, bool available, bool valid)
    {
        if (!available || dt <= 0 || dt > 1) { Reset(); return false; }
        ready += dt;
        if (ready <= Warmup || valid) { invalid = 0; return false; }
        invalid += dt;
        return invalid >= Grace;
    }
}

internal sealed class GuardRecoveryState
{
    public int Schema = 1, Returns = 0, LastRoom = -1;
    public bool InitialKnown = false, HasSafe = false, Retired = false;
    public float[] Initial = Array.Empty<float>(), Safe = Array.Empty<float>();
    public string Reason = "", ObservedAtUtc = "";
}
