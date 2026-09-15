using System;
using System.Collections.Generic;
using AnoMech.Core;
using AnoMech.Core.Game.Party;

namespace AnoMech.Multiplayer;

// Native IDs stay local. Only resolved party roles leave this per-run input boundary.
public sealed class PartyMarkerSync
{
    private readonly PartyRole?[] baseline = new PartyRole?[MpLimits.Markers];

    public IReadOnlyList<PartyMarkerRequestMessage> Capture(ReadOnlySpan<ulong> signs, ReadOnlySpan<ulong> party)
    {
        List<PartyMarkerRequestMessage>? changed = null;
        for (var index = 0; index < MpLimits.Markers; index++)
        {
            var role = Resolve(signs[index], party);
            var previous = baseline[index];
            baseline[index] = role;
            if (role == previous) continue;
            (changed ??= []).Add(new PartyMarkerRequestMessage((Sign)index, role));
        }
        return changed is null ? Array.Empty<PartyMarkerRequestMessage>() : changed;
    }

    public void Reconcile(Span<ulong> signs, ReadOnlySpan<ulong> party, ReadOnlySpan<PartyMarkerState> authority)
    {
        Span<PartyRole?> next = stackalloc PartyRole?[MpLimits.Markers];
        next.Clear();
        foreach (var marker in authority)
        {
            if ((uint)party[(int)marker.Role] == 0) throw new MpProtocolException(MpError.RoleRequired);
            next[(int)marker.Sign] = marker.Role;
        }
        for (var index = 0; index < MpLimits.Markers; index++)
        {
            if (next[index] is { } role)
                signs[index] = party[(int)role];
            else if (Resolve(signs[index], party) != null)
                signs[index] = 0;
            baseline[index] = next[index];
        }
    }

    public static void Apply(Span<ulong> signs, ReadOnlySpan<ulong> party, PartyMarkerRequestMessage marker)
    {
        if (!MpValidation.Validate(marker)) throw new MpProtocolException(MpError.InvalidMessage);
        if (marker.Role is not { } role)
        {
            if (Resolve(signs[(int)marker.Sign], party) != null)
                signs[(int)marker.Sign] = 0;
            return;
        }
        var target = party[(int)role];
        var targetId = (uint)target;
        if (targetId == 0) throw new MpProtocolException(MpError.RoleRequired);
        for (var index = 0; index < MpLimits.Markers; index++)
            if ((uint)signs[index] == targetId)
                signs[index] = 0;
        signs[(int)marker.Sign] = target;
    }

    public static PartyMarkerState[] Snapshot(ReadOnlySpan<ulong> signs, ReadOnlySpan<ulong> party)
    {
        List<PartyMarkerState>? result = null;
        for (var index = 0; index < MpLimits.Markers; index++)
            if (Resolve(signs[index], party) is { } role)
                (result ??= []).Add(new PartyMarkerState(role, (Sign)index));
        return result?.ToArray() ?? Array.Empty<PartyMarkerState>();
    }

    private static PartyRole? Resolve(ulong target, ReadOnlySpan<ulong> party)
    {
        // Match the existing HandPlacedSigns and WorldReplicator ObjectId convention.
        var objectId = (uint)target;
        if (objectId == 0) return null;
        for (var index = 0; index < party.Length; index++)
            if ((uint)party[index] == objectId)
                return (PartyRole)index;
        return null;
    }
}
