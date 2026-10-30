using System;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace DungeonDepthPrototype;

internal static class MiniBossArenaTrial
{
    internal static Vector3 Create(Vector3 origin, int index, int seed, bool archer = false,
        Quaternion? rotation = null)
    {
        var encounter = new MiniBossEncounter { Seed = seed, Depth = 64, Trial = true, TrialIndex = index,
            Archer = archer, Name = MiniBossTrial.Name(seed), Prefab = index % 2 == 0 ? "Skeleton" : "Ghost" };
        var random = new System.Random(unchecked(seed ^ 0x27182818));
        encounter.Helpers = Enumerable.Range(0, archer ? 0 : MiniBossRules.TrialHelpers(index))
            .Select(_ => random.Next(2) == 0 ? "Skeleton" : "Ghost").ToArray();
        MiniBossArenaSection.Create(origin, rotation ?? Quaternion.identity, encounter,
            Guid.NewGuid().ToString("N"), 0);
        return new ArenaFrame(origin, rotation ?? Quaternion.identity).Point(new Vector3(0, .08f, -22));
    }
}
