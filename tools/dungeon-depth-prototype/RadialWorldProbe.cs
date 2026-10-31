using System;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DungeonDepthPrototype;

// Приватное исследование отдельных миров. Никаких перестроений посещённых интерьеров.
internal static class RadialWorldProbe
{
    internal static object Run(JObject command)
    {
        if (!ZNet.instance.IsServer() || ZNet.instance.GetPeers().Any() ||
            !Plugin.Instance.Config.Bind("ForestCrypt", "RadialDiagnostics", false,
                "Исследование новых отдельных миров без игроков.").Value)
            throw new InvalidOperationException("Нужен отдельный радиальный стенд без игроков");
        string phase = command.Value<string>("phase") ?? "locations";
        if (phase == "reload-config")
        {
            RadialRuntime.Load();
            return new { revision = RadialRuntime.Configuration?.Rewards.Revision };
        }
        if (phase == "metrics-reset") { RadialMetrics.Reset(); return new { reset = true }; }
        if (phase == "metrics") return RadialMetrics.Read();
        if (phase == "cleanup")
        {
            var tokens = command["tokens"]!.ToObject<string[]>()!.ToHashSet();
            return new { remaining = ForestFixture.AllZdos().Values.Count(z =>
                tokens.Contains(z.GetString(ForestRuntime.Tag))) };
        }
        if (phase == "fallback-at")
        {
            RadialEntrance.FailureZone = ZoneSystem.GetZone(new Vector3(command.Value<float>("x"), 0,
                command.Value<float>("z")));
            return new { armed = true };
        }
        if (phase == "visit")
        {
            var p = new Vector3(command.Value<float>("x"), command.Value<float?>("y") ?? 0,
                command.Value<float>("z"));
            MiniBossArenaProbe.Reference = p; ZNet.instance.SetReferencePosition(p);
            return new { reference_set = true };
        }
        if (phase == "status") return new {
            load_health = new { RadialLoadHealth.Loads, RadialLoadHealth.Wounded, RadialLoadHealth.Failures,
                RadialLoadHealth.WardLoads, RadialLoadHealth.WardFailures, RadialLoadHealth.StackFailures },
            zdo = ForestFixture.AllZdos().Count, active_characters = Character.GetAllCharacters().Count,
            generators = UnityEngine.Object.FindObjectsByType<DungeonGenerator>(FindObjectsSortMode.None)
                .Select(g => new { prefab = Utils.GetPrefabName(g.gameObject),
                    position = ForestFixture.V(g.transform.position), size = ForestFixture.V(g.m_zoneSize),
                    seed = g.m_generatedSeed, rooms = g.GetComponentsInChildren<Room>().Length }),
            saved_interiors = ForestFixture.AllZdos().Values.Where(z =>
                z.GetString(RadialRuntime.Key).Length > 0 || z.GetString(RadialEntrance.Key).Length > 0)
                .Select(z => new { position = ForestFixture.V(z.GetPosition()), prefab = z.GetPrefab(),
                    radial = z.GetString(RadialRuntime.Key), entrance = z.GetString(RadialEntrance.Key),
                    layout = z.GetString(ForestRuntime.LayoutKey) }),
            actor_profiles = ForestFixture.AllZdos().Values.Where(z =>
                z.GetString(RadialRuntime.ProfileKey).Length > 0).Select(z => new {
                    entity = z.GetString(CombatRecorder.EntityKey), token = z.GetString(ForestRuntime.Tag),
                    profile = z.GetString(RadialRuntime.ProfileKey), revision = z.GetString(RadialRuntime.RevisionKey),
                    health_factor = z.GetFloat(RadialRuntime.HealthKey),
                    damage_factor = z.GetFloat(RadialRuntime.DamageKey),
                    empowered = z.GetInt(RadialRuntime.EmpoweredKey),
                    maximum_health = z.GetFloat(ZDOVars.s_maxHealth),
                    maximum_ward = z.GetFloat("pt.ward.max"), ward = z.GetFloat("pt.ward.cur"),
                    health = z.GetFloat(ZDOVars.s_health, z.GetFloat(ZDOVars.s_maxHealth)),
                    modifiers = z.GetInt(PovBridge.ModsKey), boss = z.GetBool(MiniBossRuntime.BossKey) }),
            transitions = UnityEngine.Object.FindObjectsByType<Teleport>(FindObjectsSortMode.None)
                .Select(t => new { position = ForestFixture.V(t.transform.position),
                    linked = t.m_targetPoint != null,
                    target = t.m_targetPoint == null ? null : ForestFixture.V(t.m_targetPoint.transform.position) }),
            heap_bytes = GC.GetTotalMemory(false), frame_seconds = Time.unscaledDeltaTime };
        if (phase != "locations") throw new ArgumentException("Неизвестная проба наружных входов");
        if (!ZoneSystem.instance.LocationsGenerated) throw new InvalidOperationException("Список локаций не готов");
        var world = (World)AccessTools.Field(typeof(WorldGenerator), "m_world").GetValue(WorldGenerator.instance);
        var catalog = ZoneSystem.instance.m_locations.Where(l => l.m_enable).Select(l => {
            l.m_prefab.Load();
            try
            {
                var asset = l.m_prefab.Asset;
                var generators = asset.GetComponentsInChildren<DungeonGenerator>(true);
                var outside = asset.GetComponentsInChildren<Teleport>(true).Where(t =>
                    asset.transform.InverseTransformPoint(t.transform.position).y < 1000).ToArray();
                return new { location = l.m_prefab.Name, random_rotation = l.m_randomRotation,
                    forest = generators.Any(g => Utils.GetPrefabName(g.gameObject) == "DG_ForestCrypt"),
                    entrances = outside.Select(t => asset.transform.InverseTransformPoint(t.transform.position))
                        .ToArray(),
                    generators = generators.Select(g => new { prefab = Utils.GetPrefabName(g.gameObject),
                        position = ForestFixture.V(asset.transform.InverseTransformPoint(g.transform.position)),
                        size = ForestFixture.V(g.m_zoneSize) }).ToArray() };
            }
            finally { l.m_prefab.Release(); }
        }).Where(l => l.generators.Length > 0).ToArray();
        var locations = ZoneSystem.instance.m_locationInstances.Values.Select(l => {
            var template = catalog.FirstOrDefault(c => c.location == l.m_location.m_prefab.Name && c.forest);
            if (template == null) return null;
            var radii = template.entrances.SelectMany(p => Enumerable.Range(0, template.random_rotation ? 16 : 1)
                .Select(turn => l.m_position + Quaternion.Euler(0, turn * 22.5f, 0) * p))
                .Select(p => Math.Sqrt((double)p.x * p.x + (double)p.z * p.z)).ToArray();
            return new { location = template.location, position = ForestFixture.V(l.m_position),
                placed = l.m_placed, minimum_entrance_radius = radii.DefaultIfEmpty(0).Min(),
                maximum_entrance_radius = radii.DefaultIfEmpty(0).Max(), entrances = template.entrances.Length };
        }).Where(l => l != null).ToArray();
        return new { seed = world.m_seedName, seed_hash_verified = world.m_seed == world.m_seedName.GetStableHashCode(),
            world_generation_version = world.m_worldGenVersion,
            catalog = catalog.Select(c => new { c.location, c.random_rotation, c.forest,
                entrances = c.entrances.Select(ForestFixture.V).ToArray(), c.generators }), locations,
            note = "Непосещённый вход: диапазон всех штатных поворотов, не измеренный окончательный радиус" };
    }
}
