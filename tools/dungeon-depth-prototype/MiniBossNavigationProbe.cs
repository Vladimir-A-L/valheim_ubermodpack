using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DungeonDepthPrototype;

// Парная нативная проверка высоты: одинаковый пол и штатные агенты, без игроков и изменения мира.
internal static class MiniBossNavigationProbe
{
    private static readonly List<GameObject> floors = new List<GameObject>();
    private static float started;

    internal static object Run(string phase)
    {
        if (!ZNet.instance.IsServer() || ZNet.instance.GetPeers().Any(p => p.IsReady()))
            throw new InvalidOperationException("Нужен отдельный сервер без игроков");
        if (phase == "create")
        {
            if (floors.Count != 0) throw new InvalidOperationException("Проба уже существует");
            var origin = ZNet.instance.GetReferencePosition();
            foreach (float height in new[] { MiniBossTrial.Hub.y, 7000f })
            {
                var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
                floor.name = "MiniBossNavigationProbe";
                floor.layer = LayerMask.NameToLayer("static_solid");
                floor.transform.position = new Vector3(origin.x, height - .5f, origin.z);
                floor.transform.localScale = new Vector3(20, 1, 20);
                floors.Add(floor);
            }
            Physics.SyncTransforms(); started = Time.realtimeSinceStartup;
        }
        else if (phase == "cleanup")
        {
            foreach (var floor in floors) Object.Destroy(floor);
            floors.Clear();
            return new { cleaned = true };
        }
        else if (phase != "status") throw new ArgumentException("Неизвестная фаза");
        if (floors.Count != 2) throw new InvalidOperationException("Сначала создать пробу");
        var navigation = Pathfinding.instance;
        float center = navigation.GetTilePos(Vector3Int.zero).y;
        float heightLimit = (float)AccessTools.Field(typeof(Pathfinding), "m_tileHeight").GetRawConstantValue();
        var results = (from floor in floors
            from prefab in new[] { "Skeleton", "Ghost" }
            let agent = ZNetScene.instance.GetPrefab(prefab).GetComponent<BaseAI>().m_pathAgentType
            let surface = floor.transform.position + Vector3.up * .5f
            let start = surface + new Vector3(-3, .1f, 0)
            let end = surface + new Vector3(3, .1f, 0)
            select new { prefab, agent = agent.ToString(), floor_height = surface.y,
                full_path = navigation.HavePath(start, end, agent),
                floor_in_build_bounds = Math.Abs(surface.y - center) < heightLimit / 2 }).ToArray();
        return new { elapsed_seconds = Time.realtimeSinceStartup - started,
            navigation_min_y = center - heightLimit / 2, navigation_max_y = center + heightLimit / 2,
            results, passed = results.All(r => r.full_path == r.floor_in_build_bounds) &&
                results.Any(r => r.full_path),
            limitation = "native-path-query-on-identical-floors; not-client-combat" };
    }
}
