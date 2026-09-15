using System;
using System.Collections.Generic;

namespace AnoMech.Core.Game.Party;

// Pure display-only mapping. MainGroup slot identity stays with the source row;
// the returned index says which existing row coordinate that source row should use.
public static class PartyListDisplayOrderPlanner
{
    public const int RoleCount = 8;

    public static PartyRole[] CreateDefaultOrder() =>
    [
        PartyRole.MainTank,
        PartyRole.OffTank,
        PartyRole.RegenHealer,
        PartyRole.ShieldHealer,
        PartyRole.MeleeDpsA,
        PartyRole.MeleeDpsB,
        PartyRole.PhysRangedDps,
        PartyRole.CasterDps,
    ];

    public static bool IsValidOrder(IReadOnlyList<PartyRole>? order)
    {
        if (order is null || order.Count != RoleCount) return false;

        var seen = 0;
        for (var i = 0; i < order.Count; i++)
        {
            var value = (int)order[i];
            if (value < 0 || value >= RoleCount) return false;
            var bit = 1 << value;
            if ((seen & bit) != 0) return false;
            seen |= bit;
        }

        return seen == (1 << RoleCount) - 1;
    }

    public static bool TryBuildSourceToTargetRows(
        IReadOnlyList<PartyRole?>? slotRoles,
        IReadOnlyList<PartyRole>? displayOrder,
        int[]? sourceToTarget)
    {
        if (sourceToTarget is null || sourceToTarget.Length < RoleCount)
            return false;
        Array.Fill(sourceToTarget, -1);
        if (slotRoles is null || slotRoles.Count != RoleCount
            || !IsValidOrder(displayOrder))
            return false;

        var seen = 0;
        for (var sourceSlot = 0; sourceSlot < RoleCount; sourceSlot++)
        {
            if (slotRoles[sourceSlot] is not { } role) return false;
            var roleValue = (int)role;
            if (roleValue < 0 || roleValue >= RoleCount) return false;

            var roleBit = 1 << roleValue;
            if ((seen & roleBit) != 0) return false;
            seen |= roleBit;

            var targetRow = -1;
            for (var i = 0; i < displayOrder!.Count; i++)
            {
                if (displayOrder[i] != role) continue;
                targetRow = i;
                break;
            }

            if (targetRow < 0) return false;
            sourceToTarget[sourceSlot] = targetRow;
        }

        return seen == (1 << RoleCount) - 1;
    }
}

public readonly record struct PartyListRowPosition(float X, float Y);

// Lifecycle state is kept separate from native pointers so its generation rules
// can be tested without calling the game. A new generation discards the old
// baseline; only the still-current generation may restore it.
public sealed class PartyListDisplayOrderLifecyclePlanner
{
    private readonly PartyListRowPosition[] original = new PartyListRowPosition[PartyListDisplayOrderPlanner.RoleCount];
    private bool captured;
    private long generation;

    public bool Capture(long generation, IReadOnlyList<PartyListRowPosition>? positions)
    {
        if (positions is null || positions.Count != PartyListDisplayOrderPlanner.RoleCount)
            return false;
        if (captured && this.generation == generation) return true;

        for (var i = 0; i < original.Length; i++) original[i] = positions[i];
        this.generation = generation;
        captured = true;
        return true;
    }

    public bool TryArrange(
        long generation,
        IReadOnlyList<PartyListRowPosition>? current,
        IReadOnlyList<PartyRole?>? slotRoles,
        IReadOnlyList<PartyRole>? displayOrder,
        PartyListRowPosition[]? arranged,
        int[]? sourceToTarget)
    {
        if (!captured || this.generation != generation
            || current is null || current.Count != original.Length
            || arranged is null || arranged.Length < original.Length
            || !PartyListDisplayOrderPlanner.TryBuildSourceToTargetRows(
                slotRoles, displayOrder, sourceToTarget))
            return false;

        for (var sourceSlot = 0; sourceSlot < original.Length; sourceSlot++)
            arranged[sourceSlot] = original[sourceToTarget![sourceSlot]];
        return true;
    }

    public bool TryRestore(
        long generation,
        IReadOnlyList<PartyListRowPosition>? current,
        out PartyListRowPosition[] restored)
    {
        restored = [];
        if (!captured || this.generation != generation
            || current is null || current.Count != original.Length)
            return false;

        restored = (PartyListRowPosition[])original.Clone();
        return true;
    }

    public void Discard()
    {
        captured = false;
        generation = 0;
    }
}
