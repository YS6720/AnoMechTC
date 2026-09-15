using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using AnoMech.Core.Game;
using AnoMech.Core.Native;
using AnoMech.Core.SimObjects;

namespace AnoMech.Scenarios.Recorded;

public partial class RecordedScenario
{
    private readonly record struct VisualTransform(float Time, Vector3 Position, Quaternion Rotation, Vector3 Scale);

    private void ScheduleVisualEvidence()
    {
        if (!IsV2 || timeline!.VisualEvidence.Count == 0) return;
        var scheduled = new HashSet<int>();
        var rejected = 0;
        foreach (var item in timeline.VisualEvidence)
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("replay", out var replay) || replay.ValueKind != JsonValueKind.True)
                continue;
            // Actor AVFX can free itself. Until an ownership-safe native API is verified,
            // neither fire-and-forget (lost lifetime) nor delayed Dtor is a valid replay.
            if (!TryGetString(item, "kind", out var kind) || kind != "svfx"
                || !item.TryGetProperty("vfxId", out var identity)
                || identity.ValueKind != JsonValueKind.Number || !identity.TryGetInt32(out var id) || id <= 0
                || !TryGetString(item, "path", out var path) || !IsRecordedVfxPath(path!)
                || !TryGetFloat(item, "t", out var start) || !TryGetFloat(item, "end", out var end)
                || end <= start || !TryReadVisualTransforms(item, start, end, out var frames))
            {
                rejected++;
                continue;
            }
            if (scheduled.Contains(id)) continue;
            if (!VfxFunctions.VfxPathExists(path!))
            {
                rejected++;
                continue;
            }
            scheduled.Add(id);
            QueueStaticVfx(path!, frames, end);
        }
        if (rejected != 0)
            Plugin.Log.Warning($"Recorded visualEvidence: {rejected} replay candidates rejected; actor ownership or static metadata/asset unavailable.");
    }

    private void QueueStaticVfx(string path, List<VisualTransform> frames, float end)
    {
        SimOmen? visual = null;
        var first = frames[0];
        // Creation alone supplies no placement. Start at the first observed native
        // transform, then apply every recorded update without inventing interpolation.
        world.Events.Add(At(first.Time), () =>
        {
            if (world.IsActive)
                visual = world.SpawnOmen(path, new Placement(first.Position, 0f), first.Scale, null, first.Rotation);
        });
        for (var i = 1; i < frames.Count; i++)
        {
            var frame = frames[i];
            world.Events.Add(At(frame.Time), () =>
                visual?.UpdateRecordedTransform(frame.Position, frame.Rotation, frame.Scale));
        }
        // Timeline time, not wall-clock time; pause/reset follows the existing owner.
        world.Events.Add(At(end), () => visual?.Despawn());
        RequireEndpoint(At(end));
    }

    private static bool TryReadVisualTransforms(JsonElement item, float start, float end,
        out List<VisualTransform> frames)
    {
        frames = [];
        if (!item.TryGetProperty("transforms", out var transforms)
            || transforms.ValueKind != JsonValueKind.Array || transforms.GetArrayLength() == 0)
            return false;
        var previous = start;
        Span<float> scale = stackalloc float[3];
        Span<float> rotation = stackalloc float[4];
        foreach (var transform in transforms.EnumerateArray())
        {
            if (transform.ValueKind != JsonValueKind.Object
                || !TryGetFloat(transform, "t", out var time) || time < previous || time >= end
                || !TryGetFloat(transform, "x", out var x) || !TryGetFloat(transform, "y", out var y)
                || !TryGetFloat(transform, "z", out var z)
                || !TryReadVisualVector(transform, "scale", scale)
                || !TryReadVisualVector(transform, "rotation", rotation))
                return false;
            var quaternion = new Quaternion(rotation[0], rotation[1], rotation[2], rotation[3]);
            if (!float.IsFinite(quaternion.LengthSquared()) || quaternion.LengthSquared() < 0.000001f)
                return false;
            frames.Add(new VisualTransform(time, new Vector3(x, y, z), quaternion,
                new Vector3(scale[0], scale[1], scale[2])));
            previous = time;
        }
        return true;
    }

    private static bool IsRecordedVfxPath(string path)
    {
        if (!(path.StartsWith("vfx/", StringComparison.OrdinalIgnoreCase)
              || path.StartsWith("bg/", StringComparison.OrdinalIgnoreCase)
              || path.StartsWith("bgcommon/", StringComparison.OrdinalIgnoreCase)
              || path.StartsWith("chara/", StringComparison.OrdinalIgnoreCase))
            || !path.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase)
            || path.Contains("//", StringComparison.Ordinal) || path.Contains("/../", StringComparison.Ordinal)
            || path.Contains("/./", StringComparison.Ordinal))
            return false;
        foreach (var c in path)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '/' or '.' or '-')) return false;
        return true;
    }

    private static bool TryReadVisualVector(JsonElement obj, string name, Span<float> values)
    {
        if (!obj.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() != values.Length) return false;
        var index = 0;
        foreach (var component in element.EnumerateArray())
        {
            if (!TryGetFloat(component, out values[index])) return false;
            index++;
        }
        return true;
    }

    private static bool TryGetString(JsonElement obj, string name, out string? value)
    {
        value = null;
        if (!obj.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetFloat(JsonElement obj, string name, out float value)
    {
        value = 0f;
        return obj.TryGetProperty(name, out var element) && TryGetFloat(element, out value);
    }

    private static bool TryGetFloat(JsonElement element, out float value)
    {
        value = 0f;
        return element.ValueKind == JsonValueKind.Number && element.TryGetSingle(out value) && float.IsFinite(value);
    }
}
