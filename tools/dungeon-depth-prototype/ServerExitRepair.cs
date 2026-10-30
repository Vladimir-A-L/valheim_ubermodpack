using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using BepInEx.Bootstrap;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace DungeonDepthPrototype;

// Совместимость с конкретной поставкой; протокол и серверный источник истины остаются в ServerManager.
internal static class ServerExitRepair
{
    private const string ExpectedHash = "c5d6fd0ecbfba232d7c5bb33ab890aec42a277757229ea8809515320bdc62e38";
    private static Type runtime = null!;
    private static float nextLog;
    internal const int DrainLimit = 256 * 1024;
    [ThreadStatic] internal static int FinalFlushDepth;
    internal static void Install(Harmony harmony)
    {
        if (!Chainloader.PluginInfos.TryGetValue("sighsorry.ServerManager", out var info)) return;
        var assembly = info.Instance.GetType().Assembly;
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(assembly.Location);
        string hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        if (info.Metadata.Version.ToString() != "1.2.5" || hash != ExpectedHash)
            throw new InvalidOperationException("Исправление выхода требует закреплённый ServerManager 1.2.5");
        runtime = assembly.GetType("ServerManager.ServerManagerRuntime", true)!;
        void Patch(string method, string callback) => harmony.Patch(AccessTools.Method(runtime, method),
            postfix: new HarmonyMethod(typeof(ServerExitRepair), callback));
        Patch("TryFlushClientWorldMutations", nameof(Flushed));
        Patch("CaptureDeferredClientExitSnapshot", nameof(Captured));
        Patch("HandleClientFinalSaveReady", nameof(GateReady));
        Patch("ResumeDeferredClientExit", nameof(Resumed));
        Patch("ApplyInitialServerCharacter", nameof(Loaded));
        Patch("ApplySaveResponse", nameof(Acknowledged));
        Patch("SendClientSave", nameof(Sent));
        harmony.Patch(AccessTools.Method(runtime, "TryFlushClientWorldMutations"),
            prefix: new HarmonyMethod(typeof(ServerExitRepair), nameof(EnterFlush)),
            finalizer: new HarmonyMethod(typeof(ServerExitRepair), nameof(LeaveFlush)));
        harmony.Patch(AccessTools.Method(typeof(ZDOMan), "SendZDOs"),
            transpiler: new HarmonyMethod(typeof(ServerExitRepair), nameof(FlushBudget)));
        Plugin.LogInfo("EXIT_PROTOCOL compatibility=ServerManager-1.2.5 verified");
    }
    private static void EnterFlush(out int __state) { __state = FinalFlushDepth; FinalFlushDepth++; }
    private static Exception? LeaveFlush(Exception? __exception, int __state)
    { FinalFlushDepth = __state; return __exception; }
    private static IEnumerable<CodeInstruction> FlushBudget(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var target = AccessTools.Method(typeof(ISocket), nameof(ISocket.GetSendQueueSize));
        if (code.Count(i => i.Calls(target)) != 1)
            throw new InvalidOperationException("Не совпадает закреплённая структура ZDOMan.SendZDOs");
        foreach (var instruction in code)
        {
            if (!instruction.Calls(target)) { yield return instruction; continue; }
            // flush=true в 1.0.20 всё равно останавливается на обычных 10 КБ.
            // Меняем только бюджет внутри существующего финального drain, не очередь и не подтверждение.
            instruction.opcode = OpCodes.Ldarg_2; instruction.operand = null;
            yield return instruction;
            yield return new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(ServerExitRepair), nameof(QueueForFlushBudget)));
        }
    }
    internal static int QueueForFlushBudget(ISocket socket, bool flush)
    {
        int actual = socket.GetSendQueueSize();
        // Пакеты остаются штатного размера. При достижении лимита снова ждём сеть и общий таймаут.
        return FinalFlushDepth > 0 && flush && actual < DrainLimit ? 0 : actual;
    }
    private static void Flushed(bool __result)
    {
        if (__result || Time.realtimeSinceStartup >= nextLog)
        {
            nextLog = Time.realtimeSinceStartup + 2;
            Plugin.LogInfo("EXIT_PROTOCOL drain=" + __result + " " + JsonConvert.SerializeObject(Status()));
        }
    }
    private static void Captured(byte[] __result) => Plugin.LogInfo("EXIT_PROTOCOL snapshot_bytes=" + __result.Length);
    private static object? Get(object value, string property) =>
        AccessTools.Property(value.GetType(), property).GetValue(value);
    private static string Revision(object session) => "revision=" + Get(session, "AcknowledgedProfileRevision") +
        " failed=" + Get(session, "Failed");
    private static void Loaded(object __0) =>
        Plugin.LogInfo("EXIT_PROTOCOL loaded=" + Get(__0, "InitialTransferCompleted") + " " + Revision(__0));
    private static void Acknowledged(object __0) => Plugin.LogInfo("EXIT_PROTOCOL acknowledgement " + Revision(__0));
    private static void Sent(object __1) => Plugin.LogInfo("EXIT_PROTOCOL submitted revision=" +
        Get(__1, "Revision") + " capture=" + Get(__1, "CaptureId") + " reason=" + Get(__1, "Reason"));
    private static void GateReady()
    {
        var exit = AccessTools.Field(runtime, "_deferredClientExit").GetValue(null);
        Plugin.LogInfo("EXIT_PROTOCOL gate_ready=" + (exit != null && (bool)Get(exit, "ServerGateAcknowledged")!));
    }
    private static void Resumed(bool succeeded, string detail)
    {
        Plugin.LogInfo("EXIT_PROTOCOL completed=" + succeeded + " detail=" + detail);
        if (succeeded) return;
        // Используем существующее представление ошибок ServerManager при возврате в меню.
        var plugin = runtime.Assembly.GetType("ServerManager.ServerManagerPlugin", true)!;
        AccessTools.Property(plugin, "ConnectionError").SetValue(null,
            "Последнее сохранение перед выходом не подтверждено. Сохранена предыдущая подтверждённая " +
            "ревизия персонажа; последние изменения могли не сохраниться. Подробности — в журнале EXIT_PROTOCOL.");
        var access = runtime.Assembly.GetType("ServerManager.ValheimPrivateAccess", true)!;
        AccessTools.Method(access, "SetConnectionStatus").Invoke(null,
            new object[] { ZNet.ConnectionStatus.ErrorConnectFailed });
    }
    internal static object Status()
    {
        var manager = ZDOMan.instance;
        if (manager == null) return new { available = false };
        var queue = (IEnumerable)AccessTools.Field(typeof(ZDOMan), "m_clientChangeQueue").GetValue(manager);
        var peers = (IEnumerable)AccessTools.Field(typeof(ZDOMan), "m_peers").GetValue(manager);
        var items = queue.Cast<ZDOID>().Select(manager.GetZDO).Where(z => z != null).ToArray();
        return new { available = true, queued = manager.GetClientChangeQueue(),
            destroyed = ((ICollection)AccessTools.Field(typeof(ZDOMan), "m_destroySendList")
                .GetValue(manager)).Count,
            peers = peers.Cast<object>().Select(p => {
                var peer = (ZNetPeer)AccessTools.Field(p.GetType(), "m_peer").GetValue(p);
                return new { ready = peer.IsReady(), send_bytes = peer.m_socket.GetSendQueueSize(),
                    should_send = items.Count(z => (bool)AccessTools.Method(p.GetType(), "ShouldSend")
                        .Invoke(p, new object[] { z })) };
            }).ToArray(),
            prefabs = items.GroupBy(z => ZNetScene.instance.GetPrefab(z.GetPrefab())?.name ?? "missing")
                .ToDictionary(g => g.Key, g => g.Count()),
            deferred = runtime != null && AccessTools.Field(runtime, "_deferredClientExit").GetValue(null) != null,
            player_enabled = Player.m_localPlayer != null && Player.m_localPlayer.enabled };
    }
}
