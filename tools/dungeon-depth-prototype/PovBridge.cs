using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace DungeonDepthPrototype;

// Экспериментальный мост к одной DLL; публичный API PoV пока не умеет назначать модификаторы.
internal static class PovBridge
{
    internal const string Guid = "treextr.pathofvalheiman";
    internal const string ProfileKey = "uberdungeon.probe.profile";
    internal const string ModsKey = "uberdungeon.probe.mods";
    private const string ExpectedHash = "ea9ce4acc6eb85c903ec415bae66d23a1ea112f6c82b846facd847bab3e72416";
    private static MethodInfo fill = null!;
    private static MethodInfo set = null!;
    private static MethodInfo read = null!;
    internal static MethodInfo Sync = null!;
    internal static Assembly Assembly = null!;

    internal static void Install(Harmony harmony)
    {
        var info = Chainloader.PluginInfos[Guid];
        if (info.Metadata.Version.ToString() != "5.3.6")
            throw new InvalidOperationException("Мост проверяется только с PoV 5.3.6");
        Assembly = info.Instance.GetType().Assembly;
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(Assembly.Location);
        string hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        if (hash != ExpectedHash)
            throw new InvalidOperationException("SHA256 PoV отличается от исследованной DLL");
        fill = Method("PassiveTree.RarityMods", "Fill");
        set = Method("PassiveTree.RarityMods", "Set");
        read = Method("PassiveTree.RarityMods", "On");
        Sync = Method("PassiveTree.RarityRoll", "Sync");
        harmony.Patch(Sync, transpiler: new HarmonyMethod(typeof(PovBridge), nameof(Transpile)));
    }

    internal static MethodInfo Method(string type, string name) =>
        AccessTools.Method(Assembly.GetType(type, true), name)
        ?? throw new MissingMethodException(type, name);

    private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.Select(i => new CodeInstruction(i)).ToList();
        var calls = code.Where(i => i.Calls(fill)).ToList();
        if (calls.Count != 1)
            throw new InvalidOperationException("Структура RarityRoll.Sync не соответствует проверенной");
        calls[0].opcode = OpCodes.Call;
        calls[0].operand = AccessTools.Method(typeof(PovBridge), nameof(ControlledFill));
        return code;
    }

    public static void ControlledFill(Character character, int ordinaryCount)
    {
        var view = character.GetComponent<ZNetView>();
        var zdo = view != null && view.IsValid() ? view.GetZDO() : null;
        if (view == null || zdo == null || zdo.GetInt(ProfileKey, 0) != 1)
        {
            fill.Invoke(null, new object[] { character, ordinaryCount });
            return;
        }
        // Владельца не перехватываем. Сам Sync вызывается PoV на владельце ZDO.
        if (!view.IsOwner()) return;
        int mask = zdo.GetInt(ModsKey, 0);
        if (Read(character) != mask) Set(character, mask);
    }

    internal static int Read(Character character) => Convert.ToInt32(read.Invoke(null, new object[] { character }));

    internal static void RestoreWard(Character character)
    {
        var ward = Method("PassiveTree.RuneWard", "Of").Invoke(null, new object[] { character });
        if (ward == null) return;
        var type = ward.GetType();
        AccessTools.Field(type, "Current").SetValue(ward, AccessTools.Field(type, "Max").GetValue(ward));
        AccessTools.Method(type, "Save").Invoke(ward, null);
    }

    internal static string[] Ids(int mask)
    {
        var modType = set.GetParameters()[1].ParameterType;
        var idOf = Method("PassiveTree.RarityMods", "IdOf");
        return Enum.GetValues(modType).Cast<object>().Where(v =>
            Convert.ToInt32(v) != 0 && (mask & Convert.ToInt32(v)) == Convert.ToInt32(v))
            .Select(v => (string)idOf.Invoke(null, new[] { v })).OrderBy(v => v).ToArray();
    }

    internal static int ValidateIds(params string[] ids)
    {
        var modType = set.GetParameters()[1].ParameterType;
        int mask = 0;
        foreach (string id in ids)
        {
            object[] args = { id, Enum.ToObject(modType, 0) };
            if (!(bool)Method("PassiveTree.RarityMods", "Parse").Invoke(null, args))
                throw new ArgumentException("Неизвестный модификатор PoV: " + id);
            string canonical = (string)Method("PassiveTree.RarityMods", "IdOf").Invoke(null, new[] { args[1] });
            if (canonical != id) throw new ArgumentException("Нужен точный ID PoV: " + id);
            mask |= Convert.ToInt32(args[1]);
        }
        if (Count(mask) != ids.Length) throw new ArgumentException("Повторные ID PoV");
        return mask;
    }

    internal static int AssignIds(Character character, params string[] ids)
    {
        int mask = ValidateIds(ids);
        var view = character.GetComponent<ZNetView>();
        if (view == null || !view.IsValid() || !view.IsOwner())
            throw new InvalidOperationException("Назначение разрешено только владельцу");
        Set(character, mask);
        if (Read(character) != mask) throw new InvalidOperationException("PoV отклонил набор");
        view.GetZDO().Set(ModsKey, mask);
        view.GetZDO().Set(ProfileKey, 1);
        return mask;
    }

    private static void Set(Character character, int mask)
    {
        object value = Enum.ToObject(set.GetParameters()[1].ParameterType, mask);
        set.Invoke(null, new[] { (object)character, value });
    }

    internal static int Assign(Character character, int count)
    {
        if (count < 0 || count > 26) throw new ArgumentOutOfRangeException(nameof(count));
        var view = character.GetComponent<ZNetView>();
        if (view == null || !view.IsValid() || !view.IsOwner())
            throw new InvalidOperationException("Назначение разрешено только владельцу существующего ZDO");
        Set(character, 0);
        fill.Invoke(null, new object[] { character, count });
        int mask = Read(character);
        if (Count(mask) != count)
            throw new InvalidOperationException($"Доступно {Count(mask)} модификаторов вместо {count}");
        view.GetZDO().Set(ModsKey, mask);
        view.GetZDO().Set(ProfileKey, 1);
        return mask;
    }

    internal static int Count(int mask)
    {
        uint bits = unchecked((uint)mask);
        int result = 0;
        while (bits != 0) { bits &= bits - 1; result++; }
        return result;
    }

    internal static float ConfigFloat(string type, string field)
    {
        var setting = AccessTools.Field(Assembly.GetType(type, true), field).GetValue(null);
        if (setting == null) throw new InvalidOperationException($"Конфиг {field} ещё не создан");
        return Convert.ToSingle(setting.GetType().GetProperty("Value")!.GetValue(setting));
    }
}
