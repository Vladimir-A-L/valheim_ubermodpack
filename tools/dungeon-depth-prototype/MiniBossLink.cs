using System;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DungeonDepthPrototype;

// Связь описывает уже созданную встречу. Загрузка никогда не создаёт бой, дверь или награды заново.
internal sealed class MiniBossLink
{
    public int Schema = 1, Seed, Depth, Room, Socket, Turn;
    public string Token = "", ParentPrefab = "";
    public float[] ParentPosition = Array.Empty<float>(), Origin = Array.Empty<float>();
    internal static Vector3 V(float[] p) => new Vector3(p[0], p[1], p[2]);
}

internal sealed class MiniBossRoomLink : MonoBehaviour
{
    private GameObject? blocker, linkedArena;
    private RoomConnection? socket;
    private string saved = "";
    private MiniBossLink? link;
    private float next;
    internal bool Open { get; private set; }

    private void Update()
    {
        if (Time.realtimeSinceStartup < next || ZNetScene.instance == null) return;
        next = Time.realtimeSinceStartup + .25f;
        Refresh();
    }

    internal void Refresh()
    {
        var data = MiniBossRuntime.Data(this);
        if (data == null) return;
        string json = data.GetString(MiniBossConnection.LinkKey);
        if (json.Length == 0) return;
        if (saved != json) { link = JsonConvert.DeserializeObject<MiniBossLink>(json); saved = json; }
        if (link == null) return;
        if (socket == null)
        {
            var parent = GetComponentsInChildren<Room>(true).FirstOrDefault(r =>
                r.gameObject.name == link.ParentPrefab &&
                Vector3.Distance(r.transform.position, MiniBossLink.V(link.ParentPosition)) < .02f);
            if (parent == null) return;
            var sockets = parent.GetConnections();
            if (link.Socket < 0 || link.Socket >= sockets.Length) return;
            socket = sockets[link.Socket];
            blocker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            blocker.name = "miniboss-loading-cap";
            blocker.layer = LayerMask.NameToLayer("static_solid");
            blocker.transform.SetParent(parent.transform, true);
            blocker.transform.position = socket.transform.position + Vector3.up * 1.2f;
            blocker.transform.rotation = socket.transform.rotation;
            blocker.transform.localScale = new Vector3(2, 2.4f, .15f);
            blocker.GetComponent<Renderer>().sharedMaterial = ZNetScene.instance.GetPrefab("stone_floor_2x2")
                .GetComponentInChildren<MeshRenderer>(true).sharedMaterial;
        }
        if (linkedArena == null) linkedArena = MiniBossConnection.Instance(MiniBossConnection.Arena(data));
        var arena = linkedArena == null ? null : linkedArena.GetComponent<MiniBossArena>();
        // Блокировка с обеих сторон остаётся до загрузки геометрии и подтверждения пола.
        Open = arena != null && MiniBossConnection.Ready(arena, data) &&
            MiniBossConnection.TryConnect(arena, socket!, blocker!.GetComponent<Collider>());
        blocker!.SetActive(!Open);
        if (arena != null) arena.transform.Find(MiniBossConnection.CapName).gameObject.SetActive(!Open);
    }

    internal static void MapRooms(DungeonGenerator generator)
    {
        string saved = MiniBossRuntime.Data(generator)?.GetString(MiniBossConnection.LinkKey) ?? "";
        if (saved.Length == 0 || generator.transform.Find("miniboss-map") != null) return;
        var link = JsonConvert.DeserializeObject<MiniBossLink>(saved)!;
        var root = new GameObject("miniboss-map"); root.transform.SetParent(generator.transform, false);
        root.transform.position = MiniBossLink.V(link.Origin);
        root.transform.rotation = Quaternion.Euler(0, link.Turn * 90, 0);
        void Add(float x, float z, int width, int length)
        {
            var marker = new GameObject("map-region"); marker.transform.SetParent(root.transform, false);
            marker.transform.localPosition = new Vector3(x, 3, z);
            var room = marker.AddComponent<Room>(); room.m_size = new Vector3Int(width, 6, length);
            room.m_theme = generator.m_themes;
        }
        // Независимые области исследования карты не являются рёбрами боевого графа.
        for (int z = -28; z < 0; z += 4) Add(0, z + 2, 4, 4);
        foreach (var r in MiniBossArenaPlan.Floor.Skip(1))
            Add((r.X0 + r.X1) / 2f, (r.Z0 + r.Z1) / 2f, r.X1 - r.X0, r.Z1 - r.Z0);
    }
}
