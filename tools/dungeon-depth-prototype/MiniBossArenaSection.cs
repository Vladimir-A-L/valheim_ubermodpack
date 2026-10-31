using System;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace DungeonDepthPrototype;

internal static class MiniBossArenaSection
{
    internal static GameObject Create(Vector3 origin, Quaternion rotation, MiniBossEncounter encounter,
        string token, int room)
    {
        var frame = new ArenaFrame(origin, rotation);
        int index = encounter.TrialIndex, seed = encounter.Seed;
        string name = encounter.Name;
        bool archer = encounter.Archer;
        GameObject Spawn(string prefab, Vector3 local)
        {
            var obj = MiniBossTrial.Spawn(prefab, frame.Point(local), frame.Rotation);
            MiniBossRuntime.Data(obj.transform)!.Set(ForestRuntime.Tag, token); return obj;
        }
        var root = Spawn(MiniBossArenaPlan.Prefab, Vector3.zero);
        MiniBossCryptDecor.Build(root); // Геометрия готова до общей проверки новых существ в этом кадре.
        var data = MiniBossRuntime.Data(root.transform)!;
        if (encounter.Trial) data.Set(MiniBossTrial.IndexKey, index);
        data.Set(ForestRuntime.RoomKey, room); data.Set(ForestRuntime.DepthKey, encounter.Depth);
        var gateObj = Spawn("dungeon_forestcrypt_door", Vector3.zero);
        var gate = gateObj.GetComponent<ZNetView>();
        gate.m_syncInitialScale = true; gate.SetLocalScale(Vector3.one * 2);
        gate.GetZDO().Set(ForestGates.GateKey, true); gate.GetZDO().Set(ForestGates.ReleasedKey, false);
        gate.GetZDO().Set(MiniBossArena.Key, true); gate.GetZDO().Set(ZDOVars.s_state, 0);
        string prefab = encounter.Prefab;
        var boss = Spawn(prefab, MiniBossArena.Center(Vector3.zero)).GetComponent<Character>();
        RadialRuntime.Mark(boss, encounter.Radial, true, encounter.Depth, RadialRole.Boss);
        if (archer) MiniBossRuntime.Data(boss)!.Set(MiniBossArenaFixtures.WeaponKey, "skeleton_bow");
        MiniBossRuntime.Mark(boss, name, seed);
        string[] modifiers = encounter.Modifiers;
        if (encounter.Trial)
        {
            var previous = UnityEngine.Random.state;
            try
            {
                UnityEngine.Random.InitState(seed); PovBridge.Assign(boss, 10);
                modifiers = PovBridge.Ids(PovBridge.Read(boss));
                if (index == 0 && !modifiers.Contains("warded")) modifiers[0] = "warded";
            }
            finally { UnityEngine.Random.state = previous; }
        }
        PovBridge.AssignIds(boss, modifiers);
        encounter.Modifiers = modifiers;
        MiniBossTrial.Tag(boss, token, room, "boss", encounter.Depth);
        var bossData = MiniBossRuntime.Data(boss)!;
        bossData.Set(MiniBossConnection.GateKey, true);
        if (!encounter.Trial)
            bossData.Set(MiniBossConnection.EncounterKey, JsonConvert.SerializeObject(encounter));
        bossData.Set(MiniBossArena.Key, true); bossData.Set(MiniBossArena.OriginKey, origin);
        bossData.Set(MiniBossArena.RotationKey, frame.Rotation.eulerAngles);
        bossData.Set(MiniBossArena.MaximumKey, boss.GetMaxHealth());
        bossData.Set(MiniBossArena.StateKey, JsonConvert.SerializeObject(new MiniBossArenaState()));
        boss.gameObject.AddComponent<MiniBossArenaController>();
        var assistants = encounter.Helpers;
        for (int i = 0; i < assistants.Length; i++)
        {
            var helper = Spawn(assistants[i], new Vector3(23, .08f, 19 + i * 4)).GetComponent<Character>();
            helper.SetLevel(1); RadialRuntime.Mark(helper, encounter.Radial, false, encounter.Depth, RadialRole.Helper);
            PovBridge.AssignIds(helper, modifiers);
            MiniBossTrial.Tag(helper, token, room, "helper", encounter.Depth);
            ForestGuardRecovery.MarkSpawn(helper);
            MiniBossRuntime.Data(helper)!.Set(MiniBossConnection.GateKey, true);
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
        rune.transform.rotation = frame.Rotation * Quaternion.Euler(0, 90, 0);
        var runeData = MiniBossRuntime.Data(rune.transform)!;
        runeData.SetRotation(rune.transform.rotation);
        runeData.Set(MiniBossTrial.RuneKey, "Здесь покоится владыка " + name);
        runeData.Set(MiniBossTrial.RuneWallKey, true); runeData.Set(MiniBossTrial.RuneVisualKey, 2);
        runeData.Set(MiniBossArenaFixtures.RuneScaleKey, .26f);
        runeData.Set(MiniBossArenaFixtures.RevisionKey, true);
        MiniBossRuntime.ConfigureRune(rune.GetComponent<ZNetView>());
        data.Set(MiniBossTrial.SpecKey, JsonConvert.SerializeObject(new { index, seed, token, name, prefab,
            modifiers, assistants, values = selected, custom_arena = 1,
            hall_area = 480, segment_length = MiniBossArenaPlan.Segment,
            corridor_width = MiniBossArenaPlan.CorridorWidth, ceiling_height = MiniBossArenaPlan.Height,
            combat_depth = encounter.Depth, door_position = ForestFixture.V(origin),
            boss_position = ForestFixture.V(boss.transform.position),
            scale = 1.8, health = encounter.Radial?.Profile.BossHealth ?? MiniBossRules.HealthFactor,
            damage = encounter.Radial?.Profile.BossDamage ?? MiniBossRules.DamageFactor,
            helper_chance_percent = MiniBossRules.HelperChancePercent, forced_test_variant = encounter.Trial,
            archer, budget = encounter.Budget,
            reward_plan = encounter.Reward, rules_revision = encounter.RulesRevision }));
        if (!encounter.Trial)
        {
            MiniBossRuntime.PrepareReward(boss);
            data.Set(MiniBossConnection.EncounterKey, JsonConvert.SerializeObject(encounter));
        }
        return root;
    }
}
