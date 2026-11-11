using System;
using System.Collections.Generic;
using AnoMech.Core;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;

namespace AnoMech.Scenarios.Umad.P3BlackHole;

// The native head-marker I/O behind YAN(211). Markings has native dependencies, so this half
// stays out of the Tests build; its test double lives in AnoMechUmadP3BlackHoleSimFakes.cs.
public sealed partial class UmadP3BlackHoleAi
{
    private static void PlaceSigns(AiManager ai, float time, Dictionary<PartyRole, Sign> marks) =>
        ai.Automarker(time, () => marks);

    // Same lookup as HandPlacedSigns: the party member whose object id the sign sits on.
    private PartyRole? RoleWearing(Sign sign)
    {
        var marked = Markings.Get(sign).ObjectId;
        if (marked == 0) return null;
        foreach (var role in Enum.GetValues<PartyRole>())
            if (world.Party.Get(role) is { } member && member.GameObjectId.ObjectId == marked)
                return role;
        return null;
    }
}
