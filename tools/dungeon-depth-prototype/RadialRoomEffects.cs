using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DungeonDepthPrototype;

// Дополнение к отдельным механизмам: эффекты внутри созданной комнаты и неизменность привязки сундука.
internal static class RadialRoomEffects
{
    internal static (bool Passed, object Report) Run(ForestGuardSpace space, Character[] guards)
    {
        var source = guards.FirstOrDefault(g => PovBridge.Count(PovBridge.Read(g)) == 20 &&
            ForestRuntime.Zdo(g).GetString(GuardedChestReward.IdentityKey).Length > 0) ??
            throw new InvalidOperationException("Нужна охрана сундука с двадцатью свойствами");
        var data = ForestRuntime.Zdo(source);
        string identity = data.GetString(GuardedChestReward.IdentityKey);
        var chest = Object.FindObjectsByType<Container>(FindObjectsSortMode.None).Single(c =>
            GuardedChestReward.Read(c)?.GuardToken == identity);
        string reward = ForestRuntime.Zdo(chest).GetString(GuardedChestReward.StateKey);
        string profile = data.GetString(RadialRuntime.ProfileKey); int mask = PovBridge.Read(source);
        var before = Object.FindObjectsByType<ZNetView>(FindObjectsSortMode.None).ToHashSet();
        var carves = RadialAffixProbe.Effects("Carve").ToHashSet();
        var pools = RadialAffixProbe.Effects("Pool").ToHashSet();
        var previousPlayer = Player.m_localPlayer;
        try
        {
            var player = Object.Instantiate(ZNetScene.instance.GetPrefab("Player"),
                source.transform.position + Vector3.up * 30, Quaternion.identity).GetComponent<Player>();
            AccessTools.Field(typeof(Player), "m_localPlayer").SetValue(null, previousPlayer);
            player.enabled = false; player.GetComponent<Rigidbody>().isKinematic = true;
            player.SetGodMode(false); player.SetGhostMode(false);
            AccessTools.Field(typeof(Player), "m_debugFly").SetValue(player, false);
            AccessTools.Field(typeof(Player), "m_stealthFactor").SetValue(player, 1f);
            int room = data.GetInt(ForestRuntime.RoomKey);
            int viewMask = (int)AccessTools.Field(typeof(BaseAI), "m_viewBlockMask").GetValue(null);
            bool allowed(Vector3 p) => Vector3.Distance(p, source.transform.position) >= 1.3f &&
                Vector3.Distance(p, source.transform.position) <= 2.8f &&
                !Physics.Linecast(source.GetEyePoint(), p + Vector3.up * 1.6f, viewMask);
            if (!space.FreePoint(player, room, out var target, allowed))
                throw new InvalidOperationException("Нет свободной видимой точки игрока в комнате свойства");
            player.transform.position = target; player.GetComponent<Rigidbody>().position = target;
            ForestRuntime.Zdo(player).SetPosition(target); Physics.SyncTransforms();
            source.transform.rotation = Quaternion.LookRotation(new Vector3(target.x - source.transform.position.x,
                0, target.z - source.transform.position.z));
            AccessTools.Field(typeof(MonsterAI), "m_targetCreature").SetValue(source.GetComponent<MonsterAI>(), player);
            bool visible = source.GetBaseAI().CanSeeTarget(player);
            RadialAffixProbe.Tick(source);
            bool rune = RadialAffixProbe.Effects("Carve").Except(carves).Any();
            bool poison = player.GetSEMan().HaveStatusEffect("Poison".GetStableHashCode());
            for (int i = 0; i < 3; i++) PovBridge.Sync.Invoke(null, new object[] { source });
            bool binding = reward == ForestRuntime.Zdo(chest).GetString(GuardedChestReward.StateKey) &&
                identity == data.GetString(GuardedChestReward.IdentityKey) && PovBridge.Read(source) == mask &&
                profile == data.GetString(RadialRuntime.ProfileKey);
            int actors = Character.GetAllCharacters().Count;
            int runes = RadialAffixProbe.Effects("Carve").Length, puddles = RadialAffixProbe.Effects("Pool").Length;
            source.SetHealth(0); source.OnDeath();
            bool deathEffects = RadialAffixProbe.Effects("Carve").Length == runes + 1 &&
                RadialAffixProbe.Effects("Pool").Length == puddles + 1;
            bool noOffspring = Character.GetAllCharacters().Count <= actors;
            bool chestUnchanged = reward == ForestRuntime.Zdo(chest).GetString(GuardedChestReward.StateKey);
            bool passed = visible && rune && poison && binding && deathEffects && noOffspring && chestUnchanged;
            return (passed, new { passed,
                visible, rune, poison, binding, deathEffects, noOffspring, chestUnchanged, room,
                scope = "native-room-effects; client-delay-not-measured" });
        }
        finally
        {
            AccessTools.Field(typeof(Player), "m_localPlayer").SetValue(null, previousPlayer);
            foreach (var effect in RadialAffixProbe.Effects("Carve").Except(carves)
                         .Concat(RadialAffixProbe.Effects("Pool").Except(pools)))
                if (effect != null) Object.Destroy(effect.gameObject);
            foreach (var view in Object.FindObjectsByType<ZNetView>(FindObjectsSortMode.None).Except(before))
                if (view.IsValid() && view.IsOwner()) ZNetScene.instance.Destroy(view.gameObject);
        }
    }
}
