using System;
using System.Collections.Generic;
using System.Numerics;
using AnoMech.Core.Game.Party;

namespace AnoMech.Scenarios.Top.P3HelloWorld;

public readonly record struct TopP3HelloWorldTetherBreak(
    int Round, TopP3HelloWorldRole Role, float At);

public sealed partial class TopP3HelloWorldState
{
    // A physical break resolves the line, not its original ten-second window.
    private readonly Dictionary<(int Round, TopP3HelloWorldRole Role), (float Start, float Expiry)> activeTethers = [];

    internal bool IsTetherResolved(int round, TopP3HelloWorldRole role)
        => tethersResolved.Contains((round, role));

    public void ActivateTethers(int round, float now)
    {
        if (Failed || Completed) return;
        Activate(TopP3HelloWorldRole.RemoteTether);
        Activate(TopP3HelloWorldRole.LocalTether);

        void Activate(TopP3HelloWorldRole role)
        {
            var key = (round, role);
            if (!tethersResolved.Contains(key))
                activeTethers.TryAdd(key, (now, now + TopP3HelloWorldRules.ActiveTetherDuration));
        }
    }

    private void ResetTethers() => activeTethers.Clear();

    internal float NextTetherExpiryAt()
    {
        var next = float.PositiveInfinity;
        if (!Failed && !Completed)
            foreach (var active in activeTethers.Values)
                next = MathF.Min(next, active.Expiry);
        return next;
    }

    internal bool ResolveExpiredTethers(float now)
    {
        if (Failed || Completed) return false;
        foreach (var (key, active) in activeTethers)
        {
            if (now < active.Expiry) continue;
            if (!tethersResolved.Contains(key))
                return Fail(TopP3HelloWorldFailure.WrongTetherBreak,
                    $"R{key.Round} {key.Role} active duration expired at {active.Expiry:F3}");
            if (!CheckContactDeadline(key.Round, key.Role)) return false;
            activeTethers.Remove(key);
        }
        return true;
    }

    internal TopP3HelloWorldTetherBreak? NextTetherBreak(
        float from, float to, IReadOnlyDictionary<PartyRole, Vector3> before,
        IReadOnlyDictionary<PartyRole, Vector3> after)
    {
        if (Failed || Completed) return null;
        TopP3HelloWorldTetherBreak? next = null;
        foreach (var (key, active) in activeTethers)
        {
            if (tethersResolved.Contains(key)) continue;
            var start = MathF.Max(from, active.Start);
            if (start > to) continue;
            var pair = Pattern.RoleGroup(key.Round, key.Role);
            float at;
            if (pair.Count != 2 || !members[pair[0]].Alive || !members[pair[1]].Alive
                || !before.TryGetValue(pair[0], out var first)
                || !before.TryGetValue(pair[1], out var second))
            {
                // Schedule the invalid pair now so the transaction fails instead
                // of silently keeping an unobservable tether until its deadline.
                at = start;
            }
            else
            {
                var near = key.Role == TopP3HelloWorldRole.LocalTether;
                var relative = first - second;
                var hasEnd = after.TryGetValue(pair[0], out var endFirst)
                    & after.TryGetValue(pair[1], out var endSecond);
                if (start > from)
                {
                    if (!hasEnd) continue;
                    relative = Vector3.Lerp(relative, endFirst - endSecond, (start - from) / (to - from));
                }
                if (TetherDistanceQualifies(relative, near)) at = start;
                else
                {
                    if (to <= start || !hasEnd) continue;
                    var fraction = TetherCrossing(relative, endFirst - endSecond, near);
                    if (fraction is not { } crossing) continue;
                    at = (float)(start + (to - start) * crossing);
                    // Far requires strictly beyond, not equality at the boundary.
                    if (!near) at = MathF.BitIncrement(at);
                    if (at > to) continue;
                }
            }
            if (at >= active.Expiry) continue; // A strict deadline cannot resolve successfully.
            if (next is null || at < next.Value.At)
                next = new(key.Round, key.Role, at);
        }
        return next;
    }

    internal IReadOnlyList<TopP3HelloWorldTetherBreak> ResolveActiveTethers(
        float now, IReadOnlyDictionary<PartyRole, Vector3> snapshot,
        TopP3HelloWorldTetherBreak? crossing = null)
    {
        if (Failed || Completed || activeTethers.Count == 0)
            return Array.Empty<TopP3HelloWorldTetherBreak>();
        List<TopP3HelloWorldTetherBreak>? breaks = null;
        foreach (var (key, active) in activeTethers)
        {
            if (tethersResolved.Contains(key)) continue;
            if (now < active.Start) continue;
            var pair = Pattern.RoleGroup(key.Round, key.Role);
            if (pair.Count != 2 || !members[pair[0]].Alive || !members[pair[1]].Alive
                || !snapshot.TryGetValue(pair[0], out var first)
                || !snapshot.TryGetValue(pair[1], out var second))
            {
                Fail(TopP3HelloWorldFailure.WrongTetherBreak, $"R{key.Round} {key.Role} pair missing or dead");
                return Array.Empty<TopP3HelloWorldTetherBreak>();
            }
            if (now >= active.Expiry) continue;
            var crossed = crossing is { } hit && hit.Round == key.Round && hit.Role == key.Role;
            if (!crossed && !TetherDistanceQualifies(first - second,
                    key.Role == TopP3HelloWorldRole.LocalTether)) continue;
            (breaks ??= []).Add(new(key.Round, key.Role, now));
        }
        if (breaks is null) return Array.Empty<TopP3HelloWorldTetherBreak>();
        LastProcessedAt = MathF.Max(LastProcessedAt, now);
        foreach (var tether in breaks)
        {
            tethersResolved.Add((tether.Round, tether.Role));
        }
        return breaks;
    }

    private static bool TetherDistanceQualifies(Vector3 relative, bool near)
    {
        double distanceSquared = (double)relative.X * relative.X + (double)relative.Z * relative.Z;
        var threshold = TopP3HelloWorldRules.TetherBreakDistance;
        return near ? distanceSquared <= threshold * threshold : distanceSquared > threshold * threshold;
    }

    private static double? TetherCrossing(Vector3 before, Vector3 after, bool near)
    {
        double x = before.X, z = before.Z;
        double dx = (double)after.X - x, dz = (double)after.Z - z;
        var a = dx * dx + dz * dz;
        if (a == 0) return null;
        var threshold = TopP3HelloWorldRules.TetherBreakDistance;
        var c = x * x + z * z - threshold * threshold;
        var b = x * dx + z * dz;
        var discriminant = b * b - a * c;
        if (discriminant < 0) return null;
        var root = Math.Sqrt(discriminant);
        var fraction = (-b + (near ? -root : root)) / a;
        return fraction >= 0 && fraction <= 1 ? fraction : null;
    }
}
