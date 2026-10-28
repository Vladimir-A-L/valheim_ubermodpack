using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace DungeonDepthPrototype;

[BepInPlugin(Guid, "Dungeon Depth Prototype", "0.1.0")]
[BepInDependency(PovBridge.Guid, BepInDependency.DependencyFlags.HardDependency)]
public sealed class Plugin : BaseUnityPlugin
{
    internal const string Guid = "ubermodpack.dungeon-depth-prototype";
    internal static double RareFloorChance = 0.5;
    private Harmony? harmony;

    private void Awake()
    {
        if (!Config.Bind("Probe", "Enabled", false, "Только для одноразового технического мира.").Value) return;
        RareFloorChance = Config.Bind("Reward", "RareFloorReferenceChance", 0.5,
            "Минимум EpicLoot Rare, когда расчётная вероятность PoV Rare достигает этого значения.").Value;
        if (double.IsNaN(RareFloorChance) || RareFloorChance <= 0 || RareFloorChance > 1)
        {
            Logger.LogError("RareFloorReferenceChance должен быть в диапазоне (0, 1]");
            return;
        }
        string expectedWorld = Config.Bind("Probe", "ExpectedWorld", "DungeonPrototype",
            "Отказ от испытания при другом имени мира.").Value;
        harmony = new Harmony(Guid);
        try
        {
            StartCoroutine(Run(expectedWorld));
        }
        catch (Exception error)
        {
            harmony.UnpatchSelf();
            Logger.LogError("Отказ включения прототипа: " + error);
        }
    }

    private IEnumerator Run(string expectedWorld)
    {
        float deadline = Time.realtimeSinceStartup + 180;
        while (ZNet.instance == null || ZNetScene.instance == null || ObjectDB.instance == null ||
               ObjectDB.instance.GetItemPrefab("SwordBronze") == null)
        {
            if (Time.realtimeSinceStartup > deadline) { Logger.LogError("Таймаут готовности мира"); yield break; }
            yield return new WaitForSeconds(1);
        }
        var report = new Dictionary<string, object>
        {
            ["observed_at_utc"] = DateTime.UtcNow.ToString("O"),
            ["prototype_version"] = "0.1.0",
            ["pov_version"] = "5.3.6",
            ["scope"] = "headless-object-probe",
            ["gameplay_accepted"] = false,
            ["two_client_tested"] = false
        };
        try
        {
            var getWorld = AccessTools.Method(typeof(ZNet), "GetWorldName");
            string actualWorld = (string)getWorld.Invoke(getWorld.IsStatic ? null : ZNet.instance, null);
            if (!ZNet.instance.IsServer() || actualWorld != expectedWorld)
                throw new InvalidOperationException("Испытание разрешено только на заданном отдельном серверном мире");
            PovBridge.Install(harmony!);
            ProbeSuite.Run(report);
            report["passed"] = true;
            Logger.LogInfo("DUNGEON_PROBE_PASS");
        }
        catch (Exception error)
        {
            harmony?.UnpatchSelf();
            report["passed"] = false;
            report["error"] = error.ToString();
            Logger.LogError("DUNGEON_PROBE_FAIL: " + error);
        }
        string output = Path.Combine(Paths.ConfigPath, "DungeonDepthPrototype");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "probe-result.json"),
            JsonConvert.SerializeObject(report, Formatting.Indented));
    }

    private void OnDestroy() => harmony?.UnpatchSelf();
}
