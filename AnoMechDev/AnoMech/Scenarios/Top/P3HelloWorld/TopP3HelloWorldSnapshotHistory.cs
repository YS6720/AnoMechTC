using System;
using System.Collections.Generic;
using System.Numerics;
using AnoMech.Core.Game.Party;

namespace AnoMech.Scenarios.Top.P3HelloWorld;

/// <summary>
/// Scenario-owned position history used when one real frame crosses several
/// fixed events. It interpolates only between samples observed from the live
/// party; it never synthesizes a route for the player slot.
/// </summary>
public sealed class TopP3HelloWorldSnapshotHistory
{
    private readonly List<Sample> samples = [];

    public void Record(
        float at,
        IReadOnlyDictionary<PartyRole, Vector3> positions)
    {
        var copy = new Dictionary<PartyRole, Vector3>(positions);
        if (samples.Count > 0 && MathF.Abs(samples[^1].At - at) < 0.0001f)
            samples[^1] = new Sample(at, copy);
        else
        {
            // Scenario records once then Timeline consumes through this frame.
            // Only the preceding and current actual observations are needed.
            if (samples.Count == 2) samples.RemoveAt(0);
            samples.Add(new Sample(at, copy));
        }
    }

    public IReadOnlyDictionary<PartyRole, Vector3> At(float at)
    {
        if (samples.Count == 0) return new Dictionary<PartyRole, Vector3>();
        if (at <= samples[0].At) return Copy(samples[0].Positions);
        for (var index = 1; index < samples.Count; index++)
        {
            var before = samples[index - 1];
            var after = samples[index];
            if (at > after.At) continue;
            var fraction = after.At <= before.At
                ? 1f
                : Math.Clamp((at - before.At) / (after.At - before.At), 0f, 1f);
            var result = new Dictionary<PartyRole, Vector3>();
            foreach (var role in before.Positions.Keys)
            {
                if (!after.Positions.TryGetValue(role, out var end))
                    result[role] = before.Positions[role];
                else
                    result[role] = Vector3.Lerp(before.Positions[role], end, fraction);
            }
            foreach (var (role, position) in after.Positions)
                if (!result.ContainsKey(role)) result[role] = position;
            return result;
        }
        return Copy(samples[^1].Positions);
    }

    private static Dictionary<PartyRole, Vector3> Copy(
        IReadOnlyDictionary<PartyRole, Vector3> positions)
        => new(positions);

    private readonly record struct Sample(
        float At,
        IReadOnlyDictionary<PartyRole, Vector3> Positions);
}
