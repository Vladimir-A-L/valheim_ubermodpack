using System;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace DungeonDepthPrototype;

internal static class MiniBossArenaTrial
{
    internal static Vector3 Create(Vector3 origin, int index, int seed, bool archer = false)
    {
        string token = Guid.NewGuid().ToString("N"), name = MiniBossTrial.Name(seed);
        GameObject Spawn(string prefab, Vector3 local, string role = "")
        {
            var obj = MiniBossTrial.Spawn(prefab, origin + local, Quaternion.identity, role);
            MiniBossRuntime.Data(obj.transform)!.Set(ForestRuntime.Tag, token); return obj;
        }
        var root = Spawn(MiniBossArenaPlan.Prefab, Vector3.zero);
        var data = MiniBossRuntime.Data(root.transform)!;
        data.Set(MiniBossTrial.IndexKey, index);
        var gateObj = Spawn("dungeon_forestcrypt_door", Vector3.zero);
        var gate = gateObj.GetComponent<ZNetView>();
        gate.m_syncInitialScale = true; gate.SetLocalScale(Vector3.one * 2);
        gate.GetZDO().Set(ForestGates.GateKey, true); gate.GetZDO().Set(ForestGates.ReleasedKey, false);
        gate.GetZDO().Set(MiniBossArena.Key, true); gate.GetZDO().Set(ZDOVars.s_state, 0);
        string prefab = index % 2 == 0 ? "Skeleton" : "Ghost";
        var boss = Spawn(prefab, MiniBossArena.Center(Vector3.zero)).GetComponent<Character>();
        if (archer) MiniBossRuntime.Data(boss)!.Set(MiniBossArenaFixtures.WeaponKey, "skeleton_bow");
        MiniBossRuntime.Mark(boss, name, seed);
        var previous = UnityEngine.Random.state;
        string[] modifiers;
        try
        {
            UnityEngine.Random.InitState(seed); PovBridge.Assign(boss, 10);
            modifiers = PovBridge.Ids(PovBridge.Read(boss));
            // U5 имеет гарантированный Warded, остальные девять остаются случайной выборкой.
            if (index == 0 && !modifiers.Contains("warded"))
            { modifiers[0] = "warded"; PovBridge.AssignIds(boss, modifiers); }
        }
        finally { UnityEngine.Random.state = previous; }
        MiniBossTrial.Tag(boss, token, 0, "boss");
        var bossData = MiniBossRuntime.Data(boss)!;
        bossData.Set(MiniBossArena.Key, true); bossData.Set(MiniBossArena.OriginKey, origin);
        bossData.Set(MiniBossArena.MaximumKey, boss.GetMaxHealth());
        bossData.Set(MiniBossArena.StateKey, JsonConvert.SerializeObject(new MiniBossArenaState()));
        boss.gameObject.AddComponent<MiniBossArenaController>();
        var assistants = new string[archer ? 0 : MiniBossRules.TrialHelpers(index)];
        var random = new System.Random(unchecked(seed ^ 0x27182818));
        for (int i = 0; i < assistants.Length; i++)
        {
            assistants[i] = random.Next(2) == 0 ? "Skeleton" : "Ghost";
            var helper = Spawn(assistants[i], new Vector3(23, .08f, 21)).GetComponent<Character>();
            helper.SetLevel(1); PovBridge.AssignIds(helper, modifiers);
            MiniBossTrial.Tag(helper, token, 0, "helper");
            MiniBossRuntime.Data(helper)!.Set(MiniBossArena.MaximumKey, helper.GetMaxHealth());
        }
        var selected = new string[MiniBossArenaPlan.Pedestals.Length];
        for (int i = 0; i < selected.Length; i++)
        {
            var p = MiniBossArenaPlan.Pedestals[i];
            var randomState = UnityEngine.Random.state;
            try
            {
                UnityEngine.Random.InitState(unchecked(seed ^ (i * 104729)));
                var item = Spawn(MiniBossArenaPlan.ValuePrefab, new Vector3(p.X, .42f, p.Z));
                var pickable = item.GetComponent<PickableItem>();
                selected[i] = pickable.m_itemPrefab.name + ":" + pickable.m_stack;
                MiniBossRuntime.Data(item.transform)!.Set(MiniBossArena.ValueKey, true);
            }
            finally { UnityEngine.Random.state = randomState; }
        }
        var rune = Spawn("BossStone_TheElder", new Vector3(-1.75f, 0, -1.8f));
        rune.transform.rotation = Quaternion.Euler(0, 90, 0);
        var runeData = MiniBossRuntime.Data(rune.transform)!;
        runeData.SetRotation(rune.transform.rotation);
        runeData.Set(MiniBossTrial.RuneKey, "Здесь покоится владыка " + name);
        runeData.Set(MiniBossTrial.RuneWallKey, true); runeData.Set(MiniBossTrial.RuneVisualKey, 2);
        runeData.Set(MiniBossArenaFixtures.RuneScaleKey, .26f);
        runeData.Set(MiniBossArenaFixtures.RevisionKey, true);
        MiniBossRuntime.ConfigureRune(rune.GetComponent<ZNetView>());
        foreach (int segment in Enumerable.Range(1, 6))
        {
            var sign = Spawn("sign", new Vector3(1.85f, 1.4f, -segment * MiniBossArenaPlan.Segment + .2f), "sign");
            sign.transform.rotation = Quaternion.Euler(0, 270, 0);
            MiniBossRuntime.Data(sign.transform)!.Set(ZDOVars.s_text,
                segment == 5 ? "5 · ГРАНИЦА БОЯ\nДальше — возврат" : segment + " · сегмент");
        }
        // Угол для подготовки U6/U7; исходные материалы выдаёт отдельный тестовый сундук.
        var supplies = Spawn("piece_chest_wood", new Vector3(27, .05f, 13), "supplies").GetComponent<Container>();
        var inventory = supplies.GetInventory(); inventory.RemoveAll();
        foreach (var item in new[] { ("Hammer", 1), ("Wood", 100), ("Stone", 50) })
        {
            var source = ObjectDB.instance.GetItemPrefab(item.Item1);
            int remaining = item.Item2;
            while (remaining > 0)
            {
                var value = source.GetComponent<ItemDrop>().m_itemData.Clone(); value.m_dropPrefab = source;
                value.m_stack = Math.Min(remaining, value.m_shared.m_maxStackSize); remaining -= value.m_stack;
                if (!inventory.AddItem(value)) throw new InvalidOperationException("Не помещаются материалы U6/U7");
            }
        }
        HarmonyLib.AccessTools.Method(typeof(Container), "Save").Invoke(supplies, null);
        data.Set(MiniBossTrial.SpecKey, JsonConvert.SerializeObject(new { index, seed, token, name, prefab,
            modifiers, assistants, values = selected, custom_arena = 1,
            hall_area = 480, segment_length = MiniBossArenaPlan.Segment,
            corridor_width = MiniBossArenaPlan.CorridorWidth, ceiling_height = MiniBossArenaPlan.Height,
            combat_depth = 64, door_position = ForestFixture.V(origin),
            boss_position = ForestFixture.V(boss.transform.position),
            scale = 1.8, health = MiniBossRules.HealthFactor, damage = MiniBossRules.DamageFactor,
            helper_chance_percent = MiniBossRules.HelperChancePercent, forced_test_variant = true, archer }));
        return origin + new Vector3(0, .08f, -22);
    }
}
