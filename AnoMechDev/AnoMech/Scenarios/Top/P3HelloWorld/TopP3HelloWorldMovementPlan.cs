using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game.Party;

namespace AnoMech.Scenarios.Top.P3HelloWorld;

public enum TopP3HelloWorldMovementPhase
{
    Route,
    ContactChase,
    ContactHold,
    ContactDepart,
    ContactStop,
}

public readonly record struct TopP3HelloWorldMovementCommand(
    PartyRole Role,
    float At,
    Vector3 Target,
    TopP3HelloWorldMovementPhase Phase)
{
    public float Speed => TopP3HelloWorldRules.MoveSpeed;
    public bool ContactOnly { get; init; }
    public int RequiredContactRound { get; init; }
}

public readonly record struct TopP3HelloWorldChaseWindow(
    PartyRole Receiver,
    PartyRole Source,
    float Start,
    float Stop,
    Vector3 DepartureTarget,
    int Round,
    bool Near);

/// <summary>Nominal pickup routes are replaced by actual contact-driven NPC phases.</summary>
public static class TopP3HelloWorldMovementPlan
{
    // 維護者 requested 0.5–1s after actual infection; use the midpoint, 0.75s.
    public const float NearContactHoldSeconds = 0.75f;

    // The tower snapshot follows the AOE by only 45ms in some rounds.
    public static float PickupAt(int round)
    {
        var times = TopP3HelloWorldRules.RoundTimes(round);
        return MathF.Max(times.AoeAt, times.TowerEffectAt) + 0.05f;
    }

    public static IReadOnlyList<TopP3HelloWorldMovementCommand> RouteCommandsFor(
        PartyRole playerRole, TopP3HelloWorldPattern? pattern = null)
    {
        pattern ??= new TopP3HelloWorldPattern(playerRole);
        return TopP3HelloWorldRules.RecordingOrder
            .Where(role => role != playerRole)
            .SelectMany(role => pattern.RuntimeRouteFor(role)
                .Select(point => new TopP3HelloWorldMovementCommand(
                    role, point.At, point.Target, TopP3HelloWorldMovementPhase.Route)
                {
                    ContactOnly = point.ContactOnly,
                    RequiredContactRound = point.RequiredContactRound,
                }))
            .ToArray();
    }

    public static IReadOnlyList<TopP3HelloWorldChaseWindow> ChaseWindowsFor(
        PartyRole playerRole, TopP3HelloWorldPattern? pattern = null)
    {
        pattern ??= new TopP3HelloWorldPattern(playerRole);
        return Enumerable.Range(1, 3)
            .SelectMany(round => pattern.ContactsForRound(round)
                .Where(contact => contact.Receiver != playerRole)
                .Select(contact =>
                {
                    var near = pattern.RoleGroup(
                        round, TopP3HelloWorldRole.LocalTether).Contains(contact.Receiver);
                    return new TopP3HelloWorldChaseWindow(
                        contact.Receiver, contact.Source,
                        TopP3HelloWorldRules.RuntimeAt(PickupAt(round)),
                        TopP3HelloWorldRules.TetherExpiryAt(round),
                        pattern.PostContactPositionFor(round, contact.Receiver), round, near);
                }))
            .ToArray();
    }
}
