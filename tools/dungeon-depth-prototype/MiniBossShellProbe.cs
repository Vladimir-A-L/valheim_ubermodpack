using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace DungeonDepthPrototype;

// Проверяет именно отрисовываемые треугольники: физический raycast не обнаруживает дырки в декоре.
internal static class MiniBossShellProbe
{
    internal readonly struct Triangle
    {
        internal readonly Vector3 A, B, C;
        internal Triangle(Vector3 a, Vector3 b, Vector3 c) { A = a; B = b; C = c; }
        internal bool Hit(Vector3 origin, Vector3 direction)
        {
            var e1 = B - A; var e2 = C - A; var h = Vector3.Cross(direction, e2);
            float det = Vector3.Dot(e1, h);
            if (det <= 1e-7f) return false; // Учитывается отсечение невидимых обратных сторон.
            var s = origin - A; float u = Vector3.Dot(s, h) / det;
            if (u < -.0001f || u > 1.0001f) return false;
            var q = Vector3.Cross(s, e1); float v = Vector3.Dot(direction, q) / det;
            return v >= -.0001f && u + v <= 1.0001f && Vector3.Dot(e2, q) / det > .0001f;
        }
    }
    internal static object Connection(MiniBossArena arena, Room parent)
    {
        var triangles = new List<Triangle>();
        foreach (var filter in arena.GetComponentsInChildren<MeshFilter>()
                     .Concat(parent.GetComponentsInChildren<MeshFilter>()))
        {
            var renderer = filter.GetComponent<MeshRenderer>();
            if (renderer == null || !renderer.enabled || filter.sharedMesh == null) continue;
            var vertices = filter.sharedMesh.vertices.Select(v =>
                arena.transform.InverseTransformPoint(filter.transform.TransformPoint(v))).ToArray();
            var indices = filter.sharedMesh.triangles;
            for (int i = 0; i < indices.Length; i += 3)
                triangles.Add(new Triangle(vertices[indices[i]], vertices[indices[i + 1]], vertices[indices[i + 2]]));
        }
        int rays = 0, leaks = 0, otherExits = 0;
        var examples = new List<object>();
        foreach (float z in new[] { -28.75f, -28.4f, -28.1f, -28f, -27.9f, -27.6f, -27.25f })
        foreach (float x in new[] { -.7f, 0, .7f })
        foreach (float y in new[] { .05f, 1.6f, 2.3f })
        foreach (float along in new[] { -.2f, 0, .2f })
        foreach (var direction in new[] { new Vector3(1, 0, along), new Vector3(-1, 0, along),
                     new Vector3(0, 1, along), new Vector3(0, -1, along) })
        {
            rays++; var from = new Vector3(x, y, z);
            if (triangles.Any(t => t.Hit(from, direction.normalized))) continue;
            var worldFrom = arena.transform.TransformPoint(from);
            var worldDirection = arena.transform.TransformDirection(direction.normalized);
            bool throughOtherSocket = parent.GetConnections().Any(socket => {
                var normal = socket.transform.forward;
                float denominator = Vector3.Dot(worldDirection, normal);
                if (denominator >= -.001f) return false;
                float distance = Vector3.Dot(socket.transform.position - worldFrom, normal) / denominator;
                if (distance <= 0) return false;
                var point = socket.transform.InverseTransformPoint(worldFrom + worldDirection * distance);
                return Math.Abs(point.x) <= 1 && point.y >= 0 && point.y <= 2.4f;
            });
            if (throughOtherSocket) { otherExits++; continue; }
            leaks++;
            if (examples.Count < 12) examples.Add(new { x, y, z, direction = ForestFixture.V(direction) });
        }
        return new { passed = leaks == 0, rays, leaks, other_exits = otherExits, examples,
            triangles = triangles.Count };
    }

    internal static object Run(MiniBossArena arena)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var filters = arena.GetComponentsInChildren<MeshFilter>(true)
            .Where(f => f.name == MiniBossShell.Name).ToArray();
        var visible = new List<Triangle>(); var opening = new List<Triangle>();
        foreach (var filter in filters)
        {
            var vertices = filter.sharedMesh.vertices.Select(v =>
                arena.transform.InverseTransformPoint(filter.transform.TransformPoint(v))).ToArray();
            var indices = filter.sharedMesh.triangles;
            var target = filter.gameObject.activeInHierarchy ? visible : opening;
            for (int i = 0; i < indices.Length; i += 3)
                target.Add(new Triangle(vertices[indices[i]], vertices[indices[i + 1]], vertices[indices[i + 2]]));
        }
        var directions = new List<Vector3> { Vector3.up, Vector3.down };
        foreach (float y in new[] { -1f, -.3f, 0, .3f, 1f })
        for (int i = 0; i < 24; i++)
            directions.Add(new Vector3(Mathf.Cos(i * Mathf.PI / 12), y,
                Mathf.Sin(i * Mathf.PI / 12)).normalized);
        int rays = 0, leaks = 0, expectedExits = 0; var examples = new List<object>();
        for (float x = -2.75f; x < 31; x += 2)
        for (float z = -27.75f; z < 31; z += 2)
        {
            if (!MiniBossArenaPlan.FloorAt(x, z)) continue;
            foreach (float y in new[] { .05f, 1.6f, 5.95f })
            foreach (var direction in directions)
            {
                rays++; var from = new Vector3(x, y, z);
                if (visible.Any(t => t.Hit(from, direction))) continue;
                if (opening.Any(t => t.Hit(from, direction))) { expectedExits++; continue; }
                leaks++;
                if (examples.Count < 5) examples.Add(new { from = ForestFixture.V(from),
                    direction = ForestFixture.V(direction) });
            }
        }
        var materials = filters.Select(f => f.GetComponent<MeshRenderer>()).Select(r => new {
            r.enabled, shadow = r.shadowCastingMode.ToString(), shader = r.sharedMaterial.shader.name,
            queue = r.sharedMaterial.renderQueue, render_type = r.sharedMaterial.GetTag("RenderType", false),
            alpha_test = r.sharedMaterial.IsKeywordEnabled("_ALPHATEST_ON"),
            opaque = r.sharedMaterial.renderQueue <= 2500 &&
                r.sharedMaterial.GetTag("RenderType", false) != "Transparent" &&
                r.shadowCastingMode == ShadowCastingMode.TwoSided }).ToArray();
        var zone = arena.GetComponentInChildren<EnvZone>(); var box = zone.GetComponent<BoxCollider>();
        bool covered = MiniBossArenaPlan.ShellFloor.All(rect => new[] {
            new Vector3(rect.X0 + .05f, 1.6f, rect.Z0 + .05f),
            new Vector3(rect.X1 - .05f, 1.6f, rect.Z1 - .05f) }.All(p =>
                box.bounds.Contains(arena.transform.TransformPoint(p))));
        var env = EnvMan.instance.m_environments.Single(e => e.m_name == zone.m_environment);
        bool complete = filters.All(f => f.sharedMesh.normals.Length == f.sharedMesh.vertexCount &&
            f.sharedMesh.tangents.Length == f.sharedMesh.vertexCount &&
            f.sharedMesh.uv.Length == f.sharedMesh.vertexCount &&
            f.sharedMesh.colors.Length == f.sharedMesh.vertexCount);
        return new { rays, leaks, expected_exits = expectedExits, examples, triangles = visible.Count,
            materials, mesh_attributes_complete = complete, environment_covered = covered,
            environment = zone.m_environment,
            sunlight_day = env.m_lightIntensityDay, sunlight_night = env.m_lightIntensityNight,
            elapsed_ms = started.ElapsedMilliseconds,
            passed = filters.Length == 2 && visible.Count > 0 && leaks == 0 && covered && complete &&
                env.m_lightIntensityDay == 0 && env.m_lightIntensityNight == 0 &&
                materials.All(m => m.enabled && m.opaque && !m.alpha_test) };
    }
}
