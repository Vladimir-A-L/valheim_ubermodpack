using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace DungeonGeometryProbe;

internal static class GenerationHooks
{
    internal static DungeonGenerator? Active;
    internal static bool Deep;
    internal static GeometryCapture.Snapshot? Snapshot;
    internal static readonly List<ZNetView> Spawned = new List<ZNetView>();
    internal static readonly HashSet<RoomConnection> Blocked = new HashSet<RoomConnection>();
    internal static readonly Dictionary<ZNetView, Room> Origins = new Dictionary<ZNetView, Room>();
    internal static RoomConnection? Selected;

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(DungeonGenerator), "Save"),
            prefix: new HarmonyMethod(typeof(GenerationHooks), nameof(BeforeSave)));
        harmony.Patch(AccessTools.Method(typeof(DungeonGenerator), "GetOpenConnection"),
            prefix: new HarmonyMethod(typeof(GenerationHooks), nameof(ChooseConnection)));
        harmony.Patch(AccessTools.Method(typeof(DungeonGenerator), "PlaceOneRoom"),
            postfix: new HarmonyMethod(typeof(GenerationHooks), nameof(AfterAttempt)));
        harmony.Patch(AccessTools.Method(typeof(ZNetView), "Awake"),
            postfix: new HarmonyMethod(typeof(GenerationHooks), nameof(SpawnedView)));
        var placement = AccessTools.GetDeclaredMethods(typeof(DungeonGenerator))
            .Single(m => m.Name == "PlaceRoom" && m.ReturnType == typeof(Room));
        harmony.Patch(placement, prefix: new HarmonyMethod(typeof(GenerationHooks), nameof(BeforePlacement)),
            postfix: new HarmonyMethod(typeof(GenerationHooks), nameof(AfterPlacement)));
    }

    private static void BeforePlacement(out int __state) => __state = Spawned.Count;

    private static void AfterPlacement(DungeonGenerator __instance, Room __result, int __state)
    {
        if (__instance != Active || __result == null) return;
        for (int i = __state; i < Spawned.Count; i++) Origins[Spawned[i]] = __result;
    }

    private static void SpawnedView(ZNetView __instance)
    {
        if (Active != null && __instance.IsValid()) Spawned.Add(__instance);
    }

    private static void BeforeSave(DungeonGenerator __instance)
    {
        if (__instance != Active) return;
        var rooms = (List<Room>)AccessTools.Field(typeof(DungeonGenerator), "m_placedRooms").GetValue(null);
        Snapshot = GeometryCapture.Capture(__instance, rooms.ToArray(), true);
    }

    private static bool ChooseConnection(DungeonGenerator __instance, ref RoomConnection __result)
    {
        if (__instance != Active || !Deep) return true;
        var open = (List<RoomConnection>)AccessTools.Field(typeof(DungeonGenerator), "m_openConnections")
            .GetValue(null);
        var candidates = open.Where(c => c != null && !Blocked.Contains(c)).ToList();
        if (candidates.Count == 0) { __result = null!; Selected = null; return false; }
        var rooms = (List<Room>)AccessTools.Field(typeof(DungeonGenerator), "m_placedRooms").GetValue(null);
        // Сначала пробуем получить длинный маршрут, затем заполняем оставшиеся ранние развилки.
        bool spine = rooms.Max(r => r.m_placeOrder) < 17;
        int desired = spine ? candidates.Max(c => c.m_placeOrder) : candidates.Min(c => c.m_placeOrder);
        candidates = candidates.Where(c => c.m_placeOrder == desired).ToList();
        Selected = candidates[UnityEngine.Random.Range(0, candidates.Count)];
        __result = Selected;
        return false;
    }

    private static void AfterAttempt(DungeonGenerator __instance, bool __result)
    {
        if (__instance == Active && Deep && !__result && Selected != null) Blocked.Add(Selected);
    }
}
