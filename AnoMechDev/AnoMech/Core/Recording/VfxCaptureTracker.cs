using System;
using System.Collections.Generic;
using System.Numerics;

namespace AnoMech.Core.Recording;

internal enum RecordedVfxKind : byte
{
    Actor,
    Static,
}

internal readonly record struct TrackedVfx(int Id, RecordedVfxKind Kind);

internal readonly record struct VfxTransform(Vector3 Position, Quaternion Rotation, Vector3 Scale);

/// <summary>
/// Keeps only native addresses as lookup keys. The address is never retained as a pointer or
/// dereferenced outside the native callback that supplied it; output uses the session-local Id.
/// </summary>
internal sealed class VfxCaptureTracker
{
    // A recording can run for an entire duty. Keep a bad/missing native lifetime callback from
    // growing this lookup without bound; the recorder emits a vfx-gap when a start is omitted.
    public const int MaxTrackedEntries = 4096;

    private readonly Dictionary<(RecordedVfxKind Kind, nint Address), TrackedVfx> entries = new();
    private readonly Dictionary<int, VfxTransform> lastTransforms = new();
    private int nextId;

    public int Count => entries.Count;

    public void Reset()
    {
        entries.Clear();
        lastTransforms.Clear();
        nextId = 0;
    }

    public bool TryStart(RecordedVfxKind kind, nint address, out TrackedVfx started, out TrackedVfx replaced)
    {
        started = default;
        replaced = default;
        if (address == nint.Zero) return false;
        if (nextId == int.MaxValue) return false;

        var key = (kind, address);
        if (entries.TryGetValue(key, out replaced))
        {
            // Native pools can reuse an address when a missed lifetime callback leaves an
            // old key behind. Never let the new object inherit the old session id.
            lastTransforms.Remove(replaced.Id);
        }
        else if (entries.Count >= MaxTrackedEntries)
        {
            return false;
        }

        started = new TrackedVfx(++nextId, kind);
        entries[key] = started;
        return true;
    }

    public bool TryFind(RecordedVfxKind kind, nint address, out TrackedVfx tracked)
        => entries.TryGetValue((kind, address), out tracked);

    public bool TryTransform(RecordedVfxKind kind, nint address, VfxTransform transform,
                             out TrackedVfx tracked, out bool changed)
    {
        changed = false;
        if (!entries.TryGetValue((kind, address), out tracked)) return false;

        if (!lastTransforms.TryGetValue(tracked.Id, out var previous) || previous != transform)
        {
            lastTransforms[tracked.Id] = transform;
            changed = true;
        }

        return true;
    }

    public bool TryEnd(RecordedVfxKind kind, nint address, out TrackedVfx ended)
    {
        var key = (kind, address);
        if (!entries.TryGetValue(key, out ended)) return false;
        entries.Remove(key);
        lastTransforms.Remove(ended.Id);
        return true;
    }
}
