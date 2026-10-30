using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DungeonDepthPrototype;

// Локальный выбор штатного окружения: не пишет ZDO и не оставляет глобальный forceEnv после телепорта.
internal sealed class ForestAtmosphere : MonoBehaviour
{
    private static readonly HashSet<ForestAtmosphere> active = new HashSet<ForestAtmosphere>();
    private ForestPlan.Volume[] volumes = Array.Empty<ForestPlan.Volume>();
    private string saved = "";
    internal static Vector3? ProbePosition;
    private void OnEnable() => active.Add(this);
    private void OnDisable() => active.Remove(this);
    private void OnDestroy() => active.Remove(this);

    internal static void Install(Harmony harmony) => harmony.Patch(
        AccessTools.Method(typeof(EnvMan), "GetEnvironmentOverride"),
        prefix: new HarmonyMethod(typeof(ForestAtmosphere), nameof(Select)));

    private static bool Select(EnvMan __instance, ref string __result)
    {
        if (!string.IsNullOrEmpty(__instance.m_debugEnv)) return true;
        var player = Player.m_localPlayer;
        if (!ProbePosition.HasValue && (player == null || player.InIntro())) return true;
        var position = ProbePosition ?? player.transform.position;
        if (!Contains(position)) return true;
        __result = "Crypt";
        return false;
    }

    internal static bool Connected(Component owner) =>
        MiniBossRuntime.Data(owner)?.GetString(MiniBossConnection.LinkKey).Length > 0;

    internal static bool Contains(Vector3 position)
    {
        foreach (var region in active)
        {
            if (region == null || !region.isActiveAndEnabled) continue;
            region.Refresh();
            if (InteriorCoverage.Contains(region.volumes, ForestRuntime.P(position))) return true;
        }
        return false;
    }

    internal void Refresh()
    {
        var data = MiniBossRuntime.Data(this);
        if (data == null || !Connected(this)) { volumes = Array.Empty<ForestPlan.Volume>(); saved = ""; return; }
        string layout = data.GetString(ForestRuntime.LayoutKey);
        string signature = layout.Length > 0 ? layout : data.GetString(MiniBossConnection.LinkKey);
        if (signature == saved) return;
        if (layout.Length > 0)
            volumes = ((JArray)JObject.Parse(layout)["rooms"]!).SelectMany(r => (JArray)r["volumes"]!)
                .Select(v => new ForestPlan.Volume { Center = Point(v["center"]!), Size = Point(v["size"]!) })
                .ToArray();
        else
        {
            // Арена может загрузиться раньше генератора; её часть окружения всё равно доступна.
            var link = JObject.Parse(signature);
            var section = new ForestPlan.Placement { Template = ForestPlan.BossTemplate(""),
                Position = Point(link["Origin"]!), Turn = link.Value<int>("Turn") };
            volumes = section.Bounds.ToArray();
        }
        saved = signature;
    }

    internal static ForestPlan.Point Point(JToken value) =>
        new ForestPlan.Point(value[0]!.Value<float>(), value[1]!.Value<float>(), value[2]!.Value<float>());
}
