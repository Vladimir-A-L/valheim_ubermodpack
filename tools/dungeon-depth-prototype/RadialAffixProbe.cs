using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DungeonDepthPrototype;

// Изолированные нативные механизмы; не подмена ручного боя и не измерение вероятности победы.
internal static class RadialAffixProbe
{
    internal static readonly string[] Additional = { "burning", "runecarver", "headsman", "blast", "rotten",
        "reeking", "bound", "leader", "charger", "mirror" };
    private static readonly string HabitsType = "PassiveTree.RarityMods2+Habits";
    private static Type Type(string name) => PovBridge.Assembly.GetType(name, true);
    private static object Habits(Character c) => PovBridge.Method(HabitsType, "Ensure")
        .Invoke(null, new object[] { c });
    internal static void Tick(Character c)
    {
        var habits = Habits(c);
        foreach (string field in new[] { "nextLook", "nextReek", "nextMirror", "nextCarve", "nextLead" })
            AccessTools.Field(habits.GetType(), field).SetValue(habits, 0f);
        AccessTools.Method(habits.GetType(), "RefreshNow").Invoke(habits, null);
        AccessTools.Method(habits.GetType(), "Update").Invoke(habits, null);
    }
    internal static Component[] Effects(string name) => Object.FindObjectsByType(
        Type("PassiveTree.RarityMods2+" + name), FindObjectsSortMode.None).Cast<Component>().ToArray();

    internal static object Run()
    {
        if (!ZNet.instance.IsServer() || ZNet.instance.GetPeers().Any() ||
            !Plugin.Instance.Config.Bind("ForestCrypt", "RadialDiagnostics", false).Value)
            throw new InvalidOperationException("Нужен отдельный радиальный стенд без игроков");
        var actors = new List<Character>();
        var beforeCarve = new HashSet<Component>(Effects("Carve"));
        var beforePool = new HashSet<Component>(Effects("Pool"));
        var previousPlayer = Player.m_localPlayer;
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var origin = new Vector3(0, 4200, 0);
        floor.transform.position = origin - Vector3.up * .5f;
        floor.transform.localScale = new Vector3(40, 1, 40);
        floor.layer = LayerMask.NameToLayer("static_solid");
        Character Spawn(string prefab, Vector3 offset)
        {
            var c = Object.Instantiate(ZNetScene.instance.GetPrefab(prefab), origin + offset + Vector3.up * .08f,
                Quaternion.identity).GetComponent<Character>();
            actors.Add(c); c.enabled = false;
            if (c.GetComponent<BaseAI>() != null) c.GetComponent<BaseAI>().enabled = false;
            c.GetComponent<Rigidbody>().isKinematic = true;
            if (!c.IsPlayer()) PovBridge.AssignIds(c);
            // PoV один раз пересчитывает SetHealth в кадре нового масштаба; завершаем начальное заполнение HP.
            c.SetHealth(c.GetMaxHealth());
            return c;
        }
        HitData Hit(Character? attacker = null, float amount = 10)
        {
            var h = new HitData(); h.m_damage.m_damage = amount;
            if (attacker != null) h.SetAttacker(attacker);
            return h;
        }
        try
        {
            var source = Spawn("Skeleton", Vector3.zero);
            var partner = Spawn("Skeleton", Vector3.right * 4);
            var target = Spawn("Skeleton", Vector3.left * 14);
            Physics.SyncTransforms();
            var ids = new List<object>();
            foreach (string id in Additional)
            {
                PovBridge.AssignIds(source, id);
                var actual = PovBridge.Ids(PovBridge.Read(source));
                ids.Add(new { id, passed = actual.Length == 1 && actual[0] == id });
                if (actual.Length != 1 || actual[0] != id)
                    throw new InvalidOperationException("Не назначен отдельный ID: " + id);
            }
            var first = PovBridge.Method("PassiveTree.RarityMods+Character_RPC_Damage_Mods", "Prefix");
            var second = PovBridge.Method("PassiveTree.RarityMods2+Character_RPC_Damage_Wave2", "Prefix");
            PovBridge.AssignIds(source, "burning");
            var elemental = Hit(source);
            first.Invoke(null, new object[] { target, elemental });
            var resisted = Hit(); resisted.m_damage.m_damage = 0; resisted.m_damage.m_fire = 10;
            first.Invoke(null, new object[] { source, resisted });
            bool burning = Math.Abs(elemental.m_damage.m_fire - 2.2f) < .001f &&
                Math.Abs(elemental.m_damage.m_damage - 7.8f) < .001f && resisted.m_damage.m_fire == 5;

            PovBridge.AssignIds(source, "headsman");
            float headsmanThreshold = PovBridge.ConfigFloat("PassiveTree.ModifierTuning", "HeadsmanBelow");
            float headsmanFactor = PovBridge.ConfigFloat("PassiveTree.ModifierTuning", "HeadsmanBlow");
            target.SetHealth(target.GetMaxHealth() * .3f);
            float woundedFraction = target.GetHealth() / target.GetMaxHealth();
            bool avoided = (bool)AccessTools.Property(Type("PassiveTree.IncomingHit"), "Avoided").GetValue(null);
            bool targetOwner = target.GetComponent<ZNetView>().IsOwner();
            var finishing = Hit(source); bool attackerKnown = finishing.GetAttacker() == source;
            second.Invoke(null, new object[] { target, finishing });
            target.SetHealth(target.GetMaxHealth());
            var normal = Hit(source); second.Invoke(null, new object[] { target, normal });
            bool headsman = Math.Abs(finishing.m_damage.GetTotalDamage() / normal.m_damage.GetTotalDamage() - 1.3f)
                < .0001f;

            // Парная проба полного RPC: повторная броня на перенаправленной половине раны.
            PovBridge.AssignIds(source, "armored"); PovBridge.AssignIds(partner, "armored");
            source.SetHealth(source.GetMaxHealth()); partner.SetHealth(partner.GetMaxHealth());
            var rpc = AccessTools.Method(typeof(Character), "RPC_Damage");
            float hp = source.GetHealth() + partner.GetHealth();
            rpc.Invoke(source, new object[] { 0L, Hit() });
            float baselineLoss = hp - source.GetHealth() - partner.GetHealth();
            source.SetHealth(source.GetMaxHealth()); partner.SetHealth(partner.GetMaxHealth());
            PovBridge.AssignIds(source, "armored", "bound"); Tick(source);
            bool linked = (Character)AccessTools.Method(Type(HabitsType), "Thread")
                .Invoke(Habits(source), null) == partner;
            hp = source.GetHealth() + partner.GetHealth();
            rpc.Invoke(source, new object[] { 0L, Hit() });
            float sharedLoss = hp - source.GetHealth() - partner.GetHealth();
            bool bound = linked && baselineLoss > 0 && Math.Abs(sharedLoss / baselineLoss - .85f) < .001f;

            PovBridge.AssignIds(source, "leader"); PovBridge.AssignIds(partner);
            float run = partner.m_runSpeed; Tick(source);
            bool leader = Math.Abs(partner.m_runSpeed / run - 1.2f) < .001f;

            PovBridge.AssignIds(source, "mirror"); Tick(source);
            partner.SetHealth(partner.GetMaxHealth());
            hp = partner.GetHealth(); var ranged = Hit(partner); ranged.m_ranged = true;
            second.Invoke(null, new object[] { source, ranged });
            float reflected = hp - partner.GetHealth();
            bool mirror = reflected > 0 && Math.Abs(ranged.m_damage.GetTotalDamage() - 10) < .001f;

            var enemy = Spawn("Greydwarf", Vector3.forward * 12);
            PovBridge.AssignIds(source, "charger");
            AccessTools.Method(Type(HabitsType), "Rush").Invoke(Habits(source), new object[] { enemy });
            var rushing = (System.Collections.IEnumerable)AccessTools.Field(Type(HabitsType), "Rushing")
                .GetValue(null);
            bool charger = rushing.Cast<Character>().Contains(source);

            var player = (Player)Spawn("Player", Vector3.forward * 2);
            player.SetGodMode(false); player.SetGhostMode(false);
            AccessTools.Field(typeof(Player), "m_debugFly").SetValue(player, false);
            AccessTools.Field(typeof(Player), "m_stealthFactor").SetValue(player, 1f);
            player.GetComponent<ZNetView>().GetZDO().Set(ZDOVars.s_stealth, 1f);
            AccessTools.Field(typeof(Player), "m_localPlayer").SetValue(null, previousPlayer);
            Physics.SyncTransforms();
            var attack = new HitData.DamageTypes { m_blunt = 100 };
            AccessTools.Method(Type(HabitsType), "SawAttack").Invoke(Habits(source), new object[] { attack });
            PovBridge.AssignIds(source, "reeking"); Tick(source);
            bool reeking = player.GetSEMan().HaveStatusEffect("Poison".GetStableHashCode());
            PovBridge.AssignIds(source, "runecarver");
            AccessTools.Field(typeof(MonsterAI), "m_targetCreature").SetValue(source.GetComponent<MonsterAI>(), player);
            int priorRunes = Effects("Carve").Length; Tick(source);
            var rune = Effects("Carve").Except(beforeCarve).FirstOrDefault();
            float runeDamage = rune == null ? 0 : ((HitData.DamageTypes)AccessTools.Field(rune.GetType(), "damage")
                .GetValue(rune)).GetTotalDamage();
            bool runecarver = Effects("Carve").Length == priorRunes + 1 && Math.Abs(runeDamage - 60) < .001f;

            PovBridge.AssignIds(source, EncounterPlan.ForestModifiers.Concat(Additional).ToArray());
            for (int i = 0; i < 3; i++) PovBridge.Sync.Invoke(null, new object[] { source });
            bool combined = PovBridge.Count(PovBridge.Read(source)) == 20;
            int alive = Character.GetAllCharacters().Count(c => !c.IsDead());
            PovBridge.AssignIds(source, "blast", "rotten");
            int carves = Effects("Carve").Length, pools = Effects("Pool").Length;
            source.SetHealth(0); source.OnDeath();
            bool blast = Effects("Carve").Length == carves + 1;
            bool rotten = Effects("Pool").Length == pools + 1;
            bool noOffspring = Character.GetAllCharacters().Count(c => c != source && !c.IsDead()) == alive - 1;
            var checks = new { burning, headsman, bound, leader, mirror, charger, reeking, runecarver,
                combined, blast, rotten, no_offspring = noOffspring };
            return new { passed = burning && headsman && bound && leader && mirror && charger && reeking &&
                    runecarver && combined && blast && rotten && noOffspring,
                ids, checks, armor_bound = new { baseline_loss = baselineLoss, shared_loss = sharedLoss,
                    group_damage_ratio = baselineLoss == 0 ? 0 : sharedLoss / baselineLoss },
                reflected_damage = reflected, rune_damage = runeDamage,
                headsman_probe = new { threshold = headsmanThreshold, factor = headsmanFactor,
                    woundedFraction, avoided, targetOwner, attacker = attackerKnown,
                    finishing = finishing.m_damage.GetTotalDamage(), normal = normal.m_damage.GetTotalDamage() },
                rune_probe = new { target = source.GetBaseAI().GetTargetCreature() == player,
                    visible = source.GetBaseAI().CanSeeTarget(player),
                    player_health = player.GetHealth(), owner = source.GetComponent<ZNetView>().IsOwner() },
                scope = "isolated-native-hooks-and-rpc; no-client-combat-or-dps-rating" };
        }
        finally
        {
            AccessTools.Field(typeof(Player), "m_localPlayer").SetValue(null, previousPlayer);
            foreach (var effect in Effects("Carve").Except(beforeCarve).Concat(Effects("Pool").Except(beforePool)))
                if (effect != null) Object.Destroy(effect.gameObject);
            foreach (var actor in actors)
                if (actor != null && actor.GetComponent<ZNetView>().IsValid())
                    ZNetScene.instance.Destroy(actor.gameObject);
            Object.Destroy(floor);
        }
    }
}
