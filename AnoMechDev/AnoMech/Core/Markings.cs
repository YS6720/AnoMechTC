using System;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace AnoMech.Core;

// Client-side party-sign writer. Bypasses the network path (the canonical
// MarkingController.MarkObject member function broadcasts to the party); we
// just stamp the GameObjectId directly into _markers, which is what the
// nameplate renderer reads each frame. Empty/cleared slots are 0.
//
// Lemegeton drives signs by sig-scanning the same member function; for a
// single-player simulator we don't need network broadcast, so direct writes
// (mirroring Waymarks) are simpler and have no side effects.
internal static unsafe class Markings
{
    private const int SlotCount = 17;

    // Framework-thread view; callers must not retain it across ticks.
    public static Span<ulong> GetSlots()
    {
        var ctrl = MarkingController.Instance();
        return ctrl == null ? Span<ulong>.Empty : MemoryMarshal.Cast<GameObjectId, ulong>(ctrl->Markers);
    }

    public static void Set(Sign sign, GameObjectId target)
    {
        var idx = (int)sign;
        if (idx < 0 || idx >= SlotCount) return;
        var ctrl = MarkingController.Instance();
        if (ctrl == null) return;
        ctrl->Markers[idx] = target;
    }

    public static GameObjectId Get(Sign sign)
    {
        var idx = (int)sign;
        if (idx < 0 || idx >= SlotCount) return default;
        var ctrl = MarkingController.Instance();
        if (ctrl == null) return default;
        return ctrl->Markers[idx];
    }


    public static void Clear(Sign sign)
    {
        var idx = (int)sign;
        if (idx < 0 || idx >= SlotCount) return;
        var ctrl = MarkingController.Instance();
        if (ctrl == null) return;
        ctrl->Markers[idx] = default;
    }

    public static void ClearAll()
    {
        var ctrl = MarkingController.Instance();
        if (ctrl == null) return;
        for (int i = 0; i < SlotCount; i++)
            ctrl->Markers[i] = default;
    }
}
