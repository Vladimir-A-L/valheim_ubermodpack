using UnityEngine;

namespace DungeonDepthPrototype;

internal readonly struct ArenaFrame
{
    internal readonly Vector3 Origin;
    internal readonly Quaternion Rotation;
    internal ArenaFrame(Vector3 origin, Quaternion rotation) { Origin = origin; Rotation = rotation; }
    internal Vector3 Point(Vector3 local) => Origin + Rotation * local;
    internal Vector3 Local(Vector3 world) => Quaternion.Inverse(Rotation) * (world - Origin);
    internal Vector3 Direction(Vector3 local) => Rotation * local;
}
