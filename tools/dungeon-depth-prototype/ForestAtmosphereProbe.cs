using System;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DungeonDepthPrototype;

internal static class ForestAtmosphereProbe
{
    internal static object Run(JObject command)
    {
        if (!ZNet.instance.IsServer() || ZNet.instance.GetPeers().Any())
            throw new InvalidOperationException("Нужна отдельная копия без игроков");
        string phase = command.Value<string>("phase") ?? "coverage";
        if (phase == "hub")
        {
            MiniBossArenaProbe.Reference = MiniBossTrial.Hub;
            ZNet.instance.SetReferencePosition(MiniBossTrial.Hub);
            return new { reference_set = true };
        }
        if (phase == "access-status")
        {
            var all = ForestFixture.AllZdos().Values.Where(z => z.GetString(ResourceArenaAccess.Key).Length > 0)
                .ToArray();
            var portals = all.Where(z => z.GetPrefab() == "portal_wood".GetStableHashCode()).ToArray();
            return new { passed = all.Length == 3 && portals.Length == 2 && portals.All(z => {
                    var target = ZDOMan.instance.GetZDO(z.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal));
                    return target != null && target != z &&
                        target.GetString(ResourceArenaAccess.Key) == z.GetString(ResourceArenaAccess.Key);
                }), objects = all.Length, portals = portals.Select(z => new {
                    tag = z.GetString(ZDOVars.s_tag), position = ForestFixture.V(z.GetPosition()),
                    scale = ForestFixture.V(z.GetVec3("scale", Vector3.one)),
                    linked = z.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) != ZDOID.None }).ToArray() };
        }
        if (phase == "access-plan") return ResourceArenaAccess.Prepare();
        if (phase == "access-create") return ResourceArenaAccess.Create();
        var data = ForestFixture.AllZdos().Values.Single(z =>
            z.GetString(MiniBossConnection.LinkKey).Length > 0 && z.GetString(ForestRuntime.LayoutKey).Length > 0);
        var generator = MiniBossConnection.Instance(data)!;
        var rooms = (JArray)JObject.Parse(data.GetString(ForestRuntime.LayoutKey))["rooms"]!;
        var section = rooms.Single(r => r.Value<bool>("miniboss"));
        var arena = MiniBossConnection.Instance(MiniBossConnection.Arena(data))!;
        var points = rooms.SelectMany(r => ((JArray)r["volumes"]!).Select(v =>
            ForestRuntime.V(ForestAtmosphere.Point(v["center"]!))))
            .Concat(rooms.SelectMany(r => ((JArray)r["sockets"]!).Select(v =>
                ForestRuntime.V(ForestAtmosphere.Point(v["position"]!)) + Vector3.up))).ToArray();
        int covered = points.Count(ForestAtmosphere.Contains);
        var method = AccessTools.Method(typeof(EnvMan), "GetEnvironmentOverride");
        string? Select(Vector3 p)
        {
            ForestAtmosphere.ProbePosition = p;
            try { return (string?)method.Invoke(EnvMan.instance, null); }
            finally { ForestAtmosphere.ProbePosition = null; }
        }
        string? outsideBefore = Select(MiniBossTrial.Hub);
        bool nativeSelection = points.All(p => Select(p) == "Crypt");
        string? outsideAfter = Select(MiniBossTrial.Hub);
        var sectionPoint = ForestRuntime.V(ForestAtmosphere.Point(section["position"]!)) + Vector3.up;
        var mainPoint = ForestRuntime.V(ForestAtmosphere.Point(rooms[0]["position"]!));
        var generatorRegion = generator.GetComponent<ForestAtmosphere>();
        var arenaRegion = arena.GetComponent<ForestAtmosphere>();
        bool arenaFirst, generatorFirst, unloaded, restored;
        try
        {
            generatorRegion.enabled = false;
            arenaFirst = ForestAtmosphere.Contains(sectionPoint) && !ForestAtmosphere.Contains(mainPoint);
            generatorRegion.enabled = true; arenaRegion.enabled = false;
            generatorFirst = ForestAtmosphere.Contains(sectionPoint) && ForestAtmosphere.Contains(mainPoint);
            generatorRegion.enabled = false;
            unloaded = !ForestAtmosphere.Contains(sectionPoint) && !ForestAtmosphere.Contains(mainPoint);
        }
        finally { generatorRegion.enabled = true; arenaRegion.enabled = true; }
        restored = ForestAtmosphere.Contains(mainPoint) && ForestAtmosphere.Contains(sectionPoint);
        var env = EnvMan.instance.m_environments.Single(e => e.m_name == "Crypt");
        bool noForceZone = arena.GetComponentInChildren<EnvZone>() == null;
        return new { passed = covered == points.Length && nativeSelection && outsideBefore == outsideAfter &&
                !ForestAtmosphere.Contains(MiniBossTrial.Hub) && arenaFirst && generatorFirst && unloaded &&
                restored && noForceZone && env.m_lightIntensityDay == 0 && env.m_lightIntensityNight == 0,
            samples = points.Length, covered, native_selection = nativeSelection,
            outside_before = outsideBefore, outside_after = outsideAfter, arena_first = arenaFirst,
            generator_first = generatorFirst, unloaded, restored, no_force_zone = noForceZone,
            sunlight_day = env.m_lightIntensityDay, sunlight_night = env.m_lightIntensityNight,
            ambient_loop = env.m_ambientLoop == null ? "" : env.m_ambientLoop.name,
            ambient_list = env.m_ambientList, music_day = env.m_musicDay, music_night = env.m_musicNight,
            scope = "headless-selection-and-coverage-not-client-audio" };
    }
}
