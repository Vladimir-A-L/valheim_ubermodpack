using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace DungeonDepthPrototype;

internal static class MiniBossCryptDecor
{
    private static ZNetScene? scene;
    private static readonly List<DungeonDB.RoomData> held = new List<DungeonDB.RoomData>();
    private static readonly Dictionary<string, MeshFilter> meshes = new Dictionary<string, MeshFilter>();
    internal static void Reset()
    {
        meshes.Clear(); foreach (var room in held) room.m_prefab.Release(); held.Clear(); scene = null;
    }
    private static void Load()
    {
        if (scene == ZNetScene.instance && meshes.Count > 0) return;
        Reset(); scene = ZNetScene.instance;
        foreach (string name in new[] { "forestcrypt_Corridor1", "forestcrypt_new_BurialChamber02" })
        {
            var room = DungeonDB.GetRooms().Single(r => r.m_prefab.Name == name);
            room.m_prefab.Load(); held.Add(room);
            foreach (var filter in room.RoomInPrefab.GetComponentsInChildren<MeshFilter>(true))
                if (filter.sharedMesh != null && filter.GetComponent<MeshRenderer>() != null &&
                    !meshes.ContainsKey(filter.sharedMesh.name)) meshes.Add(filter.sharedMesh.name, filter);
        }
    }
    // Копируются штатные mesh/материалы вместе с UV и цветами вершин, без объектов лута и поведения комнаты.
    private static GameObject Fit(GameObject parent, string mesh, Vector3 center, Vector3 size,
        Quaternion rotation)
    {
        var source = meshes[mesh]; var bounds = source.sharedMesh.bounds;
        var obj = new GameObject("crypt-" + mesh); obj.transform.SetParent(parent.transform, false);
        obj.transform.localRotation = rotation;
        var scale = new Vector3(size.x / bounds.size.x, size.y / bounds.size.y, size.z / bounds.size.z);
        obj.transform.localScale = scale;
        obj.transform.localPosition = center - rotation * Vector3.Scale(bounds.center, scale);
        obj.AddComponent<MeshFilter>().sharedMesh = source.sharedMesh;
        var renderer = obj.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = source.GetComponent<MeshRenderer>().sharedMaterials;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        renderer.receiveShadows = true;
        return obj;
    }
    internal static void Build(GameObject root)
    {
        Load();
        var visuals = new GameObject("crypt-architecture"); visuals.transform.SetParent(root.transform, false);
        // Неровные края штатных камней не заполняют свой bounds целиком. Сплошной задний слой
        // из штатного пола закрывает стыки; он не меняет коллайдеры и навигацию.
        foreach (var rect in MiniBossArenaPlan.Floor)
        {
            var center = new Vector3((rect.X0 + rect.X1) / 2, -.56f, (rect.Z0 + rect.Z1) / 2);
            var size = new Vector3(rect.X1 - rect.X0 + .1f, 1, rect.Z1 - rect.Z0 + .1f);
            Fit(visuals, "stonefloor2", center, size, Quaternion.identity);
            center.y = 6.56f;
            Fit(visuals, "stonefloor2", center, size, Quaternion.Euler(180, 0, 0));
        }
        foreach (var rect in MiniBossArenaPlan.Floor)
        for (float x = rect.X0; x < rect.X1; x += 2)
        for (float z = rect.Z0; z < rect.Z1; z += 2)
        {
            float width = Math.Min(2, rect.X1 - x), length = Math.Min(2, rect.Z1 - z);
            Fit(visuals, "stonefloor2", new Vector3(x + width / 2, -.255f, z + length / 2),
                new Vector3(width, .5f, length), Quaternion.identity);
            Fit(visuals, "stonefloor2", new Vector3(x + width / 2, 6.255f, z + length / 2),
                new Vector3(width, .5f, length), Quaternion.Euler(180, 0, 0));
        }
        // Соседние метровые отрезки оболочки объединяются в штатные каменные секции длиной до 4 м.
        foreach (var group in MiniBossArenaPlan.Walls().GroupBy(w => (w.AlongX, Cross: w.AlongX ? w.Z : w.X)))
        {
            var points = group.Select(w => w.AlongX ? w.X : w.Z).OrderBy(v => v).ToArray();
            for (int i = 0; i < points.Length;)
            {
                int count = 1;
                while (count < 4 && i + count < points.Length && points[i + count] == points[i] + count) count++;
                float middle = points[i] - .5f + count / 2f;
                var backing = group.Key.AlongX ? new Vector3(middle, 3, group.Key.Cross) :
                    new Vector3(group.Key.Cross, 3, middle);
                Fit(visuals, "stonefloor2", backing, new Vector3(count + .12f, .08f, 6.2f),
                    Quaternion.Euler(90, group.Key.AlongX ? 0 : 90, 0));
                Fit(visuals, "stonefloor2", backing, new Vector3(count + .12f, .08f, 6.2f),
                    Quaternion.Euler(-90, group.Key.AlongX ? 0 : 90, 0));
                for (int row = 0; row < 2; row++)
                {
                    var center = group.Key.AlongX ? new Vector3(middle, 1.5f + row * 3, group.Key.Cross) :
                        new Vector3(group.Key.Cross, 1.5f + row * 3, middle);
                    Fit(visuals, "stonewall", center, new Vector3(.3f, 3.08f, count + .08f),
                        Quaternion.Euler(0, group.Key.AlongX ? 90 : 0, 0));
                }
                i += count;
            }
        }
        // Штатные каменные столбы и плита образуют проём, сохраняя ширину 4 м и высоту 4,4 м.
        foreach (float x in new[] { -2.3f, 2.3f })
            Fit(visuals, "stonepillar", new Vector3(x, 2.2f, 0), new Vector3(.6f, 4.4f, .7f),
                Quaternion.identity);
        Fit(visuals, "stoneslab", new Vector3(0, 5.2f, 0), new Vector3(4.05f, 1.6f, .3f),
            Quaternion.identity);
        foreach (var p in MiniBossArenaPlan.Pedestals)
            Fit(visuals, "stoneslab", new Vector3(p.X, .2f, p.Z), new Vector3(1.5f, .4f, 1.5f),
                Quaternion.identity);
        foreach (var p in new[] { (1.84f, -18f), (-1.84f, -6f), (12.84f, 8f), (7.16f, 20f),
                     (30.84f, 20f), (19f, 30.84f) })
            Fit(visuals, "polySurface1", new Vector3(p.Item1, 2.6f, p.Item2),
                new Vector3(.18f, 1.1f, .18f), Quaternion.identity);
        var volume = new GameObject("crypt-environment"); volume.transform.SetParent(root.transform, false);
        var trigger = volume.AddComponent<BoxCollider>(); trigger.isTrigger = true;
        trigger.center = new Vector3(14, 3, 3.5f); trigger.size = new Vector3(34, 8, 55);
        var env = volume.AddComponent<EnvZone>(); env.m_environment = "Crypt"; env.m_force = true;
    }
    internal static object Catalog()
    {
        var rooms = new List<object>();
        foreach (var data in DungeonDB.GetRooms().Where(r => r.m_enabled &&
                     (r.m_theme & Room.Theme.ForestCrypt) != 0))
        {
            data.m_prefab.Load();
            try
            {
                var root = data.RoomInPrefab.transform;
                rooms.Add(new { name = data.m_prefab.Name, size = ForestFixture.V(data.RoomInPrefab.m_size),
                    meshes = root.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null)
                        .Select(f => new {
                        path = Path(root, f.transform), mesh = f.sharedMesh.name,
                        position = ForestFixture.V(root.InverseTransformPoint(f.transform.position)),
                        rotation = ForestFixture.V(f.transform.eulerAngles),
                        scale = ForestFixture.V(f.transform.lossyScale),
                        center = ForestFixture.V(f.sharedMesh.bounds.center),
                        size = ForestFixture.V(f.sharedMesh.bounds.size),
                        materials = f.GetComponent<Renderer>()?.sharedMaterials.Select(m =>
                            new { m.name, shader = m.shader.name }).ToArray() }).ToArray() });
            }
            finally { data.m_prefab.Release(); }
        }
        var skeleton = ZNetScene.instance.GetPrefab("Skeleton").GetComponent<Humanoid>();
        return new { rooms,
            weapons = skeleton.m_randomWeapon.Select(p => p == null ? "" : p.name).ToArray(),
            sets = skeleton.m_randomSets.Select(s => s.m_items.Select(p => p.name).ToArray()).ToArray(),
            pieces = new[] { "piece_cookingstation", "wood_stack", "stone_pile", "piece_workbench",
                "portal_wood" }.Select(name => {
                    var obj = ZNetScene.instance.GetPrefab(name); var p = obj?.GetComponent<Piece>();
                    return new { name, exists = obj != null, allowed = p?.m_allowedInDungeons,
                        station = p?.m_craftingStation?.name,
                        colliders = obj?.GetComponentsInChildren<Collider>(true).Select(c => new {
                            c.name, trigger = c.isTrigger, bounds = ForestFixture.V(c.bounds.size) }).ToArray() };
                }).ToArray() };
    }
    internal static object Status()
    {
        var all = ForestFixture.AllZdos().Values;
        return new {
            dungeon_build = ZoneSystem.instance.GetGlobalKey(GlobalKeys.DungeonBuild),
            workbenches = all.Count(z => z.GetString(MiniBossTrial.HubKey) == "workbench"),
            portals = all.Count(z => z.GetString(MiniBossTrial.HubKey) == "portal"),
            probe_barriers = all.Count(z => z.GetLong(ZDOVars.s_creator) == 1L),
            runes = all.Where(z => z.GetString(MiniBossTrial.RuneKey).Length > 0).Select(z => new {
                scale = z.GetFloat(MiniBossArenaFixtures.RuneScaleKey, .2f),
                yaw = z.GetRotation().eulerAngles.y,
                revised = z.GetBool(MiniBossArenaFixtures.RevisionKey) }).ToArray(),
            loaded = UnityEngine.Object.FindObjectsByType<MiniBossArena>(FindObjectsSortMode.None).Select(a => new {
                renderers = a.GetComponentsInChildren<MeshRenderer>().Length,
                meshes = a.GetComponentsInChildren<MeshFilter>().GroupBy(f => f.sharedMesh.name)
                    .ToDictionary(g => g.Key, g => g.Count()),
                environment = a.GetComponentInChildren<EnvZone>()?.m_environment }).ToArray(),
            actors = Character.GetAllCharacters().Where(MiniBossArena.Applies).Select(c => new {
                kind = Utils.GetPrefabName(c.gameObject), fixed_weapon = MiniBossRuntime.Data(c)!
                    .GetString(MiniBossArenaFixtures.WeaponKey),
                weapon = MiniBossAttackProfile.Weapon(c).m_dropPrefab.name,
                windup = MiniBossAttackProfile.Windup(c, MiniBossAttackProfile.Weapon(c)) }).ToArray()
        };
    }
    internal static object PortalProbe(Character boss, Vector3 origin)
    {
        var portal = UnityEngine.Object.FindObjectsByType<WearNTear>(FindObjectsSortMode.None)
            .Single(w => MiniBossArenaFixtures.Portal(w) &&
                Vector3.Distance(w.transform.position, origin + new Vector3(0, .08f, -22)) < 3);
        var data = MiniBossRuntime.Data(portal)!; float before = data.GetFloat(ZDOVars.s_health, portal.m_health);
        var hit = MiniBossAttackProfile.Damage(boss); hit.m_damage.m_blunt = 5000;
        AccessTools.Method(typeof(WearNTear), "RPC_Damage").Invoke(portal, new object[] { 0L, hit });
        float afterNormal = data.GetFloat(ZDOVars.s_health, portal.m_health);
        MiniBossRock.Launch(boss, portal.transform.position, 511000);
        var rock = UnityEngine.Object.FindObjectsByType<MiniBossRock>(FindObjectsSortMode.None).Single();
        rock.Explode(portal.transform.position + Vector3.up);
        float afterExplosion = data.GetFloat(ZDOVars.s_health, portal.m_health);
        return new { before, afterNormal, afterExplosion, passed = before == afterNormal && before == afterExplosion };
    }
    private static string Path(Transform root, Transform value) => value == root ? "" :
        Path(root, value.parent) + "/" + value.name;
}
