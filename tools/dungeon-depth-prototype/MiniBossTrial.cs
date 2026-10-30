using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DungeonDepthPrototype;

// Четыре явно создаваемых опыта. Короткая геометрия не меняет обычный профиль глубины.
internal static class MiniBossTrial
{
    internal const string IndexKey = "uberdungeon.miniboss.trial";
    internal const string HubKey = IndexKey + ".hub", RuneKey = IndexKey + ".rune";
    internal const string RuneWallKey = RuneKey + ".wall";
    internal const string RuneVisualKey = RuneKey + ".visual-turn";
    internal const string SpecKey = IndexKey + ".spec", ActorKey = IndexKey + ".actor";
    // Навигация Valheim 1.0.20 охватывает Y = -500..5500. Высота 7000 исключала пол из NavMesh.
    // 4000 также отделяет испытания от штатных интерьеров, размещённых около 5000.
    internal static readonly Vector3 Hub = new Vector3(2048, 4000, 2048);

    private static void CheckNavigationHeight()
    {
        float center = Pathfinding.instance.GetTilePos(Vector3Int.zero).y;
        float height = (float)AccessTools.Field(typeof(Pathfinding), "m_tileHeight").GetRawConstantValue();
        if (Math.Abs(Hub.y - center) + 20 >= height / 2)
            throw new InvalidOperationException("Площадка вне высотного диапазона штатной навигации");
    }
    internal static bool Enabled => Plugin.Instance.Config.Bind("MiniBossTrial", "Enabled", false,
        "Четыре склепа только для изолированного ручного испытания.").Value;
    internal static bool Ready => Enabled && ZNet.instance != null && ZNetScene.instance != null &&
        ZNet.instance.GetWorldName() == "ForestCryptQA" && PovBridge.Assembly != null;
    internal static bool CustomArena => Plugin.Instance.Config.Bind("MiniBossTrial", "CustomArena", false,
        "Собственная арена только в новой отдельной пробе.").Value;
    private static readonly string[] FirstNames = { "Харальд", "Эйрик", "Ульф", "Бьёрн", "Ивар", "Сигурд",
        "Рагнар", "Гуннар", "Хакон", "Рольф", "Торстейн", "Лейф", "Хальвдан", "Хельги", "Орм", "Арнбьёрн",
        "Флоки", "Кетиль", "Свейн", "Торгрим", "Аскель", "Эгиль", "Грим", "Финн" };
    private static readonly string[] Titles = { "Костолом", "Железный Клык", "Чёрный Щит", "Ледяной Клинок",
        "Волчий Клык", "Каменная Длань", "Гроза Морей", "Седой Ворон", "Кровавый Топор", "Мрачный Страж",
        "Медвежий Рёв", "Северный Волк", "Ночной Охотник", "Ледяной Ветер", "Сокрушитель Копий",
        "Пепельный Воин", "Владыка Бурь", "Стальной Шлем", "Алый Парус", "Чёрный Медведь",
        "Серый Клинок", "Длинная Тень", "Безмолвный", "Неукротимый" };

    private static void Server()
    {
        if (!Ready || !ZNet.instance.IsServer()) throw new InvalidOperationException("Нужен отдельный QA-сервер");
    }

    internal static object Execute(JObject command)
    {
        if (!Ready) throw new InvalidOperationException("Испытание мини-боссов выключено");
        switch (command.Value<string>("action"))
        {
            case "miniboss-arena-status": return MiniBossArena.Status();
            case "miniboss-interior-probe": Server(); return MiniBossInteriorProbe.Run(command);
            case "miniboss-arena-probe": Server(); return MiniBossArenaProbe.Run(command);
            case "miniboss-catalog": return Catalog();
            case "miniboss-room-catalog": Server(); return RoomCatalog();
            case "miniboss-create": Server(); return Create(command.Value<int?>("seed") ?? 20261030);
            case "miniboss-status": return Status();
            case "miniboss-navigation": return Navigation();
            case "miniboss-probe": Server(); return Probe();
            case "miniboss-death-probe": Server(); return DeathProbe(command.Value<bool>("verify"));
            case "miniboss-gate-probe": Server(); return GateProbe(command.Value<string>("phase") ?? "create");
            case "miniboss-navigation-probe": Server();
                return MiniBossNavigationProbe.Run(command.Value<string>("phase") ?? "status");
            case "miniboss-rune-preview": return PreviewRune(command.Value<int>("quarter_turns"),
                command.Value<float?>("normal_offset") ?? 0);
            default: throw new ArgumentException("Неизвестная команда мини-босса");
        }
    }

    private static object PreviewRune(int quarterTurns, float normalOffset)
    {
        if (quarterTurns < 0 || quarterTurns > 3 || float.IsNaN(normalOffset) || Math.Abs(normalOffset) > 1 ||
            !ForestManual.Target(Player.m_localPlayer))
            throw new InvalidOperationException("Нужен выбранный тестовый персонаж и поворот 0..3");
        var hover = Player.m_localPlayer.GetHoverObject();
        var view = hover == null ? null : hover.GetComponentInParent<ZNetView>();
        if (view == null || !view.IsValid() || !view.GetZDO().GetBool(RuneWallKey))
            view = Object.FindObjectsByType<ZNetView>(FindObjectsSortMode.None)
                .Where(v => v.IsValid() && v.GetZDO().GetBool(RuneWallKey) &&
                    Vector3.Distance(v.transform.position, Player.m_localPlayer.transform.position) < 3)
                .OrderBy(v => Vector3.Distance(v.transform.position, Player.m_localPlayer.transform.position))
                .FirstOrDefault();
        if (view == null || !view.IsValid() || !view.IsOwner() || !view.GetZDO().GetBool(RuneWallKey))
            throw new InvalidOperationException("Нужен принадлежащий клиенту камень в стене не далее трёх метров");
        view.GetZDO().Set(RuneVisualKey, quarterTurns);
        view.transform.position += view.transform.forward * normalOffset;
        view.GetZDO().SetPosition(view.transform.position);
        return new { quarter_turns = quarterTurns, normal_offset = normalOffset };
    }

    private static object Navigation()
    {
        var player = Player.m_localPlayer ?? throw new InvalidOperationException("Нужен клиент");
        var position = player.transform.position;
        var body = player.GetComponent<Collider>();
        return Physics.CapsuleCastAll(position + Vector3.up * .45f, position + Vector3.up * 1.5f,
            .4f, player.GetLookYaw() * Vector3.forward, 5, Solid, QueryTriggerInteraction.Ignore)
            .OrderBy(h => h.distance).Select(h => new { h.distance, name = h.collider.name,
                parents = h.collider.GetComponentsInParent<Transform>().Select(t => t.name).ToArray(),
                ignored = Physics.GetIgnoreCollision(body, h.collider),
                size = new[] { h.collider.bounds.size.x, h.collider.bounds.size.y, h.collider.bounds.size.z },
                center = new[] { h.collider.bounds.center.x, h.collider.bounds.center.y,
                    h.collider.bounds.center.z } }).ToArray();
    }

    private static object Catalog() => new
    {
        runestones = ZNetScene.instance.m_prefabs.Where(p => p.GetComponentInChildren<RuneStone>(true) != null)
            .Select(p => new { name = p.name, network = p.GetComponent<ZNetView>() != null }).ToArray(),
        creatures = Pool(),
        modifiers = PovBridge.Method("PassiveTree.RarityMods", "All").Invoke(null, null)
    };

    private static object GateProbe(string phase)
    {
        if (ZNet.instance.GetPeers().Any(p => p.IsReady()))
            throw new InvalidOperationException("Проба удаления двери требует отсутствия игроков");
        const string role = "gate-probe";
        var actor = Character.GetAllCharacters().SingleOrDefault(c =>
            MiniBossRuntime.Data(c)?.GetString(ActorKey) == role);
        if (phase == "create")
        {
            if (actor != null) throw new InvalidOperationException("Проба уже существует");
            string token = Guid.NewGuid().ToString("N");
            // Без игроков сервер выгружает зону площадки между командами пробы.
            var origin = ZNet.instance.GetReferencePosition() + Vector3.up * 20;
            actor = Spawn("Skeleton", origin, Quaternion.identity).GetComponent<Character>();
            PovBridge.AssignIds(actor); Tag(actor, token, -1, role);
            var prefab = ZNetScene.instance.GetPrefab("DG_ForestCrypt").GetComponent<DungeonGenerator>()
                .m_doorTypes.First(d => d.m_prefab.GetComponent<Door>() != null).m_prefab;
            var gate = Spawn(prefab.name, origin + Vector3.forward * 3, Quaternion.identity);
            var zdo = MiniBossRuntime.Data(gate.transform)!;
            zdo.Set(ForestGates.GateKey, true); zdo.Set(ForestRuntime.Tag, token);
            return new { created = true };
        }
        if (actor == null) throw new InvalidOperationException("Проба не найдена");
        string group = MiniBossRuntime.Data(actor)!.GetString(ForestRuntime.Tag);
        var ai = actor.GetComponent<BaseAI>();
        if (phase == "destroy")
        {
            if (ai.enabled) throw new InvalidOperationException("Закрытая дверь не удержала ИИ");
            var gate = Object.FindObjectsByType<ZNetView>(FindObjectsSortMode.None).Single(v => v.IsValid() &&
                v.GetZDO().GetBool(ForestGates.GateKey) && v.GetZDO().GetString(ForestRuntime.Tag) == group);
            ZNetScene.instance.Destroy(gate.gameObject);
            return new { closed_disabled_ai = true, gate_removed = true };
        }
        if (phase != "verify") throw new ArgumentException("Неизвестная фаза");
        bool passed = ai.enabled && !ForestFixture.AllZdos().Values.Any(z => z.GetBool(ForestGates.GateKey) &&
            z.GetString(ForestRuntime.Tag) == group);
        ZNetScene.instance.Destroy(actor.gameObject);
        if (!passed) throw new InvalidOperationException("После удаления двери ИИ остался выключен");
        return new { passed, active_without_gate = true };
    }

    private static object RoomCatalog()
    {
        if (ZNet.instance.GetPeers().Any(p => p.IsReady()))
            throw new InvalidOperationException("Обмер комнат требует отсутствия игроков");
        var result = new List<object>();
        foreach (var source in DungeonDB.GetRooms().Where(r => r.m_enabled &&
                     (r.m_theme & Room.Theme.ForestCrypt) != 0))
        {
            source.m_prefab.Load();
            GameObject? obj = null;
            try
            {
                var template = ForestRuntime.Catalog(new[] { source }).Single();
                if (template.Size.X < 8 || template.Size.Z < 8 || template.Entrance) continue;
                var position = Hub + Vector3.up * 500;
                obj = Object.Instantiate(source.RoomInPrefab.gameObject, position, Quaternion.identity);
                ClearNative(obj.GetComponentsInChildren<ZNetView>(true).ToList());
                var placement = new ForestPlan.Placement { Template = template, Position = ForestRuntime.P(position) };
                var slots = Slots(placement, .65f, 3.7f);
                var spaces = slots.Select(p => new { position = new[] { p.x, p.y, p.z }, radius = Clearance(p) })
                    .OrderByDescending(s => s.radius).ToArray();
                result.Add(new { prefab = template.Name, size = new[] {
                    template.Size.X, template.Size.Y, template.Size.Z }, slots = spaces.Take(3).ToArray() });
            }
            finally
            {
                if (obj != null)
                {
                    foreach (var view in obj.GetComponentsInChildren<ZNetView>(true))
                        if (view.IsValid()) ZNetScene.instance.Destroy(view.gameObject);
                    obj.SetActive(false); Object.Destroy(obj);
                }
                source.m_prefab.Release();
            }
        }
        return result;
    }

    private static float Clearance(Vector3 floor)
    {
        float radius = .5f;
        while (radius < 5 && !Physics.CheckBox(floor + Vector3.up * 1.8f,
                   new Vector3(radius + .25f, 1.65f, radius + .25f), Quaternion.identity,
                   Solid, QueryTriggerInteraction.Ignore)) radius += .25f;
        return radius;
    }

    private static object DeathProbe(bool verify)
    {
        if (ZNet.instance.GetPeers().Any(p => p.IsReady()))
            throw new InvalidOperationException("Проба смерти требует отсутствия игроков");
        const string key = IndexKey + ".death-probe";
        var receipt = ForestFixture.AllZdos().Values.First(z => z.GetString(HubKey) == "floor");
        if (!verify)
        {
            if (ForestFixture.AllZdos().Values.Any(z => z.GetString(key).Length > 0))
                throw new InvalidOperationException("Проба уже создана");
            var boss = Spawn("Ghost", Hub + new Vector3(0, 1, 10), Quaternion.identity).GetComponent<Character>();
            boss.GetComponent<BaseAI>().enabled = false;
            MiniBossRuntime.Mark(boss, "Проверка смерти", 531090);
            PovBridge.AssignIds(boss);
            var bossData = MiniBossRuntime.Data(boss)!;
            boss.SetHealth(0);
            AccessTools.Method(typeof(Character), "OnDeath").Invoke(boss, null);
            string saved = bossData.GetString(MiniBossRuntime.RewardKey);
            if (saved.Length == 0) throw new InvalidOperationException("Обработчик смерти не выдал награду");
            receipt.Set(key, saved);
            return new { created = true, native_death = true };
        }
        receipt = ForestFixture.AllZdos().Values.Single(z => z.GetString(key).Length > 0);
        var state = JsonConvert.DeserializeObject<MiniBossRuntime.RewardState>(receipt.GetString(key))!;
        var drops = ForestFixture.AllZdos().Values.Where(z =>
            z.GetString(MiniBossRuntime.DropKey).StartsWith(state.Token)).Select(z =>
            {
                var prefab = ZNetScene.instance.GetPrefab(z.GetPrefab());
                var item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
                item.m_dropPrefab = prefab;
                ItemDrop.LoadFromZDO(item, z);
                return item;
            }).ToArray();
        int coins = drops.Where(d => d.m_dropPrefab.name == "Coins").Sum(d => d.m_stack);
        int magic = drops.Count(d => EpicLoot.API.IsMagicItem(d));
        bool bossGone = !ForestFixture.AllZdos().Values.Any(z => z.GetBool(MiniBossRuntime.BossKey) &&
            z.GetInt(MiniBossRuntime.SeedKey) == 531090);
        bool passed = bossGone && drops.Length == state.Items.Count && magic == 2 && coins == state.Gold;
        if (!passed) throw new InvalidOperationException($"Смерть: gone={bossGone}, drops={drops.Length}, " +
            $"magic={magic}, gold={coins}/{state.Gold}");
        return new { passed, boss_gone = bossGone, magic_items = magic, gold = coins, item_objects = drops.Length };
    }

    private static string[] Pool()
    {
        var result = new HashSet<string>();
        foreach (var room in DungeonDB.GetRooms().Where(r => r.m_enabled &&
                     (r.m_theme & Room.Theme.ForestCrypt) != 0))
        {
            room.m_prefab.Load();
            try
            {
                foreach (var spawner in room.RoomInPrefab.GetComponentsInChildren<CreatureSpawner>(true))
                    if (spawner.m_creaturePrefab != null && spawner.m_creaturePrefab.GetComponent<Character>() != null)
                        result.Add(spawner.m_creaturePrefab.name);
            }
            finally { room.m_prefab.Release(); }
        }
        if (!result.Contains("Skeleton") || !result.Contains("Ghost"))
            throw new InvalidOperationException("Каталог склепа не содержит контрольные виды");
        return result.OrderBy(s => s, StringComparer.Ordinal).ToArray();
    }

    internal static GameObject Spawn(string prefab, Vector3 position, Quaternion rotation, string hub = "")
    {
        var template = ZNetScene.instance.GetPrefab(prefab) ?? throw new InvalidOperationException("Нет " + prefab);
        var obj = Object.Instantiate(template, position, rotation);
        var zdo = MiniBossRuntime.Data(obj.transform) ?? throw new InvalidOperationException("Нет ZDO: " + prefab);
        if (hub.Length > 0) zdo.Set(HubKey, hub);
        var wear = obj.GetComponent<WearNTear>();
        if (wear != null && hub.Length > 0) { wear.m_noSupportWear = false; wear.m_noRoofWear = false; }
        return obj;
    }

    internal static void Portal(Vector3 position, Quaternion rotation, string tag)
    {
        var obj = Spawn("portal_wood", position, rotation, "portal");
        MiniBossRuntime.Data(obj.transform)!.Set(ZDOVars.s_tag, tag);
    }

    internal static string Name(int seed)
    {
        var used = new HashSet<string>(ForestFixture.AllZdos().Values.Select(z => z.GetString(SpecKey))
            .Where(s => s.Length > 0).Select(s => JObject.Parse(s).Value<string>("name") ?? ""));
        var random = new System.Random(seed);
        var available = FirstNames.SelectMany(a => Titles.Select(b => a + " " + b))
            .Where(n => !used.Contains(n)).ToArray();
        if (available.Length == 0) throw new InvalidOperationException("Исчерпан словарь неповторяющихся имён");
        return available[random.Next(available.Length)];
    }

    private static object Create(int seed)
    {
        if (ZNet.instance.GetPeers().Any(p => p.IsReady()))
            throw new InvalidOperationException("Создание площадки требует отсутствия игроков");
        CheckNavigationHeight();
        if (ForestFixture.AllZdos().Values.Any(z => z.GetInt(IndexKey, -1) >= 0 || z.GetString(HubKey).Length > 0))
            throw new InvalidOperationException("Площадка уже существует; автоматический сброс запрещён");
        Pool();
        for (int x = -16; x <= 16; x += 2)
        for (int z = -12; z <= 12; z += 2)
            Spawn("stone_floor_2x2", Hub + new Vector3(x, 0, z), Quaternion.identity, "floor");
        for (int x = -16; x <= 16; x += 2)
        foreach (int z in new[] { -13, 13 })
            Spawn("stone_wall_2x1", Hub + new Vector3(x, .5f, z), Quaternion.identity, "parapet");
        for (int z = -12; z <= 12; z += 2)
        foreach (int x in new[] { -17, 17 })
            Spawn("stone_wall_2x1", Hub + new Vector3(x, .5f, z), Quaternion.Euler(0, 90, 0), "parapet");
        for (int i = 0; i < MiniBossRules.TrialCount; i++)
        {
            // Кратность 64 сохраняет положение входа относительно штатной границы зоны.
            var entrance = Hub + new Vector3(192 + i * 128, 0, 0);
            Vector3 inside;
            if (CustomArena)
                inside = MiniBossArenaTrial.Create(entrance, i, unchecked(seed + i * 104729));
            else
            {
                var obj = Object.Instantiate(ZNetScene.instance.GetPrefab("DG_ForestCrypt"), entrance,
                    Quaternion.identity);
                var generator = obj.GetComponent<DungeonGenerator>();
                var zdo = MiniBossRuntime.Data(generator)!;
                zdo.Set(IndexKey, i); zdo.Set(MiniBossRuntime.SeedKey, unchecked(seed + i * 104729));
                generator.Generate(1001, ZoneSystem.SpawnMode.Full);
                var plan = ForestRuntime.LastPlan ?? throw new InvalidOperationException("Нет плана склепа");
                var first = plan.Rooms[0];
                var slots = Slots(first, 1.1f, 3.5f);
                if (slots.Count == 0) throw new InvalidOperationException("Нет места для обратного портала");
                inside = slots.OrderBy(p => Vector3.Distance(p, entrance)).First();
            }
            string tag = "Испытание " + (i + 1);
            Portal(inside, Quaternion.identity, tag);
            var hubPortal = Hub + new Vector3(-7 + (i % 2) * 14, .5f, i < 2 ? 6 : -6);
            Portal(hubPortal, Quaternion.Euler(0, i < 2 ? 180 : 0, 0), tag);
            var sign = Spawn("sign", hubPortal + new Vector3(2, 1.2f, 0),
                Quaternion.Euler(0, i < 2 ? 180 : 0, 0), "sign");
            string species = i % 2 == 0 ? "Скелет" : "Призрак";
            MiniBossRuntime.Data(sign.transform)!.Set(ZDOVars.s_text,
                $"{i + 1}. {species}\nПомощников: {MiniBossRules.TrialHelpers(i)}\n10 свойств · ×1,8");
        }
        return Status();
    }

    internal static void ClearNative(List<ZNetView> objects)
    {
        foreach (var view in objects.ToArray())
        {
            if (view.GetComponent<CreatureSpawner>() == null && view.GetComponent<SpawnArea>() == null &&
                view.GetComponent<Character>() == null && view.GetComponent<Container>() == null &&
                view.GetComponent<Pickable>() == null) continue;
            view.gameObject.SetActive(false); ZNetScene.instance.Destroy(view.gameObject); objects.Remove(view);
        }
    }

    private static readonly int Solid = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece",
        "terrain", "blocker");

    private static List<Vector3> Slots(ForestPlan.Placement room, float radius, float height)
    {
        Physics.SyncTransforms();
        var slots = new List<Vector3>();
        var rotation = Quaternion.Euler(0, room.Turn * 90, 0);
        float floorLevel = Enumerable.Range(0, room.Template.Sockets.Length)
            .Where(i => !room.Template.Sockets[i].Entrance).Min(i => room.SocketPosition(i).Y);
        for (float x = -room.Template.Size.X / 2 + 1.5f; x < room.Template.Size.X / 2 - 1.5f; x += 1)
        for (float z = -room.Template.Size.Z / 2 + 1.5f; z < room.Template.Size.Z / 2 - 1.5f; z += 1)
        {
            var origin = ForestRuntime.V(room.Position) + rotation * new Vector3(x, 1, z);
            // Высокий вход имеет верхнюю площадку и нижний пол. Ищем от уровня внутренних стыков.
            origin.y = floorLevel + 1;
            if (!Physics.Raycast(origin, Vector3.down, out var hit, 12, Solid) || hit.normal.y < .9f) continue;
            var floor = hit.point + Vector3.up * .08f;
            if (Math.Abs(floor.y - room.Position.Y) > room.Template.Size.Y / 2 + .1f) continue;
            if (Physics.CheckCapsule(floor + Vector3.up * (radius + .05f),
                floor + Vector3.up * (height - radius), radius, Solid, QueryTriggerInteraction.Ignore)) continue;
            if (Enumerable.Range(0, room.Template.Sockets.Length)
                .Any(s => Vector3.Distance(floor, ForestRuntime.V(room.SocketPosition(s))) < 2.5f)) continue;
            slots.Add(floor);
        }
        return slots;
    }

    internal static void Populate(DungeonGenerator generator, ForestPlan.Layout layout, string token)
    {
        var root = MiniBossRuntime.Data(generator)!;
        int index = root.GetInt(IndexKey), seed = root.GetInt(MiniBossRuntime.SeedKey);
        var room = layout.Rooms.Single(r => r.Arena);
        int roomIndex = layout.Rooms.IndexOf(room);
        var parent = layout.Rooms[room.Parent];
        Vector3 doorPosition = ForestRuntime.V(parent.SocketPosition(room.ParentSocket));
        var rotation = Quaternion.Euler(0, parent.SocketTurn(room.ParentSocket) * 90, 0);
        var doorPrefab = generator.m_doorTypes.Where(d => d.m_connectionType ==
            parent.Template.Sockets[room.ParentSocket].Type).Select(d => d.m_prefab)
            .First(p => p != null && p.GetComponent<Door>() != null);
        var door = Spawn(doorPrefab.name, doorPosition, rotation);
        var gate = MiniBossRuntime.Data(door.transform)!;
        gate.Set(ForestGates.GateKey, true); gate.Set(ForestGates.ReleasedKey, false);
        gate.Set(ForestRuntime.Tag, token); gate.Set(ForestRuntime.KindKey, "gate");
        gate.Set(ForestRuntime.RoomKey, roomIndex); gate.Set(ForestRuntime.DepthKey, 64);
        gate.Set(ForestGates.FromKey, room.Parent); gate.Set(ZDOVars.s_state, 0);
        var slots = Slots(room, .65f, 3.7f).OrderByDescending(Clearance)
            .ThenByDescending(p => Vector3.Distance(p, doorPosition)).ToList();
        if (slots.Count < 1) throw new InvalidOperationException("Нет свободного места для модели ×1,8");
        var bossPosition = slots[0];
        float clearWidth = Clearance(bossPosition) * 2;
        if (clearWidth < 5) throw new InvalidOperationException("Нет достаточно просторного места для босса");
        string name = Name(seed);
        string prefab = index % 2 == 0 ? "Skeleton" : "Ghost";
        var boss = Spawn(prefab, bossPosition, Quaternion.identity).GetComponent<Character>();
        MiniBossRuntime.Mark(boss, name, seed);
        var previous = UnityEngine.Random.state;
        string[] modifiers;
        try
        {
            UnityEngine.Random.InitState(seed);
            PovBridge.Assign(boss, 10);
            modifiers = PovBridge.Ids(PovBridge.Read(boss));
        }
        finally { UnityEngine.Random.state = previous; }
        Tag(boss, token, roomIndex, "boss");
        var pool = Pool(); var random = new System.Random(unchecked(seed ^ 0x5f3759df));
        var assistants = new List<string>(); var occupied = new List<Vector3> { bossPosition };
        var helperSlots = Slots(room, .45f, 2.2f).OrderBy(p => Vector3.Distance(p, bossPosition)).ToArray();
        for (int i = 0; i < MiniBossRules.TrialHelpers(index); i++)
        {
            var choices = helperSlots.Where(p => occupied.All(v => Vector3.Distance(p, v) > 2.2f)).ToArray();
            if (choices.Length == 0) throw new InvalidOperationException("Нет места для помощника");
            var position = choices[0]; occupied.Add(position);
            string kind = pool[random.Next(pool.Length)]; assistants.Add(kind);
            var helper = Spawn(kind, position, Quaternion.identity).GetComponent<Character>();
            helper.SetLevel(1); PovBridge.AssignIds(helper, modifiers); Tag(helper, token, roomIndex, "helper");
        }
        var inward = rotation * Vector3.forward;
        if (Vector3.Dot(inward, ForestRuntime.V(parent.Position) - doorPosition) < 0) inward = -inward;
        var side = Vector3.Cross(Vector3.up, inward);
        Vector3? markerPosition = null;
        Vector3 markerNormal = Vector3.zero;
        var runeProbes = new List<object>();
        foreach (float distance in new[] { 1.2f, 1.8f, 2.4f })
        {
            var point = doorPosition + inward * distance;
            if (!Physics.Raycast(point + Vector3.up, Vector3.down, out var floor, 2, Solid,
                    QueryTriggerInteraction.Ignore) || Math.Abs(floor.point.y - doorPosition.y) > .25f) continue;
            foreach (float direction in new[] { 1f, -1f })
            {
                var ray = new Ray(floor.point + Vector3.up * .8f, side * direction);
                bool found = Physics.Raycast(ray, out var wall, 3, Solid, QueryTriggerInteraction.Ignore);
                runeProbes.Add(new { distance, direction, found, wall_distance = wall.distance,
                    normal_dot = found ? Vector3.Dot(wall.normal, -ray.direction) : 0 });
                if (!found || Vector3.Dot(wall.normal, -ray.direction) < .7f) continue;
                // Каменные стены неровные: плоскость рисунка ставим перед наиболее выступающей
                // точкой стены во всём небольшом прямоугольнике чтения, сохраняя ориентацию двери.
                float nearestWall = wall.distance;
                bool continuousWall = true;
                foreach (float along in new[] { -.18f, 0, .18f })
                foreach (float height in new[] { .15f, .75f, 1.35f })
                {
                    var sample = new Ray(floor.point + inward * along + Vector3.up * height, ray.direction);
                    if (!Physics.Raycast(sample, out var surface, 3, Solid, QueryTriggerInteraction.Ignore) ||
                        Vector3.Dot(surface.normal, -ray.direction) < .7f) { continuousWall = false; break; }
                    nearestWall = Math.Min(nearestWall, surface.distance);
                }
                if (!continuousWall) continue;
                // Лицевая плоскость вдоль боковой стены, перпендикулярно плоскости двери.
                // Основной объём уходит в стену; перед ней остаётся поверхность с рисунком.
                markerNormal = -ray.direction;
                markerPosition = floor.point + ray.direction * nearestWall - markerNormal * .1f;
                break;
            }
            if (markerPosition != null) break;
        }
        if (markerPosition == null) throw new InvalidOperationException("Нет боковой стены для рунного камня: " +
            JsonConvert.SerializeObject(runeProbes));
        var rune = Spawn("BossStone_TheElder", markerPosition.Value, Quaternion.LookRotation(markerNormal));
        var runeData = MiniBossRuntime.Data(rune.transform)!;
        runeData.Set(RuneKey, "Здесь покоится владыка " + name);
        runeData.Set(RuneWallKey, true);
        // Рисунок BossStone_TheElder обращён назад относительно локальной оси Z модели.
        runeData.Set(RuneVisualKey, 2);
        runeData.Set(ForestRuntime.Tag, token);
        MiniBossRuntime.ConfigureRune(rune.GetComponent<ZNetView>());
        var runeCheck = rune.GetComponent<MiniBossRuneShape>().CheckWallMount();
        root.Set(SpecKey, JsonConvert.SerializeObject(new { index, seed, token, name, prefab,
            modifiers, assistants, pool, combat_depth = 64, physical_depth = 5,
            boss_position = new[] { bossPosition.x, bossPosition.y, bossPosition.z },
            door_position = new[] { doorPosition.x, doorPosition.y, doorPosition.z },
            room = room.Template.Name, clear_width = clearWidth, scale = 1.8,
            health = MiniBossRules.HealthFactor, damage = MiniBossRules.DamageFactor,
            helper_chance_percent = MiniBossRules.HelperChancePercent, forced_test_variant = true,
            rune_check = runeCheck }));
    }

    internal static void Tag(Character creature, string token, int room, string role)
    {
        var zdo = MiniBossRuntime.Data(creature)!;
        zdo.Set(ActorKey, role); zdo.Set(ForestRuntime.Tag, token);
        zdo.Set(ForestRuntime.KindKey, "guard"); zdo.Set(ForestRuntime.RoomKey, room);
        zdo.Set(ForestRuntime.DepthKey, 64); zdo.Set(CombatRecorder.EntityKey, Guid.NewGuid().ToString("N"));
    }

    internal static object Status() => new
    {
        trials = ForestFixture.AllZdos().Values.Where(z => z.GetString(SpecKey).Length > 0)
            .Select(z => JObject.Parse(z.GetString(SpecKey))).OrderBy(v => v.Value<int>("index")).ToArray(),
        actors = Character.GetAllCharacters().Where(c => MiniBossRuntime.Data(c)?.GetString(ActorKey).Length > 0)
            .Select(c => new { role = MiniBossRuntime.Data(c)!.GetString(ActorKey),
                token = MiniBossRuntime.Data(c)!.GetString(ForestRuntime.Tag),
                prefab = Utils.GetPrefabName(c.gameObject),
                position = new[] { c.transform.position.x, c.transform.position.y, c.transform.position.z },
                health = c.GetHealth(), maximum = c.GetMaxHealth(), scale = c.transform.localScale.x,
                ai_enabled = c.GetComponent<BaseAI>()?.enabled,
                collider = c.GetComponent<Collider>() == null ? Array.Empty<float>() : new[] {
                    c.GetComponent<Collider>().bounds.size.x, c.GetComponent<Collider>().bounds.size.y,
                    c.GetComponent<Collider>().bounds.size.z },
                mods = PovBridge.Ids(PovBridge.Read(c)),
                health_factor = MiniBossRuntime.Data(c)!.GetFloat("PT_EnemyHealthFactor"),
                ward = MiniBossRuntime.Data(c)!.GetFloat("pt.ward.max") }).ToArray(),
        drops = Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None)
            .Where(d => MiniBossRuntime.Data(d)?.GetString(MiniBossRuntime.DropKey).Length > 0)
            .Select(d => new { id = MiniBossRuntime.Data(d)!.GetString(MiniBossRuntime.DropKey),
                prefab = d.m_itemData.m_dropPrefab.name, count = d.m_itemData.m_stack,
                magic = EpicLoot.API.GetMagicItemJson(d.m_itemData) }).ToArray(),
        portals = ForestFixture.AllZdos().Values.Where(z => z.GetString(HubKey) == "portal")
            .Select(z => new { tag = z.GetString(ZDOVars.s_tag), position = new[] {
                z.GetPosition().x, z.GetPosition().y, z.GetPosition().z } }).ToArray(),
        runestones = ForestFixture.AllZdos().Values.Where(z => z.GetString(RuneKey).Length > 0)
            .Select(z => new { text = z.GetString(RuneKey), wall_mounted = z.GetBool(RuneWallKey),
                geometry = RuneGeometry(z),
                visual_quarter_turns = z.GetInt(RuneVisualKey),
                rotation = new[] { z.GetRotation().x, z.GetRotation().y, z.GetRotation().z, z.GetRotation().w },
                position = new[] {
                z.GetPosition().x, z.GetPosition().y, z.GetPosition().z } }).ToArray()
    };

    private static object? RuneGeometry(ZDO data)
    {
        var obj = ZNetScene.instance.FindInstance(data.m_uid);
        if (obj == null) return null;
        return new { rotation = ForestFixture.V(obj.transform.eulerAngles),
            scale = ForestFixture.V(obj.transform.localScale),
            meshes = obj.GetComponentsInChildren<Renderer>(true).Select(r => new {
                r.name, r.enabled, active = r.gameObject.activeInHierarchy,
                center = ForestFixture.V(r.bounds.center), size = ForestFixture.V(r.bounds.size) }).ToArray() };
    }

    private static object Probe()
    {
        if (ZNet.instance.GetPeers().Any(p => p.IsReady()))
            throw new InvalidOperationException("Проба множителей требует отсутствия игроков");
        var rows = new List<object>();
        var sets = new[] { Array.Empty<string>(), new[] { "warded" }, EncounterPlan.ForestModifiers }.Concat(
            ForestFixture.AllZdos().Values.Where(z => z.GetString(SpecKey).Length > 0)
                .Select(z => JObject.Parse(z.GetString(SpecKey))["modifiers"]!.ToObject<string[]>()!)).ToArray();
        foreach (string prefab in new[] { "Skeleton", "Ghost" })
        foreach (var modifiers in sets)
        {
            var normal = Spawn(prefab, Hub + Vector3.up * 30, Quaternion.identity).GetComponent<Character>();
            var boss = Spawn(prefab, Hub + Vector3.up * 35, Quaternion.identity).GetComponent<Character>();
            try
            {
                normal.GetComponent<BaseAI>().enabled = false; boss.GetComponent<BaseAI>().enabled = false;
                normal.SetLevel(1); PovBridge.AssignIds(normal, modifiers);
                MiniBossRuntime.Mark(boss, "Проверка", 76001); PovBridge.AssignIds(boss, modifiers);
                float normalMax = normal.GetMaxHealth(), bossMax = boss.GetMaxHealth();
                float normalWard = MiniBossRuntime.Data(normal)!.GetFloat("pt.ward.max");
                float bossWard = MiniBossRuntime.Data(boss)!.GetFloat("pt.ward.max");
                float Factor(Character c) => Convert.ToSingle(PovBridge.Method("PassiveTree.EnemyDifficulty",
                    "DamageFactor").Invoke(null, new object[] { c }));
                float normalDamage = Factor(normal), bossDamage = Factor(boss);
                float Hit(Character attacker)
                {
                    var target = Spawn("Troll", Hub + new Vector3(0, 40, 10), Quaternion.identity)
                        .GetComponent<Character>();
                    try
                    {
                        target.GetComponent<BaseAI>().enabled = false;
                        PovBridge.AssignIds(target);
                        float before = target.GetHealth();
                        var hit = new HitData { m_point = target.transform.position,
                            m_blockable = false, m_dodgeable = false };
                        hit.m_damage.m_blunt = 10; hit.SetAttacker(attacker);
                        AccessTools.Method(typeof(Character), "RPC_Damage").Invoke(target,
                            new object[] { 0L, hit });
                        return before - target.GetHealth();
                    }
                    finally { ZNetScene.instance.Destroy(target.gameObject); }
                }
                float normalLoss = Hit(normal), bossLoss = Hit(boss);
                PovBridge.Method("PassiveTree.EnemyDifficulty", "Ensure").Invoke(null, new object[] { boss });
                bool passed = Math.Abs(bossMax / normalMax - MiniBossRules.HealthFactor) < .001 &&
                    Math.Abs(bossDamage / normalDamage - MiniBossRules.DamageFactor) < .001 &&
                    Math.Abs(bossWard - MiniBossRules.HealthFactor * normalWard) < .01 &&
                    Math.Abs(boss.GetMaxHealth() - bossMax) < .01 && normalLoss > 0 &&
                    Math.Abs(bossLoss / normalLoss - MiniBossRules.DamageFactor) < .01;
                rows.Add(new { prefab, modifiers, normalMax, bossMax, normalWard, bossWard,
                    normalDamage, bossDamage, normalLoss, bossLoss, passed });
                if (!passed) throw new InvalidOperationException("Множители не прошли контроль " + prefab);
                if (modifiers.Length == 0)
                {
                    var reward = MiniBossRuntime.DropReward(boss);
                    int count = Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None).Count(d =>
                        MiniBossRuntime.Data(d)?.GetString(MiniBossRuntime.DropKey).StartsWith(reward.Token) == true);
                    MiniBossRuntime.DropReward(boss);
                    var drops = Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None).Where(d =>
                        MiniBossRuntime.Data(d)?.GetString(MiniBossRuntime.DropKey).StartsWith(reward.Token) == true)
                        .ToArray();
                    int coins = drops.Where(d => d.m_itemData.m_dropPrefab.name == "Coins")
                        .Sum(d => d.m_itemData.m_stack);
                    bool good = count == drops.Length && coins == reward.Gold && coins >= 300 && coins <= 900 &&
                        drops.Count(d => EpicLoot.API.IsMagicItem(d.m_itemData)) == 2;
                    rows.Add(new { prefab, reward.Gold, item_objects = count, repeated = drops.Length,
                        reward_passed = good });
                    foreach (var drop in drops) ZNetScene.instance.Destroy(drop.gameObject);
                    if (!good) throw new InvalidOperationException("Награда не прошла контроль");
                }
            }
            finally { ZNetScene.instance.Destroy(normal.gameObject); ZNetScene.instance.Destroy(boss.gameObject); }
        }
        return rows;
    }
}
