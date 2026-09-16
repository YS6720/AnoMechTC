using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game.Party;

namespace AnoMech.Scenarios.Top.P3Intermission;

/// <summary>One of the eight cannon stations, named with A (−Z) as north.</summary>
public enum TopP3CannonSlot
{
    SpreadWest, SpreadSouthWest, SpreadSouthEast, SpreadEast,
    ShareNorthWest, ShareNorthEast,
    SoakNorthWest, SoakNorthEast,
}

/// <summary>
/// Who was marked with what, and therefore who stands where. Pure: built from the
/// two marked sets, no world access. The recorded rehearsal is one instance of it.
///
/// Rule (maintainer 2026-09-16, matches the recording): priority order is
/// H1 MT ST D1 D2 D3 D4 H2. The four Sniper Cannon (spread) targets take
/// 左／左下／右下／右 in priority order; the two High-Powered (share) targets take
/// 左上／右上 in priority order; the two unmarked players soak 左上／右上 in priority
/// order. Every slot is filled exactly once, so a cannon group is never short a body.
/// </summary>
public sealed class TopP3CannonAssignment
{
    public static readonly IReadOnlyList<PartyRole> Priority =
    [
        PartyRole.RegenHealer,   // H1
        PartyRole.MainTank,      // MT
        PartyRole.OffTank,       // ST
        PartyRole.MeleeDpsA,     // D1
        PartyRole.MeleeDpsB,     // D2
        PartyRole.PhysRangedDps, // D3
        PartyRole.CasterDps,     // D4
        PartyRole.ShieldHealer,  // H2
    ];

    private static readonly TopP3CannonSlot[] SpreadSlots =
    [
        TopP3CannonSlot.SpreadWest, TopP3CannonSlot.SpreadSouthWest,
        TopP3CannonSlot.SpreadSouthEast, TopP3CannonSlot.SpreadEast,
    ];
    private static readonly TopP3CannonSlot[] ShareSlots =
        [TopP3CannonSlot.ShareNorthWest, TopP3CannonSlot.ShareNorthEast];
    private static readonly TopP3CannonSlot[] SoakSlots =
        [TopP3CannonSlot.SoakNorthWest, TopP3CannonSlot.SoakNorthEast];

    private const float Diag = 0.70710677f;

    // Per station: the waymark it waits outside of (A north ⇒ D west, 3 south-west,
    // 2 south-east, B east, 4 north-west, 1 north-east), the arm-safe waypoint and
    // the final cannon position (both from the recording). The soak station sits
    // one metre inside its share partner so the pair reads as one stack while
    // native hit tests still see two distinct actors.
    //
    // Maintainer 2026-09-16: the wave rings are dodged at the edge — you wait
    // outside your waymark, step one ring inward just before the outer ring hits,
    // and step back out. Nobody crosses into the inner field for the rings.
    private static readonly IReadOnlyDictionary<TopP3CannonSlot, (Vector3 Waymark, Vector3 PreArm, Vector3 Final, string Label, string WaitLabel)> Stations =
        new Dictionary<TopP3CannonSlot, (Vector3, Vector3, Vector3, string, string)>
        {
            [TopP3CannonSlot.SpreadWest] = (new(-1f, 0f, 0f), new(-10.74f, 0f, 4.12f), new(-17f, 0f, -4f), "左砲", "D 外側"),
            [TopP3CannonSlot.SpreadSouthWest] = (new(-Diag, 0f, Diag), new(-8.94f, 0f, 7.24f), new(-4f, 0f, 16f), "左下砲", "3 外側"),
            [TopP3CannonSlot.SpreadSouthEast] = (new(Diag, 0f, Diag), new(8.94f, 0f, 7.24f), new(4f, 0f, 16f), "右下砲", "2 外側"),
            [TopP3CannonSlot.SpreadEast] = (new(1f, 0f, 0f), new(10.74f, 0f, 4.12f), new(17f, 0f, -4f), "右砲", "B 外側"),
            [TopP3CannonSlot.ShareNorthWest] = (new(-Diag, 0f, -Diag), new(-1.8f, 0f, -11.36f), new(-12f, 0f, -11f), "左上分攤", "4 外側"),
            [TopP3CannonSlot.ShareNorthEast] = (new(Diag, 0f, -Diag), new(1.8f, 0f, -11.36f), new(12f, 0f, -11f), "右上分攤", "1 外側"),
            [TopP3CannonSlot.SoakNorthWest] = (new(-Diag, 0f, -Diag), new(-1.8f, 0f, -11.36f), new(-12f, 0f, -10f), "左上分攤", "4 外側"),
            [TopP3CannonSlot.SoakNorthEast] = (new(Diag, 0f, -Diag), new(1.8f, 0f, -11.36f), new(12f, 0f, -10f), "右上分攤", "1 外側"),
        };

    // Edge wait in the outer ring (18–20), the one-ring step-in in ring 2 (12–18).
    // Soakers stand beside their share partner (one metre along the edge, same
    // radius) so the pair is visible as two people without either leaving the ring.
    private const float WaitRadius = 19.5f;
    private const float StepInRadius = 16f;
    private const float SoakSideStep = 1f;

    private static Vector3 EdgePoint(TopP3CannonSlot slot, float radius)
    {
        var direction = Stations[slot].Waymark;
        var point = direction * radius;
        if (slot is TopP3CannonSlot.SoakNorthWest or TopP3CannonSlot.SoakNorthEast)
            point += new Vector3(-direction.Z, 0f, direction.X) * SoakSideStep;
        return point;
    }

    /// <summary>The recorded rehearsal: SGE/PLD/WHM/SAM spread, DRK/BLM share, RPR/MCH soak.</summary>
    public static readonly TopP3CannonAssignment Recorded = Create(
        [PartyRole.ShieldHealer, PartyRole.OffTank, PartyRole.RegenHealer, PartyRole.MeleeDpsA],
        [PartyRole.MainTank, PartyRole.CasterDps]);

    private readonly IReadOnlyDictionary<PartyRole, TopP3CannonSlot> slotOf;
    private readonly IReadOnlyDictionary<TopP3CannonSlot, PartyRole> roleAt;

    public IReadOnlyList<TopP3CannonGroup> CannonGroups { get; }
    public IReadOnlyList<TopP3MarkerAssignment> MarkerAssignments { get; }

    private TopP3CannonAssignment(IReadOnlyDictionary<PartyRole, TopP3CannonSlot> slotOf)
    {
        this.slotOf = slotOf;
        roleAt = slotOf.ToDictionary(pair => pair.Value, pair => pair.Key);
        CannonGroups =
        [
            Spread(TopP3CannonSlot.SpreadWest),
            Spread(TopP3CannonSlot.SpreadSouthWest),
            Spread(TopP3CannonSlot.SpreadSouthEast),
            Spread(TopP3CannonSlot.SpreadEast),
            Share(TopP3CannonSlot.ShareNorthWest, TopP3CannonSlot.SoakNorthWest),
            Share(TopP3CannonSlot.ShareNorthEast, TopP3CannonSlot.SoakNorthEast),
        ];
        // Only marker actors carry a native point status; soakers are unmarked.
        MarkerAssignments = CannonGroups
            .Select(group => new TopP3MarkerAssignment(group.MarkerRole, group.Kind))
            .ToArray();
    }

    /// <summary>Four spread targets and two share targets; the remaining two soak.</summary>
    public static TopP3CannonAssignment Create(
        IReadOnlyCollection<PartyRole> spreadRoles,
        IReadOnlyCollection<PartyRole> shareRoles)
    {
        if (spreadRoles.Count != 4 || shareRoles.Count != 2 ||
            spreadRoles.Intersect(shareRoles).Any() ||
            spreadRoles.Concat(shareRoles).Distinct().Count() != 6)
            throw new ArgumentException("P3 intermission needs four spread and two share targets, all distinct.");

        var soakRoles = Priority.Where(role => !spreadRoles.Contains(role) && !shareRoles.Contains(role)).ToArray();
        var slots = new Dictionary<PartyRole, TopP3CannonSlot>(8);
        Fill(slots, spreadRoles, SpreadSlots);
        Fill(slots, shareRoles, ShareSlots);
        Fill(slots, soakRoles, SoakSlots);
        return new TopP3CannonAssignment(slots);
    }

    /// <summary>Six of eight marked from a shuffled roster: first four spread, next two share.</summary>
    public static TopP3CannonAssignment FromShuffled(IReadOnlyList<PartyRole> shuffled)
        => Create(shuffled.Take(4).ToArray(), shuffled.Skip(4).Take(2).ToArray());

    private static void Fill(
        Dictionary<PartyRole, TopP3CannonSlot> slots,
        IEnumerable<PartyRole> roles,
        TopP3CannonSlot[] targets)
    {
        var index = 0;
        foreach (var role in Priority)
        {
            if (!roles.Contains(role)) continue;
            slots[role] = targets[index++];
        }
    }

    private TopP3CannonGroup Spread(TopP3CannonSlot slot)
        => new(Stations[slot].Label, TopP3CannonKind.Spread, roleAt[slot], [roleAt[slot]]);

    private TopP3CannonGroup Share(TopP3CannonSlot marker, TopP3CannonSlot soak)
        => new(Stations[marker].Label, TopP3CannonKind.Share, roleAt[marker], [roleAt[marker], roleAt[soak]]);

    public TopP3CannonSlot SlotOf(PartyRole role) => slotOf[role];
    public string LabelFor(PartyRole role) => Stations[slotOf[role]].Label;
    public string WaitLabelFor(PartyRole role) => Stations[slotOf[role]].WaitLabel;
    public Vector3 CannonPositionFor(PartyRole role) => Stations[slotOf[role]].Final;
    public Vector3 PreArmPositionFor(PartyRole role) => Stations[slotOf[role]].PreArm;

    public TopP3CannonGroup CannonGroupFor(PartyRole role)
    {
        foreach (var group in CannonGroups)
            if (group.RequiredRoles.Contains(role)) return group;
        throw new ArgumentOutOfRangeException(nameof(role));
    }

    public Vector3 WaitPositionFor(PartyRole role) => EdgePoint(slotOf[role], WaitRadius);

    /// <summary>
    /// Recorded-relative waypoints. Every target and every segment stays outside
    /// the native central radius-6 voidzone.
    ///
    /// Wave 1: wait at the edge (ring 3); rings 0–2 hit inward of you. Just before
    /// ring 3 hits, step into ring 2 (already spent), then back to the edge.
    /// Wave 2: rings 0–1 hit inward of you; step to the arm-safe gap (ring 1, spent)
    /// for the first arms, then to the cannon position (ring 2, spent) before the
    /// cannon resolves; ring 3 and the second arms both miss the cannon positions.
    /// </summary>
    public IReadOnlyList<TopP3RoutePoint> RouteFor(PartyRole role)
    {
        var slot = slotOf[role];
        var wait = EdgePoint(slot, WaitRadius);
        var stepIn = EdgePoint(slot, StepInRadius);
        var preArm = PreArmPositionFor(role);
        var final = CannonPositionFor(role);
        return
        [
            new(TopP3IntermissionRules.QueueAt, wait),
            new(TopP3IntermissionRules.Wave1ThirdAt + 0.05f, stepIn),
            new(TopP3IntermissionRules.Wave1FourthAt + 0.05f, wait),
            new(TopP3IntermissionRules.Wave2SecondAt + 0.05f, preArm),
            new(TopP3IntermissionRules.Wave2ThirdAt + 0.05f, final),
            new(TopP3IntermissionRules.CannonAt, final),
        ];
    }

    public IReadOnlyList<TopP3RoutePoint> RuntimeRouteFor(PartyRole role)
        => RouteFor(role)
            .Select(point => new TopP3RoutePoint(TopP3IntermissionRules.RuntimeAt(point.At), point.Target))
            .ToArray();

    /// <summary>
    /// Evaluates the physical route in runtime scenario seconds. The route itself
    /// is recorded-relative; RuntimeRouteFor is the schedule the AI consumes.
    /// </summary>
    public Vector3 PositionAt(PartyRole role, float runtimeTime)
    {
        var recordedTime = runtimeTime - TopP3IntermissionRules.PreparationSeconds;
        var route = RouteFor(role);
        var position = route[0].Target;
        var arrival = route[0].At;
        for (var i = 1; i < route.Count; i++)
        {
            var depart = MathF.Max(route[i].At, arrival);
            var target = route[i].Target;
            var travel = Vector3.Distance(position, target) / TopP3IntermissionRules.MoveSpeed;
            if (recordedTime < depart) return position;
            if (recordedTime < depart + travel)
            {
                var fraction = Math.Clamp((recordedTime - depart) / travel, 0f, 1f);
                return Vector3.Lerp(position, target, fraction);
            }
            position = target;
            arrival = depart + travel;
        }
        return position;
    }

    public IReadOnlyList<TopP3HitCount> CountCannonHits(IReadOnlyList<TopP3Position> members)
    {
        var counts = TopP3IntermissionRules.RecordingOrder.ToDictionary(role => role, _ => 0);
        foreach (var group in CannonGroups)
        {
            if (!TopP3IntermissionRules.TryMarkerPosition(members, group.MarkerRole, out var center)) continue;
            foreach (var role in TopP3IntermissionRules.MembersInsideCircle(members, center, TopP3IntermissionRules.CannonRadius))
                counts[role]++;
        }
        return TopP3IntermissionRules.RecordingOrder.Select(role => new TopP3HitCount(role, counts[role])).ToArray();
    }

    public IReadOnlyList<TopP3CannonResolution> ResolveCannons(IReadOnlyList<TopP3Position> members)
    {
        var results = new List<TopP3CannonResolution>(CannonGroups.Count);
        foreach (var group in CannonGroups)
        {
            var count = TopP3IntermissionRules.TryMarkerPosition(members, group.MarkerRole, out var center)
                ? TopP3IntermissionRules.MembersInsideCircle(members, center, TopP3IntermissionRules.CannonRadius).Count
                : 0;
            var required = group.Kind == TopP3CannonKind.Spread ? 1 : 2;
            var result = count == required
                ? TopP3CannonResult.Correct
                : count == 0 || count < required
                    ? TopP3CannonResult.Missing
                    : TopP3CannonResult.Overlap;
            results.Add(new TopP3CannonResolution(group, count, result));
        }
        return results;
    }
}
