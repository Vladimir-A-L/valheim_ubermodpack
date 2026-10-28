using System;
using System.IO;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace DungeonDepthPrototype;

// Изолированная ручная проверка: штатный god оставляет 1 HP, ghost всегда выключен.
internal sealed class ForestManual : MonoBehaviour
{
    private sealed class Settings { public bool Enabled = false, Record = false, StartFullOnce = false; }
    private static Settings settings = new Settings();
    private static string attempt = "";
    private static bool floorReached;
    private static Player? current;
    private float next;
    private static string Folder => Path.Combine(Paths.ConfigPath, "ForestCryptQA");
    private static bool Matches(Player? p) => settings.Enabled && p != null &&
        ZNet.instance != null && ZNet.instance.GetWorldName() == "ForestCryptQA" && p.GetPlayerName() == "RewardQA";

    internal static void Install(GameObject host)
    {
        ReadSettings();
        var harmony = new Harmony(Plugin.Guid + ".manual-test");
        harmony.Patch(AccessTools.Method(typeof(Player), "OnSpawned"),
            prefix: new HarmonyMethod(typeof(ForestManual), nameof(Spawned)));
        harmony.Patch(AccessTools.Method(typeof(Character), "ApplyDamage"),
            prefix: new HarmonyMethod(typeof(ForestManual), nameof(BeforeDamage)),
            postfix: new HarmonyMethod(typeof(ForestManual), nameof(AfterDamage)));
        host.AddComponent<ForestManual>();
    }

    private static void ReadSettings()
    {
        string path = Path.Combine(Paths.ConfigPath, "forest-manual-test.json");
        settings = File.Exists(path)
            ? JsonConvert.DeserializeObject<Settings>(File.ReadAllText(path))! : new Settings();
    }

    private static void Spawned(Player __instance)
    {
        if (!Matches(__instance)) return;
        __instance.SetGodMode(true); __instance.SetGhostMode(false);
    }

    private void Update()
    {
        if (Time.realtimeSinceStartup < next) return;
        next = Time.realtimeSinceStartup + .5f;
        try
        {
            ReadSettings();
            var player = Player.m_localPlayer;
            if (!Matches(player)) { current = null; attempt = ""; return; }
            Spawned(player!);
            if (!settings.Record) { attempt = ""; current = null; return; }
            const string freshStart = "uberdungeon.qa.manual.fresh-start.v1";
            if (settings.StartFullOnce && !player!.m_customData.ContainsKey(freshStart))
            {
                if (player.IsTeleporting() || !player.IsOnGround()) return;
                player.Heal(player.GetMaxHealth(), false);
                player.m_customData[freshStart] = DateTime.UtcNow.ToString("O");
            }
            if (current != player || attempt.Length == 0)
            {
                current = player; attempt = Guid.NewGuid().ToString("N"); floorReached = false;
                Record("attempt-start", new { health = player!.GetHealth(), max_health = player.GetMaxHealth(),
                    starts_at_floor = player.GetHealth() <= 1.001f });
            }
        }
        catch (Exception e) { Plugin.LogError("MANUAL_TEST " + e.Message); }
    }

    private static void BeforeDamage(Character __instance, out float __state)
    {
        __state = __instance == Player.m_localPlayer && Matches(Player.m_localPlayer) ? __instance.GetHealth() : -1;
    }

    private static void AfterDamage(Character __instance, HitData hit, float __state)
    {
        if (__state < 0 || !settings.Record || attempt.Length == 0 || __instance != current) return;
        float after = __instance.GetHealth();
        if (after >= __state && after > 1.001f) return;
        var attacker = hit.GetAttacker();
        var p = __instance.transform.position;
        bool first = !floorReached && after <= 1.001f;
        if (first) floorReached = true;
        Record(first ? "conditional-death" : "damage", new { before = __state, after,
            first_floor = first, position = new[] { p.x, p.y, p.z },
            attacker = attacker == null ? "environment" : Utils.GetPrefabName(attacker.gameObject) });
    }

    private static void Record(string kind, object value)
    {
        Directory.CreateDirectory(Folder);
        File.AppendAllText(Path.Combine(Folder, "manual-attempts.jsonl"), JsonConvert.SerializeObject(new
            { observed_at_utc = DateTime.UtcNow.ToString("O"), attempt, kind, value }) + Environment.NewLine);
    }

    internal static object Probe()
    {
        var player = Player.m_localPlayer;
        if (!Matches(player) || settings.Record || player!.IsDead() || player.IsTeleporting())
            throw new InvalidOperationException("Проба допустима только до начала ручной записи");
        float before = player.GetHealth();
        try
        {
            Spawned(player);
            player.SetHealth(10);
            var hit = new HitData { m_point = player.transform.position };
            hit.m_damage.m_damage = 100000;
            player.ApplyDamage(hit, false, false);
            float after = player.GetHealth();
            bool passed = Math.Abs(after - 1) < .001f && !player.IsDead() &&
                player.InGodMode() && !player.InGhostMode();
            if (!passed) throw new InvalidOperationException("Проверка god с порогом 1 HP не пройдена");
            return new { passed, damage_applied = true, health_at_floor = after,
                god = player.InGodMode(), ghost = player.InGhostMode(), excluded_from_attempts = true };
        }
        finally { player.SetHealth(before); }
    }
}
