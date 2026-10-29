using System.IO;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace DungeonDepthPrototype;

// Скрывается только малая панель: режим карты, исследование мира и клавиша M остаются штатными.
internal static class NavigationProfile
{
    private sealed class Settings
    {
        public bool HideMinimap = false;
        public string ExpectedWorld = "ForestCryptQA";
    }
    private static Settings settings = new Settings();
    private static readonly Harmony Harmony = new Harmony(Plugin.Guid + ".navigation");
    private static Minimap? hidden;

    internal static void Install()
    {
        string path = Path.Combine(Paths.ConfigPath, "forest-navigation.json");
        if (File.Exists(path)) settings = JsonConvert.DeserializeObject<Settings>(File.ReadAllText(path))!;
        if (!settings.HideMinimap) return;
        Harmony.Patch(AccessTools.Method(typeof(Minimap), "Update"),
            postfix: new HarmonyMethod(typeof(NavigationProfile), nameof(Hide)));
        Harmony.Patch(AccessTools.Method(typeof(Minimap), "SetMapMode"),
            postfix: new HarmonyMethod(typeof(NavigationProfile), nameof(Hide)));
    }

    private static void Hide(Minimap __instance)
    {
        if (ZNet.instance == null || ZNet.instance.GetWorldName() != settings.ExpectedWorld ||
            Player.m_localPlayer == null) return;
        if (__instance.m_smallRoot.activeSelf)
        {
            __instance.m_smallRoot.SetActive(false);
            hidden = __instance;
        }
    }

    internal static void Uninstall()
    {
        Harmony.UnpatchSelf();
        if (hidden != null && hidden.m_mode == Minimap.MapMode.Small && !Game.m_noMap)
            hidden.m_smallRoot.SetActive(true);
        hidden = null;
    }
}
