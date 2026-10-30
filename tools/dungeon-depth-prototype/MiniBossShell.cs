using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace DungeonDepthPrototype;

internal static class MiniBossShell
{
    internal const string Name = "crypt-sealed-shell";
    private static Mesh? shell, cap;
    internal static void Reset()
    {
        if (shell != null) Object.Destroy(shell);
        if (cap != null) Object.Destroy(cap);
        shell = cap = null;
    }
    internal static void Build(GameObject root, Material material)
    {
        if (root.transform.Find(Name) != null) return;
        if (shell == null) shell = CreateMesh(false);
        if (cap == null) cap = CreateMesh(true);
        void Render(GameObject parent, Mesh mesh)
        {
            var obj = new GameObject(Name); obj.transform.SetParent(parent.transform, false);
            obj.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = obj.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.TwoSided; renderer.receiveShadows = true;
        }
        Render(root, shell);
        // Снимается вместе с прежней проверяемой физической заглушкой стыка.
        Render(root.transform.Find(MiniBossConnection.CapName).gameObject, cap);
    }
    private static Mesh CreateMesh(bool isCap)
    {
        var vertices = new List<Vector3>(); var uv = new List<Vector2>(); var triangles = new List<int>();
        foreach (var face in MiniBossShellPlan.Faces().Where(f => f.Cap == isCap))
        {
            int n = vertices.Count;
            foreach (var p in face.Points)
            {
                vertices.Add(new Vector3(p.X, p.Y, p.Z));
                bool horizontal = face.Points.All(v => v.Y == p.Y);
                bool alongZ = face.Points.All(v => v.X == p.X);
                uv.Add(horizontal ? new Vector2(p.X, p.Z) : new Vector2(alongZ ? p.Z : p.X, p.Y));
            }
            triangles.AddRange(new[] { n, n + 1, n + 2, n, n + 2, n + 3 });
        }
        var mesh = new Mesh { name = isCap ? "UberArenaShellCap_v1" : "UberArenaShell_v1" };
        mesh.SetVertices(vertices); mesh.SetUVs(0, uv); mesh.SetTriangles(triangles, 0);
        mesh.colors = Enumerable.Repeat(Color.white, vertices.Count).ToArray();
        mesh.RecalculateNormals(); mesh.RecalculateTangents(); mesh.RecalculateBounds();
        return mesh;
    }
}
