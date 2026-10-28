using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace DungeonDepthPrototype;

[BepInPlugin(Guid, "Dungeon Depth Prototype", "0.2.0")]
[BepInDependency(PovBridge.Guid, BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency(EpicLootBridge.Guid, BepInDependency.DependencyFlags.HardDependency)]
public sealed class Plugin : BaseUnityPlugin
{
    internal const string Guid = "ubermodpack.dungeon-depth-prototype";
    internal static Plugin Instance = null!;
    private Harmony? harmony;
    internal static void LogError(string message) => Instance.Logger.LogError(message);
    internal static void LogInfo(string message) => Instance.Logger.LogInfo(message);

    private void Awake()
    {
        Instance = this;
        bool probe = Config.Bind("Probe", "Enabled", false, "Автопроверки на отдельном сервере.").Value;
        bool enabled = Config.Bind("Runtime", "Enabled", false, "Механизм явно назначенных сундуков.").Value;
        if (!probe && !enabled) return;
        string expectedWorld = Config.Bind("Runtime", "ExpectedWorld", "DungeonRewards",
            "Изоляция от постоянного мира; одинаково на сервере и клиентах.").Value;
        string phase = Config.Bind("Probe", "Phase", "create", "create / resume / verify").Value;
        harmony = new Harmony(Guid);
        StartCoroutine(Run(expectedWorld, probe, phase));
    }

    private IEnumerator Run(string expectedWorld, bool probe, string phase)
    {
        float deadline = Time.realtimeSinceStartup + 240;
        while (ZNet.instance == null || ZNetScene.instance == null || ObjectDB.instance == null ||
               ObjectDB.instance.GetItemPrefab("Coins") == null || ZoneSystem.instance == null)
        {
            if (Time.realtimeSinceStartup > deadline) { Logger.LogError("Таймаут готовности мира"); yield break; }
            yield return new WaitForSeconds(1);
        }
        // Дать зависимостям завершить отложенную загрузку таблиц и конфигов.
        yield return new WaitForSeconds(3);
        var report = new Dictionary<string, object>
        {
            ["observed_at_utc"] = DateTime.UtcNow.ToString("O"), ["prototype_version"] = "0.2.0",
            ["phase"] = phase, ["scope"] = "headless-real-container-probe",
            ["gameplay_accepted"] = false, ["two_client_tested"] = false
        };
        try
        {
            var getWorld = AccessTools.Method(typeof(ZNet), "GetWorldName");
            string actualWorld = (string)getWorld.Invoke(getWorld.IsStatic ? null : ZNet.instance, null);
            if (actualWorld != expectedWorld || probe && !ZNet.instance.IsServer())
                throw new InvalidOperationException("Не тот тестовый мир или режим");
            GuardedChestReward.Rules = JsonConvert.DeserializeObject<RewardRules>(
                File.ReadAllText(Path.Combine(Paths.ConfigPath, "dungeon-rewards.json")))!;
            GuardedChestReward.Rules.Validate();
            EpicLootBridge.Verify();
            EpicLootBridge.Install(harmony!);
            PovBridge.Install(harmony!);
            GuardedChestReward.Install(harmony!);
            if (probe) ProbeSuite.Run(report, phase);
            report["passed"] = true;
            Logger.LogInfo("DUNGEON_REWARDS_PASS: " + phase);
        }
        catch (Exception error)
        {
            harmony?.UnpatchSelf();
            report["passed"] = false;
            report["error"] = error.ToString();
            Logger.LogError("DUNGEON_REWARDS_FAIL: " + error);
        }
        if (probe)
        {
            string output = Path.Combine(Paths.ConfigPath, "DungeonDepthPrototype");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "rewards-" + phase + ".json"),
                JsonConvert.SerializeObject(report, Formatting.Indented));
        }
    }

    private void OnDestroy() => harmony?.UnpatchSelf();
}
