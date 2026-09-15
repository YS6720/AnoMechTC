using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game.Party;

namespace AnoMech.Scenarios.Top.P3HelloWorld;

public static partial class TopP3HelloWorldRules
{
    public static IReadOnlyList<TopP3HelloWorldRoutePoint> RouteFor(PartyRole role)
    {
        var points = new List<TopP3HelloWorldRoutePoint>();
        var origin = WaitingPositionFor(1, role);
        points.Add(new(0.1f, origin));
        for (var round = 1; round <= 4; round++)
        {
            var times = RoundTimes(round);
            var carrier = RoleGroup(round, TopP3HelloWorldRole.Defamation).Contains(role)
                || RoleGroup(round, TopP3HelloWorldRole.Stack).Contains(role);
            var station = PositionForRound(round, role);
            // The colored tower circles are the movement cue. Carriers take
            // the outside arc; everyone else changes sectors on the inner arc.
            var at = times.TowerCastAt;
            var lane = !carrier ? InnerLaneRadius
                : RoleGroup(round, TopP3HelloWorldRole.Defamation).Contains(role)
                    ? OuterLaneRadius : StackTransitRadius;
            AddLaneTravel(points, ref at, ref origin, Vector3.Normalize(station) * lane, lane);

            if (round == 4 && RoleGroup(round, TopP3HelloWorldRole.LocalTether).Contains(role))
            {
                var localBreak = LocalBreakPosition(round);
                at = MathF.Max(at, times.LineStatusAt
                    - Vector3.Distance(origin, localBreak) / MoveSpeed - 0.2f);
                AddTravel(points, ref at, ref origin, localBreak);
                at = MathF.Max(at, times.LineStatusAt + 0.1f);
                AddTravel(points, ref at, ref origin, station);
            }
            else
            {
                // Non-carriers leave the inner lane only for the resolution.
                // This keeps their outward spokes out of the carriers' transit.
                var boundary = carrier || round == 4 ? times.AoeAt : times.LineStatusAt;
                if (!carrier)
                    at = MathF.Max(at, boundary - Vector3.Distance(origin, station) / MoveSpeed - 0.2f);
                AddTravel(points, ref at, ref origin, station);
            }

            if (round < 4)
            {
                var contact = ContactsForRound(round).FirstOrDefault(item => item.Receiver == role);
                if (contact != default)
                {
                    var source = PositionForRound(round, contact.Source);
                    var start = TopP3HelloWorldMovementPlan.PickupAt(round);
                    var entry = source + Vector3.Normalize(station - source)
                        * (ContactRadiusDefault - 0.1f);
                    var arrival = start + Vector3.Distance(station, entry) / MoveSpeed + 0.1f;
                    points.Add(new(start, entry) { ContactOnly = true });
                    var hold = RoleGroup(round, TopP3HelloWorldRole.LocalTether).Contains(role)
                        ? TopP3HelloWorldMovementPlan.NearContactHoldSeconds : 0f;
                    origin = PostContactPositionFor(round, role);
                    points.Add(new(arrival + hold, origin) { ContactOnly = true });
                    at = arrival + hold + Vector3.Distance(entry, origin) / MoveSpeed + 0.2f;
                    points.Add(new(at, WaitingPositionFor(round + 1, role))
                        { RequiredContactRound = round });
                }
                else
                {
                    // The outgoing source must remain isolated for its own
                    // color explosion before taking an inward spoke.
                    at = ColorExpiryForRound(round, role) + 0.2f;
                    AddTravel(points, ref at, ref origin, WaitingPositionFor(round + 1, role));
                }
                origin = WaitingPositionFor(round + 1, role);
            }
            else if (RoleGroup(round, TopP3HelloWorldRole.LocalTether).Contains(role))
            {
                points.Add(new(times.TowerEffectAt + 0.1f, LocalBreakPosition(round)));
            }
            else if (RoleGroup(round, TopP3HelloWorldRole.RemoteTether).Contains(role))
            {
                points.Add(new(times.TowerEffectAt + 0.1f,
                    TetherPositionFor(round, TopP3HelloWorldRole.RemoteTether, role)));
            }
        }
        points.Add(new(EndAt - 0.1f, PositionForRound(4, role)));
        return points.OrderBy(point => point.At).ToArray();
    }

    // Fixed traffic lanes, in yalms. Thirty-degree arc chords remain outside
    // radius 19.12, clear of both the six-yalm inner lane and tower owners.
    public const float InnerLaneRadius = 6f;
    public const float OuterLaneRadius = 19.8f;
    // Opposite rot colors never share an arc: stack carriers use the outer
    // annulus at 14.5, defamation carriers at 19.8, other actors at 6.
    public const float StackTransitRadius = 14.5f;

    private static Vector3 WaitingPositionFor(int round, PartyRole role)
    {
        var carrier = RoleGroup(round, TopP3HelloWorldRole.Defamation).Contains(role)
            || RoleGroup(round, TopP3HelloWorldRole.Stack).Contains(role);
        if (round == 1)
            return Vector3.Normalize(PositionForRound(round, role))
                * (carrier ? OuterLaneRadius : InnerLaneRadius);
        if (!carrier)
            return Vector3.Normalize(PositionForRound(round - 1, role)) * InnerLaneRadius;
        var source = ContactsForRound(round - 1).Single(contact => contact.Receiver == role).Source;
        var direction = Vector3.Normalize(PositionForRound(round - 1, source));
        if (RoleGroup(round - 1, TopP3HelloWorldRole.LocalTether).Contains(role))
        {
            // Split the near pair back outside, offset from the outgoing
            // sources so their independent five-yalm rot explosions miss us.
            var midpoint = Vector3.Normalize(LocalBreakPosition(round - 1));
            direction = Vector3.Normalize(direction + midpoint);
        }
        return direction * OuterLaneRadius;
    }

    private static float ColorExpiryForRound(int round, PartyRole role)
    {
        if (round == 1) return InitialActiveStatusAt + InitialActiveDuration;
        var contact = ContactsForRound(round - 1).Single(item => item.Receiver == role);
        var distance = Vector3.Distance(PositionForRound(round - 1, role),
            PositionForRound(round - 1, contact.Source));
        return TopP3HelloWorldMovementPlan.PickupAt(round - 1)
            + (distance - ContactRadiusDefault + 0.1f) / MoveSpeed + 0.1f + ColorDuration;
    }

    private static void AddTravel(List<TopP3HelloWorldRoutePoint> points,
        ref float at, ref Vector3 origin, Vector3 target)
    {
        points.Add(new(at, target));
        at += Vector3.Distance(origin, target) / MoveSpeed + 0.1f;
        origin = target;
    }

    private static void AddLaneTravel(List<TopP3HelloWorldRoutePoint> points,
        ref float at, ref Vector3 origin, Vector3 target, float radius)
    {
        if (MathF.Abs(origin.Length() - radius) > 0.01f)
            AddTravel(points, ref at, ref origin, Vector3.Normalize(origin) * radius);
        var start = MathF.Atan2(origin.Z, origin.X);
        var delta = MathF.IEEERemainder(MathF.Atan2(target.Z, target.X) - start, 2f * MathF.PI);
        var steps = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(delta) / (MathF.PI / 6f)));
        for (var step = 1; step <= steps; step++)
        {
            var angle = start + delta * step / steps;
            AddTravel(points, ref at, ref origin,
                new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle)) * radius);
        }
    }

    public static IReadOnlyList<TopP3HelloWorldRoutePoint> RuntimeRouteFor(PartyRole role)
        => RouteFor(role)
            .Select(point => point with { At = RuntimeAt(point.At) })
            .ToArray();


    public static Vector3 TetherPositionFor(
        int round,
        TopP3HelloWorldRole tetherRole,
        PartyRole role)
    {
        var pair = RoleGroup(round, tetherRole);
        if (!pair.Contains(role)) return PositionForRound(round, role);
        if (tetherRole == TopP3HelloWorldRole.LocalTether)
            return LocalBreakPosition(round);
        if (round < 4)
        {
            // Split outward on each stack source's own side instead of crossing
            // the defamation pickup lane. Keep half a yalm off the wall.
            var contact = ContactsForRound(round).Single(item => item.Receiver == role);
            return Vector3.Normalize(PositionForRound(round, contact.Source)) * 19.5f;
        }
        return pair[0] == role
            ? new Vector3(0f, 0f, 10f) : new Vector3(10f, 0f, -10f);
    }

    public static Vector3 LocalBreakPosition(int round)
    {
        // R4 retains its near-first stack staging; pickup rounds converge
        // between the defamation owners, not between receiver stations.
        var pair = RoleGroup(round, round == 4
            ? TopP3HelloWorldRole.LocalTether : TopP3HelloWorldRole.Defamation);
        return (PositionForRound(round, pair[0]) + PositionForRound(round, pair[1])) * 0.5f;
    }

    public static Vector3 PostContactPositionFor(int round, PartyRole role)
    {
        if (RoleGroup(round, TopP3HelloWorldRole.LocalTether).Contains(role))
            return LocalBreakPosition(round);
        if (RoleGroup(round, TopP3HelloWorldRole.RemoteTether).Contains(role))
            return TetherPositionFor(round, TopP3HelloWorldRole.RemoteTether, role);
        return PositionForRound(round, role);
    }

    public static Vector3 ContactPositionFor(int round, PartyRole role)
    {
        foreach (var contact in ContactsForRound(round))
        {
            if (contact.Receiver == role)
                return PositionForRound(round, contact.Source);
            if (contact.Source == role)
                return PositionForRound(round, role);
        }
        if (round == 4 && RoleGroup(round, TopP3HelloWorldRole.LocalTether).Contains(role))
            return LocalBreakPosition(round);
        return PositionForRound(round, role);
    }

    // This is a route target only.  ResolveAoes always takes the live owner
    // snapshot; no ideal anchor is used as a damage center.
    public static Vector3 AoePositionFor(int round, PartyRole role)
        => PositionForRound(round, role);

    public static bool IsArenaPosition(Vector3 position, float radius = 20f)
        => DistanceXZ(position, Vector3.Zero) <= radius;
    public static float ClampContactRadius(float value)
        => Math.Clamp(value, ContactRadiusMin, ContactRadiusMax);
}
