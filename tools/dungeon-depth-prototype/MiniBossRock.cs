using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DungeonDepthPrototype;

internal static class MiniBossAttackProfile
{
    internal static ItemDrop.ItemData Weapon(Character boss)
    {
        var humanoid = (Humanoid)boss;
        var weapon = humanoid.GetCurrentWeapon();
        if (weapon != null) return weapon;
        // Наш idle не вызывает штатный выбор атаки. После Start оружие уже есть в инвентаре,
        // но у призрака оно ещё не надето; выбираем существующий предмет без повторной выдачи.
        weapon = humanoid.GetInventory().GetAllItems().FirstOrDefault(i => i.IsWeapon());
        if (weapon == null || !humanoid.EquipItem(weapon, false))
            throw new InvalidOperationException("Нет готового оружия мини-босса");
        return weapon;
    }
    // Нативный обмер Monstrum 1.6.1: obsidiangolem_throw — 17 м/с, gravity 5.
    // Полная Monstrum не требуется изолированному кандидату; происхождение закреплено manifest выпуска.
    internal const float Speed = 17 * 1.5f, Gravity = 5;
    internal static string Animation(ItemDrop.ItemData weapon)
    {
        var attack = weapon.m_shared.m_attack;
        return attack.m_attackAnimation + (attack.m_attackRandomAnimations >= 2 || attack.m_attackChainLevels > 1
            ? "0" : "");
    }
    internal static float Windup(Character boss, ItemDrop.ItemData weapon)
    {
        var animator = boss.GetComponentInChildren<Animator>();
        string species = Utils.GetPrefabName(boss.gameObject);
        // Имена клипов сверены на закреплённой игре. Имена trigger и clip у скелета не совпадают.
        string clipName = species == "Ghost" ? "Attack Swipe" :
            weapon.m_shared.m_attack.m_attackType == Attack.AttackType.Projectile ? "Bow Aim Recoil" :
            weapon.m_dropPrefab.name.IndexOf("mace", StringComparison.OrdinalIgnoreCase) >= 0 ?
                "Skeleton_mace" : "Standing Melee Attack Horizontal";
        var clip = animator.runtimeAnimatorController.animationClips.Single(c => c.name == clipName);
        return clip.events.Single(e => e.functionName == "OnAttackTrigger" || e.functionName == "Hit").time;
    }
    internal static HitData Damage(Character boss)
    {
        var item = Weapon(boss); var attack = item.m_shared.m_attack;
        var hit = new HitData { m_damage = item.GetDamage(), m_blockable = item.m_shared.m_blockable,
            m_dodgeable = item.m_shared.m_dodgeable, m_toolTier = (short)item.m_shared.m_toolTier,
            m_pushForce = item.m_shared.m_attackForce * attack.m_forceMultiplier,
            m_staggerMultiplier = attack.m_staggerMultiplier, m_backstabBonus = item.m_shared.m_backstabBonus,
            m_skill = item.m_shared.m_skillType, m_itemLevel = (short)item.m_quality,
            m_hitType = HitData.HitType.EnemyHit };
        hit.m_damage.Modify(attack.m_damageMultiplier * (1 + Math.Max(0, boss.GetLevel() - 1) * .5f));
        if (attack.m_damageMultiplierPerMissingHP > 0)
            hit.m_damage.Modify(1 + (boss.GetMaxHealth() - boss.GetHealth()) * attack.m_damageMultiplierPerMissingHP);
        if (attack.m_damageMultiplierByTotalHealthMissing > 0)
            hit.m_damage.Modify(1 + (1 - boss.GetHealthPercentage()) * attack.m_damageMultiplierByTotalHealthMissing);
        hit.SetAttacker(boss);
        if (item.m_shared.m_attackStatusEffect != null &&
            UnityEngine.Random.value < item.m_shared.m_attackStatusEffectChance)
            hit.m_statusEffectHash = item.m_shared.m_attackStatusEffect.NameHash();
        boss.GetSEMan().ModifyAttack(item.m_shared.m_skillType, ref hit);
        // EnemyDifficulty и применимые свойства PoV проходят штатный RPC_Damage ровно один раз.
        return hit;
    }
}

internal sealed class MiniBossRock : MonoBehaviour
{
    internal const string Key = MiniBossArena.Key + ".rock", HitKey = Key + ".hit";
    internal const HitData.HitType BarrierHit = (HitData.HitType)211;
    private ZNetView view = null!;
    private void Awake() { view = GetComponent<ZNetView>(); }
    internal static void Install(Harmony harmony)
    {
        foreach (var type in new[] { typeof(WearNTear), typeof(Destructible) })
            harmony.Patch(AccessTools.Method(type, "RPC_Damage"),
                prefix: new HarmonyMethod(typeof(MiniBossRock), nameof(BarrierBefore)),
                finalizer: new HarmonyMethod(typeof(MiniBossRock), nameof(BarrierAfter)));
    }
    internal static bool AllowedBarrier(Component target, HitData hit) => hit.m_hitType == BarrierHit &&
        hit.GetAttacker() is Character attacker && MiniBossArena.Applies(attacker) &&
        MiniBossRuntime.Data(target)?.GetString(ForestRuntime.Tag) ==
            MiniBossRuntime.Data(attacker)!.GetString(ForestRuntime.Tag);
    private static void BarrierBefore(Component __instance, HitData hit, out HitData.DamageModifiers? __state)
    {
        __state = null;
        if (hit.m_hitType != BarrierHit || !MiniBossArena.Applies(hit.GetAttacker()) ||
            __instance.GetComponentInParent<MiniBossArena>() != null) return;
        var field = AccessTools.Field(__instance.GetType(), "m_damages");
        var previous = (HitData.DamageModifiers)field.GetValue(__instance); __state = previous;
        var current = previous.Clone();
        current.m_chop = current.m_pickaxe = current.m_nonPlayer = HitData.DamageModifier.Normal;
        field.SetValue(__instance, current);
        hit.m_toolTier = short.MaxValue;
    }
    private static void BarrierAfter(Component __instance, HitData.DamageModifiers? __state)
    {
        if (__state.HasValue) AccessTools.Field(__instance.GetType(), "m_damages").SetValue(__instance, __state.Value);
    }
    internal static void Launch(Character boss, Vector3 target, int sequence)
    {
        var start = boss.transform.position + Vector3.up * 2;
        var direction = (target - start).normalized;
        var obj = Object.Instantiate(ZNetScene.instance.GetPrefab(MiniBossArenaPlan.Projectile), start,
            Quaternion.LookRotation(direction));
        var data = MiniBossRuntime.Data(obj.transform)!;
        var hit = MiniBossAttackProfile.Damage(boss); var package = new ZPackage(); hit.Serialize(ref package);
        data.Set(Key, true); data.Set(HitKey, package.GetBase64());
        data.Set(Key + ".velocity", direction * MiniBossAttackProfile.Speed);
        data.Set(Key + ".sequence", sequence); data.Set(Key + ".attacker", boss.GetZDOID());
        data.Set(ForestRuntime.Tag, MiniBossRuntime.Data(boss)!.GetString(ForestRuntime.Tag));
    }
    private void FixedUpdate()
    {
        if (!view.IsValid()) return;
        var data = view.GetZDO();
        if (!view.IsOwner()) { transform.position = data.GetPosition(); return; }
        if (!data.GetBool(Key)) return;
        if (data.GetBool(Key + ".exploded")) { ZNetScene.instance.Destroy(gameObject); return; }
        var velocity = data.GetVec3(Key + ".velocity", Vector3.zero);
        velocity.y -= MiniBossAttackProfile.Gravity * Time.fixedDeltaTime;
        data.Set(Key + ".velocity", velocity);
        var step = velocity * Time.fixedDeltaTime;
        float remaining = MiniBossArenaPlan.FlightDistance - data.GetFloat(Key + ".distance");
        if (step.magnitude > remaining) step = step.normalized * remaining;
        var source = ZNetScene.instance.FindInstance(data.GetZDOID(Key + ".attacker"));
        var contact = Physics.SphereCastAll(transform.position, .25f, step.normalized, step.magnitude,
                MiniBossArena.Solid, QueryTriggerInteraction.Ignore)
            .Where(h => source == null || !h.collider.transform.IsChildOf(source.transform))
            .OrderBy(h => h.distance).ToArray();
        if (contact.Length > 0)
        { Explode(contact[0].point - step.normalized * .03f); return; }
        transform.position += step; data.SetPosition(transform.position);
        data.Set(Key + ".distance", data.GetFloat(Key + ".distance") + step.magnitude);
        if (step.magnitude >= remaining - .001f) Explode(transform.position);
    }
    internal void Explode(Vector3 position)
    {
        var data = view.GetZDO();
        if (!view.IsOwner() || data.GetBool(Key + ".exploded")) return;
        data.Set(Key + ".exploded", true);
        var original = new HitData(); var package = new ZPackage(data.GetString(HitKey));
        original.Deserialize(ref package);
        var attacker = original.GetAttacker();
        var targets = new HashSet<IDestructible>(); int characters = 0, barriers = 0, shielded = 0;
        foreach (var collider in Physics.OverlapSphere(position, MiniBossArenaPlan.CorridorWidth,
                     MiniBossArena.Solid, QueryTriggerInteraction.Ignore))
        {
            var target = collider.GetComponentInParent<IDestructible>();
            if (target == null || ReferenceEquals(target, attacker) || !targets.Add(target) ||
                collider.GetComponentInParent<MiniBossArena>() != null) continue;
            if (target is Component fixture && MiniBossArenaFixtures.Portal(fixture)) continue;
            var character = target as Character;
            var point = character == null ? collider.ClosestPoint(position) : character.GetCenterPoint();
            if (MiniBossArena.Shielded(position, point)) { shielded++; continue; }
            if (character != null && attacker != null && !BaseAI.IsEnemy(attacker, character)) continue;
            var hit = original.Clone(); hit.m_point = point; hit.m_dir = (point - position).normalized;
            hit.m_hitCollider = collider;
            if (character == null)
            {
                var component = target as Component;
                if (component == null || (component.GetComponent<WearNTear>() == null &&
                    component.GetComponent<Destructible>() == null)) continue;
                float factor = attacker == null ? 1 : Convert.ToSingle(PovBridge.Method(
                    "PassiveTree.EnemyDifficulty", "DamageFactor").Invoke(null, new object[] { attacker }));
                float amount = Math.Max(1, hit.m_damage.GetTotalDamage() * factor * 5);
                hit.m_damage = new HitData.DamageTypes { m_chop = amount / 2, m_pickaxe = amount / 2 };
                hit.m_toolTier = short.MaxValue; hit.m_hitType = BarrierHit; barriers++;
            }
            else characters++;
            target.Damage(hit);
        }
        foreach (string effect in new[] { "vfx_stonegolem_attack_hit", "sfx_stonegolem_attack_hit" })
            ZNetScene.instance.SpawnObject(position, Quaternion.identity, ZNetScene.instance.GetPrefab(effect));
        if (attacker != null)
            MiniBossRuntime.Data(attacker)!.Set(Key + ".result", $"characters={characters};barriers={barriers};" +
                $"shielded={shielded};sequence={data.GetInt(Key + ".sequence")}");
        ZNetScene.instance.Destroy(gameObject);
    }
}
