using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace DungeonDepthPrototype;

// Только собственный PickableItem: двигается дочерняя модель, сетевые предмет/стопка/позиция не меняются.
internal sealed class MiniBossValuePlacement : MonoBehaviour
{
    internal const string PedestalName = "crypt-value-pedestal";
    internal const float ContactInset = .001f;
    private IEnumerator Start()
    {
        var pickable = GetComponent<PickableItem>();
        while (true)
        {
            var data = MiniBossRuntime.Data(this);
            if (data == null) yield break;
            var visual = Visual(pickable);
            string token = data.GetString(ForestRuntime.Tag);
            var arena = UnityEngine.Object.FindObjectsByType<MiniBossArena>(FindObjectsSortMode.None)
                .FirstOrDefault(a => token.Length > 0 &&
                    MiniBossRuntime.Data(a)?.GetString(ForestRuntime.Tag) == token);
            var pedestal = arena == null ? null : Support(arena, transform.position);
            if (visual != null && pedestal != null)
            {
                Align(visual, pedestal);
                yield break;
            }
            // Предмет и оболочка арены могут попасть в ZNetScene в разные кадры.
            yield return new WaitForSeconds(.25f);
        }
    }
    internal static GameObject? Visual(PickableItem item) =>
        (GameObject?)AccessTools.Field(typeof(PickableItem), "m_instance").GetValue(item);
    internal static MeshFilter? Support(MiniBossArena arena, Vector3 position)
    {
        var local = arena.transform.InverseTransformPoint(position);
        return arena.GetComponentsInChildren<MeshFilter>().Where(f => f.name == PedestalName)
            .FirstOrDefault(f => {
                var center = arena.transform.InverseTransformPoint(f.GetComponent<Renderer>().bounds.center);
                return new Vector2(center.x - local.x, center.z - local.z).sqrMagnitude < .01f;
            });
    }
    internal static float Align(GameObject visual, MeshFilter pedestal)
    {
        float shift = Correction(visual, pedestal) - ContactInset;
        visual.transform.localPosition += Vector3.up * shift;
        return shift;
    }
    internal static float Correction(GameObject visual, MeshFilter pedestal)
    {
        var frame = visual.transform.parent;
        var support = Triangles(pedestal, frame).ToArray();
        var item = visual.GetComponentsInChildren<MeshFilter>().Where(f =>
            f.GetComponent<MeshRenderer>()?.enabled == true).SelectMany(f => Triangles(f, frame));
        float result = float.NegativeInfinity;
        foreach (var a in item)
        foreach (var b in support)
        {
            if (!a.Overlaps(b)) continue;
            var polygon = Clip(a, b);
            foreach (var p in polygon) result = Math.Max(result, b.Height(p) - a.Height(p));
        }
        if (float.IsInfinity(result))
            throw new InvalidOperationException("Модель награды не имеет опоры на постаменте");
        return result;
    }
    private readonly struct Triangle
    {
        internal readonly Vector3 A, B, C;
        internal readonly Vector2[] Projection;
        private readonly Vector3 normal;
        private readonly float minX, maxX, minZ, maxZ;
        internal Triangle(Vector3 a, Vector3 b, Vector3 c)
        {
            A = a; B = b; C = c; normal = Vector3.Cross(b - a, c - a);
            Projection = new[] { new Vector2(a.x, a.z), new Vector2(b.x, b.z), new Vector2(c.x, c.z) };
            minX = Math.Min(a.x, Math.Min(b.x, c.x)); maxX = Math.Max(a.x, Math.Max(b.x, c.x));
            minZ = Math.Min(a.z, Math.Min(b.z, c.z)); maxZ = Math.Max(a.z, Math.Max(b.z, c.z));
        }
        internal bool HasArea => Math.Abs(normal.y) > 1e-8f;
        internal float Height(Vector2 p) => A.y - (normal.x * (p.x - A.x) + normal.z * (p.y - A.z)) / normal.y;
        internal bool Overlaps(Triangle other) => minX <= other.maxX && maxX >= other.minX &&
            minZ <= other.maxZ && maxZ >= other.minZ;
    }
    private static IEnumerable<Triangle> Triangles(MeshFilter filter, Transform frame)
    {
        var matrix = frame.worldToLocalMatrix * filter.transform.localToWorldMatrix;
        var vertices = filter.sharedMesh.vertices.Select(matrix.MultiplyPoint3x4).ToArray();
        var indices = filter.sharedMesh.triangles;
        for (int i = 0; i < indices.Length; i += 3)
        {
            var triangle = new Triangle(vertices[indices[i]], vertices[indices[i + 1]], vertices[indices[i + 2]]);
            if (triangle.HasArea) yield return triangle;
        }
    }
    // На пересечении проекций разность высот двух плоскостей линейна: максимум находится на вершинах.
    // Учитываются и пересечения рёбер; одних bounds или вершин предмета для неровной опоры недостаточно.
    private static List<Vector2> Clip(Triangle item, Triangle support)
    {
        float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
        var t = support.Projection;
        float sign = Math.Sign(Cross(t[1] - t[0], t[2] - t[0]));
        var polygon = item.Projection.ToList();
        for (int edge = 0; edge < 3 && polygon.Count > 0; edge++)
        {
            var a = t[edge]; var direction = t[(edge + 1) % 3] - a;
            float Side(Vector2 p) => sign * Cross(direction, p - a);
            var next = new List<Vector2>(); var previous = polygon[polygon.Count - 1];
            float before = Side(previous);
            foreach (var current in polygon)
            {
                float after = Side(current);
                if ((before >= 0) != (after >= 0))
                    next.Add(previous + (current - previous) * (before / (before - after)));
                if (after >= 0) next.Add(current);
                previous = current; before = after;
            }
            polygon = next;
        }
        return polygon;
    }
}
