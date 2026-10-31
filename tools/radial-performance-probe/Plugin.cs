using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RadialPerformanceProbe;

// Ограниченный измеритель: отдельная DLL для сравнения старых версий либо встроенный QA-компонент.
#if EMBEDDED_PERFORMANCE
public sealed class Plugin : MonoBehaviour
#else
[BepInPlugin("ubermodpack.radial-performance-probe", "Radial Performance Probe", "0.1.0")]
[BepInDependency("ubermodpack.dungeon-depth-prototype")]
public sealed class Plugin : BaseUnityPlugin
#endif
{
    private sealed class Counter
    {
        internal long Calls, Ticks, Maximum, Exceptions;
        internal void Clear() { Calls = Ticks = Maximum = Exceptions = 0; }
    }
    private static readonly Dictionary<MethodBase, Counter> counters = new Dictionary<MethodBase, Counter>();
    private readonly List<double> frames = new List<double>(20000);
    private readonly List<object> timing = new List<object>(20000), census = new List<object>();
    private readonly List<string> missing = new List<string>();
    private readonly FrameTiming[] latest = new FrameTiming[1];
    private static bool measuring;
    private Harmony? harmony;
    private string id = "", mode = "baseline", folder = "";
    private double started, ends, lastFrame, nextPoll, nextCensus, warmupUntil;
    private long heapBefore, allocatedBefore;
    private int[] gcBefore = Array.Empty<int>();
    private TimeSpan cpuBefore;
    private ulong lastTiming;
    private bool armed, probeEnabled, allocationCounterAvailable;
#if EMBEDDED_PERFORMANCE
    private bool comparing, originalCache;
    private int comparePhase;
#endif
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private void Awake()
    {
#if EMBEDDED_PERFORMANCE
        probeEnabled = DungeonDepthPrototype.Plugin.Instance.Config.Bind("Performance", "Enabled", false,
            "Короткий приватный замер F8; сравнение кэша F9 только на QA-стенде.").Value;
#else
        probeEnabled = Config.Bind("Probe", "Enabled", false, "Только отдельная диагностическая копия.").Value;
#endif
        folder = Path.Combine(Paths.ConfigPath, "RadialPerformanceProbe");
    }
    private bool Ready => ZNet.instance != null && ZNetScene.instance != null &&
        ZNet.instance.GetWorldName() == "ForestCryptQA";
    private void Install()
    {
        harmony = new Harmony("ubermodpack.radial-performance-probe.timing");
        foreach (var spec in new[] {
            "DungeonDepthPrototype.ForestGuardRecovery:Update", "DungeonDepthPrototype.ForestGuardRecovery:Tick",
            "DungeonDepthPrototype.ForestGuardSpace:Refresh", "DungeonDepthPrototype.ForestGuardSpace:Valid",
            "DungeonDepthPrototype.ForestGuardSpace:Map", "DungeonDepthPrototype.ForestGuardWalkmap:Contains",
            "DungeonDepthPrototype.ForestAtmosphere:Contains",
            "DungeonDepthPrototype.MiniBossArenaController:FixedUpdate",
            "PassiveTree.RarityMods+Worn:Update", "PassiveTree.RarityMods2+Habits:Update",
            "PassiveTree.RarityMods2+Mark:Update", "PassiveTree.RarityMods2+Pool:Update",
            "PassiveTree.RarityMods2+Carve:Update", "PassiveTree.RarityMods2+Led:Update",
            "PassiveTree.RuneWard:Update", "PassiveTree.GroundTelegraph:LateUpdate",
            "MonsterAI:UpdateAI", "Character:CustomFixedUpdate", "ZNetScene:Update",
            "ZDOMan:Update", "LightLod:UpdateLights", "ZNet:Update" })
        {
            var parts = spec.Split(':'); var type = AccessTools.TypeByName(parts[0]);
            var method = type == null ? null : AccessTools.Method(type, parts[1]);
            if (method == null) { missing.Add(spec); continue; }
            counters[method] = new Counter();
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(Plugin), nameof(Before)),
                finalizer: new HarmonyMethod(typeof(Plugin), nameof(After)));
        }
        Directory.CreateDirectory(folder);
    }
    private static void Before(out long __state) => __state = measuring ? Stopwatch.GetTimestamp() : 0;
    private static void After(MethodBase __originalMethod, long __state, Exception? __exception)
    {
        if (__state == 0) return;
        long ticks = Stopwatch.GetTimestamp() - __state;
        var counter = counters[__originalMethod];
        counter.Calls++; counter.Ticks += ticks; counter.Maximum = Math.Max(counter.Maximum, ticks);
        if (__exception != null) counter.Exceptions++;
    }
    private void Update()
    {
        if (!probeEnabled) return;
        if (!Ready)
        {
            if (armed || measuring) Finish("session-ended");
            return;
        }
        if (harmony == null) Install();
#if EMBEDDED_PERFORMANCE
        if (Player.m_localPlayer != null && !armed && !measuring)
        {
            if (Input.GetKeyDown(KeyCode.F9))
            {
                comparing = true; comparePhase = 0;
                originalCache = DungeonDepthPrototype.ForestGuardSpace.CanonicalDimensions;
                DungeonDepthPrototype.ForestGuardSpace.SetCanonicalDimensions(false);
                Manual("cache-original");
            }
            else if (Input.GetKeyDown(KeyCode.F8)) Manual("current");
        }
#endif
        double now = Now;
        if (armed && now >= warmupUntil) Begin();
        if (measuring)
        {
            if (lastFrame > 0) frames.Add((now - lastFrame) * 1000);
            lastFrame = now;
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, latest) > 0 && latest[0].frameStartTimestamp != lastTiming)
            {
                var f = latest[0]; lastTiming = f.frameStartTimestamp;
                timing.Add(new { cpu_ms = f.cpuFrameTime, main_ms = f.cpuMainThreadFrameTime,
                    present_wait_ms = f.cpuMainThreadPresentWaitTime, render_ms = f.cpuRenderThreadFrameTime,
                    gpu_ms = f.gpuFrameTime });
            }
            if (now >= nextCensus)
            {
                census.Add(new { elapsed_seconds = Now - started,
                    characters = Character.GetAllCharacters().Count, census_ms = 0,
                    scope = "character-count-only" });
                nextCensus = now + 5;
            }
            if (now >= ends || frames.Count >= 20000) Finish("completed");
        }
        if (now < nextPoll) return;
        nextPoll = now + 1;
        string request = Path.Combine(folder, "request.json");
        if (!File.Exists(request)) return;
        try
        {
            var command = JObject.Parse(File.ReadAllText(request));
            string nextId = command.Value<string>("id") ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(nextId, "^[a-zA-Z0-9_-]{1,64}$"))
                throw new ArgumentException("Недопустимый id");
            string receipt = Path.Combine(folder, nextId + ".request.json");
            if (File.Exists(receipt)) throw new InvalidOperationException("Повторная команда");
            File.Move(request, receipt);
            if (measuring || armed) throw new InvalidOperationException("Измерение уже идёт");
            double seconds = command.Value<double?>("seconds") ?? 30;
            double warmup = command.Value<double?>("warmup") ?? 5;
            mode = command.Value<string>("mode") ?? "baseline";
            if (seconds < 10 || seconds > 120 || warmup < 0 || warmup > 60 || mode != "baseline")
                throw new ArgumentException("Нужны baseline, 10–120 секунд и прогрев 0–60 секунд");
            id = nextId; ends = seconds; warmupUntil = Now + warmup; armed = true;
        }
        catch (Exception e) { LogError("PERFORMANCE_REQUEST " + e.Message); }
    }
    private void Begin()
    {
        armed = false; frames.Clear(); timing.Clear(); census.Clear();
        foreach (var counter in counters.Values) counter.Clear();
        gcBefore = Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray();
        long allocationProbe = GC.GetAllocatedBytesForCurrentThread();
        GC.KeepAlive(new byte[128]);
        allocationCounterAvailable = GC.GetAllocatedBytesForCurrentThread() > allocationProbe;
        heapBefore = GC.GetTotalMemory(false); allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        Census(); started = Now; ends += started; lastFrame = 0; lastTiming = 0;
        nextCensus = started + 5; measuring = true;
    }
    private void Census()
    {
        long begin = Stopwatch.GetTimestamp();
        var characters = Character.GetAllCharacters();
        var particles = Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None);
        census.Add(new { elapsed_seconds = measuring ? Now - started : 0,
            characters = characters.Count, owned_characters = characters.Count(c => c.IsOwner()),
            generators = Object.FindObjectsByType<DungeonGenerator>(FindObjectsSortMode.None).Length,
            rooms = Object.FindObjectsByType<Room>(FindObjectsSortMode.None).Length,
            particle_systems = particles.Length, playing_particles = particles.Count(p => p.isPlaying),
            particles = particles.Sum(p => p.particleCount),
            lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None).Count(l => l.enabled),
            camera_position = Camera.main == null ? null : Coordinates(Camera.main.transform.position),
            camera_forward = Camera.main == null ? null : Coordinates(Camera.main.transform.forward),
            player_position = Player.m_localPlayer == null ? null :
                Coordinates(Player.m_localPlayer.transform.position),
            effects = new[] { "PassiveTree.GroundTelegraph", "PassiveTree.RarityMods2+Mark",
                "PassiveTree.RarityMods2+Carve", "PassiveTree.RarityMods2+Pool" }.Select(name => {
                    var type = AccessTools.TypeByName(name);
                    return new { type = name, count = type == null ? (int?)null :
                        Object.FindObjectsByType(type, FindObjectsSortMode.None).Length };
                }).ToArray(),
            walkmap_cache = CacheCensus(),
            census_ms = (Stopwatch.GetTimestamp() - begin) * 1000d / Stopwatch.Frequency });
    }
    private static float[] Coordinates(Vector3 value) => new[] { value.x, value.y, value.z };
    private static object[] CacheCensus()
    {
        var type = AccessTools.TypeByName("DungeonDepthPrototype.ForestGuardSpace");
        if (type == null) return Array.Empty<object>();
        return Object.FindObjectsByType(type, FindObjectsSortMode.None).Select(space => {
            var maps = (IDictionary)AccessTools.Field(type, "maps").GetValue(space);
            var dimensions = new List<object>();
            foreach (DictionaryEntry pair in maps)
            {
                var key = pair.Key; var keyType = key.GetType();
                dimensions.Add(new { room = ((Object)keyType.GetField("Item1")!.GetValue(key)).GetInstanceID(),
                    radius = (float)keyType.GetField("Item2")!.GetValue(key),
                    height = (float)keyType.GetField("Item3")!.GetValue(key) });
            }
            return (object)new { entries = maps.Count, dimensions };
        }).ToArray();
    }
    private void Finish(string outcome)
    {
        bool hadSamples = measuring;
        measuring = false; armed = false;
        if (!hadSamples)
        {
#if EMBEDDED_PERFORMANCE
            if (comparing) DungeonDepthPrototype.ForestGuardSpace.SetCanonicalDimensions(originalCache);
            comparing = false;
#endif
            return;
        }
        double duration = Now - started;
        var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
        var gc = Enumerable.Range(0, GC.MaxGeneration + 1)
            .Select(i => GC.CollectionCount(i) - gcBefore[i]).ToArray();
        long heapAfter = GC.GetTotalMemory(false), allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
        Census(); // Полная перепись после закрытия измеряемого интервала, без искусственного длинного кадра.
        var methods = counters.Select(p => new { method = p.Key.DeclaringType!.FullName + "." + p.Key.Name,
            calls = p.Value.Calls, total_ms = p.Value.Ticks * 1000d / Stopwatch.Frequency,
            maximum_ms = p.Value.Maximum * 1000d / Stopwatch.Frequency, exceptions = p.Value.Exceptions })
            .OrderByDescending(p => p.total_ms).ToArray();
        var sorted = frames.OrderBy(x => x).ToArray();
        double? Q(double q) => sorted.Length == 0 ? (double?)null :
            sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * q) - 1)];
        var report = new { schema = 1, observed_at_utc = DateTime.UtcNow.ToString("O"), id, mode, outcome,
            scope = ZNet.instance != null && ZNet.instance.IsDedicated() ? "dedicated-server" : "client",
            seconds = duration, frame_count = sorted.Length, median_ms = Q(.5), p95_ms = Q(.95), p99_ms = Q(.99),
            maximum_ms = Q(1), over_33_ms = sorted.Count(x => x > 33.333),
            over_50_ms = sorted.Count(x => x > 50), over_100_ms = sorted.Count(x => x > 100),
            process_cpu_percent = cpu.TotalSeconds / duration * 100, gc_collections = gc,
            managed_heap_before = heapBefore, managed_heap_after = heapAfter,
            main_thread_allocated_bytes = allocationCounterAvailable ? (long?)(allocatedAfter - allocatedBefore) : null,
            allocation_counter_available = allocationCounterAvailable,
            resolution = new[] { Screen.width, Screen.height }, graphics_api = SystemInfo.graphicsDeviceType.ToString(),
            processor = SystemInfo.processorType, graphics = SystemInfo.graphicsDeviceName,
            graphics_quality = QualitySettings.GetQualityLevel(), shadow_distance = QualitySettings.shadowDistance,
            shadows = QualitySettings.shadows.ToString(), vsync = QualitySettings.vSyncCount,
            target_fps = Application.targetFrameRate,
            frame_timing_samples = timing.Count,
            gpu_status = timing.Count == 0 ? "unavailable-no-samples" : "inspect-samples-zero-is-unavailable",
            methods, missing_methods = missing.ToArray(), census, frame_ms = frames, frame_timing = timing,
            timing_note = "Время методов включает вложенные вызовы; строки нельзя складывать. " +
                "Полная перепись выполняется вне интервала. Внутри записывается только число существ." };
        File.WriteAllText(Path.Combine(folder, id + ".json"), JsonConvert.SerializeObject(report, Formatting.Indented));
        LogInfo("PERFORMANCE_COMPLETE " + id);
#if EMBEDDED_PERFORMANCE
        if (comparing && outcome == "completed" && comparePhase == 0 && Ready)
        {
            comparePhase = 1; DungeonDepthPrototype.ForestGuardSpace.SetCanonicalDimensions(true);
            Manual("cache-fixed");
        }
        else
        {
            if (comparing) DungeonDepthPrototype.ForestGuardSpace.SetCanonicalDimensions(originalCache);
            comparing = false;
            if (Player.m_localPlayer != null)
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, "Замер сохранён: " + id);
        }
#endif
    }
#if EMBEDDED_PERFORMANCE
    private void Manual(string phase)
    {
        id = "manual-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + phase;
        mode = phase; ends = 30; warmupUntil = Now + 15; armed = true;
        Player.m_localPlayer.Message(MessageHud.MessageType.Center,
            "Замер " + (comparing ? (comparePhase + 1) + "/2" : "") + ": 15 с прогрева и 30 с записи");
    }
    private void LogInfo(string message) => DungeonDepthPrototype.Plugin.LogInfo(message);
    private void LogError(string message) => DungeonDepthPrototype.Plugin.LogError(message);
#else
    private void LogInfo(string message) => Logger.LogInfo(message);
    private void LogError(string message) => Logger.LogError(message);
#endif
    private void OnDestroy()
    {
        if (measuring || armed) Finish("plugin-destroyed");
        harmony?.UnpatchSelf(); counters.Clear();
    }
}
