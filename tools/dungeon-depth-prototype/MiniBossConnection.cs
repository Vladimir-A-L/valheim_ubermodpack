using System;
using System.Linq;
using UnityEngine;

namespace DungeonDepthPrototype;

// Переход вне шести сегментов. Генератор пока не вызывает открытие стыка.
internal static class MiniBossConnection
{
    internal const string AnchorName = "crypt-connection", CapName = "crypt-connection-cap";
    internal static void Build(GameObject root)
    {
        var anchor = new GameObject(AnchorName); anchor.transform.SetParent(root.transform, false);
        anchor.transform.localPosition = new Vector3(0, 0, MiniBossArenaPlan.ConnectionZ);
        anchor.transform.localRotation = Quaternion.Euler(0, 180, 0);
        void Solid(string name, Vector3 center, Vector3 size)
        {
            var obj = new GameObject(name); obj.transform.SetParent(root.transform, false);
            obj.layer = LayerMask.NameToLayer("static_solid");
            var box = obj.AddComponent<BoxCollider>(); box.center = center; box.size = size;
        }
        float z = MiniBossArenaPlan.ConnectionZ;
        Solid(CapName, new Vector3(0, 1.2f, z), new Vector3(2, 2.4f, .3f));
        Solid("connection-lintel", new Vector3(0, 4.2f, z), new Vector3(4, 3.6f, .3f));
        foreach (float x in new[] { -1.5f, 1.5f })
            Solid("connection-jamb", new Vector3(x, 1.2f, z), new Vector3(1, 2.4f, .3f));
    }
    internal static bool TryConnect(MiniBossArena arena, RoomConnection otherSocket)
    {
        if (otherSocket.GetComponentInParent<Room>() == null) return false;
        var other = otherSocket.transform;
        var anchor = arena.transform.Find(AnchorName);
        // У штатного RoomConnection forward направлен внутрь комнаты, к продолжению нашего прохода.
        if (Vector3.Distance(anchor.position, other.position) > .02f ||
            Vector3.Dot(anchor.forward, other.forward) < .999f ||
            Vector3.Dot(other.up, Vector3.up) < .999f) return false;
        int mask = LayerMask.GetMask("static_solid", "Default", "terrain", "piece");
        // Сначала доказать опору снаружи, затем убрать заглушку; при ошибке она остаётся.
        foreach (float forward in new[] { .25f, .75f, 1.25f })
        foreach (float side in new[] { -.5f, 0, .5f })
        {
            var p = anchor.position + anchor.forward * forward + anchor.right * side;
            if (!Physics.Raycast(p + Vector3.up, Vector3.down, out var hit, 1.1f, mask,
                    QueryTriggerInteraction.Ignore) || Math.Abs(hit.point.y - anchor.position.y) > .03f ||
                hit.collider.GetComponentInParent<MiniBossArena>() != null) return false;
            var cap = arena.transform.Find(CapName).GetComponent<Collider>();
            if (Physics.OverlapCapsule(p + Vector3.up * .4f, p + Vector3.up * 1.5f, .3f, mask,
                    QueryTriggerInteraction.Ignore).Any(c => c != cap)) return false;
        }
        arena.transform.Find(CapName).gameObject.SetActive(false);
        return true;
    }
}
