namespace DungeonDepthPrototype;

// Считается переход к порогу, а не каждый удар по уже лежащему на пороге персонажу.
internal static class ManualDeathCounter
{
    internal static bool ReachedFloor(float before, float after) => before > 1.001f && after <= 1.001f;
}
