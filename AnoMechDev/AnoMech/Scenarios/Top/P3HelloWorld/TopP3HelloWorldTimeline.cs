using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game.Party;

namespace AnoMech.Scenarios.Top.P3HelloWorld;

/// <summary>
/// Scenario-local chronological transaction driver. It is deliberately not the
/// shared EventScheduler: fixed events, dynamic expiries, one position snapshot,
/// and contact commits are ordered here before presentation is projected.
/// </summary>
public sealed class TopP3HelloWorldTimeline
{
    private readonly TopP3HelloWorldState state;
    private readonly List<Entry> entries = [];
    private int nextEntry;
    private float lastTime;
    private int sequence;
    private bool positionsInitialized;

    public TopP3HelloWorldTimeline(TopP3HelloWorldState state)
    {
        this.state = state;
    }

    public float LastTime => lastTime;

    public void Add(
        float at,
        Action<float, IReadOnlyDictionary<PartyRole, Vector3>> action,
        bool afterContact = false)
    {
        entries.Add(new(at, sequence++, action, afterContact));
        entries.Sort(static (left, right) =>
        {
            var time = left.At.CompareTo(right.At);
            return time != 0 ? time : left.Sequence.CompareTo(right.Sequence);
        });
    }

    public void Advance(
        float now,
        Func<float, IReadOnlyDictionary<PartyRole, Vector3>> snapshotAt,
        Action<IReadOnlyList<TopP3HelloWorldContact>> onContacts,
        Action<PartyRole, IReadOnlyDictionary<PartyRole, Vector3>> onColorExpiry,
        Action<TopP3HelloWorldTetherBreak, IReadOnlyDictionary<PartyRole, Vector3>>? onTetherBreak = null)
    {
        if (state.Failed || state.Completed || now < lastTime) return;
        if (!positionsInitialized)
        {
            // Do not invent an earlier observation when the first call is late.
            if (now == lastTime)
                onContacts(state.UpdatePositions(lastTime, snapshotAt(lastTime), swept: false));
            positionsInitialized = true;
        }
        while (!state.Failed && !state.Completed)
        {
            var fixedAt = nextEntry < entries.Count ? entries[nextEntry].At : float.PositiveInfinity;
            var dynamicAt = state.NextDynamicAt() ?? float.PositiveInfinity;
            var scheduledAt = MathF.Min(fixedAt, dynamicAt);
            var expiryAt = state.NextTetherExpiryAt();
            var contact = now > lastTime
                // Solve against the actual frame end, not an interpolated
                // expiry/fixed endpoint whose rounding could move an exact tie.
                ? state.NextContact(lastTime, now, snapshotAt(lastTime), snapshotAt(now)) : null;
            var tether = state.NextTetherBreak(lastTime, now, snapshotAt(lastTime), snapshotAt(now));
            var at = MathF.Min(MathF.Min(scheduledAt, expiryAt),
                MathF.Min(contact?.At ?? float.PositiveInfinity, tether?.At ?? float.PositiveInfinity));
            if (float.IsPositiveInfinity(at) || at > now) break;
            var snapshot = snapshotAt(at);
            // A fixed event at the same timestamp owns the boundary. This is
            // what lets an active/required status resolve its scheduled AOE at
            // the exact expiry without adding an arbitrary grace duration;
            // color/latent expiry still runs before the fixed snapshot.
            var fixedAtBoundary = fixedAt < float.PositiveInfinity
                && fixedAt == at
                && dynamicAt < float.PositiveInfinity
                && MathF.Abs(dynamicAt - at) < 0.0001f;
            if (!state.ResolveExpiredMechanics(
                    at,
                    includeActive: !fixedAtBoundary,
                    includeRequirements: !fixedAtBoundary)
                || !state.ResolveExpiredLatents(at)
                || !state.ResolveExpiredTethers(at)) break;
            foreach (var role in state.ResolveExpiredColors(at, snapshot))
                onColorExpiry(role, snapshot);

            var beforeContact = new List<Entry>();
            var afterContact = new List<Entry>();
            while (nextEntry < entries.Count
                   && fixedAt == at
                   && MathF.Abs(entries[nextEntry].At - at) < 0.0001f)
            {
                var entry = entries[nextEntry++];
                (entry.AfterContact ? afterContact : beforeContact).Add(entry);
            }
            foreach (var entry in beforeContact)
                entry.Action(at, snapshot);
            onContacts(state.UpdatePositions(at, snapshot,
                contact is { } simultaneous && simultaneous.At == at ? simultaneous.Pairs : null,
                swept: false));
            foreach (var entry in afterContact)
                entry.Action(at, snapshot);
            foreach (var broken in state.ResolveActiveTethers(at, snapshot,
                         tether is { } crossing && crossing.At == at ? crossing : null))
                onTetherBreak?.Invoke(broken, snapshot);
            lastTime = at;
            if (state.LastProcessedAt < at) state.UpdatePositions(at, snapshot, swept: false);
        }

        if (!state.Failed && !state.Completed && now > lastTime)
        {
            var snapshot = snapshotAt(now);
            if (state.ResolveExpiredMechanics(now) && state.ResolveExpiredLatents(now)
                && state.ResolveExpiredTethers(now))
            {
                foreach (var role in state.ResolveExpiredColors(now, snapshot))
                    onColorExpiry(role, snapshot);
                onContacts(state.UpdatePositions(now, snapshot, swept: false));
                foreach (var broken in state.ResolveActiveTethers(now, snapshot))
                    onTetherBreak?.Invoke(broken, snapshot);
                lastTime = now;
            }
        }
    }

    private readonly record struct Entry(
        float At,
        int Sequence,
        Action<float, IReadOnlyDictionary<PartyRole, Vector3>> Action,
        bool AfterContact);
}
