using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DungeonDepthPrototype;

internal static class MiniBossValueProbe
{
    private const string RemovedKey = "uberdungeon.value-probe.removed";
    private static PickableItem[] Values(MiniBossArena arena)
    {
        string token = MiniBossRuntime.Data(arena)!.GetString(ForestRuntime.Tag);
        return Object.FindObjectsByType<PickableItem>(FindObjectsSortMode.None).Where(p =>
            MiniBossRuntime.Data(p)?.GetBool(MiniBossArena.ValueKey) == true &&
            MiniBossRuntime.Data(p)!.GetString(ForestRuntime.Tag) == token).ToArray();
    }
    internal static object Check(MiniBossArena arena)
    {
        var values = Values(arena); var root = MiniBossRuntime.Data(arena)!;
        var results = values.Select(p => {
            var data = MiniBossRuntime.Data(p)!; var visual = MiniBossValuePlacement.Visual(p)!;
            var support = MiniBossValuePlacement.Support(arena, p.transform.position)!;
            float correction = MiniBossValuePlacement.Correction(visual, support);
            return new { item = p.m_itemPrefab.name, count = p.m_stack, correction,
                root_unchanged = Vector3.Distance(data.GetPosition(), p.transform.position) < .0001f,
                saved_item_matches = data.GetInt(ZDOVars.s_itemPrefab) ==
                    ObjectDB.instance.GetPrefabHash(p.m_itemPrefab.gameObject) &&
                    data.GetInt(ZDOVars.s_itemStack) == p.m_stack,
                contact = Math.Abs(correction - MiniBossValuePlacement.ContactInset) < .0006f };
        }).ToArray();
        bool removed = root.GetBool(RemovedKey);
        bool absent = !removed || values.All(v => Vector3.Distance(v.transform.position,
            root.GetVec3(RemovedKey + ".position", Vector3.zero)) > .1f);
        return new { results, removed_item_absent = absent,
            passed = values.Length == (removed ? 5 : 6) && absent &&
                results.All(v => v.contact && v.root_unchanged && v.saved_item_matches) };
    }
    internal static object Models(MiniBossArena arena)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var results = new List<object>(); bool passed = true;
        var p = MiniBossArenaPlan.Pedestals[0];
        var position = arena.transform.TransformPoint(new Vector3(p.X, .42f, p.Z));
        var support = MiniBossValuePlacement.Support(arena, position)!;
        foreach (string name in new[] { "Amber", "AmberPearl", "Ruby", "Coins" })
        foreach (float yaw in new[] { 0, 90f, 180f, 270f })
        {
            var root = new GameObject("value-model-probe");
            try
            {
                root.transform.SetPositionAndRotation(position, arena.transform.rotation * Quaternion.Euler(0, yaw, 0));
                var attach = ObjectDB.instance.GetItemPrefab(name).transform.Find("attach");
                var visual = Object.Instantiate(attach.gameObject, root.transform, false);
                visual.transform.localPosition = attach.localPosition;
                visual.transform.localRotation = attach.localRotation;
                float before = MiniBossValuePlacement.Correction(visual, support);
                float moved = MiniBossValuePlacement.Align(visual, support);
                float after = MiniBossValuePlacement.Correction(visual, support);
                var first = visual.transform.localPosition;
                MiniBossValuePlacement.Align(visual, support);
                bool repeat = Vector3.Distance(first, visual.transform.localPosition) < .0006f;
                bool ok = Math.Abs(after - MiniBossValuePlacement.ContactInset) < .0006f && repeat;
                passed &= ok;
                results.Add(new { item = name, yaw, before, moved, after, idempotent = repeat, passed = ok });
            }
            finally { Object.DestroyImmediate(root); }
        }
        return new { results, elapsed_ms = started.ElapsedMilliseconds, passed };
    }
    internal static object Pick(MiniBossArena arena)
    {
        var root = MiniBossRuntime.Data(arena)!;
        if (root.GetBool(RemovedKey)) throw new InvalidOperationException("Повторный подбор пробы запрещён");
        var target = Values(arena).First();
        var before = Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None)
            .Select(x => x.GetInstanceID()).ToHashSet();
        int count = target.m_stack; string item = target.m_itemPrefab.name;
        var position = target.transform.position;
        MiniBossRuntime.Data(target)!.SetOwner(ZDOMan.GetSessionID());
        AccessTools.Method(typeof(PickableItem), "RPC_Pick").Invoke(target, new object[] { 0L });
        var drop = Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None)
            .Single(x => !before.Contains(x.GetInstanceID()));
        var data = MiniBossRuntime.Data(drop)!;
        data.Set(ForestRuntime.Tag, root.GetString(ForestRuntime.Tag)); // Удаление вместе с пробной ареной.
        bool exact = drop.m_itemData.m_stack == count && Utils.GetPrefabName(drop.gameObject) == item;
        root.Set(RemovedKey, true); root.Set(RemovedKey + ".position", position);
        return new { item, count, exact_drop = exact };
    }
}
