using System;
using AnoMech.Core.Game.Party;

namespace AnoMech.Scenarios.Top.P6AlphaOmega;

internal static class TopP6AutoAttackTargets
{
    // PositiveInfinity denotes an unoccupied/dead slot. Solo is an explicit
    // one-role drill, NOT a full party that has lost seven members.
    internal static (int First, int Farthest) Select(ReadOnlySpan<float> distances, bool solo, PartyRole playerRole)
    {
        if (solo)
        {
            var player = (int)playerRole;
            if (!float.IsFinite(distances[player])) return (-1, -1);
            return playerRole switch
            {
                PartyRole.MainTank => (player, -1),
                PartyRole.OffTank => (-1, player),
                _ => (-1, -1),
            };
        }

        var first = -1;
        var farthest = -1;
        var farthestDistance = float.NegativeInfinity;
        for (var i = 0; i < distances.Length; i++)
        {
            if (!float.IsFinite(distances[i])) continue;
            // Slots are ordered MT, ST, then non-tanks; MT owns first enmity by default.
            if (first < 0) first = i;
            if (distances[i] > farthestDistance)
            {
                farthestDistance = distances[i];
                farthest = i;
            }
        }
        // Deliberately retain duplicate targets in party mode: baiting both is a real failure.
        return (first, farthest);
    }
}
