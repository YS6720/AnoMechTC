using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game.Party;

namespace AnoMech.Scenarios.Top.P3HelloWorld;

public sealed partial class TopP3HelloWorldState
{
    public IReadOnlyList<TopP3HelloWorldContact> UpdatePositions(
        float now, IReadOnlyDictionary<PartyRole, Vector3> positions)
        => UpdatePositions(now, positions, null, swept: true);

    internal IReadOnlyList<TopP3HelloWorldContact> UpdatePositions(
        float now,
        IReadOnlyDictionary<PartyRole, Vector3> positions,
        IReadOnlySet<(PartyRole Receiver, PartyRole Source)>? crossingPairs = null,
        bool swept = true)
    {
        if (Failed) return Array.Empty<TopP3HelloWorldContact>();
        LastProcessedAt = MathF.Max(LastProcessedAt, now);
        foreach (var role in TopP3HelloWorldRules.RecordingOrder)
        {
            var member = members[role];
            if (!positions.TryGetValue(role, out var position)) continue;
            member.PreviousPosition = member.PositionInitialized ? member.Position : null;
            member.Position = position;
            member.PositionInitialized = true;
        }

        var sourceSnapshot = members.Values
            .Where(member => member.Alive && member.Color != TopP3HelloWorldColor.None
                             && now < member.ColorExpiresAt)
            .ToDictionary(member => member.Role, member => new TopP3HelloWorldPosition(
                member.Position, member.PreviousPosition));
        var results = new List<TopP3HelloWorldContact>();
        foreach (var receiverRole in TopP3HelloWorldRules.RecordingOrder)
        {
            var receiver = members[receiverRole];
            if (!receiver.Alive || !receiver.PositionInitialized || receiver.PreviousPosition is null)
                continue;
            var touching = sourceSnapshot
                .Where(pair => pair.Key != receiverRole
                    && (crossingPairs?.Contains((receiverRole, pair.Key)) == true
                        || (swept ? TopP3HelloWorldRules.Contact(
                        new TopP3HelloWorldPosition(receiver.Position, receiver.PreviousPosition),
                        pair.Value, ContactRadius)
                            : TopP3HelloWorldRules.Inside(receiver.Position, pair.Value.Current, ContactRadius))))
                .Select(pair => (Source: pair.Key, Color: members[pair.Key].Color))
                .ToArray();
            if (touching.Length == 0) continue;

            var colors = touching.Select(item => item.Color).Distinct().ToArray();
            if (colors.Length != 1)
            {
                Fail(TopP3HelloWorldFailure.WrongColor,
                    $"opposite color contact at {receiverRole} at {now:F3}"
                    + $" sources={string.Join(',', touching.Select(item => item.Source))}",
                    receiverRole);
                break;
            }
            var color = colors[0];
            if (receiver.Color != TopP3HelloWorldColor.None)
            {
                if (receiver.Color != color)
                    Fail(TopP3HelloWorldFailure.WrongColor,
                        $"{receiverRole} touched {color} while holding {receiver.Color} at {now:F3}",
                        receiverRole);

                continue;
            }

            // Evaluate every physical source first, then de-duplicate same-color
            // sources. ExpectedContacts is deliberately not a collision filter.
            var source = touching
                .OrderBy(item => TopP3HelloWorldRules.RoleOrderIndex(item.Source))
                .First(item => item.Color == color).Source;
            var round = TopP3HelloWorldRules.ContactRoundFor(now);
            var key = (round, Receiver: receiverRole, Source: source);
            if (contactsResolved.Contains(key) || blockedContacts.Contains(key)) continue;
            var vaccineBlocked = receiver.ConsumeColorVaccine(color);
            if (vaccineBlocked)
            {
                // A persistent color vaccine blocks infection but is not a
                // successful contact. Keep the blocked physical pair separate
                // so it cannot satisfy a tether deadline or FinalizeRun.
                foreach (var item in touching.Where(item => item.Color == color))
                    blockedContacts.Add((round, receiverRole, item.Source));
            }
            else
            {
                contactsResolved.Add(key);
                SetColor(receiverRole, color,
                    now + TopP3HelloWorldRules.ColorDuration, round);
            }
            var contact = new TopP3HelloWorldContact(
                round, receiverRole, source, color, now, vaccineBlocked);
            results.Add(contact);
        }
        return results;
    }

    // Native projection uses the observation clock, not a fresh duration from
    // the callback time. A stale or blocked contact cannot re-add removed rot.
    internal float ColorRemainingFor(TopP3HelloWorldContact contact, float now)
    {
        var member = members[contact.Receiver];
        if (contact.VaccineBlocked || !member.Alive || member.Color == TopP3HelloWorldColor.None
            || member.Color != contact.Color) return 0f;
        return MathF.Max(0f, member.ColorExpiresAt - now);
    }

    // Find the first entry along the relative segment between actual observations.
    // The timeline merges this event with fixed events and strict status expiry.
    internal (float At, HashSet<(PartyRole Receiver, PartyRole Source)> Pairs)? NextContact(
        float from, float to, IReadOnlyDictionary<PartyRole, Vector3> before,
        IReadOnlyDictionary<PartyRole, Vector3> after)
    {
        var earliest = float.PositiveInfinity;
        var pairs = new HashSet<(PartyRole Receiver, PartyRole Source)>();
        foreach (var receiver in members.Values)
        foreach (var source in members.Values)
        {
            if (receiver.Role == source.Role || !receiver.Alive || !source.Alive
                || !receiver.PositionInitialized || !source.PositionInitialized
                || source.Color == TopP3HelloWorldColor.None || source.Color == receiver.Color
                || source.ColorExpiresAt <= from
                || !before.TryGetValue(receiver.Role, out var r0) || !after.TryGetValue(receiver.Role, out var r1)
                || !before.TryGetValue(source.Role, out var s0) || !after.TryGetValue(source.Role, out var s1)) continue;
            var fraction = FirstEntry(r0 - s0, r1 - s1, ContactRadius);
            if (fraction is not { } entry) continue;
            var at = (float)(from + (to - from) * entry);
            if (at >= source.ColorExpiresAt) continue; // Equality belongs to expiry, never infection.
            var key = (TopP3HelloWorldRules.ContactRoundFor(at), receiver.Role, source.Role);
            if (receiver.Color == TopP3HelloWorldColor.None
                && (blockedContacts.Contains(key) || contactsResolved.Contains(key))) continue;
            if (at < earliest)
            {
                earliest = at;
                pairs.Clear();
            }
            if (at == earliest) pairs.Add((receiver.Role, source.Role));
        }
        return pairs.Count == 0 ? null : (earliest, pairs);
    }

    private static double? FirstEntry(Vector3 before, Vector3 after, float radius)
    {
        double x = before.X, z = before.Z;
        double dx = (double)after.X - x, dz = (double)after.Z - z;
        var c = x * x + z * z - radius * radius;
        if (c <= 0) return 0;
        var a = dx * dx + dz * dz;
        if (a == 0) return null;
        var b = x * dx + z * dz;
        var discriminant = b * b - a * c;
        if (discriminant < 0) return null;
        var entry = (-b - Math.Sqrt(discriminant)) / a;
        return entry >= 0 && entry <= 1 ? entry : null;
    }
}
