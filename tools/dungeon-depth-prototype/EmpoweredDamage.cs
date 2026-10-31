using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;

namespace DungeonDepthPrototype;

// Метка только производного отражения: byte 201 свободен в закреплённом HitData.HitType.
// PoV уже использует 200 для Bound, собственный бросок — 211. Размер сетевого пакета не меняется.
internal static class EmpoweredDamage
{
    internal const HitData.HitType Inherited = (HitData.HitType)201;
    internal sealed class Scope
    {
        internal Scope? Previous;
        internal Character? Attacker;
        internal bool Skip, Powered;
    }
    [ThreadStatic] private static Scope? current;

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(Character), "RPC_Damage"),
            prefix: new HarmonyMethod(typeof(EmpoweredDamage), nameof(Open)) { priority = Priority.First + 100 },
            finalizer: new HarmonyMethod(typeof(EmpoweredDamage), nameof(Close)));
        harmony.Patch(PovBridge.Method("PassiveTree.RarityMods2+Character_RPC_Damage_Wave2", "Prefix"),
            transpiler: new HarmonyMethod(typeof(EmpoweredDamage), nameof(Forward)));
        harmony.Patch(AccessTools.Method(typeof(Character), "GetHoverName"),
            postfix: new HarmonyMethod(typeof(EmpoweredDamage), nameof(Name)));
    }
    internal static int Stacks(Character? creature) => creature == null || creature.IsPlayer() ? 0 :
        MiniBossRuntime.Data(creature)?.GetInt(RadialRuntime.EmpoweredKey) ?? 0;

    private static void Open(HitData hit, out Scope __state)
    {
        var attacker = hit?.GetAttacker();
        bool inherited = hit != null && hit.m_hitType == Inherited;
        __state = new Scope { Previous = current, Attacker = attacker,
            Skip = inherited || hit != null && (byte)hit.m_hitType == 200,
            Powered = inherited || Stacks(attacker) > 0 };
        current = __state;
        // Штатные обработчики получают исходный Undefined отражения, а не служебный тип.
        if (inherited) hit!.m_hitType = HitData.HitType.Undefined;
    }
    private static void Close(Scope __state) => current = __state.Previous;
    internal static bool Suppress(Character creature) => current?.Skip == true && current.Attacker == creature;

    private static IEnumerable<CodeInstruction> Forward(IEnumerable<CodeInstruction> instructions)
    {
        var damage = AccessTools.Method(typeof(Character), "Damage", new[] { typeof(HitData) });
        var code = instructions.Select(i => new CodeInstruction(i)).ToList();
        var calls = code.Where(i => i.Calls(damage)).ToArray();
        if (calls.Length != 2)
            throw new InvalidOperationException("PoV: изменились точки передачи урона Mirror/Bound");
        foreach (var call in calls)
        {
            call.opcode = OpCodes.Call;
            call.operand = AccessTools.Method(typeof(EmpoweredDamage), nameof(Derived));
        }
        return code;
    }
    internal static void Derived(Character target, HitData hit)
    {
        // Bound сохраняет свой штатный 200 и защиту от рекурсии. Mirror копирует уже усиленный урон.
        // Если исходный удар игрока не был усилен, отражение босса получает его собственный множитель.
        if (current?.Powered == true && hit.m_hitType == HitData.HitType.Undefined)
            hit.m_hitType = Inherited;
        target.Damage(hit);
    }
    private static void Name(Character __instance, ref string __result)
    {
        int count = Stacks(__instance);
        if (count > 0) __result += "\nУсилен ×" + count;
    }
}
