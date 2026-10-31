using System;
using System.IO;
using System.Linq;
using BepInEx;
using Newtonsoft.Json;

namespace DungeonDepthPrototype;

internal sealed class RadialConfiguration
{
    public int Schema = 1;
    public string Fallback = "disabled";
    public RadialTypeRules[] Types = Array.Empty<RadialTypeRules>();
    public RewardRules Rewards = new RewardRules();
}

// Выбор и таблица наград записываются до поиска. Повтор использует только сохранённые правила.
internal sealed class RadialSnapshot
{
    public int Schema = 1;
    public RadialSelection Selection = new RadialSelection();
    public RewardRules Rewards = new RewardRules();
    internal RewardRules Priced => Rewards.AtPrice(Selection.Profile.ModifierPrice);
    internal void Validate()
    {
        Rewards.Validate(); Selection.Profile.Validate(Selection.Modifiers.Length);
        double measured = Math.Sqrt(Selection.OutsideX * Selection.OutsideX + Selection.OutsideZ * Selection.OutsideZ);
        Rewards.AtPrice(Selection.Profile.ModifierPrice);
        bool legacy = Schema == 1 && Selection.Schema == 1 && Rewards.Schema == 3 &&
            Selection.OrdinaryModifiers.Length == 0;
        bool empowered = Schema == 2 && Selection.Schema == 2 && Rewards.Schema == 4 &&
            Selection.OrdinaryModifiers.Length > 0 &&
            Selection.OrdinaryModifiers.Distinct().Count() == Selection.OrdinaryModifiers.Length &&
            Selection.OrdinaryModifiers.All(Selection.Modifiers.Contains);
        if ((!legacy && !empowered) ||
            string.IsNullOrWhiteSpace(Selection.Type) || string.IsNullOrWhiteSpace(Selection.Revision) ||
            double.IsNaN(Selection.Radius) || double.IsInfinity(Selection.Radius) || Selection.Radius < 0 ||
            double.IsNaN(measured) || double.IsInfinity(measured) || Math.Abs(Selection.Radius - measured) > .001 ||
            Selection.RewardRevision != Rewards.Revision ||
            Selection.MaximumDepth < Selection.Profile.MinimumDepth ||
            Selection.MaximumDepth > Selection.Profile.MaximumDepth ||
            Selection.Modifiers.Distinct().Count() != Selection.Modifiers.Length ||
            Selection.Modifiers.Any(id => !Rewards.Weights.ContainsKey(id)))
            throw new InvalidOperationException("Повреждён снимок радиального профиля");
    }
}

internal static class RadialRuntime
{
    internal const string Key = "uberdungeon.radial.v1";
    internal const string HealthKey = Key + ".health", DamageKey = Key + ".damage";
    internal const string ProfileKey = Key + ".profile", RevisionKey = Key + ".revision";
    internal const string EmpoweredKey = "uberdungeon.empowered.v1";
    internal static RadialConfiguration? Configuration;
    internal static void Load()
    {
        string file = Path.Combine(Paths.ConfigPath, "radial-dungeons.json");
        Configuration = File.Exists(file) ? JsonConvert.DeserializeObject<RadialConfiguration>(
            File.ReadAllText(file))! : null;
        if (Configuration == null) return;
        if ((Configuration.Schema != 1 && Configuration.Schema != 2) ||
            Configuration.Schema == 1 && Configuration.Rewards.Schema != 3 ||
            Configuration.Schema == 2 && Configuration.Rewards.Schema != 4 ||
            Configuration.Types.Select(t => t.Type).Distinct().Count() !=
            Configuration.Types.Length) throw new InvalidOperationException("Неверный список радиальных типов");
        foreach (var type in Configuration.Types)
        {
            type.Validate(Configuration.Rewards, type.Enabled);
            PovBridge.ValidateIds(type.Modifiers);
            if (type.Enabled && type.Type != "ForestCrypt")
                throw new InvalidOperationException("Генератор этого типа ещё не реализован: " + type.Type);
            foreach (var profile in new[] { type.Near, type.Middle, type.Far })
                type.TargetStart(profile, Configuration.Rewards);
        }
    }

    internal static RadialSnapshot? Read(ZDO data)
    {
        string json = data.GetString(Key);
        if (json.Length == 0) return null;
        var saved = JsonConvert.DeserializeObject<RadialSnapshot>(json)!; saved.Validate(); return saved;
    }

    internal static RadialSnapshot Save(ZDO data, RadialSelection selection)
    {
        var existing = Read(data);
        if (existing != null) return existing;
        if (data.GetString(ForestRuntime.LayoutKey).Length > 0)
            throw new InvalidOperationException("Посещённый данж не получает новый профиль");
        var saved = new RadialSnapshot { Schema = selection.Schema, Selection = selection,
            Rewards = Configuration!.Rewards };
        saved.Validate();
        string json = JsonConvert.SerializeObject(saved); data.Set(Key, json);
        return JsonConvert.DeserializeObject<RadialSnapshot>(json)!;
    }

    internal static RadialSnapshot Probe(ZDO data, string profile, int seed)
    {
        if (!Plugin.Instance.Config.Bind("ForestCrypt", "RadialDiagnostics", false).Value ||
            ZNet.instance.GetPeers().Any()) throw new InvalidOperationException("Явный профиль только на стенде");
        var type = Configuration?.Types.Single(t => t.Type == "ForestCrypt") ??
            throw new InvalidOperationException("Нет радиального конфига");
        return Save(data, type.Probe(profile, seed, Configuration!.Rewards));
    }

    internal static void Mark(Character creature, RadialSelection? selection, bool boss, int depth = 0,
        RadialRole? role = null)
    {
        if (selection == null) return;
        var data = MiniBossRuntime.Data(creature)!;
        if (data.GetString(ForestRuntime.Tag).Length == 0 || creature.IsPlayer() ||
            !creature.GetComponent<ZNetView>().IsOwner())
            throw new InvalidOperationException("Множитель требует своей новой охраны");
        var factors = selection.Factors(boss);
        int stacks = selection.Loadout(depth, role ?? (boss ? RadialRole.Boss : RadialRole.Guard)).Empowered;
        data.Set(HealthKey, factors.Health * EmpoweredRules.Health(stacks)); data.Set(DamageKey, factors.Damage);
        if (selection.Schema >= 2) data.Set(EmpoweredKey, stacks);
        data.Set(ProfileKey, selection.Profile.Id); data.Set(RevisionKey, selection.Revision);
        // Как при штатной загрузке: исходная база prefab и уровень, затем сохранённый коэффициент.
        // Mark вызывается только при создании: посещённые/раненые существа сюда не попадают.
        creature.SetLevel(creature.GetLevel());
        // PoV FollowMaxHealth ожидает исходное число после SetMaxHealth в том же кадре.
        creature.SetHealth(creature.GetMaxHealthBase() * creature.GetLevel());
    }

    internal static float? Factor(Character creature, bool hp)
    {
        var data = MiniBossRuntime.Data(creature);
        if (creature.IsPlayer() || data == null || data.GetString(ForestRuntime.Tag).Length == 0 ||
            data.GetString(ProfileKey).Length == 0) return null;
        float factor = data.GetFloat(hp ? HealthKey : DamageKey, 1);
        if (float.IsNaN(factor) || float.IsInfinity(factor) || factor <= 0)
            throw new InvalidOperationException("Повреждён сохранённый множитель");
        return hp || EmpoweredDamage.Suppress(creature) ? factor :
            factor * EmpoweredRules.Damage(data.GetInt(EmpoweredKey));
    }
}
