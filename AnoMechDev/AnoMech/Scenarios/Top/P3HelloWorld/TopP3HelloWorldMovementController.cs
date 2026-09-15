using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game.Party;

namespace AnoMech.Scenarios.Top.P3HelloWorld;

/// <summary>
/// Pure phase authority. Advance uses current time/positions after state has
/// processed contacts. It never integrates or rewinds movement to rescue a
/// missed deadline. Each NPC gets at most one destination per observed tick.
/// </summary>
public sealed class TopP3HelloWorldMovementController
{
    private const float ArrivalTolerance = 0.1f;
    private readonly TopP3HelloWorldState state;
    private readonly Dictionary<PartyRole, Actor> actors;
    private float lastAt = float.NegativeInfinity;

    public TopP3HelloWorldMovementController(TopP3HelloWorldState state)
    {
        this.state = state;
        var routes = TopP3HelloWorldMovementPlan.RouteCommandsFor(state.PlayerRole, state.Pattern);
        var windows = TopP3HelloWorldMovementPlan.ChaseWindowsFor(state.PlayerRole, state.Pattern);
        actors = TopP3HelloWorldRules.RecordingOrder.Where(role => role != state.PlayerRole)
            .ToDictionary(role => role, role => new Actor(
                routes.Where(command => command.Role == role).OrderBy(command => command.At).ToArray(),
                windows.Where(window => window.Receiver == role).OrderBy(window => window.Start).ToArray()));
    }

    public IReadOnlyList<TopP3HelloWorldMovementCommand> Advance(
        float now, IReadOnlyDictionary<PartyRole, Vector3> positions)
    {
        var result = new List<TopP3HelloWorldMovementCommand>();
        if (now <= lastAt || state.Completed) return result;
        lastAt = now;
        foreach (var role in TopP3HelloWorldRules.RecordingOrder)
        {
            if (!actors.TryGetValue(role, out var actor)) continue;
            if (state.Failed)
            {
                // Lock new movement and cancel an already issued chase once.
                if (actor.ChaseIssued && state.Members[role].Alive
                    && positions.TryGetValue(role, out var stopped))
                    result.Add(new(role, now, stopped,
                        TopP3HelloWorldMovementPhase.ContactStop));
                actor.Chase = null;
                actor.ChaseIssued = false;
                continue;
            }
            // Nominal pickup input is not an NPC command. Waiting-lane routes
            // remain eligible after the actual hold and break movement finish.
            while (actor.RouteIndex < actor.Routes.Length && actor.Routes[actor.RouteIndex].At <= now)
            {
                var route = actor.Routes[actor.RouteIndex++];
                if (!route.ContactOnly) actor.Pending = route;
            }

            while (actor.WindowIndex < actor.Windows.Length && actor.Windows[actor.WindowIndex].Start <= now)
            {
                actor.Chase = actor.Windows[actor.WindowIndex++];
                actor.ChaseIssued = false;
                actor.Departing = null;
                actor.HoldUntil = null;
                actor.HoldIssued = false;
                if (actor.Pending is { } old && old.At < actor.Chase.Value.Start) actor.Pending = null;
            }
            if (!state.Members[role].Alive || !positions.TryGetValue(role, out var position)) continue;
            if (actor.Chase is { } window)
            {
                var infected = state.ResolvedContacts.Contains((window.Round, role, window.Source));
                if (now < window.Stop && infected)
                {
                    if (window.Near)
                    {
                        actor.HoldUntil ??= state.Members[role].ColorExpiresAt
                            - TopP3HelloWorldRules.ColorDuration
                            + TopP3HelloWorldMovementPlan.NearContactHoldSeconds;
                        if (now < actor.HoldUntil.Value)
                        {
                            if (actor.ChaseIssued || !actor.HoldIssued)
                                result.Add(new(role, now, position,
                                    TopP3HelloWorldMovementPhase.ContactHold));
                            actor.ChaseIssued = false;
                            actor.HoldIssued = true;
                            continue;
                        }
                    }
                    var destination = window.DepartureTarget;
                    if (window.Near)
                    {
                        var owners = state.Pattern.RoleGroup(
                            window.Round, TopP3HelloWorldRole.Defamation);
                        if (!positions.TryGetValue(owners[0], out var first)
                            || !positions.TryGetValue(owners[1], out var second)
                            || !state.Members[owners[0]].Alive || !state.Members[owners[1]].Alive)
                        {
                            actor.Chase = null;
                            actor.ChaseIssued = false;
                            result.Add(new(role, now, position,
                                TopP3HelloWorldMovementPhase.ContactStop));
                            continue;
                        }
                        destination = (first + second) * 0.5f;
                    }
                    actor.Chase = null;
                    actor.ChaseIssued = false;
                    actor.Departing = destination;
                    result.Add(new(role, now, destination,
                        TopP3HelloWorldMovementPhase.ContactDepart));
                    continue;
                }
                if (CanChase(window, now, positions))
                {
                    actor.ChaseIssued = true;
                    // Stop just inside the contact band, not at the source's
                    // center: a coarse frame must not stretch a far line early.
                    var source = positions[window.Source];
                    var approach = position - source;
                    var approachTarget = approach.LengthSquared() > 0f
                        ? source + Vector3.Normalize(approach) * (state.ContactRadius - 0.1f)
                        : position;
                    result.Add(new(role, now, approachTarget,
                        TopP3HelloWorldMovementPhase.ContactChase));
                    continue;
                }
                actor.Chase = null;
                actor.ChaseIssued = false;
                // A skipped window must not replace a later round's route.
                // Missing contact only cancels pursuit; it never grants a
                // nominal success or sends an uninfected actor to the break.
                if (actor.Pending is not { } later || later.At <= window.Stop)
                {
                    result.Add(new(role, now, position,
                        TopP3HelloWorldMovementPhase.ContactStop));
                    continue;
                }
            }
            if (actor.Departing is { } target)
            {
                if (TopP3HelloWorldRules.DistanceXZ(position, target) > ArrivalTolerance) continue;
                actor.Departing = null;
            }
            if (actor.Pending is { } next)
            {
                var member = state.Members[role];
                if (next.RequiredContactRound > 0
                    && member.ColorRound != next.RequiredContactRound)
                {
                    actor.Pending = null;
                    continue;
                }
                if (next.RequiredContactRound > 0)
                {
                    var kind = state.Pattern.RoleGroup(next.RequiredContactRound,
                        TopP3HelloWorldRole.LocalTether).Contains(role)
                        ? TopP3HelloWorldRole.LocalTether : TopP3HelloWorldRole.RemoteTether;
                    if (!state.IsTetherResolved(next.RequiredContactRound, kind)) continue;
                }
                // An outgoing source stays outside until its actual buff
                // expires, even if the player delivered its poison late.
                if (member.Color != TopP3HelloWorldColor.None
                    && !member.ActiveDefamation && !member.ActiveStack)
                    continue;
                actor.Pending = null;
                result.Add(next with { At = now });
            }
        }
        return result;
    }

    private bool CanChase(TopP3HelloWorldChaseWindow window, float now,
        IReadOnlyDictionary<PartyRole, Vector3> positions)
        => now < window.Stop
           && state.Members[window.Receiver].Color == TopP3HelloWorldColor.None
           && state.Members[window.Source].Alive
           && state.Members[window.Source].Color != TopP3HelloWorldColor.None
           && now < state.Members[window.Source].ColorExpiresAt
           && positions.ContainsKey(window.Source);

    private sealed class Actor(
        TopP3HelloWorldMovementCommand[] routes, TopP3HelloWorldChaseWindow[] windows)
    {
        public readonly TopP3HelloWorldMovementCommand[] Routes = routes;
        public readonly TopP3HelloWorldChaseWindow[] Windows = windows;
        public int RouteIndex;
        public int WindowIndex;
        public TopP3HelloWorldMovementCommand? Pending;
        public TopP3HelloWorldChaseWindow? Chase;
        public bool ChaseIssued;
        public Vector3? Departing;
        public float? HoldUntil;
        public bool HoldIssued;
    }
}

/// <summary>Per-run lifecycle shared by the native AI adapter and offline tests.</summary>
public sealed class TopP3HelloWorldMovementSession
{
    private TopP3HelloWorldMovementController? controller;

    public void Start(TopP3HelloWorldState state, bool enabled)
        => controller = enabled ? new TopP3HelloWorldMovementController(state) : null;

    public IReadOnlyList<TopP3HelloWorldMovementCommand> Advance(
        float now, IReadOnlyDictionary<PartyRole, Vector3> positions)
        => controller?.Advance(now, positions) ?? System.Array.Empty<TopP3HelloWorldMovementCommand>();
}
