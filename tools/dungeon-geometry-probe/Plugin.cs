using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DungeonGeometryProbe;

[BepInPlugin(Guid, "Dungeon Geometry Probe", "0.1.0")]
public sealed class Plugin : BaseUnityPlugin
{
    private const string Guid = "ubermodpack.dungeon-geometry-probe";
    private readonly Harmony harmony = new Harmony(Guid);
    private readonly List<object> rows = new List<object>();
    private string output = "";

    private void Awake()
    {
        if (!Config.Bind("Probe", "Enabled", false, "Только отдельный мир технической проверки.").Value) return;
        string world = Config.Bind("Probe", "ExpectedWorld", "DungeonGeometry", "Разрешённое имя мира.").Value;
        StartCoroutine(Run(world));
    }

    private IEnumerator Run(string expectedWorld)
    {
        float deadline = Time.realtimeSinceStartup + 180;
        while (ZNetScene.instance == null || DungeonDB.instance == null || DungeonDB.GetRooms().Count == 0 ||
               ZoneSystem.instance == null || ZNet.instance == null)
        {
            if (Time.realtimeSinceStartup > deadline) { Logger.LogError("GEOMETRY_TIMEOUT"); yield break; }
            yield return new WaitForSeconds(1);
        }
        var worldMethod = AccessTools.Method(typeof(ZNet), "GetWorldName");
        string world = (string)worldMethod.Invoke(worldMethod.IsStatic ? null : ZNet.instance, null);
        if (!ZNet.instance.IsServer() || world != expectedWorld)
        {
            Logger.LogError("Отказ: нужен отдельный заданный серверный мир");
            yield break;
        }
        output = Path.Combine(Paths.ConfigPath, "DungeonGeometryProbe");
        Directory.CreateDirectory(output);
        GenerationHooks.Install(harmony);
        var anchors = LocationAnchors.Read();
        File.WriteAllText(Path.Combine(output, "anchors.json"),
            JsonConvert.SerializeObject(anchors, Formatting.Indented));
        var prefabs = (Dictionary<int, GameObject>)AccessTools.Field(typeof(ZNetScene), "m_namedPrefabs")
            .GetValue(ZNetScene.instance);
        var generators = prefabs.Values.Select(p => p.GetComponent<DungeonGenerator>())
            .Where(g => g != null && g.m_algorithm == DungeonGenerator.Algorithm.Dungeon)
            .OrderBy(g => g.name, StringComparer.Ordinal).ToArray();
        File.WriteAllText(Path.Combine(output, "inventory.json"), JsonConvert.SerializeObject(generators.Select(g => new
        {
            prefab = g.name, themes = g.m_themes.ToString(), min_rooms = g.m_minRooms, max_attempts = g.m_maxRooms,
            bounds = new[] { g.m_zoneSize.x, g.m_zoneSize.y, g.m_zoneSize.z },
            original_position = new[] { g.m_originalPosition.x, g.m_originalPosition.y, g.m_originalPosition.z },
            required = g.m_requiredRooms, required_min = g.m_minRequiredRooms
        }), Formatting.Indented));
        Logger.LogInfo("GEOMETRY_INVENTORY: " + string.Join(", ", generators.Select(g => g.name)));
        foreach (var template in generators)
            foreach (bool deep in new[] { false, true })
                foreach (int seed in new[] { 1001, 1002, 1003, 1004, 1005, 1006, 1001 })
                {
                    rows.Add(RunOne(template, deep, seed, anchors));
                    Write(false);
                    yield return null;
                }
        Write(true);
        harmony.UnpatchSelf();
        Logger.LogInfo("GEOMETRY_PROBE_COMPLETE: " + rows.Count);
    }

    private object RunOne(DungeonGenerator template, bool deep, int seed,
        Dictionary<string, LocationAnchors.Anchor> anchors)
    {
        GameObject? instance = null;
        try
        {
            if (!anchors.TryGetValue(template.name, out var anchor))
                throw new InvalidOperationException("Не найдена штатная локация с исходным положением входа");
            instance = Object.Instantiate(template.gameObject, anchor.Position, anchor.Rotation);
            var generator = instance.GetComponent<DungeonGenerator>();
            generator.m_originalPosition = anchor.Original;
            if (deep)
            {
                generator.m_minRooms = Math.Max(generator.m_minRooms, 48);
                generator.m_maxRooms = Math.Max(generator.m_maxRooms, 160);
            }
            GenerationHooks.Active = generator;
            GenerationHooks.Deep = deep;
            GenerationHooks.Snapshot = null;
            GenerationHooks.Blocked.Clear();
            GenerationHooks.Spawned.Clear();
            GenerationHooks.Origins.Clear();
            var watch = Stopwatch.StartNew();
            DungeonGenerator.m_forceSeed = seed;
            generator.Generate(ZoneSystem.SpawnMode.Ghost);
            watch.Stop();
            var snapshot = GenerationHooks.Snapshot ?? throw new InvalidOperationException("Не перехвачен граф комнат");
            GenerationHooks.Active = null;
            // Восстановление через штатные Load/Spawn, без сохранённого m_placeOrder.
            AccessTools.Method(typeof(DungeonGenerator), "Load").Invoke(generator, null);
            AccessTools.Method(typeof(DungeonGenerator), "Spawn").Invoke(generator, null);
            var restored = GeometryCapture.Capture(generator, generator.GetComponentsInChildren<Room>(), false);
            bool reloaded = restored.fingerprint == snapshot.fingerprint &&
                restored.max_depth == snapshot.max_depth && restored.edges.Count == snapshot.edges.Count &&
                restored.edges.Zip(snapshot.edges, (a, b) => a.SequenceEqual(b)).All(equal => equal) &&
                restored.rooms.Select(r => r.depth).SequenceEqual(snapshot.rooms.Select(r => r.depth));
            return new { prefab = template.name, profile = deep ? "depth-first" : "vanilla", seed,
                duration_ms = watch.Elapsed.TotalMilliseconds, reload_matches = reloaded,
                snapshot, structural_pass = snapshot.disconnected == 0 && snapshot.overlap_pairs == 0 &&
                    snapshot.outside_bounds == 0 && snapshot.open_sockets == 0 && snapshot.required_satisfied,
                proposed_depth_pass = snapshot.max_depth >= 16 && snapshot.branching_rooms >= 2 };
        }
        catch (Exception error)
        {
            Logger.LogError("GEOMETRY_CASE_FAILED: " + template.name + " " + seed + " " + error);
            return new { prefab = template.name, profile = deep ? "depth-first" : "vanilla", seed,
                error = error.ToString() };
        }
        finally
        {
            GenerationHooks.Active = null;
            foreach (var view in GenerationHooks.Spawned)
                if (view != null && view.IsValid() && view.IsOwner()) ZNetScene.instance.Destroy(view.gameObject);
            GenerationHooks.Spawned.Clear();
            GenerationHooks.Origins.Clear();
            if (instance != null) ZNetScene.instance.Destroy(instance);
        }
    }

    private void Write(bool complete) => File.WriteAllText(Path.Combine(output, "geometry-results.json"),
        JsonConvert.SerializeObject(new { observed_at_utc = DateTime.UtcNow.ToString("O"), complete,
            scope = "headless-room-graph-and-bounds", gameplay_accepted = false, rows }, Formatting.Indented));

    private void OnDestroy() => harmony.UnpatchSelf();
}
