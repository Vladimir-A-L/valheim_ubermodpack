using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DungeonDepthPrototype;

// Добровольный вход к началу сохранённого подхода; саму встречу не создаёт и не сбрасывает.
internal static class ResourceArenaAccess
{
    internal const string Key = "uberdungeon.resource-access.v1";
    private const float DungeonScale = .65f;
    private static string preparedToken = "";
    private static Vector3 preparedPoint;
    private static Quaternion preparedRotation;
    internal static object Prepare()
    {
        var data = ForestFixture.AllZdos().Values.Single(z =>
            z.GetString(MiniBossConnection.LinkKey).Length > 0 && z.GetString(ForestRuntime.LayoutKey).Length > 0);
        string token = data.GetString(ForestRuntime.Tag);
        var existing = ForestFixture.AllZdos().Values.Where(z => z.GetString(Key) == token).ToArray();
        if (existing.Length > 0)
            return new { created = false, existing_objects = existing.Length };
        var generator = MiniBossConnection.Instance(data)!.GetComponent<DungeonGenerator>();
        var rooms = (JArray)JObject.Parse(data.GetString(ForestRuntime.LayoutKey))["rooms"]!;
        var arena = rooms.Single(r => r.Value<bool>("arena"));
        var child = arena;
        while (rooms[child.Value<int>("parent")].Value<int>("depth") > arena.Value<int>("attachment"))
            child = rooms[child.Value<int>("parent")];
        var parent = rooms[child.Value<int>("parent")];
        var position = ForestRuntime.V(ForestAtmosphere.Point(parent["position"]!));
        var room = generator.GetComponentsInChildren<Room>().Single(r =>
            r.name == parent.Value<string>("prefab") && Vector3.Distance(r.transform.position, position) < .02f);
        var socket = room.GetConnections()[child.Value<int>("parent_socket")].transform;
        var rotation = Quaternion.LookRotation(-socket.forward, Vector3.up);
        Physics.SyncTransforms();
        Vector3? selected = null;
        foreach (float distance in new[] { 2.4f, 3.2f, 4f, 4.8f, 5.6f })
        foreach (float side in new[] { -1.6f, 1.6f, -2.4f, 2.4f, 0f })
        {
            var p = socket.position + socket.forward * distance + socket.right * side;
            if (!Floor(p, out var ground)) continue;
            if (Clear(ground, rotation, DungeonScale)) { selected = ground; break; }
        }
        if (!selected.HasValue) throw new InvalidOperationException("Нет свободного входа перед подходом арены");
        preparedToken = token; preparedPoint = selected.Value; preparedRotation = rotation;
        return new { prepared = true, approach_depth = parent.Value<int>("depth"),
            arena_depth = arena.Value<int>("depth"), dungeon_position = ForestFixture.V(selected.Value) };
    }
    internal static object Create()
    {
        if (preparedToken.Length == 0) throw new InvalidOperationException("Сначала проверить точку у подхода");
        if (ForestFixture.AllZdos().Values.Any(z => z.GetString(Key) == preparedToken))
            throw new InvalidOperationException("Портал уже существует; повторное создание запрещено");
        var hub = MiniBossTrial.Hub + new Vector3(-12, .5f, 0);
        var hubRotation = Quaternion.Euler(0, 90, 0);
        Physics.SyncTransforms();
        if (!Floor(hub, out hub) || !Clear(hub, hubRotation, 1))
            throw new InvalidOperationException("Нет свободного места портала на хабе");
        const string tag = "Ресурсная арена";
        void Portal(Vector3 point, Quaternion turn, float scale)
        {
            var obj = MiniBossTrial.Spawn("portal_wood", point, turn, "portal");
            var view = obj.GetComponent<ZNetView>();
            view.m_syncInitialScale = true; view.SetLocalScale(Vector3.one * scale);
            var zdo = view.GetZDO();
            zdo.Set(ZDOVars.s_tag, tag); zdo.Set(Key, preparedToken);
        }
        Portal(preparedPoint, preparedRotation, DungeonScale); Portal(hub, hubRotation, 1);
        var sign = MiniBossTrial.Spawn("sign", hub + hubRotation * new Vector3(2.5f, 1.2f, 0), hubRotation, "sign");
        var signData = MiniBossRuntime.Data(sign.transform)!;
        signData.Set(ZDOVars.s_text, "Ресурсная арена\n5 переходов до зала\nОсмотр атмосферы и наполнения");
        signData.Set(Key, preparedToken);
        return new { created = true, portals = 2, tag, dungeon_scale = DungeonScale,
            dungeon_position = ForestFixture.V(preparedPoint),
            player_moved = false, inventory_changed = false };
    }
    private static int Mask => LayerMask.GetMask("static_solid", "Default", "piece", "terrain");
    private static bool Floor(Vector3 p, out Vector3 ground)
    {
        bool found = Physics.Raycast(p + Vector3.up, Vector3.down, out var hit, 3, Mask,
            QueryTriggerInteraction.Ignore) && hit.normal.y > .9f;
        ground = hit.point + Vector3.up * .08f; return found;
    }
    internal static Bounds FrameBounds()
    {
        var prefab = ZNetScene.instance.GetPrefab("portal_wood");
        var bounds = new Bounds(Vector3.zero, Vector3.zero);
        foreach (var mesh in prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mesh.sharedMesh == null) continue;
            var b = mesh.sharedMesh.bounds;
            foreach (float x in new[] { b.min.x, b.max.x })
            foreach (float y in new[] { b.min.y, b.max.y })
            foreach (float z in new[] { b.min.z, b.max.z })
                bounds.Encapsulate(prefab.transform.InverseTransformPoint(
                    mesh.transform.TransformPoint(new Vector3(x, y, z))));
        }
        return bounds;
    }
    private static bool Clear(Vector3 ground, Quaternion rotation, float scale)
    {
        var frame = FrameBounds();
        var center = ground + rotation * (frame.center * scale);
        if (Physics.CheckBox(center, frame.extents * scale, rotation, Mask,
                QueryTriggerInteraction.Ignore)) return false;
        float distance = ZNetScene.instance.GetPrefab("portal_wood").GetComponent<TeleportWorld>().m_exitDistance;
        var exit = ground + rotation * Vector3.forward * distance;
        return Floor(exit, out var floor) && Math.Abs(floor.y - ground.y) < .15f &&
            !Physics.CheckCapsule(floor + Vector3.up * .4f, floor + Vector3.up * 1.6f,
                .35f, Mask, QueryTriggerInteraction.Ignore);
    }
}
