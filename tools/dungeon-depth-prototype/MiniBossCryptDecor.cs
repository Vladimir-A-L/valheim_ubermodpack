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
        MiniBossShell.Reset();
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
        if (root.transform.Find("crypt-architecture") != null) return;
        Load();
        MiniBossShell.Build(root, meshes["stonewall"].GetComponent<MeshRenderer>().sharedMaterial);
        var visuals = new GameObject("crypt-architecture"); visuals.transform.SetParent(root.transform, false);
        // Видимые плиты сохранены; их неровную подложку заменяет замкнутый собственный mesh.
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
        // Швы кладки соседних рядов разнесены на 2 м; за кладкой находится одна замкнутая оболочка.
        foreach (var group in MiniBossArenaPlan.Walls()
                     .Where(w => !(w.AlongX && w.Z == MiniBossArenaPlan.ConnectionZ))
                     .GroupBy(w => (w.AlongX, Cross: w.AlongX ? w.Z : w.X)))
        {
            var points = group.Select(w => w.AlongX ? w.X : w.Z).OrderBy(v => v).ToArray();
            for (int i = 0; i < points.Length;)
            {
                int end = i + 1;
                while (end < points.Length && points[end] == points[end - 1] + 1) end++;
                float start = points[i] - .5f, finish = points[end - 1] + .5f;
                bool along = group.Key.AlongX;
                float cross = group.Key.Cross;
                float sign = MiniBossArenaPlan.FloorAt(along ? points[i] : cross + .5f,
                    along ? cross + .5f : points[i]) ? -1 : 1;
                Vector3 Center(float middle, float y, float offset) => along ?
                    new Vector3(middle, y, cross + sign * offset) :
                    new Vector3(cross + sign * offset, y, middle);
                for (int row = 0; row < 3; row++)
                for (float edge = start - (row % 2) * 2; edge < finish; edge += 4)
                {
                    float left = Math.Max(start, edge), right = Math.Min(finish, edge + 4);
                    Fit(visuals, "stonewall", Center((left + right) / 2, 1 + row * 2, .08f + row * .015f),
                        new Vector3(.42f, 2.13f, right - left + .16f),
                        Quaternion.Euler(0, along ? 90 : 0, 0));
                }
                i = end;
            }
        }
        // Пол принятого коридора не меняется. Снаружи его плиты переходят к штатной толщине пола склепа.
        for (int z = -28; z < -24; z += 2)
        for (int x = -2; x < 2; x += 2)
        {
            float depth = z == -28 ? 1.001338f : .5f;
            Fit(visuals, "stonefloor2", new Vector3(x + 1, -depth / 2 - .005f, z + 1),
                new Vector3(2, depth, 2), Quaternion.identity);
            Fit(visuals, "stonefloor2", new Vector3(x + 1, 6.255f, z + 1),
                new Vector3(2, .5f, 2), Quaternion.Euler(180, 0, 0));
        }
        var cap = root.transform.Find(MiniBossConnection.CapName).gameObject;
        Fit(cap, "stonewall", new Vector3(0, 1.2f, -28), new Vector3(.3f, 2.4f, 2),
            Quaternion.Euler(0, 90, 0));
        foreach (float x in new[] { -1.5f, 1.5f })
            Fit(visuals, "stonepillar", new Vector3(x, 1.2f, -28), new Vector3(1, 2.4f, .3f),
                Quaternion.identity);
        Fit(visuals, "stoneslab", new Vector3(0, 4.2f, -28), new Vector3(4, 3.6f, .3f),
            Quaternion.identity);
        // Штатные каменные столбы и плита образуют проём, сохраняя ширину 4 м и высоту 4,4 м.
        foreach (float x in new[] { -2.3f, 2.3f })
            Fit(visuals, "stonepillar", new Vector3(x, 2.2f, 0), new Vector3(.6f, 4.4f, .7f),
                Quaternion.identity);
        Fit(visuals, "stoneslab", new Vector3(0, 5.2f, 0), new Vector3(4.05f, 1.6f, .3f),
            Quaternion.identity);
        foreach (var p in MiniBossArenaPlan.Pedestals)
            Fit(visuals, "stoneslab", new Vector3(p.X, .2f, p.Z), new Vector3(1.5f, .4f, 1.5f),
                Quaternion.identity).name = MiniBossValuePlacement.PedestalName;
        foreach (var p in new[] { (1.84f, -18f), (-1.84f, -6f), (12.84f, 8f), (7.16f, 20f),
                     (30.84f, 20f), (19f, 30.84f) })
        {
            Fit(visuals, "polySurface1", new Vector3(p.Item1, 2.6f, p.Item2),
                new Vector3(.18f, 1.1f, .18f), Quaternion.identity);
            Torch(visuals, new Vector3(p.Item1, 3.12f, p.Item2));
        }
        if (ForestAtmosphere.Connected(root.transform)) return;
        var volume = new GameObject("crypt-environment"); volume.transform.SetParent(root.transform, false);
        var trigger = volume.AddComponent<BoxCollider>(); trigger.isTrigger = true;
        // Переход раньше оставался за пределами зоны и получал наружное освещение.
        trigger.center = new Vector3(14, 3, 1.5f); trigger.size = new Vector3(34, 8, 59);
        var env = volume.AddComponent<EnvZone>(); env.m_environment = "Crypt"; env.m_force = true;
    }
    private static void Torch(GameObject parent, Vector3 position)
    {
        var source = held.SelectMany(r => r.RoomInPrefab.GetComponentsInChildren<Transform>(true))
            .First(t => t.name == "CastleKit_groundtorch");
        var holder = new GameObject("crypt-torch-flame"); holder.SetActive(false);
        holder.transform.SetParent(parent.transform, false); holder.transform.localPosition = position;
        // Только штатные визуальные эффекты. Поведение топлива, коллайдеры и сетевые объекты не копируются.
        foreach (Transform child in source)
        {
            if (child.GetComponentsInChildren<ParticleSystem>(true).Length == 0 &&
                child.GetComponentsInChildren<Light>(true).Length == 0) continue;
            var obj = UnityEngine.Object.Instantiate(child.gameObject, holder.transform, false);
            foreach (var component in obj.GetComponentsInChildren<Component>(true))
                if (component is MonoBehaviour || component is Collider || component is Rigidbody)
                    UnityEngine.Object.DestroyImmediate(component);
            obj.SetActive(true);
        }
        // Эффекты исходного факела размещены относительно его основания; переносим пламя к вершине mesh.
        var particles = holder.GetComponentsInChildren<ParticleSystem>(true);
        if (particles.Length == 0) throw new InvalidOperationException("В штатном факеле не найдено пламя");
        Vector3 flame = holder.transform.InverseTransformPoint(particles[0].transform.position);
        foreach (Transform child in holder.transform) child.localPosition -= flame;
        foreach (var light in holder.GetComponentsInChildren<Light>(true))
        { light.enabled = true; light.shadows = LightShadows.None; }
        holder.SetActive(true);
        foreach (var particle in particles) { var main = particle.main; main.loop = true; particle.Play(); }
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
