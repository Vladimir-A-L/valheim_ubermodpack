using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using Newtonsoft.Json;
using UnityEngine;

namespace DungeonDepthPrototype;

// Журнал и подсчёт принадлежат серверу; источник измерений — клиент, владеющий персонажем.
internal sealed class ManualTelemetry : MonoBehaviour
{
    private sealed class ServerSettings
    {
        public bool Enabled = false, AllowProbe = false;
        public string[] Players = Array.Empty<string>();
    }
    private sealed class Reply
    {
        public string Server = "", Character = "", Id = "", Error = "";
        public int Total, AttemptDeaths;
        public bool Death, Probe, Duplicate;
    }
    private const string Hello = "UberManualHelloV1", Event = "UberManualEventV1", Ack = "UberManualAckV1";
    private static ManualTelemetry instance = null!;
    private ZNet? network;
    private readonly HashSet<ZRpc> bound = new HashSet<ZRpc>();
    private ServerSettings policy = new ServerSettings();
    private ManualLedger? ledger;
    private readonly List<ManualSample> queue = new List<ManualSample>();
    private string serverKey = "", character = "", outbox = "", journal = "", error = "";
    private bool ready, failed;
    private float nextSend, nextError, helloStarted;
    private int total, probeTotal;
    private ManualSample? lastProbe;

    internal static bool Ready => instance != null && instance.ready && !instance.failed;
    private static ZRpc? ServerRpc => ZNet.instance.GetServerPeer()?.m_rpc;
    internal static void Install(GameObject host) => instance = host.AddComponent<ManualTelemetry>();
    internal static object Status() => new { ready = Ready, pending = instance.queue.Count,
        total = instance.total, probe_total = instance.probeTotal, error = instance.error };

    private static string Hash(string value)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value)))
            .Replace("-", "").ToLowerInvariant();
    }

    private void Update()
    {
        try
        {
            if (ZNet.instance == null || ZNet.instance.GetWorldName() != "ForestCryptQA")
            {
                network = null; ready = false; failed = false; ledger = null; serverKey = "";
                character = ""; queue.Clear(); bound.Clear(); return;
            }
            if (network != ZNet.instance) Bind();
            if (failed) return;
            if (ZNet.instance.IsServer())
            {
                if (ledger == null) return;
                foreach (var peer in network!.GetPeers().Where(p => p.IsReady()))
                    if (bound.Add(peer.m_rpc))
                    {
                        peer.m_rpc.Register<string>(Hello, ReceiveHello);
                        peer.m_rpc.Register<string>(Event, ReceiveEvent);
                    }
                return;
            }
            if (ServerRpc == null || !ForestManual.Target(Player.m_localPlayer)) return;
            if (bound.Add(ServerRpc)) ServerRpc.Register<string>(Ack, ReceiveAck);
            if (Time.realtimeSinceStartup < nextSend) return;
            nextSend = Time.realtimeSinceStartup + .5f;
            if (!ready)
            {
                if (helloStarted == 0) helloStarted = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - helloStarted > 45)
                    throw new TimeoutException("Сервер не подтвердил журнал за 45 секунд; требуется повторный вход");
                ServerRpc.Invoke(Hello, "");
            }
            else foreach (var sample in queue.Take(8).ToArray())
                ServerRpc.Invoke(Event, JsonConvert.SerializeObject(sample));
        }
        catch (Exception e) { Fail(e); }
    }

    private void Bind()
    {
        network = ZNet.instance;
        ready = false; failed = false; error = ""; helloStarted = 0; queue.Clear(); bound.Clear();
        if (!ZNet.instance.IsServer()) return;
        string settings = Path.Combine(Paths.ConfigPath, "forest-manual-server.json");
        policy = File.Exists(settings) ? JsonConvert.DeserializeObject<ServerSettings>(File.ReadAllText(settings))! :
            new ServerSettings();
        if (!policy.Enabled) return;
        string folder = Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), "ForestManual",
            ZNet.instance.GetWorldUID().ToString());
        Directory.CreateDirectory(folder);
        string identity = Path.Combine(folder, "identity.txt");
        if (!File.Exists(identity)) File.WriteAllText(identity, Guid.NewGuid().ToString("N"));
        serverKey = File.ReadAllText(identity).Trim();
        if (!Guid.TryParseExact(serverKey, "N", out _)) throw new InvalidDataException("Повреждён ID журнала");
        journal = Path.Combine(folder, "events.jsonl");
        ledger = new ManualLedger();
        if (File.Exists(journal)) foreach (string line in File.ReadLines(journal))
        {
            var receipt = JsonConvert.DeserializeObject<ManualReceipt>(line)!;
            if (Hash(JsonConvert.SerializeObject(receipt.Sample)) != receipt.Fingerprint)
                throw new InvalidDataException("Не совпал отпечаток сохранённого события");
            ledger.Restore(receipt);
        }
        Plugin.LogInfo("MANUAL_SERVER_READY");
    }

    private ZNetPeer Peer(ZRpc sender)
    {
        var peer = ZNet.instance.GetPeers().FirstOrDefault(p => p.m_rpc == sender);
        if (peer == null || !peer.IsReady() || !policy.Players.Contains(peer.m_playerName) ||
            string.IsNullOrWhiteSpace(peer.m_socket.GetHostName()))
            throw new InvalidOperationException("Отправитель не допущен к ручной проверке");
        return peer;
    }

    private void ReceiveHello(ZRpc sender, string unused)
    {
        try
        {
            var peer = Peer(sender);
            Respond(sender, new Reply { Server = serverKey, Character = peer.m_playerName,
                Total = ledger!.Total(peer.m_socket.GetHostName(), peer.m_playerName) });
        }
        catch (Exception e) { Plugin.LogError("MANUAL_HELLO " + e.Message); }
    }

    private void ReceiveEvent(ZRpc sender, string json)
    {
        try
        {
            var peer = Peer(sender);
            if (json.Length > 4096) throw new ArgumentException("Слишком большое сообщение");
            var sample = JsonConvert.DeserializeObject<ManualSample>(json)!;
            if (sample.Character != peer.m_playerName || (sample.Probe && !policy.AllowProbe))
                throw new InvalidOperationException("Не разрешены имя или контрольная проба");
            string actor = peer.m_socket.GetHostName();
            string fingerprint = Hash(JsonConvert.SerializeObject(sample));
            var receipt = ledger!.Accept(actor, sample, fingerprint, row =>
            {
                byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(row) + "\n");
                using var file = new FileStream(journal, FileMode.Append, FileAccess.Write, FileShare.Read);
                file.Write(bytes, 0, bytes.Length); file.Flush(true);
            }, out bool duplicate);
            Respond(sender, new Reply { Server = serverKey, Character = sample.Character, Id = sample.Id,
                Total = ledger.Total(actor, sample.Character, sample.Probe), AttemptDeaths = receipt.AttemptDeaths,
                Death = receipt.ConditionalDeath, Probe = sample.Probe, Duplicate = duplicate });
        }
        catch (Exception e)
        {
            Plugin.LogError("MANUAL_EVENT_REJECTED " + e.Message);
            Respond(sender, new Reply { Server = serverKey, Error = e.Message });
        }
    }

    private void Respond(ZRpc sender, Reply reply) => sender.Invoke(Ack, JsonConvert.SerializeObject(reply));

    private void ReceiveAck(ZRpc sender, string json)
    {
        try
        {
            if (sender != ServerRpc || json.Length > 4096) return;
            var reply = JsonConvert.DeserializeObject<Reply>(json)!;
            if (reply.Error.Length > 0) throw new InvalidOperationException(reply.Error);
            if (Player.m_localPlayer == null || reply.Character != Player.m_localPlayer.GetPlayerName()) return;
            if (!ready)
            {
                if (!Guid.TryParseExact(reply.Server, "N", out _))
                    throw new InvalidDataException("Неверный ID сервера");
                serverKey = reply.Server; character = reply.Character; total = reply.Total;
                string folder = Path.Combine(Paths.ConfigPath, "ForestCryptQA", "outbox");
                Directory.CreateDirectory(folder);
                outbox = Path.Combine(folder, Hash(serverKey + "\n" + character) + ".json");
                queue.Clear();
                if (File.Exists(outbox)) queue.AddRange(JsonConvert.DeserializeObject<List<ManualSample>>(
                    File.ReadAllText(outbox))!);
                ready = true;
            }
            if (reply.Server != serverKey || reply.Id.Length == 0) return;
            var sample = queue.FirstOrDefault(s => s.Id == reply.Id);
            if (sample == null) return;
            queue.Remove(sample); SaveOutbox();
            if (reply.Probe) { probeTotal = reply.Total; lastProbe = sample; }
            else
            {
                total = reply.Total;
                if (sample.Kind == "start") Player.m_localPlayer.Message(MessageHud.MessageType.Center,
                    "Тест готов: god включён, ghost выключен. Счётчик сервера: " + total);
                else if (reply.Death && !reply.Duplicate) Player.m_localPlayer.Message(MessageHud.MessageType.Center,
                    "Условная смерть. Сервер: " + total + " всего, " + reply.AttemptDeaths + " за вход");
            }
        }
        catch (Exception e) { Fail(e); }
    }

    private void SaveOutbox()
    {
        string temporary = outbox + ".tmp";
        byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(queue));
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
        if (File.Exists(outbox)) File.Replace(temporary, outbox, null);
        else File.Move(temporary, outbox);
    }

    internal static void Enqueue(ManualSample sample)
    {
        if (!Ready) throw new InvalidOperationException("Нет подтверждённой связи с серверным журналом");
        sample.Validate();
        if (instance.queue.Count >= 10000) throw new InvalidOperationException("Заполнена очередь событий");
        instance.queue.Add(sample);
        try { instance.SaveOutbox(); }
        catch (Exception e) { instance.Fail(e); throw; }
    }

    internal static void ReportFailure(Exception error) => instance.Fail(error);

    internal static object ReplayProbe()
    {
        if (instance.lastProbe == null || instance.queue.Count != 0)
            throw new InvalidOperationException("Нет завершённой пробы для проверки повтора");
        Enqueue(instance.lastProbe);
        return Status();
    }

    private void Fail(Exception e)
    {
        failed = true; ready = false; error = e.Message;
        if (Time.realtimeSinceStartup < nextError) return;
        nextError = Time.realtimeSinceStartup + 10;
        Plugin.LogError("MANUAL_TELEMETRY " + e.Message);
        if (Player.m_localPlayer != null) Player.m_localPlayer.Message(MessageHud.MessageType.Center,
            "Учёт теста остановлен: " + e.Message);
    }
}
