using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game.Party;

namespace AnoMech.Scenarios.Top.P3Monitors;

public enum TopP3BossSide
{
    Right,
    Left,
}

public enum TopP3MonitorStatus
{
    Right,
    Left,
}

public enum TopP3MonitorSlot
{
    M1,
    M2,
    M3,
}

public enum TopP3HitResult
{
    Missing,
    Correct,
    Overlap,
}

public readonly record struct TopP3MonitorPosition(PartyRole Role, Vector3 Position);

public readonly record struct TopP3MonitorCircleSnapshot(
    Vector3 Center,
    IReadOnlyList<PartyRole> HitRoles);

public readonly record struct TopP3HitCount(PartyRole Role, int Count);

public readonly record struct TopP3MonitorMove(
    PartyRole Role,
    Vector3 Target,
    float? FinalRotation);

public sealed class TopP3MonitorAssignment
{
    public IReadOnlyList<PartyRole> MonitorRoles { get; }
    public IReadOnlyList<PartyRole> NormalRoles { get; }

    internal TopP3MonitorAssignment(PartyRole[] monitorRoles, PartyRole[] normalRoles)
    {
        MonitorRoles = monitorRoles;
        NormalRoles = normalRoles;
    }

    public bool IsMonitor(PartyRole role) => MonitorRoles.Contains(role);

    // M1..M3 are slots 0..2; N1..N5 are slots 3..7.
    public int SlotOf(PartyRole role)
    {
        for (var i = 0; i < MonitorRoles.Count; i++)
            if (MonitorRoles[i] == role) return i;
        for (var i = 0; i < NormalRoles.Count; i++)
            if (NormalRoles[i] == role) return i + 3;
        return -1;
    }
}

/// <summary>
/// Pure P3 monitor assignment, geometry, facing, and hit-count rules.
/// The production scene supplies live positions and rotations; this file deliberately
/// has no Dalamud dependency so the same rules are linked into Tests.
/// </summary>
public static class TopP3MonitorRules
{
    // Deliberately not PartyRole enum order: this is the TOP P3 priority order.
    public static readonly IReadOnlyList<PartyRole> Priority =
    [
        PartyRole.RegenHealer,
        PartyRole.MainTank,
        PartyRole.OffTank,
        PartyRole.MeleeDpsA,
        PartyRole.MeleeDpsB,
        PartyRole.PhysRangedDps,
        PartyRole.CasterDps,
        PartyRole.ShieldHealer,
    ];

    public const float CircleRadius = 7f;
    public const float ArenaRadius = 20f;

    // Recorded P3 preparation/cast timeline. All offsets are scenario time from Run.
    public const float QueueAt = 0.1f;
    public const float QueueToBuff = 5f;
    public const float BuffAt = QueueAt + QueueToBuff;
    public const float CastStartAfterBuff = 0.082f;
    public const float CastStartAt = BuffAt + CastStartAfterBuff;
    public const float MoveStartAfterBuff = 0.25f;
    public const float MoveStartAt = BuffAt + MoveStartAfterBuff;
    public const float MoveSpeed = 4f;
    public const float CastSeconds = 9.7f;
    public const float EffectAfterCast = 9.971f;
    public const float StatusRemoveAfterBuff = 10.090f;
    public const float CircleAfterBuff = 10.145f;
    public const float StatusRemoveAt = BuffAt + StatusRemoveAfterBuff;
    public const float CircleAt = BuffAt + CircleAfterBuff;

    // X=east, Z=south. BossSide.Left mirrors X only.
    private static readonly Vector3[] SlotPositions =
    [
        new(-9f, 0f, -14f),
        new(-15f, 0f, -5f),
        new(-15f, 0f, 5f),
    ];

    private static readonly Vector3[] SlotNormals =
    [
        new(-1f, 0f, 0f),
        new(0f, 0f, -1f),
        new(0f, 0f, 1f),
    ];

    private static readonly Vector3[] NormalSlotPositions =
    [
        new(-1f, 0f, -16f),
        new(3f, 0f, 0f),
        new(13f, 0f, 0f),
        new(-2f, 0f, 11f),
        new(-2f, 0f, 19f),
    ];

    private static readonly Vector3[] QueueSlotPositions =
    [
        new(-15f, 0f, -10.5f),
        new(-15f, 0f, -7.5f),
        new(-15f, 0f, -4.5f),
        new(-15f, 0f, -1.5f),
        new(-15f, 0f, 1.5f),
        new(-15f, 0f, 4.5f),
        new(-15f, 0f, 7.5f),
        new(-15f, 0f, 10.5f),
    ];

    public static readonly string[] RoleSelectorLabels =
        ["自動", "H1", "MT", "ST", "D1", "D2", "D3", "D4", "H2"];

    public static TopP3MonitorAssignment Assign(IReadOnlyCollection<PartyRole> monitorRoles)
    {
        var selected = monitorRoles.ToArray();
        if (selected.Length != 3 || selected.Distinct().Count() != 3)
            throw new ArgumentException("P3 requires exactly three distinct monitor roles.", nameof(monitorRoles));

        var monitors = Priority.Where(monitorRoles.Contains).ToArray();
        var normal = Priority.Where(role => !monitorRoles.Contains(role)).ToArray();
        if (monitors.Length != 3 || normal.Length != 5)
            throw new ArgumentException("P3 monitor roles must come from the eight party roles.", nameof(monitorRoles));
        return new TopP3MonitorAssignment(monitors, normal);
    }

    public static int PriorityIndex(PartyRole role)
    {
        for (var i = 0; i < Priority.Count; i++)
            if (Priority[i] == role) return i;
        return -1;
    }

    public static string RoleLabel(PartyRole role) => role switch
    {
        PartyRole.RegenHealer => "H1",
        PartyRole.MainTank => "MT",
        PartyRole.OffTank => "ST",
        PartyRole.MeleeDpsA => "D1",
        PartyRole.MeleeDpsB => "D2",
        PartyRole.PhysRangedDps => "D3",
        PartyRole.CasterDps => "D4",
        PartyRole.ShieldHealer => "H2",
        _ => role.ToString(),
    };

    public static string PriorityLabel => string.Join(
        " > ", Priority.Select(RoleLabel));

    public static PartyRole? RoleForSelectorIndex(int index)
    {
        if (index == 0) return null;
        if (index < 0 || index > Priority.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return Priority[index - 1];
    }

    public static int SelectorIndex(PartyRole? role)
        => role is { } value ? PriorityIndex(value) + 1 : 0;

    public static Vector3 QueuePositionFor(PartyRole role)
    {
        var index = PriorityIndex(role);
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(role));
        return QueueSlotPositions[index];
    }

    public static IReadOnlyList<TopP3MonitorMove> PlanQueueMoves(PartyRole localRole)
        => Priority
            .Where(role => role != localRole)
            .Select(role => new TopP3MonitorMove(role, QueuePositionFor(role), null))
            .ToArray();

    public static Vector3 PositionFor(TopP3MonitorSlot slot, TopP3BossSide bossSide)
    {
        var position = SlotPositions[(int)slot];
        return bossSide == TopP3BossSide.Left
            ? new Vector3(-position.X, position.Y, position.Z)
            : position;
    }

    public static Vector3 NormalFor(TopP3MonitorSlot slot, TopP3BossSide bossSide)
    {
        var normal = SlotNormals[(int)slot];
        return bossSide == TopP3BossSide.Left
            ? new Vector3(-normal.X, normal.Y, normal.Z)
            : normal;
    }

    public static Vector3 NormalPositionFor(int normalSlot, TopP3BossSide bossSide)
    {
        if (normalSlot is < 0 or >= 5)
            throw new ArgumentOutOfRangeException(nameof(normalSlot));
        var position = NormalSlotPositions[normalSlot];
        return bossSide == TopP3BossSide.Left
            ? new Vector3(-position.X, position.Y, position.Z)
            : position;
    }

    // The status says whether the screen normal is on the actor's right or left.
    // Solve actor rotation from that normal instead of mirroring a raw rotation.
    public static float FacingFor(
        TopP3MonitorSlot slot,
        TopP3BossSide bossSide,
        TopP3MonitorStatus status)
    {
        var normal = NormalFor(slot, bossSide);
        var normalAngle = MathF.Atan2(normal.X, normal.Z);
        var quarterTurn = status == TopP3MonitorStatus.Right ? MathF.PI / 2f : -MathF.PI / 2f;
        return NormalizeRotation(normalAngle + quarterTurn);
    }

    // CharacterFind.OnSideN's side convention: -1 selects a positive right-vector
    // dot product, +1 selects a negative one. Keep this mapping shared with the scene.
    public static int SideMultiplier(TopP3MonitorStatus status)
        => status == TopP3MonitorStatus.Right ? -1 : 1;

    public static int BossSideMultiplier(TopP3BossSide side)
        => side == TopP3BossSide.Right ? -1 : 1;

    public static IReadOnlyList<TopP3MonitorMove> PlanMoves(
        TopP3MonitorAssignment assignment,
        TopP3BossSide bossSide,
        IReadOnlyDictionary<PartyRole, TopP3MonitorStatus> statuses,
        PartyRole localRole)
    {
        var moves = new List<TopP3MonitorMove>(7);
        foreach (var role in Priority)
        {
            if (role == localRole) continue;
            var slot = assignment.SlotOf(role);
            if (slot < 0) continue;

            var target = slot < 3
                ? PositionFor((TopP3MonitorSlot)slot, bossSide)
                : NormalPositionFor(slot - 3, bossSide);
            float? finalRotation = null;
            if (slot < 3)
            {
                if (!statuses.TryGetValue(role, out var status))
                    throw new ArgumentException($"Missing status for monitor role {role}.", nameof(statuses));
                finalRotation = FacingFor((TopP3MonitorSlot)slot, bossSide, status);
            }
            moves.Add(new TopP3MonitorMove(role, target, finalRotation));
        }
        return moves;
    }

    public static IReadOnlyList<PartyRole> MembersInsideCircle(
        IReadOnlyList<TopP3MonitorPosition> members,
        Vector3 center,
        float radius = CircleRadius)
        => members
            .Where(member => IsInsideCircle(member.Position, center, radius))
            .Select(member => member.Role)
            .ToArray();

    public static bool IsInsideCircle(Vector3 position, Vector3 center, float radius = CircleRadius)
    {
        var dx = position.X - center.X;
        var dz = position.Z - center.Z;
        return dx * dx + dz * dz <= radius * radius;
    }

    public static bool IsInsideArena(Vector3 position, float radius = ArenaRadius)
        => position.X * position.X + position.Z * position.Z <= radius * radius;

    // Count every circle independently. In particular, two overlapping circles produce
    // Count=2; do not collapse the result into a HashSet hit/not-hit flag.
    public static IReadOnlyList<TopP3HitCount> CountHits(
        IReadOnlyList<TopP3MonitorCircleSnapshot> circles)
    {
        var counts = Priority.ToDictionary(role => role, _ => 0);
        foreach (var circle in circles)
            foreach (var role in circle.HitRoles)
                if (counts.ContainsKey(role)) counts[role]++;

        return Priority.Select(role => new TopP3HitCount(role, counts[role])).ToArray();
    }

    public static TopP3HitResult ResultFor(int hitCount) => hitCount switch
    {
        1 => TopP3HitResult.Correct,
        0 => TopP3HitResult.Missing,
        _ => TopP3HitResult.Overlap,
    };

    private static float NormalizeRotation(float radians)
    {
        radians %= MathF.Tau;
        if (radians <= -MathF.PI) radians += MathF.Tau;
        if (radians > MathF.PI) radians -= MathF.Tau;
        return radians;
    }
}
