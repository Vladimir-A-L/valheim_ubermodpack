using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DungeonDepthPrototype;

// Поведение только явно помеченных экземпляров. Prefab обычных существ не изменяется.
internal sealed class MiniBossRuntime : MonoBehaviour
{
    internal const string BossKey = "uberdungeon.miniboss.v1";
    internal const string NameKey = BossKey + ".name", SeedKey = BossKey + ".seed";
    internal const string RewardKey = BossKey + ".reward", DropKey = BossKey + ".drop";
    private float next;

    internal static ZDO? Data(Component value)
    {
        var view = value.GetComponent<ZNetView>();
        return view != null && view.IsValid() ? view.GetZDO() : null;
    }

    internal static bool IsBoss(Character value) => value != null && !value.IsPlayer() &&
        Data(value)?.GetBool(BossKey) == true;

    internal static void Install(GameObject host, Harmony harmony)
    {
        if (!MiniBossTrial.Enabled) return;
        MiniBossArenaController.Install(harmony);
        MiniBossArenaFixtures.Install(harmony);
        harmony.Patch(PovBridge.Method("PassiveTree.EnemyDifficulty", "Desired"),
            postfix: new HarmonyMethod(typeof(MiniBossRuntime), nameof(Factor)));
        harmony.Patch(AccessTools.Method(typeof(Character), "OnDeath"),
            prefix: new HarmonyMethod(typeof(MiniBossRuntime), nameof(Died)) { priority = Priority.First });
        harmony.Patch(AccessTools.Method(typeof(WearNTear), "UpdateWear"),
            prefix: new HarmonyMethod(typeof(MiniBossRuntime), nameof(Wear)));
        if (host.GetComponent<MiniBossRuntime>() == null) host.AddComponent<MiniBossRuntime>();
    }

    private static void Factor(Character c, bool hp, ref float __result)
    {
        if (IsBoss(c)) __result *= hp ? MiniBossRules.HealthFactor : MiniBossRules.DamageFactor;
    }

    private static bool Wear(WearNTear __instance) =>
        Data(__instance)?.GetString(MiniBossTrial.HubKey).Length is not > 0;

    internal static void Mark(Character boss, string name, int seed)
    {
        var zdo = Data(boss) ?? throw new InvalidOperationException("Нет ZDO босса");
        if (!boss.GetComponent<ZNetView>().IsOwner()) throw new InvalidOperationException("Нужен владелец");
        boss.SetLevel(1);
        zdo.Set(BossKey, true); zdo.Set(NameKey, name); zdo.Set(SeedKey, seed);
        // Ensure учитывает старый и новый коэффициенты; повторная загрузка не умножает HP снова.
        PovBridge.Method("PassiveTree.EnemyDifficulty", "Ensure").Invoke(null, new object[] { boss });
        boss.SetHealth(boss.GetMaxHealth());
        Attach(boss);
    }

    private static void Attach(Character boss)
    {
        if (boss.GetComponent<MiniBossAppearance>() == null) boss.gameObject.AddComponent<MiniBossAppearance>();
        if (MiniBossArena.Applies(boss) && boss.GetComponent<MiniBossArenaController>() == null)
            boss.gameObject.AddComponent<MiniBossArenaController>();
    }

    private void Update()
    {
        if (!MiniBossTrial.Ready || Time.realtimeSinceStartup < next) return;
        next = Time.realtimeSinceStartup + .25f;
        var gates = ForestFixture.AllZdos().Values.Where(z => z.GetBool(ForestGates.GateKey))
            .GroupBy(z => z.GetString(ForestRuntime.Tag)).ToDictionary(g => g.Key,
                g => g.Any(z => z.GetBool(ForestGates.ReleasedKey)));
        foreach (var creature in Character.GetAllCharacters())
        {
            if (IsBoss(creature)) Attach(creature);
            var data = Data(creature);
            if (data?.GetString(MiniBossTrial.ActorKey).Length > 0)
            {
                var ai = creature.GetComponent<BaseAI>();
                // Разрушенная дверь удаляется из ZDO: отсутствие преграды тоже выпускает охрану.
                if (ai != null) ai.enabled = MiniBossArena.Applies(creature) ||
                    !gates.TryGetValue(data.GetString(ForestRuntime.Tag), out bool released)
                    || released;
            }
        }
        foreach (var view in Object.FindObjectsByType<ZNetView>(FindObjectsSortMode.None))
        {
            if (!view.IsValid()) continue;
            var zdo = view.GetZDO();
            string text = zdo.GetString(MiniBossTrial.RuneKey);
            if (text.Length > 0) ConfigureRune(view);
        }
    }

    internal static void ConfigureRune(ZNetView view)
    {
        view.transform.localScale = Vector3.one * view.GetZDO().GetFloat(MiniBossArenaFixtures.RuneScaleKey, .2f);
        if (view.GetComponent<MiniBossRuneShape>() == null) view.gameObject.AddComponent<MiniBossRuneShape>();
        foreach (var stand in view.GetComponentsInChildren<ItemStand>(true)) Object.Destroy(stand);
        foreach (var stone in view.GetComponentsInChildren<BossStone>(true)) Object.Destroy(stone);
        foreach (var rune in view.GetComponentsInChildren<RuneStone>(true))
            Object.Destroy(rune);
        if (view.GetComponent<MiniBossRuneText>() == null) view.gameObject.AddComponent<MiniBossRuneText>();
    }

    internal sealed class RewardState
    {
        public int Schema = 1, Delivered, Gold;
        public string Token = "", GeneratedAtUtc = "";
        public List<string> Items = new List<string>();
    }

    private static RewardState Generate(Character boss)
    {
        var zdo = Data(boss)!;
        var state = new RewardState { Token = Guid.NewGuid().ToString("N"),
            GeneratedAtUtc = DateTime.UtcNow.ToString("O") };
        var previous = UnityEngine.Random.state;
        try
        {
            int seed = zdo.GetInt(SeedKey);
            UnityEngine.Random.InitState(unchecked(seed ^ 0x31415926));
            var identities = new HashSet<string>();
            for (int i = 0; i < 2; i++)
            {
                ItemDrop.ItemData? item = null;
                for (int attempt = 0; attempt < 64; attempt++)
                {
                    var candidate = EpicLootBridge.Generate("TreasureChest_forestcrypt", 1, RewardQuality.Mythic);
                    if (identities.Add(EpicLootBridge.Identity(candidate))) { item = candidate; break; }
                }
                if (item == null) throw new InvalidOperationException("Не удалось получить разные предметы");
                item.m_customData[DropKey] = state.Token + ":" + state.Items.Count;
                state.Items.Add(GuardedChestReward.PackItem(item));
            }
            state.Gold = new System.Random(unchecked(seed ^ 0x27d4eb2d)).Next(300, 901);
            var prefab = ObjectDB.instance.GetItemPrefab("Coins");
            var source = prefab.GetComponent<ItemDrop>().m_itemData;
            for (int left = state.Gold; left > 0;)
            {
                var coins = source.Clone(); coins.m_dropPrefab = prefab;
                coins.m_stack = Math.Min(left, source.m_shared.m_maxStackSize); left -= coins.m_stack;
                coins.m_customData[DropKey] = state.Token + ":" + state.Items.Count;
                state.Items.Add(GuardedChestReward.PackItem(coins));
            }
            return state;
        }
        finally { UnityEngine.Random.state = previous; }
    }

    // Используется и пробой; повторный вызов на том же ZDO не создаёт предметы заново.
    internal static RewardState DropReward(Character boss)
    {
        var view = boss.GetComponent<ZNetView>();
        if (!IsBoss(boss) || !view.IsOwner()) throw new InvalidOperationException("Нужен владелец босса");
        var zdo = view.GetZDO();
        string saved = zdo.GetString(RewardKey);
        var state = saved.Length > 0 ? JsonConvert.DeserializeObject<RewardState>(saved)! : Generate(boss);
        zdo.Set(RewardKey, JsonConvert.SerializeObject(state));
        while (state.Delivered < state.Items.Count)
        {
            var item = GuardedChestReward.Unpack(state.Items[state.Delivered], 1, 1).GetAllItems().Single();
            var drop = ItemDrop.DropItem(item, item.m_stack,
                boss.transform.position + Vector3.up * .5f + Vector3.right * (.3f * state.Delivered),
                Quaternion.identity);
            Data(drop)!.Set(DropKey, item.m_customData[DropKey]);
            state.Delivered++;
            zdo.Set(RewardKey, JsonConvert.SerializeObject(state));
        }
        return state;
    }

    private static bool Died(Character __instance)
    {
        if (!IsBoss(__instance) || !__instance.GetComponent<ZNetView>().IsOwner()) return true;
        try
        {
            DropReward(__instance);
            return true;
        }
        catch (Exception error)
        {
            Plugin.LogError("MINIBOSS_REWARD_FAILED: " + error);
            return false; // Сохраняем исходный объект и квитанцию для диагностики незавершённой выдачи.
        }
    }
}

// Предупреждение использует штатное окно рун, но не выдаёт серверное очко PoV за открытие обычной руны.
internal sealed class MiniBossRuneText : MonoBehaviour, Hoverable, Interactable
{
    public string GetHoverName() => "Рунный камень";
    public string GetHoverText() => Localization.instance.Localize(
        "Рунный камень\n[<color=yellow><b>$KEY_Use</b></color>] $piece_rune_read");
    public float GetHoverOffset() => 0;
    public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;
    public bool Interact(Humanoid character, bool hold, bool alt)
    {
        if (!hold && character == Player.m_localPlayer)
            TextViewer.instance.ShowText(TextViewer.Style.Rune, "Усыпальница",
                MiniBossRuntime.Data(this)!.GetString(MiniBossTrial.RuneKey), true);
        return false;
    }
}

// Только область чтения. У камня в стене она вынесена к рисунку и имеет толщину 5 см.
internal sealed class MiniBossRuneShape : MonoBehaviour
{
    private BoxCollider interaction = null!;
    private readonly Dictionary<Transform, (Vector3 Position, Quaternion Rotation)> visuals =
        new Dictionary<Transform, (Vector3, Quaternion)>();
    private void Awake()
    {
        interaction = gameObject.AddComponent<BoxCollider>();
        bool wall = MiniBossRuntime.Data(this)?.GetBool(MiniBossTrial.RuneWallKey) == true;
        interaction.center = new Vector3(0, 3.75f, wall ? .75f : 0);
        interaction.size = new Vector3(2, 7.5f, wall ? .25f : 1.75f);
        Maintain();
        InvokeRepeating(nameof(Maintain), .25f, .25f);
    }

    private void Maintain()
    {
        var data = MiniBossRuntime.Data(this);
        if (data?.GetBool(MiniBossTrial.RuneWallKey) == true)
        {
            var rotation = Quaternion.Euler(0, data.GetInt(MiniBossTrial.RuneVisualKey) * 90, 0);
            foreach (Transform child in transform)
            {
                if (!visuals.TryGetValue(child, out var source))
                    visuals.Add(child, source = (child.localPosition, child.localRotation));
                child.localPosition = rotation * source.Position;
                child.localRotation = rotation * source.Rotation;
            }
        }
        // Дочерние модели могут включить свои коллайдеры после Awake. Камень — только указатель:
        // взаимодействие лучом сохраняется, но декоративный объект не перекрывает узкий проход.
        foreach (var collider in GetComponentsInChildren<Collider>(true))
            if (collider != interaction) collider.enabled = false;
        foreach (var character in Character.GetAllCharacters())
        foreach (var collider in character.GetComponentsInChildren<Collider>(true))
            Physics.IgnoreCollision(interaction, collider);
    }

    internal object CheckWallMount()
    {
        Maintain(); Physics.SyncTransforms();
        var ray = new Ray(interaction.bounds.center + transform.forward, -transform.forward);
        bool visible = Physics.Raycast(ray, out var hit, 1.5f,
            LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "blocker"),
            QueryTriggerInteraction.Ignore) && hit.collider == interaction;
        var colliders = Character.GetAllCharacters()
            .SelectMany(c => c.GetComponentsInChildren<Collider>(true)).ToArray();
        bool nonblocking = colliders.All(c => Physics.GetIgnoreCollision(interaction, c));
        if (!visible || !nonblocking)
            throw new InvalidOperationException("Камень в стене недоступен лучу или сталкивается с персонажем");
        return new { interaction_ray_visible = visible, character_collisions_ignored = nonblocking,
            checked_colliders = colliders.Length, interaction_depth = interaction.size.z * transform.lossyScale.z };
    }
}

internal sealed class MiniBossAppearance : MonoBehaviour
{
    private Character creature = null!;
    private Vector3 normal;
    private void Awake()
    {
        creature = GetComponent<Character>();
        var prefab = ZNetScene.instance.GetPrefab(Utils.GetPrefabName(gameObject));
        normal = prefab == null ? transform.localScale : prefab.transform.localScale;
        var glow = new GameObject("MiniBossColdGlow");
        glow.transform.SetParent(transform, false); glow.transform.localPosition = Vector3.up;
        var light = glow.AddComponent<Light>();
        light.type = LightType.Point; light.color = new Color(.35f, .65f, 1f);
        light.range = 4; light.intensity = .8f; light.shadows = LightShadows.None;
    }

    private void LateUpdate()
    {
        transform.localScale = normal * 1.8f;
        string name = MiniBossRuntime.Data(creature)?.GetString(MiniBossRuntime.NameKey) ?? "";
        if (name.Length > 0) creature.m_name = name;
    }
}
