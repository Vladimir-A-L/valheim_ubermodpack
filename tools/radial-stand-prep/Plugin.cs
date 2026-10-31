using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RadialStandPrep;

// Временный инструмент подготовки. Не входит в клиент и снимается до ручного испытания.
[BepInPlugin("ubermodpack.radial-stand-prep", "Radial Stand Prep", "0.1.1")]
[BepInDependency("ubermodpack.dungeon-depth-prototype", "0.15.1")]
public sealed class Plugin : BaseUnityPlugin
{
    private const string Key = "uberdungeon.radial-stand.fixture";
    private float next;
    private string Folder => Path.Combine(Paths.ConfigPath, "RadialStandPrep");
    private static Assembly Runtime => AppDomain.CurrentDomain.GetAssemblies()
        .Single(a => a.GetName().Name == "DungeonDepthPrototype");
    private static Type TypeOf(string name) => Runtime.GetType("DungeonDepthPrototype." + name, true)!;
    private static object? Call(string type, string method, params object[] args) =>
        AccessTools.Method(TypeOf(type), method).Invoke(null, args);
    private static string Field(string type, string name) =>
        (string)AccessTools.Field(TypeOf(type), name).GetValue(null);
    private static Dictionary<ZDOID, ZDO> All =>
        (Dictionary<ZDOID, ZDO>)Call("ForestFixture", "AllZdos")!;
    private static int Solid => LayerMask.GetMask("Default", "static_solid", "piece", "terrain", "blocker");
    private static float[] V(Vector3 p) => new[] { p.x, p.y, p.z };
    private static Vector3 Point(JToken p) { var v = p.ToObject<float[]>()!; return new Vector3(v[0], v[1], v[2]); }

    private void Update()
    {
        if (Time.realtimeSinceStartup < next || ZNet.instance == null || !ZNet.instance.IsServer() ||
            ZNet.instance.GetWorldName() != "ForestCryptQA" || ZNetScene.instance == null ||
            !Config.Bind("Preparation", "Enabled", false, "Только отдельный стенд без игроков.").Value) return;
        next = Time.realtimeSinceStartup + .2f;
        Directory.CreateDirectory(Folder);
        string request = Path.Combine(Folder, "request.json");
        if (!File.Exists(request)) return;
        try
        {
            var command = JObject.Parse(File.ReadAllText(request));
            string id = command.Value<string>("id") ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-zA-Z0-9_-]{1,64}$"))
                throw new ArgumentException("Некорректный ID");
            string response = Path.Combine(Folder, id + ".json");
            if (File.Exists(response)) throw new InvalidOperationException("ID уже выполнен");
            File.Move(request, Path.Combine(Folder, id + ".request.json"));
            object result;
            try
            {
                if (ZNet.instance.GetPeers().Any()) throw new InvalidOperationException("На стенде есть игрок");
                result = new { ok = true, value = Run(command) };
            }
            catch (Exception e) { result = new { ok = false, error = e.ToString() }; }
            string temp = response + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(new {
                observed_at_utc = DateTime.UtcNow.ToString("O"), result }, Formatting.Indented));
            File.Move(temp, response);
        }
        catch (Exception e) { Logger.LogError(e); }
    }

    private static GameObject Spawn(string prefab, Vector3 p, Quaternion q, string kind, string id)
    {
        var obj = (GameObject)Call("MiniBossTrial", "Spawn", prefab, p, q, kind)!;
        obj.GetComponent<ZNetView>().GetZDO().Set(Key, id);
        return obj;
    }

    private static object Sample(Vector3 p, float lift = .5f)
    {
        bool floor = Physics.Raycast(p + Vector3.up * lift, Vector3.down, out var hit, lift + 2, Solid,
            QueryTriggerInteraction.Ignore);
        var foot = floor ? hit.point + Vector3.up * .08f : p;
        var blockers = Physics.OverlapCapsule(foot + Vector3.up * .4f, foot + Vector3.up * 1.5f,
            .3f, Solid, QueryTriggerInteraction.Ignore);
        return new { requested = V(p), floor, position = V(foot), normal = floor ? hit.normal.y : 0,
            clear = blockers.Length == 0, support = floor ? hit.collider.name : "",
            blockers = blockers.Select(c => c.name).Distinct().ToArray(),
            area_ready = ZNetScene.instance.IsAreaReady(p) };
    }

    private static object Run(JObject c)
    {
        string action = c.Value<string>("action") ?? "";
        if (action == "survey")
        {
            Physics.SyncTransforms();
            return new { points = c["points"]!.Select(p => Sample(Point(p), c.Value<float?>("lift") ?? .5f)).ToArray(),
                transitions = Object.FindObjectsByType<Teleport>(FindObjectsSortMode.None).Select(t => new {
                    position = V(t.transform.position), forward = V(t.transform.forward),
                    arrival = V(t.transform.position + t.transform.forward - t.transform.up),
                    linked = t.m_targetPoint != null }).ToArray() };
        }
        if (action == "relocate")
        {
            string id = c.Value<string>("fixture")!;
            var data = All.Values.Single(z => z.GetString(Key) == id);
            if (data.GetPrefab() != "portal_wood".GetStableHashCode())
                throw new InvalidOperationException("Переносить можно только служебный портал");
            var obj = ZNetScene.instance.FindInstance(data.m_uid);
            if (obj == null || !obj.GetComponent<ZNetView>().IsOwner())
                throw new InvalidOperationException("Портал не загружен или не принадлежит серверу");
            var p = Point(c["position"]!); var q = Quaternion.Euler(0, c.Value<float>("yaw"), 0);
            var sample = JObject.FromObject(Sample(p));
            if (!sample.Value<bool>("floor") || !sample.Value<bool>("clear"))
                throw new InvalidOperationException("Точка переноса не прошла проверку");
            data.SetPosition(p); data.SetRotation(q); obj.transform.SetPositionAndRotation(p, q);
            ZDOMan.instance.SetDirtySector(data); Physics.SyncTransforms();
            return new { moved = true, exit = Sample(p + q * Vector3.forward + Vector3.up) };
        }
        if (action == "hub")
        {
            string id = c.Value<string>("fixture")!;
            if (All.Values.Any(z => z.GetString(Key) == id)) throw new InvalidOperationException("Хаб уже создан");
            var p = Point(c["position"]!);
            int count = 0;
            for (int x = -10; x <= 10; x += 2)
            for (int z = -10; z <= 10; z += 2)
            { Spawn("stone_floor_2x2", p + new Vector3(x, 0, z), Quaternion.identity, "floor", id); count++; }
            Physics.SyncTransforms();
            return new { created = count, center = Sample(p + Vector3.up) };
        }
        if (action == "portal")
        {
            string id = c.Value<string>("fixture")!;
            if (All.Values.Any(z => z.GetString(Key) == id)) throw new InvalidOperationException("Портал уже создан");
            var p = Point(c["position"]!); var q = Quaternion.Euler(0, c.Value<float>("yaw"), 0);
            var sample = JObject.FromObject(Sample(p));
            if (!sample.Value<bool>("floor") || !sample.Value<bool>("clear") || sample.Value<float>("normal") < .8f)
                throw new InvalidOperationException("Нет опоры/свободного корпуса для портала");
            var obj = Spawn("portal_wood", p, q, "portal", id);
            var data = obj.GetComponent<ZNetView>().GetZDO(); data.Set(ZDOVars.s_tag, c.Value<string>("tag"));
            var tw = obj.GetComponent<TeleportWorld>();
            Physics.SyncTransforms();
            return new { created = true, position = V(p), fields = tw.GetType().GetFields()
                .Where(f => f.Name.Contains("exit", StringComparison.OrdinalIgnoreCase))
                .Select(f => new { f.Name, value = f.GetValue(tw)?.ToString() }).ToArray() };
        }
        if (action == "sign")
        {
            string id = c.Value<string>("fixture")!;
            if (All.Values.Any(z => z.GetString(Key) == id))
                throw new InvalidOperationException("Табличка уже создана");
            var obj = Spawn("sign", Point(c["position"]!), Quaternion.Euler(0, c.Value<float>("yaw"), 0), "sign", id);
            obj.GetComponent<ZNetView>().GetZDO().Set(ZDOVars.s_text, c.Value<string>("text"));
            return new { created = true };
        }
        if (action == "actors")
        {
            var pov = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetType("PassiveTree.RuneWard") != null);
            var wardType = pov.GetType("PassiveTree.RuneWard")!;
            return Character.GetAllCharacters().Where(ch => ch.GetComponent<ZNetView>()?.IsValid() == true)
                .Select(ch => {
                    var z = ch.GetComponent<ZNetView>().GetZDO();
                    var ward = AccessTools.Method(wardType, "Of").Invoke(null, new object[] { ch });
                    return new { entity = z.GetString(Field("CombatRecorder", "EntityKey")),
                        token = z.GetString(Field("ForestRuntime", "Tag")), position = V(ch.transform.position),
                        hp = ch.GetHealth(), max_hp = ch.GetMaxHealth(),
                        shield = ward == null ? 0 :
                            Convert.ToSingle(AccessTools.Field(wardType, "Current").GetValue(ward)),
                        max_shield = ward == null ? 0 :
                            Convert.ToSingle(AccessTools.Field(wardType, "Max").GetValue(ward)),
                        mods = Call("PovBridge", "Read", ch), room = z.GetInt(Field("ForestRuntime", "RoomKey")),
                        depth = z.GetInt(Field("ForestRuntime", "DepthKey")),
                        profile = z.GetString(Field("RadialRuntime", "ProfileKey")),
                        prefab = Utils.GetPrefabName(ch.gameObject), ai = ch.GetBaseAI()?.enabled };
                }).Where(a => a.token.Length > 0).ToArray();
        }
        if (action == "fixtures") return All.Values.Where(z => z.GetString(Key).Length > 0).Select(z => new {
            fixture = z.GetString(Key), position = V(z.GetPosition()), prefab = z.GetPrefab(),
            tag = z.GetString(ZDOVars.s_tag), id = z.m_uid.ToString(),
            target = z.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal).ToString(),
            rotation = z.GetRotation().eulerAngles.y,
            exit = z.GetPrefab() == "portal_wood".GetStableHashCode()
                ? Sample(z.GetPosition() + z.GetRotation() * Vector3.forward + Vector3.up) : null }).ToArray();
        throw new ArgumentException("Неизвестная команда подготовки");
    }
}
