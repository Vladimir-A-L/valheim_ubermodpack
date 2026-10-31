using System;
using HarmonyLib;
using UnityEngine;

namespace DungeonDepthPrototype;

// Character.Awake заново считает полное HP, не учитывая уже сохранённое разделение RuneWard.
// Сохраняем оба значения только у загружаемой собственной радиальной охраны, не у нового prefab.
internal static class RadialLoadHealth
{
    internal sealed class Saved
    {
        internal float Maximum, Current, Factor;
    }
    internal static int Loads, Wounded, Failures;
    internal static void Install(Harmony harmony) => harmony.Patch(
        AccessTools.Method(typeof(Character), "Awake"),
        prefix: new HarmonyMethod(typeof(RadialLoadHealth), nameof(Before)) { priority = Priority.First },
        postfix: new HarmonyMethod(typeof(RadialLoadHealth), nameof(After)) { priority = Priority.Last });

    private static void Before(Character __instance, out Saved? __state)
    {
        __state = null;
        var data = MiniBossRuntime.Data(__instance);
        if (__instance.IsPlayer() || data == null || !__instance.GetComponent<ZNetView>().IsOwner() ||
            data.GetString(ForestRuntime.Tag).Length == 0 || data.GetString(RadialRuntime.ProfileKey).Length == 0)
            return;
        float maximum = data.GetFloat(ZDOVars.s_maxHealth, 0);
        float current = data.GetFloat(ZDOVars.s_health, maximum);
        if (maximum <= 0 || current <= 0 || float.IsNaN(maximum) || float.IsInfinity(maximum) ||
            float.IsNaN(current) || float.IsInfinity(current)) return;
        __state = new Saved { Maximum = maximum, Current = current,
            Factor = data.GetFloat("PT_EnemyHealthFactor", 1) };
    }

    private static void After(Character __instance, Saved? __state)
    {
        if (__state == null || !__instance.GetComponent<ZNetView>().IsOwner()) return;
        var type = PovBridge.Assembly.GetType("PassiveTree.EnemyDifficulty+HealthPlan", true);
        object plan = Activator.CreateInstance(type);
        AccessTools.Field(type, "Maximum").SetValue(plan, __state.Maximum);
        AccessTools.Field(type, "Current").SetValue(plan, __state.Current);
        // Штатный WriteHealth обходит повторное масштабирование и одноразовый FollowMaxHealth.
        // Сохранённый коэффициент оставляет дальнейшую обычную смену сложности в руках PoV.Ensure.
        PovBridge.Method("PassiveTree.EnemyDifficulty", "WriteHealth")
            .Invoke(null, new[] { (object)__instance, plan, __state.Factor });
        if (Plugin.Instance.Config.Bind("ForestCrypt", "RadialDiagnostics", false).Value)
        {
            Loads++;
            if (__state.Current < __state.Maximum) Wounded++;
            if (Math.Abs(__instance.GetMaxHealth() - __state.Maximum) > .001f ||
                Math.Abs(__instance.GetHealth() - __state.Current) > .001f) Failures++;
        }
    }
}
