using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DungeonDepthPrototype;

internal static class ForestGates
{
    internal const string GateKey = "uberdungeon.gate.v1";
    internal const string ReleasedKey = GateKey + ".released";
    internal const string FromKey = GateKey + ".from";
    internal static bool Enabled => Plugin.Instance.Config.Bind("ForestCrypt", "StageGates", false,
        "Двери между ступенями сложности; только для вновь создаваемых данжей.").Value;

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(Door), "Interact"),
            prefix: new HarmonyMethod(typeof(ForestGates), nameof(Interact)));
        harmony.Patch(AccessTools.Method(typeof(Door), "RPC_UseDoor"),
            postfix: new HarmonyMethod(typeof(ForestGates), nameof(Used)));
        harmony.Patch(AccessTools.Method(typeof(Destructible), "RPC_Damage"),
            prefix: new HarmonyMethod(typeof(ForestGates), nameof(Damage)) { priority = Priority.First });
        harmony.Patch(AccessTools.Method(typeof(WearNTear), "RPC_Damage"),
            prefix: new HarmonyMethod(typeof(ForestGates), nameof(Damage)) { priority = Priority.First });
        harmony.Patch(AccessTools.Method(typeof(ZDO), "Deserialize"),
            postfix: new HarmonyMethod(typeof(ForestGates), nameof(Received)));
    }

    private static void Received(ZDO __instance)
    {
        // В 1.0.20 получение данных неподвижной двери от клиента не помечает chunk для записи.
        // Без этого сервер видит открытие, но после перезапуска читает прежнее состояние двери.
        if (ZNet.instance != null && ZNet.instance.IsServer() && __instance.GetBool(GateKey))
            ZDOMan.instance.SetDirtySector(__instance);
    }

    private static ZDO? Gate(Component value)
    {
        var view = value.GetComponent<ZNetView>();
        var zdo = view != null && view.IsValid() ? view.GetZDO() : null;
        return zdo != null && zdo.GetBool(GateKey) ? zdo : null;
    }

    private static bool Interact(Door __instance, Humanoid character) =>
        Gate(__instance) == null || character is Player;

    private static void Used(Door __instance)
    {
        var zdo = Gate(__instance);
        if (zdo != null && __instance.GetComponent<ZNetView>().IsOwner() &&
            zdo.GetInt(ZDOVars.s_state) != 0) zdo.Set(ReleasedKey, true);
    }

    private static bool Damage(Component __instance, HitData hit)
    {
        var zdo = Gate(__instance);
        // До первого открытия охрана и окружение не могут разрушить преграду сами.
        // Удар игрока разрешён, но не открывает проход, пока дверь действительно не разрушена.
        return zdo == null || zdo.GetBool(ReleasedKey) || hit.GetAttacker() is Player;
    }

    internal static void Place(DungeonGenerator generator, ForestPlan.Layout plan, string token)
    {
        if (!plan.StageGates) return;
        foreach (var (a, b) in EncounterGates.Edges(plan))
        {
            var parent = plan.Rooms[a]; var child = plan.Rooms[b];
            string connection = parent.Template.Sockets[child.ParentSocket].Type;
            var prefab = generator.m_doorTypes.Where(d => d.m_connectionType == connection)
                .Select(d => d.m_prefab).Where(p => p != null && p.GetComponent<Door>() != null)
                .OrderBy(p => p.name, StringComparer.Ordinal).FirstOrDefault();
            if (prefab == null) throw new InvalidOperationException("Нет штатной двери для " + connection);
            var door = Object.Instantiate(prefab, ForestRuntime.V(parent.SocketPosition(child.ParentSocket)),
                Quaternion.Euler(0, parent.SocketTurn(child.ParentSocket) * 90, 0));
            var zdo = ForestRuntime.Zdo(door.transform);
            zdo.Set(GateKey, true); zdo.Set(ReleasedKey, false); zdo.Set(FromKey, a);
            zdo.Set(ForestRuntime.Tag, token); zdo.Set(ForestRuntime.RoomKey, b);
            zdo.Set(ForestRuntime.DepthKey, child.Depth); zdo.Set(ForestRuntime.KindKey, "gate");
            zdo.Set(ZDOVars.s_state, 0);
        }
    }

    internal static object Catalog()
    {
        var generator = ZNetScene.instance.GetPrefab("DG_ForestCrypt").GetComponent<DungeonGenerator>();
        return generator.m_doorTypes.Select(d => new { connection = d.m_connectionType,
            prefab = d.m_prefab.name, door = d.m_prefab.GetComponent<Door>() != null,
            destructible = d.m_prefab.GetComponent<Destructible>() != null,
            wear = d.m_prefab.GetComponent<WearNTear>() != null,
            components = d.m_prefab.GetComponents<Component>().Select(c => c.GetType().Name).ToArray() }).ToArray();
    }

    internal static bool CheckClosedColliders()
    {
        Physics.SyncTransforms();
        var doors = ForestRuntime.Spawned.Where(v => v != null && v.IsValid() &&
            v.GetZDO().GetBool(GateKey)).ToArray();
        return doors.Length > 0 && doors.All(view => new[] { -.55f, 0f, .55f }.All(x =>
        {
            var ray = new Ray(view.transform.TransformPoint(new Vector3(x, 1, -1.5f)), view.transform.forward);
            return view.GetComponentsInChildren<Collider>().Any(c => c.enabled && !c.isTrigger &&
                c.Raycast(ray, out _, 3));
        }));
    }

    internal static object Probe()
    {
        if (!ZNet.instance.IsServer()) throw new InvalidOperationException("Нужен отдельный сервер");
        var generator = ZNetScene.instance.GetPrefab("DG_ForestCrypt").GetComponent<DungeonGenerator>();
        var prefab = generator.m_doorTypes.First(d => d.m_prefab.GetComponent<Door>() != null).m_prefab;
        var obj = Object.Instantiate(prefab, new Vector3(0, 5100, 0), Quaternion.identity);
        var enemy = Object.Instantiate(ZNetScene.instance.GetPrefab("Skeleton"),
            new Vector3(0, 5100, 4), Quaternion.identity);
        try
        {
            enemy.GetComponent<BaseAI>().enabled = false;
            var zdo = ForestRuntime.Zdo(obj.transform); zdo.Set(GateKey, true);
            Component? damageable = obj.GetComponent<Destructible>();
            damageable ??= obj.GetComponent<WearNTear>();
            if (damageable == null) return new { passed = false, reason = "no-damage-component" };
            float maximum = damageable is Destructible d ? d.m_health : ((WearNTear)damageable).m_health;
            zdo.Set(ZDOVars.s_health, maximum);
            var hit = new HitData(); hit.m_damage.m_damage = 1; hit.SetAttacker(enemy.GetComponent<Character>());
            var method = AccessTools.Method(damageable.GetType(), "RPC_Damage");
            method.Invoke(damageable, new object[] { 0L, hit });
            bool protectedClosed = zdo.GetFloat(ZDOVars.s_health) == maximum;
            zdo.Set(ZDOVars.s_state, 1); Used(obj.GetComponent<Door>());
            bool released = zdo.GetBool(ReleasedKey);
            method.Invoke(damageable, new object[] { 0L, hit });
            bool damageAfterOpen = zdo.GetFloat(ZDOVars.s_health) < maximum;
            zdo.Set(ZDOVars.s_state, 0); Used(obj.GetComponent<Door>());
            return new { passed = protectedClosed && released && damageAfterOpen && zdo.GetBool(ReleasedKey),
                protected_closed = protectedClosed, released, damage_after_open = damageAfterOpen,
                remains_released_after_close = zdo.GetBool(ReleasedKey),
                limitation = "open-state-simulated; human-interaction-not-tested" };
        }
        finally { ZNetScene.instance.Destroy(obj); ZNetScene.instance.Destroy(enemy); }
    }
}
