using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace DungeonDepthPrototype;

// Только служебные объекты ручного стенда, а не обычные постройки игрока.
internal static class MiniBossArenaFixtures
{
    internal const string RevisionKey = MiniBossArena.Key + ".crypt-fixtures";
    internal const string RuneScaleKey = MiniBossTrial.RuneKey + ".scale";
    internal const string WeaponKey = MiniBossArena.Key + ".fixed-weapon";
    internal static bool Portal(Component target) =>
        MiniBossRuntime.Data(target)?.GetString(MiniBossTrial.HubKey) == "portal";
    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(Humanoid), "GiveDefaultItems"),
            prefix: new HarmonyMethod(typeof(MiniBossArenaFixtures), nameof(Equipment)));
        foreach (var type in new[] { typeof(WearNTear), typeof(Destructible) })
            harmony.Patch(AccessTools.Method(type, "RPC_Damage"), prefix:
                new HarmonyMethod(typeof(MiniBossArenaFixtures), nameof(Damage)) { priority = Priority.First });
    }
    private static void Equipment(Humanoid __instance)
    {
        if (MiniBossRuntime.Data(__instance)?.GetString(WeaponKey) != "skeleton_bow") return;
        __instance.m_randomWeapon = new[] { ZNetScene.instance.GetPrefab("skeleton_bow") };
        __instance.m_randomShield = Array.Empty<GameObject>();
    }
    private static bool Damage(Component __instance, HitData hit) =>
        !Portal(__instance) || hit.GetAttacker() == null || hit.GetAttacker().IsPlayer();

    internal static object Migrate()
    {
        if (!ZNet.instance.IsServer() || ZNet.instance.GetPeers().Any(p => p.IsReady()))
            throw new InvalidOperationException(
                "Миграция площадки требует сервера без игроков и остановленного backup");
        var all = ForestFixture.AllZdos().Values.ToArray();
        var arenas = all.Where(z => z.GetPrefab() == MiniBossArenaPlan.Prefab.GetStableHashCode()).ToArray();
        if (arenas.Length < MiniBossRules.TrialCount || arenas.Length > MiniBossRules.TrialCount + 1)
            throw new InvalidOperationException("Нужны четыре арены и необязательная проба лучника");
        var tokens = arenas.Select(z => z.GetString(ForestRuntime.Tag)).ToArray();
        int removed = 0, runes = 0, restored = 0;
        foreach (var zdo in all.Where(z => tokens.Contains(z.GetString(ForestRuntime.Tag))))
        {
            if (zdo.GetString(MiniBossTrial.HubKey) == "workbench")
            {
                zdo.SetOwner(ZDOMan.GetSessionID());
                var obj = ZNetScene.instance.FindInstance(zdo.m_uid);
                if (obj != null) ZNetScene.instance.Destroy(obj);
                else ZDOMan.instance.DestroyZDO(zdo);
                removed++;
            }
            if (zdo.GetString(MiniBossTrial.RuneKey).Length > 0)
            {
                var arena = arenas.Single(a => a.GetString(ForestRuntime.Tag) == zdo.GetString(ForestRuntime.Tag));
                var position = arena.GetPosition() + new Vector3(-1.75f, 0, -1.8f);
                var rotation = Quaternion.Euler(0, 90, 0);
                if (zdo.GetBool(RevisionKey) && Vector3.Distance(zdo.GetPosition(), position) < .01f &&
                    Quaternion.Angle(zdo.GetRotation(), rotation) < .1f &&
                    Math.Abs(zdo.GetFloat(RuneScaleKey) - .26f) < .001f) continue;
                // Плоскость рисунка должна выступать перед неровной стеной, а не только перед её bounds.
                zdo.SetPosition(position); zdo.SetRotation(rotation);
                zdo.Set(RuneScaleKey, .26f); zdo.Set(RevisionKey, true); runes++;
                var obj = ZNetScene.instance.FindInstance(zdo.m_uid);
                if (obj != null)
                {
                    obj.transform.SetPositionAndRotation(position, rotation);
                    MiniBossRuntime.ConfigureRune(obj.GetComponent<ZNetView>());
                }
            }
        }
        foreach (var arena in arenas)
        {
            int index = arena.GetInt(MiniBossTrial.IndexKey, -1);
            string tag = "Испытание " + (index + 1);
            var portalPosition = arena.GetPosition() + new Vector3(0, .08f, -22);
            if (!all.Any(z => z.GetString(MiniBossTrial.HubKey) == "portal" &&
                    z.GetString(ZDOVars.s_tag) == tag && Vector3.Distance(z.GetPosition(), portalPosition) < 3))
            { MiniBossTrial.Portal(portalPosition, Quaternion.identity, tag); restored++; }
            arena.Set(RevisionKey, true);
        }
        ZoneSystem.instance.RemoveGlobalKey(GlobalKeys.DungeonBuild);
        return new { workbenches_removed = removed, runes_updated = runes, portals_restored = restored,
            dungeon_build = ZoneSystem.instance.GetGlobalKey(GlobalKeys.DungeonBuild) };
    }
    internal static object CreateArcher()
    {
        const int index = 4;
        if (ForestFixture.AllZdos().Values.Any(z =>
                z.GetPrefab() == MiniBossArenaPlan.Prefab.GetStableHashCode() &&
                z.GetInt(MiniBossTrial.IndexKey, -1) == index)) return new { created = false, already_exists = true };
        var origin = MiniBossTrial.Hub + new Vector3(192 + index * 128, 0, 0);
        var portal = MiniBossArenaTrial.Create(origin, index, 20261101, true);
        const string tag = "Испытание 5";
        MiniBossTrial.Portal(portal, Quaternion.identity, tag);
        var hub = MiniBossTrial.Hub + new Vector3(0, .5f, 10);
        MiniBossTrial.Portal(hub, Quaternion.Euler(0, 180, 0), tag);
        var sign = MiniBossTrial.Spawn("sign", hub + new Vector3(2.5f, 1.2f, 0),
            Quaternion.Euler(0, 180, 0), "sign");
        MiniBossRuntime.Data(sign.transform)!.Set(ZDOVars.s_text, "5. Скелет-лучник\nОтдельная проба\nБез помощника");
        return new { created = true, already_exists = false };
    }
}
