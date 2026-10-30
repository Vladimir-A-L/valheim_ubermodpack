using System;
using System.Collections.Generic;
using System.Linq;

namespace DungeonDepthPrototype;

// Метры, локальное начало — порог двери; наружный коридор направлен по -Z.
internal static class MiniBossArenaPlan
{
    internal const float Segment = 4, CorridorWidth = 4, Height = 6;
    internal const float FightDistance = 5 * Segment, FlightDistance = 10 * Segment;
    internal const string Prefab = "UberMiniBossArena_v1", Projectile = "UberMiniBossRock_v1";
    internal const string ValuePrefab = "UberMiniBossValue_v1";
    internal readonly struct Rect
    {
        internal readonly int X0, Z0, X1, Z1;
        internal Rect(int x0, int z0, int x1, int z1) { X0 = x0; Z0 = z0; X1 = x1; Z1 = z1; }
        internal bool Contains(float x, float z, float margin = 0) =>
            x >= X0 + margin && x <= X1 - margin && z >= Z0 + margin && z <= Z1 - margin;
    }
    internal static readonly Rect[] Floor = {
        new Rect(-2, -24, 2, 0), new Rect(-3, 0, 3, 5),
        new Rect(-3, 5, 13, 11), new Rect(7, 11, 31, 31)
    };
    internal static readonly (float X, float Z)[] Route = { (0, 2), (0, 8), (10, 8), (10, 15), (19, 21) };
    internal static readonly (float X, float Z)[] Pedestals = {
        (9, 18), (9, 25), (15, 29), (23, 29), (29, 24), (29, 16)
    };
    internal static bool Inside(float x, float y, float z) => y >= -1 && y <= Height && z >= 0 &&
        Floor.Skip(1).Any(r => r.Contains(x, z));
    internal static bool InFightZone(float x, float y, float z) => Inside(x, y, z) ||
        y >= -1 && y <= Height && Math.Abs(x) <= CorridorWidth / 2 && z <= 0 && z >= -FightDistance;
    internal static bool FloorAt(float x, float z) => Floor.Any(r => r.Contains(x, z));
    // Постоянные стенки строятся по границе объединения прямоугольников, без внутренних перегородок на стыках.
    internal static IEnumerable<(float X, float Z, bool AlongX)> Walls()
    {
        for (int x = -3; x < 31; x++)
        for (int z = -24; z < 31; z++)
        {
            if (!FloorAt(x + .5f, z + .5f)) continue;
            if (!FloorAt(x - .5f, z + .5f)) yield return (x, z + .5f, false);
            if (!FloorAt(x + 1.5f, z + .5f)) yield return (x + 1, z + .5f, false);
            if (!FloorAt(x + .5f, z - .5f)) yield return (x + .5f, z, true);
            if (!FloorAt(x + .5f, z + 1.5f)) yield return (x + .5f, z + 1, true);
        }
    }
}

// Решение хранится в ZDO целиком; завершение броска имеет приоритет над началом возврата.
internal sealed class MiniBossArenaState
{
    public bool Engaged, Returning, Casting;
    public float Windup, Cooldown;
    public int CastSequence, Heals;
    public int ReleasedSequence = 0;
    public string Mode = "idle", ReturnReason = "", Target = "", Blocker = "", Animation = "";
    public float[] Aim = Array.Empty<float>();
    public long[] Participants = Array.Empty<long>();
    public void Observe(bool alive, bool participantNear)
    {
        if (!alive) { Casting = Returning = false; Mode = "dead"; return; }
        if (Engaged && !participantNear && !Returning)
        { Returning = true; ReturnReason = "all-participants-beyond-five-segments"; }
    }
    public bool MayCast(bool anyPlayerInside, bool blocked) => !Casting && Cooldown <= 0 &&
        (Returning ? blocked : Engaged && !anyPlayerInside);
    public bool Arrive(bool alive)
    {
        if (!alive || !Returning || Casting) return false;
        Returning = Engaged = false; Participants = Array.Empty<long>(); Heals++; Mode = "idle";
        Target = Blocker = ""; return true;
    }
}
