using System;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace DungeonDepthPrototype;

internal sealed class MiniBossArenaController : MonoBehaviour
{
    private Character boss = null!;
    private MonsterAI ai = null!;
    private ZNetView view = null!;
    private ArenaFrame frame;
    private Vector3 safe;
    private float observe;
    internal MiniBossArenaState State = new MiniBossArenaState();
    private bool owned;
    private float pushAt;
    private void Awake()
    {
        boss = GetComponent<Character>(); ai = GetComponent<MonsterAI>(); view = GetComponent<ZNetView>();
        frame = MiniBossArena.Frame(boss); safe = MiniBossArena.Inside(frame, transform.position) ?
            transform.position : MiniBossArena.Center(frame);
    }
    internal static void Install(Harmony harmony)
    {
        MiniBossRock.Install(harmony);
        harmony.Patch(AccessTools.Method(typeof(Game), "FixedUpdate"),
            postfix: new HarmonyMethod(typeof(MiniBossArenaProbe), nameof(MiniBossArenaProbe.ReferencePosition)));
        void Patch(Type type, string method, string callback, bool postfix = false)
        {
            var hook = new HarmonyMethod(typeof(MiniBossArenaController), callback) { priority = Priority.First };
            harmony.Patch(AccessTools.Method(type, method), prefix: postfix ? null : hook,
                postfix: postfix ? hook : null);
        }
        Patch(typeof(MonsterAI), "UpdateAI", nameof(UpdateAI));
        Patch(typeof(Character), "RPC_Damage", nameof(Damaged), true);
        Patch(typeof(Character), "RPC_Stagger", nameof(Stagger));
        harmony.Patch(AccessTools.Method(typeof(Character), "ApplyPushback", new[] { typeof(Vector3), typeof(float) }),
            prefix: new HarmonyMethod(typeof(MiniBossArenaController), nameof(Stagger)));
        Patch(typeof(Humanoid), "OnAttackTrigger", nameof(OrdinaryTrigger));
        Patch(typeof(Player), "TryPlacePiece", nameof(Build));
        Patch(typeof(Pickable), "Awake", nameof(PickableLoaded), true);
        Patch(typeof(ZDO), "Deserialize", nameof(Received), true);
        Patch(typeof(ZSyncAnimation), "RPC_SetTrigger", nameof(Animation));
        var blink = PovBridge.Assembly.GetType("PassiveTree.RarityMods2+Habits");
        // Тип проверяется нативным аудитом; при иной DLL мост PoV уже остановит инициализацию.
        if (blink == null) throw new InvalidOperationException("Не найден контроллер движения PoV");
        foreach (string method in new[] { "Blink", "Step", "Rush" })
            harmony.Patch(AccessTools.Method(blink, method),
                prefix: new HarmonyMethod(typeof(MiniBossArenaController), nameof(Blink)));
    }
    private static bool Blink(object __instance)
    {
        var field = AccessTools.Field(__instance.GetType(), "owner");
        var c = field?.GetValue(__instance) as Character;
        var control = c == null ? null : c.GetComponent<MiniBossArenaController>();
        return control == null || !control.Locked;
    }
    private static void Received(ZDO __instance)
    {
        if (ZNet.instance != null && ZNet.instance.IsServer() &&
            (__instance.GetBool(MiniBossArena.Key) || __instance.GetBool(MiniBossArena.ValueKey)))
            ZDOMan.instance.SetDirtySector(__instance);
    }
    private static void PickableLoaded(Pickable __instance)
    {
        if (MiniBossRuntime.Data(__instance)?.GetBool(MiniBossArena.ValueKey) == true)
            __instance.m_respawnTimeMinutes = 0;
    }
    private bool Locked => view.IsValid() && !boss.IsDead() &&
        (ReadState().Casting || ReadState().Returning);
    private MiniBossArenaState ReadState() => view.IsOwner() && owned ? State :
        JsonConvert.DeserializeObject<MiniBossArenaState>(view.GetZDO().GetString(MiniBossArena.StateKey, "{}"))!;
    private static bool Animation(ZSyncAnimation __instance, string name)
    {
        var c = __instance.GetComponent<MiniBossArenaController>();
        return c == null || !c.ReadState().Casting || c.boss.IsDead() ||
            name == c.ReadState().Animation;
    }
    private static bool Stagger(Character __instance) =>
        __instance.GetComponent<MiniBossArenaController>()?.ReadState().Casting != true;
    private static bool OrdinaryTrigger(Humanoid __instance) =>
        __instance.GetComponent<MiniBossArenaController>()?.ReadState().Casting != true;
    private static bool UpdateAI(MonsterAI __instance, float dt, ref bool __result)
    {
        var c = __instance.GetComponent<MiniBossArenaController>();
        if (c == null) return true;
        bool ordinary = c.Tick(dt); __result = true; return ordinary;
    }
    private static void Damaged(Character __instance, HitData hit)
    {
        var c = __instance.GetComponent<MiniBossArenaController>();
        if (c == null || !c.view.IsOwner() || __instance.IsDead() || !(hit.GetAttacker() is Player player)) return;
        c.LoadOwnership(); c.Join(player); c.Save();
    }
    private void Join(Player p)
    {
        State.Engaged = true;
        if (!State.Participants.Contains(p.GetPlayerID()))
            State.Participants = State.Participants.Concat(new[] { p.GetPlayerID() }).ToArray();
    }
    private void LoadOwnership()
    {
        if (owned) return;
        State = JsonConvert.DeserializeObject<MiniBossArenaState>(view.GetZDO()
            .GetString(MiniBossArena.StateKey, "{}"))!;
        frame = MiniBossArena.Frame(boss); owned = true;
    }
    private void Save() => view.GetZDO().Set(MiniBossArena.StateKey, JsonConvert.SerializeObject(State));
    internal bool Tick(float dt)
    {
        if (!view.IsValid() || !view.IsOwner()) { owned = false; return false; }
        LoadOwnership();
        if (boss.IsDead() || boss.GetHealth() <= 0) { State.Observe(false, false); Save(); return false; }
        State.Cooldown = Math.Max(0, State.Cooldown - dt);
        var players = Player.GetAllPlayers().Where(p => !p.IsDead()).ToArray();
        observe -= dt;
        if (observe <= 0)
        {
            observe = .2f;
            foreach (var p in players.Where(p => !p.InGhostMode() && MiniBossArena.Near(frame, p.transform.position)))
                if (BaseAI.IsEnemy(boss, p) && (ai.CanSeeTarget(p) || ai.CanHearTarget(p))) Join(p);
            if (AccessTools.Field(typeof(MonsterAI), "m_targetCreature").GetValue(ai) is Player target)
                Join(target);
        }
        bool participantNear = players.Any(p => State.Participants.Contains(p.GetPlayerID()) &&
            MiniBossArena.Near(frame, p.transform.position));
        State.Observe(true, participantNear);
        if (State.Casting)
        {
            ai.StopMoving();
            State.Windup -= dt * Math.Max(.05f, boss.GetComponentInChildren<Animator>().speed);
            if (State.Windup <= 0)
            {
                // Квитанция записывается до создания снаряда. Новый владелец не выпускает второй.
                if (State.ReleasedSequence < State.CastSequence)
                {
                    State.ReleasedSequence = State.CastSequence; Save();
                    MiniBossRock.Launch(boss, new Vector3(State.Aim[0], State.Aim[1], State.Aim[2]),
                        State.CastSequence);
                }
                State.Casting = false; State.Cooldown = MiniBossAttackProfile.Weapon(boss).m_shared.m_aiAttackInterval;
            }
            Save(); return false;
        }
        if (State.Returning)
        {
            State.Mode = "return";
            var center = MiniBossArena.Center(frame);
            if (Vector3.Distance(transform.position, center) <= 1)
            {
                if (State.Arrive(true))
                {
                    boss.SetHealth(boss.GetMaxHealth()); PovBridge.RestoreWard(boss);
                    AccessTools.Field(typeof(MonsterAI), "m_targetCreature").SetValue(ai, null);
                    AccessTools.Method(typeof(BaseAI), "SetAlerted").Invoke(ai, new object[] { false });
                    ai.StopMoving(); Face(frame.Direction(new Vector3(9, 0, 10))); Save();
                }
                return false;
            }
            var next = Waypoint(true);
            var blocker = Blocker(next);
            State.Blocker = blocker == null ? "" : Utils.GetPrefabName(blocker.gameObject);
            if (blocker != null)
            {
                ai.StopMoving();
                if (State.MayCast(false, true)) Begin(blocker.bounds.center, Vector3.zero, "barrier");
            }
            else Move(dt, next);
            Save(); return false;
        }
        State.Blocker = "";
        if (!State.Engaged)
        {
            State.Mode = "idle"; ai.StopMoving(); Face(frame.Direction(new Vector3(9, 0, 10)));
            Save(); return false;
        }
        if (players.Any(p => MiniBossArena.Inside(frame, p.transform.position)))
        {
            State.Mode = "melee";
            var inside = players.Where(p => !p.InGhostMode() && MiniBossArena.Inside(frame, p.transform.position) &&
                State.Participants.Contains(p.GetPlayerID())).OrderBy(p =>
                Vector3.Distance(p.transform.position, transform.position)).FirstOrDefault();
            if (inside != null) AccessTools.Field(typeof(MonsterAI), "m_targetCreature").SetValue(ai, inside);
            Save(); return true;
        }
        State.Mode = "seek-shot";
        var enemy = players.Where(p => State.Participants.Contains(p.GetPlayerID()) &&
                MiniBossArena.Near(frame, p.transform.position))
            .OrderBy(p => Vector3.Distance(p.transform.position, transform.position)).FirstOrDefault();
        if (enemy == null) { Save(); return false; }
        State.Target = enemy.GetPlayerID().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var shoot = frame.Point(new Vector3(0, .08f, 2));
        if (Vector3.Distance(transform.position, shoot) > .8f) Move(dt, Waypoint(false));
        else
        {
            ai.StopMoving();
            if (State.MayCast(false, false)) Begin(enemy.GetCenterPoint(), enemy.GetVelocity(), State.Target);
        }
        Save(); return false;
    }
    private Vector3 Waypoint(bool returning)
    {
        var route = MiniBossArenaPlan.Route.Select(p => frame.Point(new Vector3(p.X, .08f, p.Z))).ToArray();
        if (!returning) Array.Reverse(route);
        // Ближайшая достижимая точка по маршруту; стены запрещают срезать изгиб.
        for (int i = route.Length - 1; i >= 0; i--)
            if (!MiniBossArena.Shielded(transform.position + Vector3.up, route[i] + Vector3.up)) return route[i];
        return route.OrderBy(p => Vector3.Distance(p, transform.position)).First();
    }
    private void Move(float dt, Vector3 target) => AccessTools.Method(typeof(BaseAI), "MoveTo")
        .Invoke(ai, new object[] { dt, target, .5f, true });
    private Collider? Blocker(Vector3 target)
    {
        float radius = Math.Max(.35f, boss.GetRadius());
        float height = boss.GetComponent<Collider>().bounds.size.y;
        var direction = target - transform.position; direction.y = 0;
        // Полная высота корпуса: низкие стопки и ножки готовочных стоек также могут перекрывать путь.
        return Physics.CapsuleCastAll(transform.position + Vector3.up * (radius + .02f),
                transform.position + Vector3.up * Math.Max(radius + .02f, height - radius), radius,
                direction.normalized, Math.Min(3, direction.magnitude), MiniBossArena.Solid,
                QueryTriggerInteraction.Ignore).OrderBy(h => h.distance).Select(h => h.collider)
            .FirstOrDefault(c => c.GetComponentInParent<Piece>() is Piece piece && piece.GetCreator() != 0 &&
                c.GetComponentInParent<MiniBossArena>() == null && !MiniBossArenaFixtures.Portal(piece));
    }
    private void Begin(Vector3 target, Vector3 velocity, string identity)
    {
        var weapon = MiniBossAttackProfile.Weapon(boss);
        var start = transform.position + Vector3.up * 2;
        float speed = MiniBossAttackProfile.Speed;
        float windup = MiniBossAttackProfile.Windup(boss, weapon);
        float preparation = windup / Math.Max(.05f, boss.GetComponentInChildren<Animator>().speed);
        float travel = Math.Min(MiniBossArenaPlan.FlightDistance / speed, Vector3.Distance(start, target) / speed);
        var aim = target + velocity * (preparation + travel);
        aim.y += .5f * MiniBossAttackProfile.Gravity * travel * travel;
        State.Casting = true; State.CastSequence++; State.Mode = "windup"; State.Target = identity;
        State.Animation = MiniBossAttackProfile.Animation(weapon);
        State.Windup = windup;
        State.Aim = new[] { aim.x, aim.y, aim.z };
        ai.StopMoving(); Face(aim - start); Save();
        // Короткая штатная анимация того же оружия. Урон её события подавлен только на время броска.
        var attack = AccessTools.Field(typeof(Humanoid), "m_currentAttack").GetValue(boss) as Attack;
        attack?.Stop(); AccessTools.Field(typeof(Humanoid), "m_currentAttack").SetValue(boss, null);
        boss.GetComponent<ZSyncAnimation>().SetTrigger(State.Animation);
    }
    private void Face(Vector3 direction)
    {
        direction.y = 0;
        if (direction.sqrMagnitude > .01f)
        { transform.rotation = Quaternion.LookRotation(direction); boss.SetLookDir(direction.normalized); }
    }
    private void LateUpdate()
    {
        if (!view.IsValid() || !view.IsOwner() || boss.IsDead()) return;
        var p = transform.position;
        if (!MiniBossArena.Inside(frame, p) || frame.Local(p).z < boss.GetRadius())
        {
            p = safe; transform.position = p;
            var body = GetComponent<Rigidbody>(); body.position = p; body.linearVelocity = Vector3.zero;
        }
        else safe = p;
    }
    private void FixedUpdate()
    {
        // Каждый клиент прикладывает мягкую физическую силу только своему персонажу. Телепортации нет.
        var player = Player.m_localPlayer;
        if (player == null || !view.IsValid() || boss.IsDead() || !ReadState().Returning ||
            Time.time < pushAt || !MiniBossArena.Inside(frame, player.transform.position) ||
            Vector3.Distance(player.transform.position, transform.position) > boss.GetRadius() + 1.1f) return;
        pushAt = Time.time + .1f;
        var forward = Waypoint(true) - transform.position; forward.y = 0;
        var side = Vector3.Cross(Vector3.up, forward.normalized);
        if (Vector3.Dot(player.transform.position - transform.position, side) < 0) side = -side;
        int mask = LayerMask.GetMask("Default", "static_solid", "piece", "terrain", "blocker");
        foreach (var direction in new[] { side, -side })
        {
            var p = player.transform.position;
            if (!MiniBossArena.Inside(frame, p + direction) || Physics.CapsuleCast(p + Vector3.up * .4f,
                    p + Vector3.up * 1.5f, .35f, direction, .8f, mask, QueryTriggerInteraction.Ignore)) continue;
            player.ApplyPushback(direction, player.GetComponent<Rigidbody>().mass * 1.6f); break;
        }
    }
    internal bool BuildBlocked(Vector3 position, float radius = 0) => !boss.IsDead() && ReadState().Returning &&
        MiniBossArena.Inside(frame, position) && DistanceToRoute(position) < 2.5f + radius;
    private float DistanceToRoute(Vector3 position)
    {
        float distance = float.MaxValue;
        var points = MiniBossArenaPlan.Route.Select(p => frame.Point(new Vector3(p.X, 0, p.Z))).ToArray();
        position.y = frame.Origin.y;
        for (int i = 1; i < points.Length; i++)
        {
            var delta = points[i] - points[i - 1];
            var closest = points[i - 1] + delta * Mathf.Clamp01(Vector3.Dot(position - points[i - 1], delta) /
                delta.sqrMagnitude);
            distance = Math.Min(distance, Vector3.Distance(position, closest));
        }
        return distance;
    }
    private static bool Build(Player __instance, ref bool __result)
    {
        var controls = Character.GetAllCharacters().Select(c => c.GetComponent<MiniBossArenaController>())
            .Where(c => c != null && c.ReadState().Returning).ToArray();
        if (controls.Length == 0) return true;
        AccessTools.Method(typeof(Player), "UpdatePlacementGhost").Invoke(__instance, new object[] { false });
        var ghost = AccessTools.Field(typeof(Player), "m_placementGhost").GetValue(__instance) as GameObject;
        float radius = ghost == null ? 0 : ghost.GetComponentsInChildren<Collider>()
            .Select(c => new Vector2(c.bounds.extents.x, c.bounds.extents.z).magnitude).DefaultIfEmpty(0).Max();
        if (ghost == null || !controls.Any(c => c.BuildBlocked(ghost.transform.position, radius))) return true;
        __instance.Message(MessageHud.MessageType.Center, "Путь возвращающегося владыки временно закрыт для стройки");
        __result = false; return false; // TryPlacePiece возвращает false до расхода материалов и прочности.
    }
}
