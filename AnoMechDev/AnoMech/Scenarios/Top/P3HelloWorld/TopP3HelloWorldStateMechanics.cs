using System;
using System.Collections.Generic;
using System.Linq;
using AnoMech.Core.Game.Party;

namespace AnoMech.Scenarios.Top.P3HelloWorld;

public sealed partial class TopP3HelloWorldState
{
    public IReadOnlyList<PartyRole> ActiveOwners(TopP3HelloWorldRole kind)
        => members.Values
            .Where(member => member.Alive && (kind == TopP3HelloWorldRole.Stack
                ? member.ActiveStack : member.ActiveDefamation))
            .Select(member => member.Role)
            .ToArray();

    public float? NextDynamicAt()
    {
        var next = float.PositiveInfinity;
        foreach (var member in members.Values)
        {
            if (member.Color != TopP3HelloWorldColor.None)
                Consider(member.ColorExpiresAt);
            if (member.BlueLatent)
                Consider(member.BlueLatentExpiresAt);
            if (member.RedLatent)
                Consider(member.RedLatentExpiresAt);
            if (member.ActiveStack)
                Consider(member.ActiveStackExpiresAt);
            if (member.ActiveDefamation)
                Consider(member.ActiveDefamationExpiresAt);
            if (member.NeedStack)
                Consider(member.NeedStackExpiresAt);
            if (member.NeedDefamation)
                Consider(member.NeedDefamationExpiresAt);
        }
        return float.IsPositiveInfinity(next) ? null : next;

        void Consider(float at)
        {
            if (at > LastProcessedAt && at < next) next = at;
        }
    }

    public bool ResolveExpiredMechanics(
        float now,
        bool includeActive = true,
        bool includeRequirements = true)
    {
        if (Failed) return false;
        foreach (var member in members.Values)
        {
            if (includeRequirements && member.NeedStack && member.NeedStackExpiresAt > 0f
                && now >= member.NeedStackExpiresAt)
                return Fail(TopP3HelloWorldFailure.Incomplete,
                    $"{member.Role} stack requirement expired", member.Role);
            if (includeRequirements && member.NeedDefamation
                && member.NeedDefamationExpiresAt > 0f
                && now >= member.NeedDefamationExpiresAt)
                return Fail(TopP3HelloWorldFailure.Incomplete,
                    $"{member.Role} defamation requirement expired", member.Role);
            if (includeActive && member.ActiveStack && now >= member.ActiveStackExpiresAt)
                return Fail(TopP3HelloWorldFailure.Incomplete,
                    $"{member.Role} active stack expired", member.Role);
            if (includeActive && member.ActiveDefamation
                && now >= member.ActiveDefamationExpiresAt)
                return Fail(TopP3HelloWorldFailure.Incomplete,
                    $"{member.Role} active defamation expired", member.Role);
        }
        LastProcessedAt = MathF.Max(LastProcessedAt, now);
        return true;
    }

    public bool ResolveExpiredLatents(float now)
    {
        if (Failed) return false;
        foreach (var member in members.Values)
        {
            if (member.BlueLatent && now >= member.BlueLatentExpiresAt)
                return Fail(TopP3HelloWorldFailure.LatentExpired,
                    $"{member.Role} blue latent expired", member.Role);
            if (member.RedLatent && now >= member.RedLatentExpiresAt)
                return Fail(TopP3HelloWorldFailure.LatentExpired,
                    $"{member.Role} red latent expired", member.Role);
        }
        LastProcessedAt = MathF.Max(LastProcessedAt, now);
        return true;
    }

    public IReadOnlyList<PartyRole> ResolveExpiredColors(
        float now,
        IReadOnlyDictionary<PartyRole, System.Numerics.Vector3> snapshot)
    {
        if (Failed) return Array.Empty<PartyRole>();
        if (!ResolveExpiredLatents(now)) return Array.Empty<PartyRole>();
        var expired = new List<PartyRole>();
        foreach (var role in TopP3HelloWorldRules.RecordingOrder)
        {
            var member = members[role];
            if (!member.Alive || member.Color == TopP3HelloWorldColor.None
                || now < member.ColorExpiresAt) continue;
            if (!snapshot.TryGetValue(role, out var center))
                return FailAndEmpty(TopP3HelloWorldFailure.Incomplete,
                    $"color {role} has no position", role);
            var otherHit = TopP3HelloWorldRules.RecordingOrder.Any(other =>
                other != role && members[other].Alive && snapshot.TryGetValue(other, out var position)
                && TopP3HelloWorldRules.Inside(position, center, TopP3HelloWorldRules.RotRadius));
            if (otherHit)
            {
                var hit = TopP3HelloWorldRules.RecordingOrder.First(other =>
                    other != role && members[other].Alive
                    && snapshot.TryGetValue(other, out var position)
                    && TopP3HelloWorldRules.Inside(position, center,
                        TopP3HelloWorldRules.RotRadius));
                return FailAndEmpty(TopP3HelloWorldFailure.ColorExplosion,
                    $"color {role} hit another member {hit} at {now:F3}"
                    + $" center={center.X:F1},{center.Z:F1}"
                    + $" other={snapshot[hit].X:F1},{snapshot[hit].Z:F1}", role);
            }
            var latent = member.Color == TopP3HelloWorldColor.Blue
                ? member.BlueLatent && now < member.BlueLatentExpiresAt
                : member.RedLatent && now < member.RedLatentExpiresAt;
            if (!latent)
                return FailAndEmpty(TopP3HelloWorldFailure.LatentExpired,
                    $"color {role} latent missing", role);
            var resolvedColor = member.Color;
            member.LastRotationColor = resolvedColor;
            if (resolvedColor == TopP3HelloWorldColor.Blue)
            {
                member.BlueLatent = false;
                member.BlueLatentExpiresAt = 0f;
                member.BlueVaccine = 1;
            }
            else
            {
                member.RedLatent = false;
                member.RedLatentExpiresAt = 0f;
                member.RedVaccine = 1;
            }
            member.Color = TopP3HelloWorldColor.None;
            member.ColorRound = 0;
            member.ColorExpiresAt = 0f;
            expired.Add(role);
        }
        LastProcessedAt = MathF.Max(LastProcessedAt, now);
        return expired;
    }

    internal void SetActive(
        PartyRole role,
        TopP3HelloWorldRole kind,
        float expiresAt)
    {
        var member = members[role];
        if (kind == TopP3HelloWorldRole.Stack)
        {
            member.ActiveStack = true;
            member.ActiveStackExpiresAt = expiresAt;
        }
        else
        {
            member.ActiveDefamation = true;
            member.ActiveDefamationExpiresAt = expiresAt;
        }
    }

    private IReadOnlyList<PartyRole> FailAndEmpty(
        TopP3HelloWorldFailure failure,
        string message,
        PartyRole? role = null)
    {
        Fail(failure, message, role);
        return Array.Empty<PartyRole>();
    }
}
