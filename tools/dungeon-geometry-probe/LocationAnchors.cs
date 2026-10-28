using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonGeometryProbe;

internal static class LocationAnchors
{
    internal sealed class Anchor
    {
        public string location = "";
        public bool custom;
        public float[] position = Array.Empty<float>();
        public float[] rotation = Array.Empty<float>();
        public float[] original = Array.Empty<float>();
        internal Vector3 Position => new Vector3(position[0], position[1], position[2]);
        internal Quaternion Rotation => new Quaternion(rotation[0], rotation[1], rotation[2], rotation[3]);
        internal Vector3 Original => new Vector3(original[0], original[1], original[2]);
    }

    internal static Dictionary<string, Anchor> Read()
    {
        var result = new Dictionary<string, Anchor>();
        foreach (var entry in ZoneSystem.instance.m_locations)
        {
            if (!entry.m_enable) continue;
            entry.m_prefab.Load();
            try
            {
                var asset = entry.m_prefab.Asset;
                var location = asset.GetComponent<Location>();
                if (location == null) continue;
                foreach (var generator in asset.GetComponentsInChildren<DungeonGenerator>(true))
                {
                    string name = generator.name;
                    if (result.ContainsKey(name)) continue;
                    Vector3 original = Vector3.zero;
                    Vector3 p;
                    Quaternion r;
                    bool custom = location.m_useCustomInteriorTransform && location.m_interiorTransform != null &&
                        location.m_generator == generator;
                    if (custom)
                    {
                        // Штатные смещения ZoneSystem.SpawnLocation внутри зарезервированной зоны.
                        original = generator.transform.localPosition;
                        p = location.m_interiorTransform!.localPosition + original;
                        p.y = 5000 + original.y;
                        r = generator.transform.localRotation;
                    }
                    else
                    {
                        p = asset.transform.InverseTransformPoint(generator.transform.position);
                        p.y = 5000;
                        r = Quaternion.Inverse(asset.transform.rotation) * generator.transform.rotation;
                    }
                    result.Add(name, new Anchor { location = entry.m_prefabName, custom = custom,
                        position = new[] { p.x, p.y, p.z }, rotation = new[] { r.x, r.y, r.z, r.w },
                        original = new[] { original.x, original.y, original.z } });
                }
            }
            finally { entry.m_prefab.Release(); }
        }
        return result;
    }
}
