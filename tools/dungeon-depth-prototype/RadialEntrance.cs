using System;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace DungeonDepthPrototype;

internal sealed class RadialEntranceState
{
    public int Schema = 1;
    public float Height;
    public string Failure = "", Outcome = "selected";
    public bool ForcedFailure;
    public RadialSnapshot Snapshot = new RadialSnapshot();
}

// Один интерьер на штатную зону; четыре слоя раздвигают соседние объёмы 120×96×120 м.
internal static class RadialEntrance
{
    internal const string Key = RadialRuntime.Key + ".entrance";
    internal static readonly Vector3 Size = new Vector3(RadialAllocation.Width, RadialAllocation.Height,
        RadialAllocation.Width);
    internal static RadialEntranceState? Current;
    internal static Vector2s? FailureZone;
    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(LocationProxy), "SetLocation"),
            prefix: new HarmonyMethod(typeof(RadialEntrance), nameof(SaveProxy)));
        harmony.Patch(AccessTools.Method(typeof(LocationProxy), "SpawnLocation"),
            prefix: new HarmonyMethod(typeof(RadialEntrance), nameof(BeforeProxy)),
            finalizer: new HarmonyMethod(typeof(RadialEntrance), nameof(AfterProxy)));
    }
    internal static RadialEntranceState? Choose(Location location, int seed, Vector3 position,
        Quaternion rotation, ZoneSystem.SpawnMode mode)
    {
        if (mode == ZoneSystem.SpawnMode.Client) return Current;
        if (!ZNet.instance.IsServer() || !Plugin.Instance.Config.Bind(
                "ForestCrypt", "IntegratedEncounters", false).Value) return null;
        var config = RadialRuntime.Configuration;
        var type = config?.Types.SingleOrDefault(t => t.Type == "ForestCrypt");
        if (type == null) return null;
        bool probe = Plugin.Instance.Config.Bind("ForestCrypt", "RadialDiagnostics", false).Value &&
            Plugin.Instance.Config.Bind("ForestCrypt", "ProbeRadii", false).Value &&
            !ZNet.instance.GetPeers().Any();
        if (!type.Enabled && !probe) return null;
        if (!probe && config!.Fallback != "vanilla")
            throw new InvalidOperationException("Не выбран запасной вариант обычного входа");
        var entries = location.GetComponentsInChildren<Teleport>(true).Where(t =>
            location.transform.InverseTransformPoint(t.transform.position).y < 1000).ToArray();
        if (entries.Length != 1) throw new InvalidOperationException("Нужен однозначный наружный вход");
        Vector3 outside = position + rotation * location.transform.InverseTransformPoint(entries[0].transform.position);
        var selection = probe ? type.ProbeEntrance(outside.x, outside.z, seed, config!.Rewards) :
            type.Select(outside.x, outside.z, seed, config!.Rewards);
        var zone = ZoneSystem.GetZone(position);
        bool fail = probe && FailureZone.HasValue && FailureZone.Value == zone;
        if (fail) FailureZone = null;
        return new RadialEntranceState { Height = RadialAllocation.Altitude(zone.x, zone.y),
            ForcedFailure = fail,
            Snapshot = new RadialSnapshot { Selection = selection, Rewards = config.Rewards } };
    }
    private static void SaveProxy(LocationProxy __instance)
    {
        if (Current != null) MiniBossRuntime.Data(__instance)!.Set(Key, JsonConvert.SerializeObject(Current));
    }
    private static void BeforeProxy(LocationProxy __instance, out RadialEntranceState? __state)
    {
        __state = Current;
        string json = MiniBossRuntime.Data(__instance)?.GetString(Key) ?? "";
        Current = json.Length == 0 ? null : JsonConvert.DeserializeObject<RadialEntranceState>(json);
        if (Current != null)
        {
            Current.Snapshot.Validate();
            if (Current.Schema != 1 || !new[] { 3500f, 3628f, 3756f, 3884f }.Contains(Current.Height))
                throw new InvalidOperationException("Повреждён сохранённый слой интерьера");
        }
    }
    private static void AfterProxy(RadialEntranceState? __state) => Current = __state;
}
