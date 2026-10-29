using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DungeonDepthPrototype;

internal static class CombatAnalysisProbe
{
    internal static object Run()
    {
        if (!ZNet.instance.IsServer()) throw new InvalidOperationException("Проба требует отдельный сервер");
        var temporary = new List<GameObject>();
        var rows = new List<object>();
        var random = UnityEngine.Random.state;
        try
        {
            foreach (var ids in new[] { Array.Empty<string>(), new[] { "warded" }, EncounterPlan.ForestModifiers })
            {
                var results = new List<(float Hp, float Ward)>();
                foreach (bool suspended in new[] { true, false })
                {
                    CombatRecorder.Suspended = suspended;
                    var obj = Object.Instantiate(ZNetScene.instance.GetPrefab("Skeleton"),
                        new Vector3(0, 5000, 0), Quaternion.identity);
                    temporary.Add(obj);
                    var character = obj.GetComponent<Character>();
                    obj.GetComponent<BaseAI>().enabled = false;
                    character.SetLevel(1);
                    var zdo = ForestRuntime.Zdo(character);
                    zdo.Set(ForestRuntime.KindKey, "guard"); zdo.Set(ForestRuntime.Tag, "combat-probe");
                    zdo.Set(CombatRecorder.EntityKey, Guid.NewGuid().ToString("N"));
                    PovBridge.AssignIds(character, ids);
                    var hit = new HitData { m_dodgeable = false, m_blockable = false };
                    hit.m_damage.m_blunt = 24;
                    float before = character.GetHealth();
                    AccessTools.Method(typeof(Character), "RPC_Damage").Invoke(character, new object[] { 0L, hit });
                    float loss = before - character.GetHealth();
                    character.Heal(3, false);
                    results.Add((character.GetHealth(), zdo.GetFloat("pt.ward.cur")));
                    rows.Add(new { modifiers = ids, recording = !suspended, health_loss = loss,
                        final_health = character.GetHealth(), ward = zdo.GetFloat("pt.ward.cur") });
                }
                if (results[0] != results[1])
                    throw new InvalidOperationException("Запись изменила результат контрольного удара");
            }
            CombatRecorder.Flush();
            return new { passed = true, samples = rows };
        }
        finally
        {
            CombatRecorder.Suspended = false;
            foreach (var obj in temporary) if (obj != null) ZNetScene.instance.Destroy(obj);
            UnityEngine.Random.state = random;
        }
    }
}
