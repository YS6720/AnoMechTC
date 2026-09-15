using System;
using System.Collections.Generic;
using System.Linq;
using AnoMech.Core;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;

namespace AnoMech.Scenarios.Top;

// Rebuilds a strategy's slot order from party signs placed by hand. Missing,
// stale, or non-party signs leave the strategy fallback untouched.
internal static class HandPlacedSigns
{
    public static RoleList Reorder(
        SimParty party,
        RoleList fallback,
        IReadOnlyList<(Sign Sign, int Slot)> plan,
        IReadOnlyCollection<PartyRole> keepInPlace)
    {
        var order = fallback.List.ToList();
        foreach (var (sign, slot) in plan)
        {
            if (RoleWearing(party, sign) is not { } role) continue;
            if (keepInPlace.Contains(role)) continue;
            var current = order.IndexOf(role);
            if (current < 0 || current == slot || slot < 0 || slot >= order.Count) continue;
            (order[slot], order[current]) = (order[current], order[slot]);
        }

        return new RoleList(party, order);
    }

    private static PartyRole? RoleWearing(SimParty party, Sign sign)
    {
        var marked = Markings.Get(sign).ObjectId;
        if (marked == 0) return null;
        foreach (var role in Enum.GetValues<PartyRole>())
            if (party.Get(role) is { } member && member.GameObjectId.ObjectId == marked)
                return role;
        return null;
    }
}
