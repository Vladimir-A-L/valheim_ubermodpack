using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DungeonDepthPrototype;

// Сетевой prefab состоит только из собственной геометрии; материал берётся из закреплённой игры.
internal sealed class MiniBossArena : MonoBehaviour
{
    private System.Collections.IEnumerator Start()
    {
        while (DungeonDB.instance == null) yield return null;
        MiniBossCryptDecor.Build(gameObject);
    }
    internal const string Key = "uberdungeon.arena.v1", OriginKey = Key + ".origin";
    internal const string StateKey = Key + ".state", ValueKey = Key + ".value";
    internal const string MaximumKey = Key + ".flesh-maximum";
    internal static readonly int Solid = LayerMask.GetMask("Default", "static_solid", "Default_small",
        "piece", "terrain", "blocker", "character", "character_net", "character_ghost");
    private static Harmony? early;
    internal static void InstallPrefabs()
    {
        if (!MiniBossTrial.Enabled || early != null) return;
        early = new Harmony(Plugin.Guid + ".arena-prefabs");
        early.Patch(AccessTools.Method(typeof(ZNetScene), "Awake"),
            postfix: new HarmonyMethod(typeof(MiniBossArena), nameof(Register)) { priority = Priority.Last });
        early.Patch(AccessTools.Method(typeof(Character), "SetupMaxHealth"),
            prefix: new HarmonyMethod(typeof(MiniBossArena), nameof(KeepMaximum)));
    }
    private static bool KeepMaximum(Character __instance) =>
        (MiniBossRuntime.Data(__instance)?.GetFloat(MaximumKey) ?? 0) <= 0;
    internal static void Uninstall() { early?.UnpatchSelf(); early = null; MiniBossCryptDecor.Reset(); }

    private static void Register(ZNetScene __instance)
    {
        var source = __instance.GetPrefab("stone_floor_2x2");
        if (source == null) throw new InvalidOperationException("Нет материала пола арены");
        var material = source.GetComponentInChildren<MeshRenderer>(true).sharedMaterial;
        var holder = new GameObject("UberArenaPrefabs"); holder.SetActive(false);
        GameObject Prefab(string name)
        {
            var obj = new GameObject(name); obj.transform.SetParent(holder.transform, false);
            var view = obj.AddComponent<ZNetView>(); view.m_persistent = true;
            view.m_type = ZDO.ObjectType.Default;
            __instance.m_prefabs.Add(obj);
            ((Dictionary<int, GameObject>)AccessTools.Field(typeof(ZNetScene), "m_namedPrefabs")
                .GetValue(__instance)).Add(name.GetStableHashCode(), obj);
            return obj;
        }
        var arena = Prefab(MiniBossArenaPlan.Prefab); arena.AddComponent<MiniBossArena>();
        foreach (var r in MiniBossArenaPlan.Floor)
        {
            Box(arena, material, "floor", new Vector3((r.X0 + r.X1) / 2f, -.25f, (r.Z0 + r.Z1) / 2f),
                new Vector3(r.X1 - r.X0, .5f, r.Z1 - r.Z0));
            Box(arena, material, "ceiling", new Vector3((r.X0 + r.X1) / 2f, 6.25f, (r.Z0 + r.Z1) / 2f),
                new Vector3(r.X1 - r.X0, .5f, r.Z1 - r.Z0));
        }
        foreach (var wall in MiniBossArenaPlan.Walls())
            Box(arena, material, "wall", new Vector3(wall.X, 3, wall.Z),
                wall.AlongX ? new Vector3(1.02f, 6, .3f) : new Vector3(.3f, 6, 1.02f));
        // Заполняет проём над дверью; коробка двери остаётся разрушаемым отдельным объектом.
        Box(arena, material, "lintel", new Vector3(0, 5.2f, 0), new Vector3(4, 1.6f, .3f));
        foreach (var p in MiniBossArenaPlan.Pedestals)
            Box(arena, material, "pedestal", new Vector3(p.X, .2f, p.Z), new Vector3(1.5f, .4f, 1.5f));
        foreach (var p in new[] { (0f, -18f), (0f, -6f), (0f, 8f), (10f, 8f), (14f, 18f), (25f, 25f) })
        {
            var obj = new GameObject("crypt-light"); obj.transform.SetParent(arena.transform, false);
            obj.transform.localPosition = new Vector3(p.Item1, 4.5f, p.Item2);
            var light = obj.AddComponent<Light>(); light.type = LightType.Point;
            light.color = new Color(.8f, .72f, .55f); light.range = 15; light.intensity = 1.2f;
            light.shadows = LightShadows.None;
        }
        var rock = Prefab(MiniBossArenaPlan.Projectile); rock.AddComponent<MiniBossRock>();
        var visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        visual.name = "obsidian-rock"; visual.transform.SetParent(rock.transform, false);
        visual.transform.localScale = new Vector3(.65f, .5f, .6f);
        Object.DestroyImmediate(visual.GetComponent<Collider>());
        visual.GetComponent<Renderer>().sharedMaterial = material;
        var glow = rock.AddComponent<Light>(); glow.color = new Color(.35f, .6f, 1);
        glow.range = 4; glow.intensity = 2;
        var treasure = Prefab(MiniBossArenaPlan.ValuePrefab);
        var shape = treasure.AddComponent<BoxCollider>();
        shape.center = Vector3.up * .2f; shape.size = new Vector3(.5f, .4f, .5f);
        var pickable = treasure.AddComponent<PickableItem>();
        pickable.m_randomItemPrefabs = new[] { "Amber", "AmberPearl", "Ruby", "Coins" }.Select(name =>
            new PickableItem.RandomItem { m_itemPrefab = __instance.GetPrefab(name).GetComponent<ItemDrop>(),
                m_stackMin = name == "Coins" ? 10 : 1, m_stackMax = name == "Coins" ? 30 : 1 }).ToArray();
    }

    private static void Box(GameObject root, Material material, string name, Vector3 position, Vector3 size)
    {
        var obj = new GameObject(name); obj.AddComponent<BoxCollider>();
        obj.layer = LayerMask.NameToLayer("static_solid"); obj.transform.SetParent(root.transform, false);
        obj.transform.localPosition = position; obj.transform.localScale = size;
    }

    internal static Vector3 Origin(Character boss) => MiniBossRuntime.Data(boss)!.GetVec3(OriginKey, Vector3.zero);
    internal static bool Applies(Character c) => MiniBossRuntime.IsBoss(c) &&
        MiniBossRuntime.Data(c)!.GetBool(Key);
    internal static bool Inside(Vector3 origin, Vector3 position)
    { var p = position - origin; return MiniBossArenaPlan.Inside(p.x, p.y, p.z); }
    internal static bool Near(Vector3 origin, Vector3 position)
    { var p = position - origin; return MiniBossArenaPlan.InFightZone(p.x, p.y, p.z); }
    internal static Vector3 Center(Vector3 origin) => origin + new Vector3(19, .08f, 21);
    internal static bool Shielded(Vector3 from, Vector3 to) => Physics.RaycastAll(from,
        (to - from).normalized, Vector3.Distance(from, to), Solid, QueryTriggerInteraction.Ignore)
        .Any(h => h.collider.GetComponentInParent<MiniBossArena>() != null);

    internal static object Status() => Character.GetAllCharacters().Where(Applies).Select(c => new
    {
        name = c.m_name, state = JsonConvert.DeserializeObject<MiniBossArenaState>(
            MiniBossRuntime.Data(c)!.GetString(StateKey, "{}")),
        hp = c.GetHealth(), maximum = c.GetMaxHealth(),
        ward = MiniBossRuntime.Data(c)!.GetFloat("pt.ward.cur"),
        ward_max = MiniBossRuntime.Data(c)!.GetFloat("pt.ward.max"),
        position = ForestFixture.V(c.transform.position), owner = c.GetComponent<ZNetView>().IsOwner()
    }).ToArray();
}
