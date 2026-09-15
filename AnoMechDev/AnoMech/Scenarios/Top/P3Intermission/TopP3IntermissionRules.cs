using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game.Party;

namespace AnoMech.Scenarios.Top.P3Intermission;

public enum TopP3CannonKind
{
    Spread,
    Share,
}

public enum TopP3CannonResult
{
    Missing,
    Correct,
    Overlap,
}

public readonly record struct TopP3Position(PartyRole Role, Vector3 Position);

public readonly record struct TopP3CannonGroup(
    string Label,
    TopP3CannonKind Kind,
    PartyRole MarkerRole,
    IReadOnlyList<PartyRole> RequiredRoles);

public readonly record struct TopP3CannonResolution(
    TopP3CannonGroup Group,
    int HitCount,
    TopP3CannonResult Result);

public readonly record struct TopP3HitCount(PartyRole Role, int Count);

public readonly record struct TopP3MarkerAssignment(
    PartyRole Role,
    TopP3CannonKind Kind);

public readonly record struct TopP3RoutePoint(float At, Vector3 Target);

/// <summary>
/// IDs, recorded timing, fixed positions, and pure hit-count rules for the
/// independent TOP P3 intermission rehearsal.  The recording's local zero is the
/// 31567 point-marker event (1438.257); the effect timestamps below are the exact
/// events from the raw t1122 extract, not rounded guide timings.
/// </summary>
public static class TopP3IntermissionRules
{
    public static class ActionId
    {
        // 31566/31567..31570/31571..31572 are the raw P3 intermission actions.
        public const uint ColossalBlow = 31566;
        public const uint WaveRepeaterFirst = 31567;
        public const uint WaveRepeaterSecond = 31568;
        public const uint WaveRepeaterThird = 31569;
        public const uint WaveRepeaterFourth = 31570;
        public const uint SniperCannon = 31571;
        public const uint HighPoweredSniperCannon = 31572;
    }

    public static class StatusId
    {
        // Point marker statuses observed on the eight rehearsal actors.
        public const ushort SniperCannonMarker = 3425;
        public const ushort HighPoweredSniperCannonMarker = 3426;
        public const ushort MagicVulnerabilityUp = 2941;
    }

    public static class EventObjectId
    {
        // Raw spawnobj at 1444.099: base 0x1EB821, eventId 0x800375AC.
        public const uint P3IntermissionVoidzone = 0x1EB821;
        public const uint P3IntermissionVoidzoneEvent = 0x800375AC;
    }

    // Action 31567 began at 1438.331 and landed at 1443.291/1451.425;
    // subsequent ring effects are 31568 at 1445.391/1453.567,
    // 31569 at 1447.449/1455.622, and 31570 at 1449.504/1457.680.
    public const float QueueAt = 0.1f;
    public const float PointMarkerAt = 0f;
    public const float PreparationSeconds = 5f;
    public const float WaveCastSeconds = 4.7f;
    public const float Wave1EffectAfterCast = 4.960f;
    public const float Wave2EffectAfterCast = 4.959f;
    public const float WaveEffectAfterCast = Wave1EffectAfterCast;
    public const float ArmCastSeconds = 1.7f;
    public const float FirstArmEffectAfterCast = 1.964f;
    public const float SecondArmEffectAfterCast = 1.963f;
    public const float ArmEffectAfterCast = FirstArmEffectAfterCast;

    // Actor timeline controls in the same raw extract: first three arms move
    // from centre at 1441.369, second three at 1444.411.
    public const float FirstArmAppearanceAt = 3.112f;
    public const float SecondArmAppearanceAt = 6.154f;
    // Raw ActorControl timeline IDs observed on the arm units. They are the
    // early visual/position cue; the later 31566 cast remains the resolver.
    // The per-arm mapping lives in TopP3IntermissionArmRules.
    public const ushort FirstArmAppearanceTimeline = 0x1E43;
    public const ushort SecondArmAppearanceTimeline = 0x1E44;
    public const float ArmTelegraphSeconds = 13.5f;
    // The central EObj spawns at 1444.099 and despawns at 1463.091.
    public const float CentralVoidzoneSpawnAt = 5.842f;
    public const float CentralVoidzoneDespawnAt = 24.834f;
    // The native spawnobj radius is 1.0; the mechanic's lethal geometry is 6.
    public const float CentralVoidzonePacketRadius = 1f;

    public const float Wave1FirstAt = 5.034f;
    public const float Wave1SecondAt = 7.134f;
    public const float Wave1ThirdAt = 9.192f;
    public const float Wave1FourthAt = 11.247f;
    public const float Wave2FirstAt = 13.168f;
    public const float Wave2SecondAt = 15.310f;
    public const float Wave2ThirdAt = 17.365f;
    public const float Wave2FourthAt = 19.423f;
    public const float FirstArmsAt = 17.099f;
    public const float CannonAt = 19.107f;
    public const float SecondArmsAt = 19.644f;
    public const float CompleteAt = 24.9f;

    public const float FirstWaveCastAt = Wave1FirstAt - Wave1EffectAfterCast;
    public const float SecondWaveCastAt = Wave2FirstAt - Wave2EffectAfterCast;
    public const float FirstArmsCastAt = FirstArmsAt - FirstArmEffectAfterCast;
    public const float SecondArmsCastAt = SecondArmsAt - SecondArmEffectAfterCast;
    public const float Wave1FireDelay = Wave1EffectAfterCast - WaveCastSeconds;
    public const float Wave2FireDelay = Wave2EffectAfterCast - WaveCastSeconds;
    public const float WaveFireDelay = Wave1FireDelay;
    public const float FirstArmFireDelay = FirstArmEffectAfterCast - ArmCastSeconds;
    public const float SecondArmFireDelay = SecondArmEffectAfterCast - ArmCastSeconds;
    public const float ArmFireDelay = FirstArmFireDelay;

    public const float ArenaRadius = 20f;
    public const float RingWidth = 6f;
    public const float CentralVoidzoneRadius = 6f;
    public const float ArmRadius = 11f;
    public const float CannonRadius = 6f;
    public const float MarkerDuration = 20f;
    public const float MoveSpeed = 8f;

    public static readonly IReadOnlyList<float> Wave1RingTimes =
        [Wave1FirstAt, Wave1SecondAt, Wave1ThirdAt, Wave1FourthAt];
    public static readonly IReadOnlyList<float> Wave2RingTimes =
        [Wave2FirstAt, Wave2SecondAt, Wave2ThirdAt, Wave2FourthAt];

    // Centers are projected from the same six-entry native arm table consumed
    // by actor creation and timeline display, so geometry cannot drift from IDs.
    public static readonly IReadOnlyList<Vector3> FirstArmCenters =
        TopP3IntermissionArmRules.ForSet(1).Select(arm => arm.Center).ToArray();

    public static readonly IReadOnlyList<Vector3> SecondArmCenters =
        TopP3IntermissionArmRules.ForSet(2).Select(arm => arm.Center).ToArray();

    // Fixed cannon locations, in recording P01..P08 order.  X/Z are local arena
    // coordinates; y remains the floor plane for every simulated position.
    private static readonly IReadOnlyDictionary<PartyRole, Vector3> CannonPositions =
        new Dictionary<PartyRole, Vector3>
        {
            [PartyRole.ShieldHealer] = new(17f, 0f, -4f),  // P01 SGE
            [PartyRole.MainTank] = new(-12f, 0f, -11f),     // P02 DRK
            [PartyRole.CasterDps] = new(12f, 0f, -11f),     // P03 BLM
            [PartyRole.OffTank] = new(-4f, 0f, 16f),        // P04 PLD
            [PartyRole.RegenHealer] = new(-17f, 0f, -4f),   // P05 WHM
            [PartyRole.PhysRangedDps] = new(12f, 0f, -10f), // P06 MCH
            [PartyRole.MeleeDpsA] = new(4f, 0f, 16f),       // P07 SAM
            [PartyRole.MeleeDpsB] = new(-12f, 0f, -10f),   // P08 RPR
        };

    public static readonly IReadOnlyList<PartyRole> RecordingOrder =
    [
        PartyRole.ShieldHealer,
        PartyRole.MainTank,
        PartyRole.CasterDps,
        PartyRole.OffTank,
        PartyRole.RegenHealer,
        PartyRole.PhysRangedDps,
        PartyRole.MeleeDpsA,
        PartyRole.MeleeDpsB,
    ];

    // P01/P04/P05/P07 are one-person circles. P02/P08 and P03/P06 are the two
    // native high-powered two-person shares. Each pair is one cannon group even
    // though the raw effect packet contains both actors in the source list.
    public static readonly IReadOnlyList<TopP3CannonGroup> CannonGroups =
    [
        new("P01", TopP3CannonKind.Spread, PartyRole.ShieldHealer, [PartyRole.ShieldHealer]),
        new("P02+P08", TopP3CannonKind.Share, PartyRole.MainTank,
            [PartyRole.MainTank, PartyRole.MeleeDpsB]),
        new("P03+P06", TopP3CannonKind.Share, PartyRole.CasterDps,
            [PartyRole.CasterDps, PartyRole.PhysRangedDps]),
        new("P04", TopP3CannonKind.Spread, PartyRole.OffTank, [PartyRole.OffTank]),
        new("P05", TopP3CannonKind.Spread, PartyRole.RegenHealer, [PartyRole.RegenHealer]),
        new("P07", TopP3CannonKind.Spread, PartyRole.MeleeDpsA, [PartyRole.MeleeDpsA]),
    ];

    public static IReadOnlyList<TopP3CannonGroup> ResolutionGroups => CannonGroups;

    // Only the marker actor receives a native point status. RequiredRoles is a
    // resolution rule, not a list of marked actors (P06/P08 are unmarked soakers).
    public static readonly IReadOnlyList<TopP3MarkerAssignment> MarkerAssignments =
        CannonGroups
            .Select(group => new TopP3MarkerAssignment(group.MarkerRole, group.Kind))
            .ToArray();

    private static readonly IReadOnlyDictionary<PartyRole, Vector3> PreArmPositions =
        new Dictionary<PartyRole, Vector3>
        {
            [PartyRole.ShieldHealer] = new(10.74f, 0f, 4.12f),
            [PartyRole.MainTank] = new(-1.8f, 0f, -11.36f),
            [PartyRole.CasterDps] = new(1.8f, 0f, -11.36f),
            [PartyRole.OffTank] = new(-8.94f, 0f, 7.24f),
            [PartyRole.RegenHealer] = new(-10.74f, 0f, 4.12f),
            [PartyRole.PhysRangedDps] = new(1.8f, 0f, -11.36f),
            [PartyRole.MeleeDpsA] = new(8.94f, 0f, 7.24f),
            [PartyRole.MeleeDpsB] = new(-1.8f, 0f, -11.36f),
        };

    public static Vector3 CannonPositionFor(PartyRole role) => CannonPositions[role];

    public static TopP3CannonGroup CannonGroupFor(PartyRole role)
    {
        foreach (var group in ResolutionGroups)
            if (group.RequiredRoles.Contains(role)) return group;
        throw new ArgumentOutOfRangeException(nameof(role));
    }

    public static Vector3 CenterForArmSet(int set, int index)
    {
        var centers = set switch
        {
            1 => FirstArmCenters,
            2 => SecondArmCenters,
            _ => throw new ArgumentOutOfRangeException(nameof(set)),
        };
        if (index < 0 || index >= centers.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return centers[index];
    }

    public static IReadOnlyList<float> RingTimes(int wave)
        => wave switch
        {
            1 => Wave1RingTimes,
            2 => Wave2RingTimes,
            _ => throw new ArgumentOutOfRangeException(nameof(wave)),
        };

    public static bool IsInsideCircle(Vector3 position, Vector3 center, float radius)
    {
        var dx = position.X - center.X;
        var dz = position.Z - center.Z;
        return dx * dx + dz * dz <= radius * radius;
    }

    public static bool IsInsideArena(Vector3 position)
        => position.X * position.X + position.Z * position.Z <= ArenaRadius * ArenaRadius;

    public static bool IsInsideCentralVoidzone(Vector3 position)
        => IsInsideCircle(position, Vector3.Zero, CentralVoidzoneRadius);

    public static float MinimumRadiusOnSegment(Vector3 from, Vector3 to)
    {
        var a = new Vector2(from.X, from.Z);
        var delta = new Vector2(to.X - from.X, to.Z - from.Z);
        var denominator = Vector2.Dot(delta, delta);
        var t = denominator <= 0f
            ? 0f
            : Math.Clamp(-Vector2.Dot(a, delta) / denominator, 0f, 1f);
        var closest = a + delta * t;
        return closest.Length();
    }

    public static bool SegmentClearsCentralVoidzone(Vector3 from, Vector3 to)
        => MinimumRadiusOnSegment(from, to) > CentralVoidzoneRadius;

    // Ring index 0 is the centre (0..6), then 6..12, 12..18, and 18..24.
    public static int RingIndex(Vector3 position)
    {
        var radius = MathF.Sqrt(position.X * position.X + position.Z * position.Z);
        return Math.Clamp((int)(radius / RingWidth), 0, 3);
    }

    public static bool IsInsideWaveRing(Vector3 position, int ring)
    {
        if (ring < 0 || ring > 3) throw new ArgumentOutOfRangeException(nameof(ring));
        var radius = MathF.Sqrt(position.X * position.X + position.Z * position.Z);
        var inner = ring * RingWidth;
        var outer = inner + RingWidth;
        return radius >= inner && radius < outer;
    }

    public static IReadOnlyList<PartyRole> MembersInsideCircle(
        IReadOnlyList<TopP3Position> members,
        Vector3 center,
        float radius)
        => members
            .Where(member => IsInsideCircle(member.Position, center, radius))
            .Select(member => member.Role)
            .ToArray();

    public static IReadOnlyList<PartyRole> MembersInsideWaveRing(
        IReadOnlyList<TopP3Position> members,
        int ring)
        => members
            .Where(member => IsInsideWaveRing(member.Position, ring))
            .Select(member => member.Role)
            .ToArray();

    public static IReadOnlyList<TopP3HitCount> CountArmHits(
        IReadOnlyList<TopP3Position> members,
        IReadOnlyList<Vector3> centers)
    {
        var counts = RecordingOrder.ToDictionary(role => role, _ => 0);
        foreach (var center in centers)
            foreach (var member in members)
                if (IsInsideCircle(member.Position, center, ArmRadius))
                    counts[member.Role]++;
        return RecordingOrder.Select(role => new TopP3HitCount(role, counts[role])).ToArray();
    }

    public static IReadOnlyList<TopP3HitCount> CountCannonHits(
        IReadOnlyList<TopP3Position> members)
    {
        var counts = RecordingOrder.ToDictionary(role => role, _ => 0);
        foreach (var group in ResolutionGroups)
        {
            // CannonPositionFor is an AI destination only. At resolution the
            // marker actor's snapshot is the native circle centre; no ideal
            // coordinate may turn a missing/dead marker into a success.
            if (!TryMarkerPosition(members, group.MarkerRole, out var center)) continue;
            foreach (var member in members)
                if (IsInsideCircle(member.Position, center, CannonRadius))
                    counts[member.Role]++;
        }
        return RecordingOrder.Select(role => new TopP3HitCount(role, counts[role])).ToArray();
    }

    public static IReadOnlyList<TopP3CannonResolution> ResolveCannons(
        IReadOnlyList<TopP3Position> members)
    {
        var results = new List<TopP3CannonResolution>(ResolutionGroups.Count);
        foreach (var group in ResolutionGroups)
        {
            var count = TryMarkerPosition(members, group.MarkerRole, out var center)
                ? MembersInsideCircle(members, center, CannonRadius).Count
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

    public static bool TryMarkerPosition(
        IReadOnlyList<TopP3Position> members,
        PartyRole markerRole,
        out Vector3 position)
    {
        foreach (var member in members)
        {
            if (member.Role != markerRole) continue;
            position = member.Position;
            return true;
        }
        position = default;
        return false;
    }

    /// <summary>
    /// Returns recorded-relative target waypoints. Every target and every
    /// segment stays outside the native central radius-6 voidzone.
    /// </summary>
    public static IReadOnlyList<TopP3RoutePoint> RouteFor(PartyRole role)
    {
        var preArm = PreArmPositions[role];
        var direction = Vector3.Normalize(new Vector3(preArm.X, 0f, preArm.Z));
        var outer = direction * 19.5f;
        var waveSafe = direction * 8f;
        var secondWaveSafe = direction * 13f;
        var final = CannonPositionFor(role);
        return
        [
            new(QueueAt, outer),
            new(Wave1ThirdAt + 0.05f, waveSafe),
            new(Wave1FourthAt + 0.05f, secondWaveSafe),
            new(Wave2SecondAt + 0.05f, preArm),
            new(Wave2ThirdAt + 0.05f, final),
            new(CannonAt, final),
        ];
    }

    public static float RuntimeAt(float recordedOffset)
        => PreparationSeconds + recordedOffset;

    public static IReadOnlyList<TopP3RoutePoint> RuntimeRouteFor(PartyRole role)
        => RouteFor(role)
            .Select(point => new TopP3RoutePoint(RuntimeAt(point.At), point.Target))
            .ToArray();

    /// <summary>
    /// Evaluates the physical route in runtime scenario seconds. The route
    /// itself is recorded-relative; RuntimeRouteFor is the schedule consumed by
    /// the AI. Keeping this contract runtime-based catches a prep-clock mismatch.
    /// </summary>
    public static Vector3 PositionAt(PartyRole role, float runtimeTime)
    {
        var recordedTime = runtimeTime - PreparationSeconds;
        var route = RouteFor(role);
        var position = route[0].Target;
        var arrival = route[0].At;
        for (var i = 1; i < route.Count; i++)
        {
            var depart = MathF.Max(route[i].At, arrival);
            var target = route[i].Target;
            var travel = Vector3.Distance(position, target) / MoveSpeed;
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
}
