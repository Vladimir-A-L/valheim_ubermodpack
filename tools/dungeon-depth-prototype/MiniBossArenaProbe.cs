using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DungeonDepthPrototype;

internal static class MiniBossArenaProbe
{
    internal static Vector3? Reference;
    internal static void ReferencePosition()
    {
        if (Reference.HasValue && ZNet.instance != null && ZNet.instance.IsServer() &&
            !ZNet.instance.GetPeers().Any(p => p.IsReady()))
            AccessTools.Field(typeof(ZNet), "m_referencePosition").SetValue(ZNet.instance, Reference.Value);
    }
    internal static object Run(JObject command)
    {
        string phase = command.Value<string>("phase") ?? "catalog";
        int index = command.Value<int?>("index") ?? 0;
        if (phase == "snapshot") return Snapshot();
        if (phase == "decor-status") return MiniBossCryptDecor.Status();
        if (ZNet.instance.GetPeers().Any(p => p.IsReady()))
            throw new InvalidOperationException("Нативная проба требует отдельного сервера без игроков");
        if (phase == "decor-catalog") return MiniBossCryptDecor.Catalog();
        if (phase == "decor-migrate") return MiniBossArenaFixtures.Migrate();
        if (phase == "interior-migrate") return MiniBossArenaFixtures.MigrateInterior();
        if (phase == "archer-create") return MiniBossArenaFixtures.CreateArcher();
        var origin = MiniBossTrial.Hub + new Vector3(192 + index * 128, 0, 0);
        if (phase == "release") { Reference = null; return new { released = true }; }
        if (phase == "hub-visit")
        {
            Reference = MiniBossTrial.Hub + Vector3.up * 2;
            ZNet.instance.SetReferencePosition(Reference.Value);
            return new { reference_set = true, hub = true };
        }
        if (phase == "hub-check")
        {
            var target = MiniBossTrial.Hub + Vector3.up * 2;
            bool floor = ZoneSystem.instance.FindFloor(target, out float height);
            int tiles = ForestFixture.AllZdos().Values.Count(z => z.GetString(MiniBossTrial.HubKey) == "floor");
            return new { floor, height, tiles, area_ready = ZNetScene.instance.IsAreaReady(target),
                passed = floor && Math.Abs(height - MiniBossTrial.Hub.y) < 1 && tiles == 221 };
        }
        if (phase == "visit")
        {
            Reference = MiniBossArena.Center(origin);
            ZNet.instance.SetReferencePosition(MiniBossArena.Center(origin));
            return new { reference_set = true, index };
        }
        if (phase == "catalog") return Catalog();
        var boss = Character.GetAllCharacters().Single(c => MiniBossArena.Applies(c) &&
            MiniBossRuntime.Data(c)!.GetString(MiniBossTrial.ActorKey) == "boss" &&
            Vector3.Distance(MiniBossArena.Origin(c), origin) < .1f);
        if (phase == "portal-probe") return MiniBossCryptDecor.PortalProbe(boss, origin);
        if (phase == "navigation")
        {
            Physics.SyncTransforms();
            var route = MiniBossArenaPlan.Route.Select(p => origin + new Vector3(p.X, .08f, p.Z)).ToArray();
            int mask = LayerMask.GetMask("static_solid");
            var checks = (from prefab in new[] { "Skeleton", "Ghost" }
                from i in Enumerable.Range(1, route.Length - 1)
                let agent = ZNetScene.instance.GetPrefab(prefab).GetComponent<BaseAI>().m_pathAgentType
                select new { prefab, segment = i, path = Pathfinding.instance.HavePath(route[i - 1], route[i], agent),
                    capsule_clear = !Physics.CapsuleCast(route[i - 1] + Vector3.up * .7f,
                        route[i - 1] + Vector3.up * 3, .65f, (route[i] - route[i - 1]).normalized,
                        Vector3.Distance(route[i], route[i - 1]), mask, QueryTriggerInteraction.Ignore) }).ToArray();
            bool centerHidden = MiniBossArena.Shielded(origin + new Vector3(0, 1.6f, -.5f),
                MiniBossArena.Center(origin) + Vector3.up * 1.6f);
            return new { checks, center_hidden = centerHidden,
                passed = centerHidden && checks.All(c => c.path && c.capsule_clear),
                collider = ForestFixture.V(boss.GetComponent<Collider>().bounds.size) };
        }
        if (phase == "return")
        {
            var control = boss.GetComponent<MiniBossArenaController>();
            var p = origin + new Vector3(0, .08f, 2);
            boss.transform.position = p; boss.GetComponent<Rigidbody>().position = p;
            boss.SetHealth(boss.GetMaxHealth() * .5f);
            var ward = PovBridge.Method("PassiveTree.RuneWard", "Of").Invoke(null, new object[] { boss });
            if (ward != null)
            {
                AccessTools.Field(ward.GetType(), "Current").SetValue(ward, 0f);
                AccessTools.Method(ward.GetType(), "Save").Invoke(ward, null);
            }
            var state = new MiniBossArenaState { Engaged = true };
            MiniBossRuntime.Data(boss)!.Set(MiniBossArena.StateKey, JsonConvert.SerializeObject(state));
            // Проверка загрузки сохранённого решения, а не подмена пути телепортацией.
            Object.DestroyImmediate(control); boss.gameObject.AddComponent<MiniBossArenaController>();
            return new { started = true, hp = boss.GetHealth(),
                ward = MiniBossRuntime.Data(boss)!.GetFloat("pt.ward.cur") };
        }
        if (phase == "take-value")
        {
            var value = Object.FindObjectsByType<PickableItem>(FindObjectsSortMode.None).First(p =>
                MiniBossRuntime.Data(p)?.GetBool(MiniBossArena.ValueKey) == true &&
                MiniBossRuntime.Data(p)!.GetString(ForestRuntime.Tag) ==
                    MiniBossRuntime.Data(boss)!.GetString(ForestRuntime.Tag));
            AccessTools.Method(typeof(PickableItem), "RPC_Pick").Invoke(value, new object[] { 0L });
            return new { picked = true };
        }
        if (phase == "barrier")
        {
            string kind = command.Value<string>("prefab") ?? "woodwall";
            if (!new[] { "woodwall", "piece_cookingstation", "wood_stack", "stone_pile" }.Contains(kind))
                throw new ArgumentException("Неподдержанная проверочная преграда");
            var obj = MiniBossTrial.Spawn(kind, origin + new Vector3(0, .02f, 5),
                Quaternion.identity);
            // Фикстура помечена как пользовательская преграда; ID не является учётной записью.
            MiniBossRuntime.Data(obj.transform)!.Set(ZDOVars.s_creator, 1L);
            AccessTools.Field(typeof(Piece), "m_creator").SetValue(obj.GetComponent<Piece>(), 1L);
            obj.GetComponent<WearNTear>().m_noSupportWear = false;
            return new { created = true, prefab = obj.name, creator_set = obj.GetComponent<Piece>().GetCreator() != 0 };
        }
        if (phase == "damage") return Damage(boss, origin);
        if (phase == "flight-create") return FlightCreate(boss, origin);
        if (phase == "flight-check") return FlightCheck();
        if (phase == "cast")
        {
            var control = boss.GetComponent<MiniBossArenaController>();
            AccessTools.Method(typeof(MiniBossArenaController), "Begin").Invoke(control,
                new object[] { boss.transform.position + Vector3.forward * 3, Vector3.zero, "native-probe" });
            return MiniBossArena.Status();
        }
        throw new ArgumentException("Неизвестная фаза арены");
    }
    private const string FlightKey = MiniBossArena.Key + ".flight-probe";
    private static object FlightCreate(Character original, Vector3 origin)
    {
        if (ForestFixture.AllZdos().Values.Any(z => z.GetString(FlightKey).Length > 0))
            throw new InvalidOperationException("Сначала завершить прежнюю пробу снаряда");
        string token = MiniBossRuntime.Data(original)!.GetString(ForestRuntime.Tag);
        var shooter = MiniBossTrial.Spawn("Skeleton", origin + new Vector3(0, .08f, 2), Quaternion.identity)
            .GetComponent<Character>();
        MiniBossRuntime.Mark(shooter, "Проба снаряда", 410040);
        PovBridge.AssignIds(shooter); shooter.GetComponent<BaseAI>().enabled = false;
        ((Humanoid)shooter).GiveDefaultItems();
        ((Humanoid)shooter).EquipBestWeapon(original, null!, null!, null!);
        var sd = MiniBossRuntime.Data(shooter)!; sd.Set(ForestRuntime.Tag, token); sd.Set(MiniBossArena.Key, true);
        sd.Set(MiniBossArena.OriginKey, origin); sd.Set(FlightKey, "shooter");
        sd.Set(MiniBossArena.StateKey, JsonConvert.SerializeObject(new MiniBossArenaState {
            Casting = true, Windup = 100, Aim = new float[] { 0, 0, 0 } }));
        Character Target(string role, Vector3 p)
        {
            var target = MiniBossTrial.Spawn("Troll", p, Quaternion.identity).GetComponent<Character>();
            target.GetComponent<BaseAI>().enabled = false; PovBridge.AssignIds(target);
            target.GetComponent<Rigidbody>().useGravity = false;
            // Ставим середину высокой модели на высоту снаряда, не выдавая это за игровой проход.
            target.transform.position += Vector3.up * (2 - (target.GetCenterPoint().y - p.y));
            var zdo = MiniBossRuntime.Data(target)!;
            zdo.Set(FlightKey, role); zdo.Set(FlightKey + ".before", target.GetHealth());
            return target;
        }
        var exposed = Target("behind-door", origin + new Vector3(0, 0, -1.6f));
        var hidden = Target("behind-wall", origin + new Vector3(4.4f, 0, 1));
        var control = Target("one-hit-control", origin + new Vector3(20, 0, 20));
        var hit = MiniBossAttackProfile.Damage(shooter); hit.m_point = control.GetCenterPoint();
        AccessTools.Method(typeof(Character), "RPC_Damage").Invoke(control, new object[] { 0L, hit });
        sd.Set(FlightKey + ".expected", MiniBossRuntime.Data(control)!.GetFloat(FlightKey + ".before") -
            control.GetHealth());
        Physics.SyncTransforms();
        MiniBossRock.Launch(shooter, origin + new Vector3(0, 2, -4), 410040);
        return new { created = true, expected = sd.GetFloat(FlightKey + ".expected") };
    }
    private static object FlightCheck()
    {
        var actors = Character.GetAllCharacters().Where(c =>
            MiniBossRuntime.Data(c)?.GetString(FlightKey).Length > 0).ToArray();
        Character Get(string role) => actors.Single(c => MiniBossRuntime.Data(c)!.GetString(FlightKey) == role);
        var shooter = Get("shooter");
        float Loss(string role)
        { var c = Get(role); return MiniBossRuntime.Data(c)!.GetFloat(FlightKey + ".before") - c.GetHealth(); }
        float expected = MiniBossRuntime.Data(shooter)!.GetFloat(FlightKey + ".expected");
        float exposed = Loss("behind-door"), hidden = Loss("behind-wall");
        string result = MiniBossRuntime.Data(shooter)!.GetString(MiniBossRock.Key + ".result");
        bool passed = expected > 0 && Math.Abs(exposed - expected) < .05f && hidden == 0 && result.Length > 0;
        var report = new { passed, expected, exposed, hidden, result };
        foreach (var actor in actors) ZNetScene.instance.Destroy(actor.gameObject);
        return report;
    }
    private static object Catalog()
    {
        var golem = ZNetScene.instance.GetPrefab("ObsidianGolem_TW")?.GetComponent<Humanoid>();
        return new
        {
            golem = (golem == null ? Array.Empty<GameObject>() : golem.m_defaultItems)
                .Select(w => w.GetComponent<ItemDrop>()).Where(w => w != null)
                .Select(w => new { prefab = w.name, attack = w.m_itemData.m_shared.m_attack.m_attackType.ToString(),
                    speed = w.m_itemData.m_shared.m_attack.m_projectileVel,
                    gravity = w.m_itemData.m_shared.m_attack.m_attackProjectile?.GetComponent<Projectile>()?.m_gravity,
                    projectile = w.m_itemData.m_shared.m_attack.m_attackProjectile?.name }).ToArray(),
            creatures = new[] { "Skeleton", "Ghost" }.Select(name =>
            {
                var prefab = ZNetScene.instance.GetPrefab(name);
                return new { name, items = prefab.GetComponent<Humanoid>().m_defaultItems.Select(w =>
                    new { name = w.name, animation = w.GetComponent<ItemDrop>()?.m_itemData.m_shared.m_attack
                        .m_attackAnimation }).ToArray(),
                    clips = prefab.GetComponentInChildren<Animator>(true).runtimeAnimatorController.animationClips
                        .Where(c => c.events.Length > 0).Select(c => new { c.name, c.length,
                            events = c.events.Select(e => new { e.functionName, e.time }).ToArray() }).ToArray() };
            }).ToArray(),
            effects = ZNetScene.instance.m_prefabs.Concat(ZNetScene.instance.m_nonNetViewPrefabs)
                .Where(p => p.name.IndexOf("golem", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (p.name.StartsWith("vfx") || p.name.StartsWith("sfx"))).Select(p => p.name).ToArray(),
            live_weapons = Character.GetAllCharacters().Where(MiniBossArena.Applies).Select(c => new {
                prefab = Utils.GetPrefabName(c.gameObject), weapon = MiniBossAttackProfile.Weapon(c).m_dropPrefab.name,
                animation = MiniBossAttackProfile.Weapon(c).m_shared.m_attack.m_attackAnimation,
                variants = MiniBossAttackProfile.Weapon(c).m_shared.m_attack.m_attackRandomAnimations
            }).ToArray()
        };
    }
    private static object Damage(Character boss, Vector3 origin)
    {
        string token = MiniBossRuntime.Data(boss)!.GetString(ForestRuntime.Tag);
        var gate = Object.FindObjectsByType<Door>(FindObjectsSortMode.None).Single(d =>
            MiniBossRuntime.Data(d)?.GetString(ForestRuntime.Tag) == token);
        var wear = gate.GetComponent<WearNTear>();
        float before = MiniBossRuntime.Data(wear)!.GetFloat(ZDOVars.s_health, wear.m_health);
        var hit = MiniBossAttackProfile.Damage(boss);
        hit.m_hitType = MiniBossRock.BarrierHit; hit.m_damage = new HitData.DamageTypes { m_chop = 10, m_pickaxe = 10 };
        var resistance = wear.m_damages;
        AccessTools.Method(typeof(WearNTear), "RPC_Damage").Invoke(wear, new object[] { 0L, hit });
        float after = MiniBossRuntime.Data(wear)!.GetFloat(ZDOVars.s_health, wear.m_health);
        bool restored = wear.m_damages.Equals(resistance);
        if (after >= before || !restored) throw new InvalidOperationException("Урон закрытой двери не прошёл");
        return new { before, after, resistance_restored = restored, closed_special_damage = true,
            permanent_geometry = Object.FindObjectsByType<MiniBossArena>(FindObjectsSortMode.None)
                .All(a => a.GetComponent<IDestructible>() == null), raw_damage = MiniBossAttackProfile.Damage(boss)
                    .m_damage.GetTotalDamage(), projectile_speed = MiniBossAttackProfile.Speed,
            windup_animation_seconds = MiniBossAttackProfile.Windup(boss, MiniBossAttackProfile.Weapon(boss)) };
    }
    private static object Snapshot() => new
    {
        reference = ForestFixture.V(ZNet.instance.GetReferencePosition()),
        arenas = ForestFixture.AllZdos().Values.Count(z =>
            z.GetPrefab() == MiniBossArenaPlan.Prefab.GetStableHashCode()),
        values = ForestFixture.AllZdos().Values.Where(z => z.GetBool(MiniBossArena.ValueKey))
            .Select(z => new { prefab = ZNetScene.instance.GetPrefab(z.GetPrefab()).name,
                item = z.GetInt(ZDOVars.s_itemPrefab), count = z.GetInt(ZDOVars.s_itemStack),
                position = ForestFixture.V(z.GetPosition()) }).ToArray(),
        bosses = ForestFixture.AllZdos().Values.Where(z => z.GetBool(MiniBossRuntime.BossKey) &&
                z.GetBool(MiniBossArena.Key)).Select(z => new { state = z.GetString(MiniBossArena.StateKey),
                maximum = z.GetFloat(ZDOVars.s_maxHealth), hp = z.GetFloat(ZDOVars.s_health),
                ward = z.GetFloat("pt.ward.cur"), ward_max = z.GetFloat("pt.ward.max"),
                position = ForestFixture.V(z.GetPosition()) }).ToArray()
    };
}
