using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace DungeonDepthPrototype;

// Ограниченное окно стенда. Паузы модели/сервера не выдаются за клиентский FPS.
internal sealed class RadialMetrics : MonoBehaviour
{
    private static readonly List<float> frames = new List<float>();
    private static readonly Queue<object> generations = new Queue<object>();
    private static bool collecting;
    private static int total;
    internal static void Install(GameObject host, Harmony harmony)
    {
        if (!Plugin.Instance.Config.Bind("ForestCrypt", "RadialDiagnostics", false).Value) return;
        if (host.GetComponent<RadialMetrics>() == null) host.AddComponent<RadialMetrics>();
        harmony.Patch(AccessTools.Method(typeof(DungeonGenerator), "Generate",
                new[] { typeof(int), typeof(ZoneSystem.SpawnMode) }),
            prefix: new HarmonyMethod(typeof(RadialMetrics), nameof(Before)),
            finalizer: new HarmonyMethod(typeof(RadialMetrics), nameof(After)));
    }
    private void Update()
    {
        if (!collecting) return;
        total++;
        if (frames.Count < 20000) frames.Add(Time.unscaledDeltaTime * 1000);
    }
    internal static void Reset() { frames.Clear(); total = 0; collecting = true; generations.Clear(); }
    internal static object Read()
    {
        var sorted = frames.OrderBy(f => f).ToArray();
        return new { observed_at_utc = DateTime.UtcNow.ToString("O"), frames = sorted.Length, total,
            frame_mean_ms = sorted.Length == 0 ? 0 : sorted.Average(),
            frame_p95_ms = sorted.Length == 0 ? 0 : sorted[(int)((sorted.Length - 1) * .95)],
            frame_max_ms = sorted.Length == 0 ? 0 : sorted.Last(), generations = generations.ToArray(),
            zdo = ForestFixture.AllZdos().Count, characters = Character.GetAllCharacters().Count,
            heap_bytes = GC.GetTotalMemory(false) };
    }
    private static void Before(out System.Diagnostics.Stopwatch __state) =>
        __state = System.Diagnostics.Stopwatch.StartNew();
    private static void After(DungeonGenerator __instance, ZoneSystem.SpawnMode mode,
        System.Diagnostics.Stopwatch __state, Exception? __exception)
    {
        if (!collecting) return;
        if (generations.Count >= 128) generations.Dequeue();
        generations.Enqueue(new { prefab = Utils.GetPrefabName(__instance.gameObject),
            ms = __state.ElapsedMilliseconds, mode = mode.ToString(), error = __exception?.Message,
            outcome = RadialEntrance.Current?.Outcome ?? "vanilla", seed = __instance.m_generatedSeed,
            position = ForestFixture.V(__instance.transform.position),
            radial = ForestRuntime.Zdo(__instance).GetString(RadialRuntime.Key) });
    }
}
