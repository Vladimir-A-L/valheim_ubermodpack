using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace DungeonDepthPrototype;

internal static class ServerExitProbe
{
    // Реальный SendZDOs и сериализация закреплённой игры; транспорт намеренно держит очередь занятой.
    internal static object Run()
    {
        if (!ZNet.instance.IsServer() || ZNet.instance.GetPeers().Any(p => p.IsReady()))
            throw new InvalidOperationException("Нужен отдельный сервер без игроков");
        var manager = ZDOMan.instance;
        var queue = (HashSet<ZDOID>)AccessTools.Field(typeof(ZDOMan), "m_clientChangeQueue").GetValue(manager);
        var previousQueue = queue.ToArray();
        var server = AccessTools.Field(typeof(ZNet), "m_isServer");
        var send = AccessTools.Method(typeof(ZDOMan), "SendZDOs");
        var peerType = AccessTools.Inner(typeof(ZDOMan), "ZDOPeer");
        var socket = new ProbeSocket { Queued = 11000 };
        var peer = Activator.CreateInstance(peerType, true)!;
        var net = new ZNetPeer(socket, true) { m_uid = 1 };
        AccessTools.Field(peerType, "m_peer").SetValue(peer, net);
        var created = new List<ZDO>();
        try
        {
            queue.Clear();
            for (int i = 0; i < 160; i++)
            {
                var data = manager.CreateNewZDO(new Vector3(6000 + i, 4000, 6000),
                    "dungeon_forestcrypt_door".GetStableHashCode());
                data.Set("exit-probe", new byte[512]); created.Add(data); queue.Add(data.m_uid);
            }
            server.SetValue(null, false);
            bool Send(bool flush) => (bool)send.Invoke(manager, new[] { peer, (object)flush });
            bool reproduced = !Send(true) && queue.Count == 160 && socket.Packets.Count == 0;
            ServerExitRepair.FinalFlushDepth++;
            bool ordinaryUnchanged = !Send(false) && queue.Count == 160;
            int rounds = 0;
            while (Send(true)) if (++rounds > 100) throw new InvalidOperationException("Неограниченный drain");
            bool drained = queue.Count == 0 && socket.Packets.Count > 0 && socket.Queued > 11000;
            // При слишком большой очереди подтверждение не подделывается: данные остаются ждать сеть.
            socket.Queued = ServerExitRepair.DrainLimit;
            created[0].Set("exit-probe-update", true);
            bool bounded = !Send(true) && queue.Count == 1;
            socket.Queued = 0;
            while (Send(true)) if (++rounds > 100) throw new InvalidOperationException("Неограниченный retry");
            bool resumed = queue.Count == 0;
            return new { reproduced, ordinary_unchanged = ordinaryUnchanged, drained, bounded, resumed,
                packets = socket.Packets.Count, rounds, queued_bytes = socket.Queued,
                passed = reproduced && ordinaryUnchanged && drained && bounded && resumed };
        }
        finally
        {
            ServerExitRepair.FinalFlushDepth = 0; server.SetValue(null, true);
            queue.Clear(); queue.UnionWith(previousQueue);
            foreach (var data in created) manager.DestroyZDO(data);
            net.Dispose();
        }
    }
    private sealed class ProbeSocket : ISocket
    {
        internal int Queued;
        internal readonly List<byte[]> Packets = new List<byte[]>();
        public bool IsConnected() => true;
        public void Send(ZPackage pkg) { Packets.Add(pkg.GetArray()); Queued += pkg.Size(); }
        public ZPackage Recv() => null!;
        public int GetSendQueueSize() => Queued;
        public int GetCurrentSendRate() => 0;
        public bool IsHost() => false;
        public void Dispose() { }
        public bool GotNewData() => false;
        public void Close() { }
        public string GetEndPointString() => "native-exit-probe";
        public void GetAndResetStats(out int totalSent, out int totalRecv) { totalSent = 0; totalRecv = 0; }
        public void GetConnectionQuality(out float localQuality, out float remoteQuality, out int ping,
            out float outByteSec, out float inByteSec)
        { localQuality = remoteQuality = 1; ping = 0; outByteSec = inByteSec = 0; }
        public ISocket Accept() => null!;
        public int GetHostPort() => 0;
        public bool Flush() => true;
        public string GetHostName() => "native-exit-probe";
        public void VersionMatch() { }
    }
}
