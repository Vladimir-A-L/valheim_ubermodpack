using System;
using System.Linq;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DungeonDepthPrototype;

// Переход вне шести сегментов; физическое открытие зависит от готовности обеих сторон.
internal static class MiniBossConnection
{
    internal const string LinkKey = "uberdungeon.connection.v1", ArenaKey = LinkKey + ".arena";
    internal const string GeneratorKey = LinkKey + ".generator", GateKey = LinkKey + ".gate";
    internal const string EncounterKey = LinkKey + ".encounter", CompleteKey = LinkKey + ".complete";
    internal const string CandidateKey = LinkKey + ".candidate", ReleasedKey = LinkKey + ".released";
    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(ZDOMan), "HandleDestroyedZDO"),
            prefix: new HarmonyMethod(typeof(MiniBossConnection), nameof(DoorDestroyed)));
        harmony.Patch(AccessTools.Method(typeof(DungeonGenerator), "Spawn"),
            postfix: new HarmonyMethod(typeof(MiniBossConnection), nameof(RoomsLoaded)));
        harmony.Patch(AccessTools.Method(typeof(DungeonGenerator), "Awake"),
            postfix: new HarmonyMethod(typeof(MiniBossConnection), nameof(GeneratorLoaded)));
    }
    private static void DoorDestroyed(ZDOID uid)
    {
        var door = ZDOMan.instance.GetZDO(uid);
        if (door != null) Release(door);
    }
    internal static void Release(ZDO door)
    {
        if (!door.GetBool(ForestGates.GateKey)) return;
        var root = Arena(door);
        if (root != null) root.Set(ReleasedKey, true);
    }
    private static void RoomsLoaded(DungeonGenerator __instance)
    {
        __instance.GetComponent<MiniBossRoomLink>()?.Refresh();
    }
    private static void GeneratorLoaded(DungeonGenerator __instance)
    {
        if (__instance.GetComponent<MiniBossRoomLink>() == null)
            __instance.gameObject.AddComponent<MiniBossRoomLink>();
    }
    internal static void InstallMap(Harmony harmony)
    {
        var plugin = BepInEx.Bootstrap.Chainloader.PluginInfos.Values
            .FirstOrDefault(p => p.Metadata.GUID == "zl.dungeonmaps");
        var model = plugin?.Instance.GetType().Assembly.GetType("DungeonMaps.DungeonModel");
        if (model != null) harmony.Patch(AccessTools.Method(model, "Build"),
            prefix: new HarmonyMethod(typeof(MiniBossRoomLink), nameof(MiniBossRoomLink.MapRooms)));
    }
    internal static bool Ready(MiniBossArena arena, ZDO generator)
    {
        var data = MiniBossRuntime.Data(arena);
        return arena.gameObject.activeInHierarchy && data != null && data.GetBool(CompleteKey) &&
            data.GetString(LinkKey) == generator.GetString(LinkKey) &&
            data.GetString(ForestRuntime.Tag) == generator.GetString(ForestRuntime.Tag) &&
            arena.transform.Find("crypt-architecture") != null;
    }
    // DB2 перенумеровывает ZDOID при сохранении. Для постоянных ссылок используются token и роль.
    private static ZDO? Find(ZDO source, Func<ZDO, bool> predicate)
    {
        string token = source.GetString(ForestRuntime.Tag);
        if (token.Length == 0) return null;
        var all = (Dictionary<ZDOID, ZDO>)AccessTools.Field(typeof(ZDOMan), "m_objectsByID")
            .GetValue(ZDOMan.instance);
        ZDO? result = null;
        foreach (var candidate in all.Values)
        {
            if (candidate.GetString(ForestRuntime.Tag) != token || !predicate(candidate)) continue;
            if (result != null) return null; // Не открывать неоднозначную связь.
            result = candidate;
        }
        return result;
    }
    internal static ZDO? Arena(ZDO source) => Find(source,
        z => z.GetPrefab() == MiniBossArenaPlan.Prefab.GetStableHashCode());
    internal static ZDO? Generator(ZDO source) => Find(source,
        z => z.GetString(ForestRuntime.LayoutKey).Length > 0);
    internal static ZDO? Gate(ZDO source) => Find(source,
        z => z.GetBool(MiniBossArena.Key) && z.GetBool(ForestGates.GateKey));
    internal static GameObject? Instance(ZDO? data) => data == null ? null :
        ZNetScene.instance.FindInstance(data.m_uid);

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
    internal static bool TryConnect(MiniBossArena arena, RoomConnection otherSocket, Collider? loadingCap = null)
    {
        if (!otherSocket.gameObject.activeInHierarchy || otherSocket.GetComponentInParent<Room>() == null) return false;
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
                    QueryTriggerInteraction.Ignore).Any(c => c != cap && c != loadingCap)) return false;
        }
        arena.transform.Find(CapName).gameObject.SetActive(false);
        return true;
    }
}
