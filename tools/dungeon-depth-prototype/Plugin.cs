using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace DungeonDepthPrototype;

[BepInPlugin(Guid, "Dungeon Depth Prototype", "0.2.1")]
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
        // Меню клиента может оставаться открытым сколько угодно. Каждый вход — отдельная сессия.
        while (true)
        {
            while (!Ready(expectedWorld)) yield return new WaitForSeconds(1);
            var session = ZNet.instance;
            yield return new WaitForSeconds(3);
            if (session == null || session != ZNet.instance || !Ready(expectedWorld)) continue;
            var report = new Dictionary<string, object>
            {
                ["observed_at_utc"] = DateTime.UtcNow.ToString("O"), ["prototype_version"] = "0.2.1",
                ["phase"] = phase, ["scope"] = probe ? "headless-real-container-probe" : "runtime-initialization",
                ["gameplay_accepted"] = false, ["two_client_tested"] = false
            };
            ClientFixture? fixture = null;
            try
            {
                if (probe && !session.IsServer())
                    throw new InvalidOperationException("ProbeSuite разрешён только на сервере");
                GuardedChestReward.Rules = JsonConvert.DeserializeObject<RewardRules>(
                    File.ReadAllText(Path.Combine(Paths.ConfigPath, "dungeon-rewards.json")))!;
                GuardedChestReward.Rules.Validate();
                EpicLootBridge.Verify();
                EpicLootBridge.Install(harmony!);
                PovBridge.Install(harmony!);
                GuardedChestReward.Install(harmony!);
                if (Config.Bind("ClientFixture", "Enabled", false,
                        "Площадка и приватная диагностика только для DungeonClientRewards.").Value)
                    fixture = gameObject.AddComponent<ClientFixture>();
                if (probe) ProbeSuite.Run(report, phase);
                report["passed"] = true;
                Logger.LogInfo(probe ? "DUNGEON_REWARDS_PASS: " + phase : "DUNGEON_REWARDS_READY");
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
            while (session != null && session == ZNet.instance && Ready(expectedWorld))
                yield return new WaitForSeconds(1);
            if (fixture != null) Destroy(fixture);
            harmony?.UnpatchSelf();
            Logger.LogInfo("DUNGEON_REWARDS_SESSION_CLOSED");
        }
    }

    private static bool Ready(string expectedWorld) => ZNet.instance != null && ZNetScene.instance != null &&
        ObjectDB.instance != null && ObjectDB.instance.GetItemPrefab("Coins") != null && ZoneSystem.instance != null &&
        (ZNet.instance.IsServer() || Player.m_localPlayer != null) &&
        ZNet.instance.GetWorldName() == expectedWorld;

    private void OnDestroy() => harmony?.UnpatchSelf();
}
