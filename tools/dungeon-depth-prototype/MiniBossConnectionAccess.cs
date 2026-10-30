using System;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace DungeonDepthPrototype;

// Только вход ручной проверки: порталы не входят в обычное наполнение данжа и не двигают персонажа.
internal static class MiniBossConnectionAccess
{
    private const string AccessKey = MiniBossConnection.LinkKey + ".access";
    internal static object Create(MiniBossArena arena)
    {
        var data = MiniBossRuntime.Data(arena)!;
        string token = data.GetString(ForestRuntime.Tag);
        if (ForestFixture.AllZdos().Values.Any(z => z.GetString(AccessKey) == token))
            throw new InvalidOperationException("Порталы кандидата уже созданы");
        var link = JsonConvert.DeserializeObject<MiniBossLink>(data.GetString(MiniBossConnection.LinkKey))!;
        var generator = MiniBossConnection.Instance(MiniBossConnection.Generator(data))!;
        var room = generator.GetComponentsInChildren<Room>().Single(r => r.name == link.ParentPrefab &&
            Vector3.Distance(r.transform.position, MiniBossLink.V(link.ParentPosition)) < .02f);
        var socket = room.GetConnections()[link.Socket].transform;
        Physics.SyncTransforms();
        int mask = LayerMask.GetMask("static_solid", "Default", "piece", "terrain");
        Vector3? selected = null;
        foreach (float forward in new[] { 3f, 4f, 5f })
        foreach (float side in new[] { -1.5f, 1.5f, 0 })
        {
            var p = socket.position + socket.forward * forward + socket.right * side;
            if (!Physics.Raycast(p + Vector3.up, Vector3.down, out var hit, 2, mask) || hit.normal.y < .9f) continue;
            p = hit.point + Vector3.up * .08f;
            if (Physics.CheckCapsule(p + Vector3.up * .8f, p + Vector3.up * 2, .8f, mask)) continue;
            selected = p; break;
        }
        if (!selected.HasValue) throw new InvalidOperationException("Нет места для входа ручной проверки");
        const string tag = "Стык данжа";
        foreach (var point in new[] { selected.Value, MiniBossTrial.Hub + new Vector3(0, .5f, -10) })
        {
            var obj = MiniBossTrial.Spawn("portal_wood", point, Quaternion.identity, "portal");
            var zdo = MiniBossRuntime.Data(obj.transform)!;
            zdo.Set(ZDOVars.s_tag, tag); zdo.Set(AccessKey, token);
        }
        var sign = MiniBossTrial.Spawn("sign", MiniBossTrial.Hub + new Vector3(2.5f, 1.7f, -10),
            Quaternion.identity, "sign");
        MiniBossRuntime.Data(sign.transform)!.Set(ZDOVars.s_text, "Стык данжа\nПодход к мини-боссу\nКарта: M");
        MiniBossRuntime.Data(sign.transform)!.Set(AccessKey, token);
        return new { portals = 2, tag, dungeon_position = ForestFixture.V(selected.Value),
            player_moved = false, inventory_changed = false };
    }
}
