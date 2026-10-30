using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DungeonDepthPrototype;

internal static class MiniBossInteriorProbe
{
    private static GameObject? attachment;
    private static RoomConnection? attachedSocket;
    private static DungeonDB.RoomData? attachmentRoom;
    private const string ProbeKey = MiniBossArena.Key + ".interior-probe";
    private static readonly Vector3 Position = MiniBossTrial.Hub + new Vector3(0, 0, 512);
    internal static object Run(JObject command)
    {
        if (!ZNet.instance.IsServer() || ZNet.instance.GetPeers().Any(p => p.IsReady()))
            throw new InvalidOperationException("Проверка требует отдельной копии без игроков");
        string phase = command.Value<string>("phase") ?? "resources";
        if (phase == "resources") return Resources();
        if (phase == "create")
        {
            if (ForestFixture.AllZdos().Values.Any(z => z.GetInt(MiniBossTrial.IndexKey, -1) == 90))
                throw new InvalidOperationException("Пробная арена уже существует");
            MiniBossArenaTrial.Create(Position, 90, 120030, true, Quaternion.Euler(0, 90, 0));
            var door = MiniBossTrial.Spawn("dungeon_forestcrypt_door", Position + new Vector3(40, 0, 0),
                Quaternion.identity).GetComponent<ZNetView>();
            door.GetZDO().Set(MiniBossArena.Key, true); door.GetZDO().Set(ForestGates.GateKey, true);
            door.GetZDO().Set(ProbeKey, "survivor"); door.GetZDO().Set(ZDOVars.s_state, 1);
            door.GetZDO().Set(ForestGates.ReleasedKey, true);
            door.m_syncInitialScale = true; door.SetLocalScale(Vector3.one * 2);
            MiniBossArenaProbe.Reference = Position; ZNet.instance.SetReferencePosition(Position);
            return new { created = true };
        }
        if (phase == "visit")
        {
            MiniBossArenaProbe.Reference = Position; ZNet.instance.SetReferencePosition(Position);
            return new { reference_set = true };
        }
        var rootData = ForestFixture.AllZdos().Values.Single(z => z.GetInt(MiniBossTrial.IndexKey, -1) == 90);
        string token = rootData.GetString(ForestRuntime.Tag);
        var arena = ZNetScene.instance.FindInstance(rootData.m_uid).GetComponent<MiniBossArena>();
        if (phase == "connect") return Attach(arena);
        if (phase == "check") return Check(arena, token);
        if (phase == "destroy-door")
        {
            var data = ForestFixture.AllZdos().Values.Single(z => z.GetString(ForestRuntime.Tag) == token &&
                z.GetBool(ForestGates.GateKey));
            data.SetOwner(ZDOMan.GetSessionID());
            rootData.Set(ProbeKey, "door-deleted");
            var door = ZNetScene.instance.FindInstance(data.m_uid);
            AccessTools.Method(typeof(WearNTear), "Destroy").Invoke(door.GetComponent<WearNTear>(),
                new object?[] { null, true });
            return new { requested = true };
        }
        if (phase == "cleanup")
        {
            if (attachment != null) Object.Destroy(attachment);
            attachmentRoom?.m_prefab.Release(); attachmentRoom = null; attachedSocket = null;
            var all = ForestFixture.AllZdos().Values.Where(z => z.GetString(ForestRuntime.Tag) == token ||
                z.GetString(ProbeKey) == "survivor").ToArray();
            foreach (var data in all)
            {
                data.SetOwner(ZDOMan.GetSessionID());
                var obj = ZNetScene.instance.FindInstance(data.m_uid);
                if (obj != null) ZNetScene.instance.Destroy(obj); else ZDOMan.instance.DestroyZDO(data);
            }
            return new { removed = all.Length };
        }
        throw new ArgumentException("Неизвестная фаза проверки интерьера");
    }
    private static object Check(MiniBossArena arena, string token)
    {
        Physics.SyncTransforms();
        var frame = new ArenaFrame(arena.transform.position, arena.transform.rotation);
        var points = new[] { -27.5f, -26, -24.1f, -24, -23.9f, -22 };
        int mask = LayerMask.GetMask("static_solid");
        var floors = points.Select(z => {
            var p = frame.Point(new Vector3(0, 0, z));
            bool found = Physics.Raycast(p + Vector3.up, Vector3.down, out var hit, 2, mask);
            return new { z, found, height = hit.point.y - frame.Origin.y,
                clear = !Physics.CheckCapsule(p + Vector3.up * .4f, p + Vector3.up * 1.5f, .3f, mask) };
        }).ToArray();
        var agent = ZNetScene.instance.GetPrefab("Skeleton").GetComponent<BaseAI>().m_pathAgentType;
        bool path = Pathfinding.instance.HavePath(frame.Point(new Vector3(0, .08f, -27)),
            frame.Point(new Vector3(0, .08f, -22)), agent);
        bool zones = MiniBossArena.Near(frame, frame.Point(new Vector3(0, 0, -20))) &&
            !MiniBossArena.Near(frame, frame.Point(new Vector3(0, 0, -20.01f))) &&
            MiniBossArena.Inside(frame, MiniBossArena.Center(frame)) &&
            MiniBossArena.Shielded(frame.Point(new Vector3(0, 1.6f, -.5f)),
                MiniBossArena.Center(frame) + Vector3.up * 1.6f);
        var route = MiniBossArenaPlan.Route.Select(p => frame.Point(new Vector3(p.X, .08f, p.Z))).ToArray();
        bool routeClear = Enumerable.Range(1, route.Length - 1).All(i =>
            !Physics.CapsuleCast(route[i - 1] + Vector3.up * .7f, route[i - 1] + Vector3.up * 3,
                .65f, (route[i] - route[i - 1]).normalized, Vector3.Distance(route[i], route[i - 1]), mask));
        var anchor = arena.transform.Find(MiniBossConnection.AnchorName);
        var unattached = new GameObject("unattached-socket");
        unattached.transform.SetPositionAndRotation(anchor.position, Quaternion.LookRotation(anchor.forward));
        bool refused = !MiniBossConnection.TryConnect(arena, unattached.AddComponent<RoomConnection>());
        Object.Destroy(unattached);
        bool connected = attachedSocket != null && MiniBossConnection.TryConnect(arena, attachedSocket);
        bool connectedPath = connected && Pathfinding.instance.HavePath(frame.Point(new Vector3(0, .08f, -27)),
            frame.Point(new Vector3(0, .08f, -30)), agent);
        bool jointClear = connected && !Physics.CapsuleCast(frame.Point(new Vector3(0, .4f, -27)),
            frame.Point(new Vector3(0, 1.5f, -27)), .3f, anchor.forward, 3, mask);

        var all = ForestFixture.AllZdos().Values;
        var survivor = all.Single(z => z.GetString(ProbeKey) == "survivor");
        var view = ZNetScene.instance.FindInstance(survivor.m_uid).GetComponent<ZNetView>();
        bool scale = Vector3.Distance(view.transform.localScale, Vector3.one * 2) < .001f &&
            view.m_syncInitialScale && survivor.GetInt(ZDOVars.s_state) == 1 &&
            survivor.GetBool(ForestGates.ReleasedKey);
        var particles = arena.GetComponentsInChildren<ParticleSystem>(true);
        var flames = arena.GetComponentsInChildren<Transform>(true).Where(t => t.name == "crypt-torch-flame");
        bool fire = flames.Count() == 6 && flames.All(t =>
            t.GetComponentsInChildren<ParticleSystem>(true).Any(p => p.isPlaying) &&
            t.GetComponentsInChildren<Light>(true).Any(l => l.enabled) &&
            t.GetComponentsInChildren<ZNetView>(true).Length == 0 &&
            t.GetComponentsInChildren<Collider>(true).Length == 0);
        return new { floors, path, zones, route_clear = routeClear, unsafe_connection_refused = refused,
            connected, connected_path = connectedPath, joint_clear = jointClear,
            scale_restored = scale, door_present = all.Any(z => z.GetString(ForestRuntime.Tag) == token &&
                z.GetBool(ForestGates.GateKey)), flames = fire, particles = particles.Length,
            passed = floors.All(f => f.found && Math.Abs(f.height) < .01f && f.clear) && path && zones &&
                routeClear && refused && scale && fire &&
                (attachedSocket == null || connected && connectedPath && jointClear) };
    }
    private static GameObject CopyCollision(Transform root, Vector3 position, Quaternion rotation)
    {
        var collision = new GameObject("crypt-measurement");
        collision.transform.SetPositionAndRotation(position, rotation);
        foreach (var source in root.GetComponentsInChildren<Collider>(true).Where(c => c.enabled && !c.isTrigger))
        {
            if (source.GetComponentInParent<ZNetView>() != null) continue;
            var obj = new GameObject(source.name); obj.transform.SetParent(collision.transform, false);
            var matrix = root.worldToLocalMatrix * source.transform.localToWorldMatrix;
            obj.transform.localPosition = matrix.GetColumn(3); obj.transform.localRotation = matrix.rotation;
            obj.transform.localScale = matrix.lossyScale; obj.layer = LayerMask.NameToLayer("static_solid");
            if (source is BoxCollider box)
            { var copy = obj.AddComponent<BoxCollider>(); copy.center = box.center; copy.size = box.size; }
            else if (source is MeshCollider mesh)
            {
                var copy = obj.AddComponent<MeshCollider>();
                copy.sharedMesh = mesh.sharedMesh; copy.convex = mesh.convex;
            }
        }
        return collision;
    }
    private static object Attach(MiniBossArena arena)
    {
        if (attachment != null) throw new InvalidOperationException("Стык уже подготовлен");
        var data = DungeonDB.GetRooms().Single(r => r.m_prefab.Name == "forestcrypt_Corridor1");
        data.m_prefab.Load(); attachmentRoom = data;
        var root = data.RoomInPrefab.transform;
        var source = root.GetComponentsInChildren<RoomConnection>(true)
            .First(c => root.InverseTransformPoint(c.transform.position).z > 3.9f);
        var anchor = arena.transform.Find(MiniBossConnection.AnchorName);
        var local = root.InverseTransformPoint(source.transform.position);
        var localRotation = Quaternion.Inverse(root.rotation) * source.transform.rotation;
        var rotation = anchor.rotation * Quaternion.Inverse(localRotation);
        attachment = CopyCollision(root, anchor.position - rotation * local, rotation);
        attachment.AddComponent<Room>();
        var socket = new GameObject("measured-room-socket");
        socket.transform.SetParent(attachment.transform, false);
        socket.transform.localPosition = local; socket.transform.localRotation = localRotation;
        attachedSocket = socket.AddComponent<RoomConnection>();
        Physics.SyncTransforms();
        return new { attached = MiniBossConnection.TryConnect(arena, attachedSocket) };
    }
    private static object Resources()
    {
        var data = DungeonDB.GetRooms().Single(r => r.m_prefab.Name == "forestcrypt_Corridor1");
        data.m_prefab.Load();
        try
        {
            var root = data.RoomInPrefab.transform;
            var collision = CopyCollision(root, Position + new Vector3(100, 0, 100), Quaternion.identity);
            Physics.SyncTransforms();
            var sockets = root.GetComponentsInChildren<RoomConnection>(true).Select(s => {
                Vector3 local = root.InverseTransformPoint(s.transform.position);
                Quaternion rotation = Quaternion.Inverse(root.rotation) * s.transform.rotation;
                var center = collision.transform.position + local + rotation * Vector3.forward * .25f;
                var right = rotation * Vector3.right;
                var widths = new[] { .35f, 1, 1.8f }.Select(height => {
                    var from = center + Vector3.up * height;
                    bool left = Physics.Raycast(from, -right, out var l, 3, LayerMask.GetMask("static_solid"));
                    bool rightHit = Physics.Raycast(from, right, out var r, 3, LayerMask.GetMask("static_solid"));
                    return new { height, width = left && rightHit ? l.distance + r.distance : -1 };
                }).ToArray();
                bool roof = Physics.Raycast(center + Vector3.up * .1f, Vector3.up, out var ceiling, 6,
                    LayerMask.GetMask("static_solid"));
                var samples = new List<object>();
                foreach (float side in new[] { -1.25f, -1f, -.75f, -.5f, -.25f, 0, .25f, .5f, .75f, 1f, 1.25f })
                {
                    var p = collision.transform.position + local + rotation * new Vector3(side, 0, .25f);
                    bool floor = Physics.Raycast(p + Vector3.up, Vector3.down, out var hit, 3,
                        LayerMask.GetMask("static_solid"));
                    samples.Add(new { side, floor, y = hit.point.y - collision.transform.position.y,
                        player_clear = floor && !Physics.CheckCapsule(hit.point + Vector3.up * .4f,
                            hit.point + Vector3.up * 1.5f, .3f, LayerMask.GetMask("static_solid")) });
                }
                return new { position = ForestFixture.V(local), rotation = ForestFixture.V(rotation.eulerAngles),
                    widths, headroom = roof ? ceiling.distance + .1f : -1, samples };
            }).ToArray();
            var torch = root.GetComponentsInChildren<Transform>(true).First(t => t.name == "CastleKit_groundtorch");
            var components = torch.GetComponentsInChildren<Component>(true).Select(c => new {
                path = c.name, type = c.GetType().Name, position = ForestFixture.V(torch.InverseTransformPoint(
                    c.transform.position)) }).ToArray();
            Object.Destroy(collision);
            var door = ZNetScene.instance.GetPrefab("dungeon_forestcrypt_door");
            return new { sockets, torch = components, door = new { scale = ForestFixture.V(door.transform.localScale),
                sync_initial_scale = door.GetComponent<ZNetView>().m_syncInitialScale,
                colliders = door.GetComponentsInChildren<BoxCollider>(true).Select(c => new {
                    c.name, center = ForestFixture.V(c.center), size = ForestFixture.V(c.size) }).ToArray() } };
        }
        finally { data.m_prefab.Release(); }
    }
}
