using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game.Party;
using AnoMech.Scenarios.Top.P3Practice;

namespace AnoMech.Scenarios.Top.P3HelloWorld;

public enum TopP3HelloWorldColor
{
    None,
    Blue,
    Red,
}

public enum TopP3HelloWorldRole
{
    Defamation,
    RemoteTether,
    Stack,
    LocalTether,
}

public enum TopP3HelloWorldFailure
{
    None,
    WrongTower,
    EmptyTower,
    OverlappingTower,
    WrongColor,
    WrongDefamation,
    WrongStack,
    MissingContact,
    WrongTetherBreak,
    ColorExplosion,
    LatentExpired,
    Incomplete,
    PlayerDied,
}

public readonly record struct TopP3HelloWorldPosition(
    Vector3 Current,
    Vector3? Previous = null);

public readonly record struct TopP3HelloWorldContact(
    int Round,
    PartyRole Receiver,
    PartyRole Source,
    TopP3HelloWorldColor Color,
    float At,
    bool VaccineBlocked = false);

public readonly record struct TopP3HelloWorldTower(
    int Round,
    int Index,
    TopP3HelloWorldColor Color,
    Vector3 Center,
    PartyRole Owner);

public readonly record struct TopP3HelloWorldRoutePoint(
    float At,
    Vector3 Target)
{
    public bool ContactOnly { get; init; }
    public int RequiredContactRound { get; init; }
}

public readonly record struct TopP3HelloWorldRoundTimes(
    int Round,
    float TowerCastAt,
    float LineStatusAt,
    float AoeAt,
    float TowerEffectAt);

public readonly record struct TopP3HelloWorldContactExpectation(
    int Round,
    PartyRole Receiver,
    PartyRole Source,
    TopP3HelloWorldColor Color);

/// <summary>
/// Pure rules for the independent P3 Hello World rehearsal.  All values are
/// recording-relative to the 31573 cast; RuntimeAt is the only prep-clock seam.
/// </summary>
public static partial class TopP3HelloWorldRules
{
    public static class ActionId
    {
        public const uint HelloWorld = 31573;
        public const uint SynchronizationBug = 31574;
        public const uint OverflowBug = 31575;
        public const uint RedRot = 31578;
        public const uint BlueRot = 31579;
        public const uint RedTower = 31583;
        public const uint BlueTower = 31584;
        public const uint TetherBreak = 31587;
        public const uint TetherFail = 32505;
        public const uint HelloWorldFail = 31627;
    }

    public static class StatusId
    {
        public const ushort NeedStack = 3434;
        public const ushort PrepStack = 3436;
        public const ushort PrepDefamation = 3437;
        public const ushort PrepRedRot = 3438;
        public const ushort PrepBlueRot = 3439;
        public const ushort PrepLocalTether = 3503;
        public const ushort PrepRemoteTether = 3441;
        public const ushort Stack = 3524;
        public const ushort Defamation = 3525;
        public const ushort RedRot = 3526;
        public const ushort BlueRot = 3429;
        public const ushort NeedDefamation = 3527;
        public const ushort RedLatent = 3528;
        public const ushort LocalTether = 3529;
        public const ushort RemoteTether = 3530;
        public const ushort BlueLatent = 3435;
        public const ushort StackVaccine = 3430;
        public const ushort DefamationVaccine = 3431;
        public const ushort RedVaccine = 3432;
        public const ushort BlueVaccine = 3433;
        public const ushort NearWorld = 3442;
        public const ushort DistantWorld = 3443;
    }

    public static class TetherId
    {
        public const ushort PrepLocal = 200;
        public const ushort PrepRemote = 201;
        public const ushort Local = 224;
        public const ushort Remote = 225;
    }

    public static class EObjId
    {
        public const uint TowerSolo = 2013245;
        public const uint TowerPair = 2013246;
    }

    public const float PreparationSeconds = 5f;
    public const float HelloWorldCastAt = 0f;
    public const float HelloWorldCastSeconds = 4.971f;
    public const float InitialEffectAt = 4.971f;
    public const float InitialStatusAt = 4.984f;
    public const float InitialLineStatusAt = 5.084f;
    public const float InitialActiveStatusAt = 8.051f;
    public const float ColorDuration = 27f;
    public const float ContactRadiusDefault = 2f;
    public const float ContactRadiusMin = 0.5f;
    public const float ContactRadiusMax = 3f;
    public const float TowerRadius = 6f;
    public const float StackRadius = 5f;
    public const float DefamationRadius = 20f;
    public const float RotRadius = 5f;
    public const float TetherBreakDistance = 10f;
    public const float ActiveTetherDuration = 10f;
    public const float MoveSpeed = 8f;
    public const float EndAt = 105.504f;
    public const float CompleteAt = EndAt;
    public const float InitialNeedStackDuration = 68.987f;
    public const float InitialNeedDefamationDuration = 26.987f;
    public const float InitialActiveDuration = 26.958f;
    public const float InitialLatentDuration = 20.958f;

    // Active mechanic expiry is tied to recorded AOE boundaries rather than a
    // guessed status duration. The timeline resolves a fixed event at this
    // boundary before treating the active requirement as expired.
    public static float ActiveExpiryAt(int round)
        => RuntimeAt(RoundTimes(round).AoeAt);

    public static float NextActiveExpiryAt(int round)
        => RuntimeAt(round < 4 ? RoundTimes(round + 1).AoeAt : EndAt);

    public static float TetherExpiryAt(int round)
        => RuntimeAt(RoundTimes(round).LineStatusAt + ActiveTetherDuration);

    private static readonly Vector3[] BaseTowerCenters =
    [
        new(-14f, 0f, 0f),
        new(0f, 0f, 14f),
        new(14f, 0f, 0f),
        new(0f, 0f, -14f),
    ];

    private static readonly IReadOnlyDictionary<PartyRole, Vector3> FixedPositions =
        new Dictionary<PartyRole, Vector3>
        {
            [PartyRole.ShieldHealer] = new(-17f, 0f, 0f),
            [PartyRole.MainTank] = new(0f, 0f, 17f),
            [PartyRole.CasterDps] = new(12f, 0f, -3f),
            [PartyRole.OffTank] = new(3f, 0f, -12f),
            [PartyRole.RegenHealer] = new(-16f, 0f, -8f),
            [PartyRole.PhysRangedDps] = new(8f, 0f, 12f),
            [PartyRole.MeleeDpsA] = new(8f, 0f, -3f),
            [PartyRole.MeleeDpsB] = new(3f, 0f, -8f),
        };

    // Recording order per round: defamation, stack, local, remote; first two own towers.
    private static readonly IReadOnlyList<PartyRole[]> RoundRoleGroups =
    [
        [PartyRole.MainTank, PartyRole.PhysRangedDps],
        [PartyRole.OffTank, PartyRole.MeleeDpsB],
        [PartyRole.MeleeDpsA, PartyRole.ShieldHealer],
        [PartyRole.CasterDps, PartyRole.RegenHealer],

        [PartyRole.MeleeDpsA, PartyRole.ShieldHealer],
        [PartyRole.CasterDps, PartyRole.RegenHealer],
        [PartyRole.MeleeDpsB, PartyRole.OffTank],
        [PartyRole.MainTank, PartyRole.PhysRangedDps],

        [PartyRole.OffTank, PartyRole.MeleeDpsB],
        [PartyRole.MainTank, PartyRole.PhysRangedDps],
        [PartyRole.CasterDps, PartyRole.RegenHealer],
        [PartyRole.MeleeDpsA, PartyRole.ShieldHealer],

        [PartyRole.RegenHealer, PartyRole.CasterDps],
        [PartyRole.ShieldHealer, PartyRole.MeleeDpsA],
        [PartyRole.MainTank, PartyRole.PhysRangedDps],
        [PartyRole.OffTank, PartyRole.MeleeDpsB],
    ];

    private static readonly IReadOnlyList<TopP3HelloWorldContactExpectation> ExpectedContacts =
    [
        new(1, PartyRole.MeleeDpsA, PartyRole.MainTank, TopP3HelloWorldColor.Blue),
        new(1, PartyRole.ShieldHealer, PartyRole.PhysRangedDps, TopP3HelloWorldColor.Blue),
        new(1, PartyRole.CasterDps, PartyRole.OffTank, TopP3HelloWorldColor.Red),
        new(1, PartyRole.RegenHealer, PartyRole.MeleeDpsB, TopP3HelloWorldColor.Red),
        new(2, PartyRole.MeleeDpsB, PartyRole.MeleeDpsA, TopP3HelloWorldColor.Blue),
        new(2, PartyRole.OffTank, PartyRole.ShieldHealer, TopP3HelloWorldColor.Blue),
        new(2, PartyRole.MainTank, PartyRole.CasterDps, TopP3HelloWorldColor.Red),
        new(2, PartyRole.PhysRangedDps, PartyRole.RegenHealer, TopP3HelloWorldColor.Red),
        new(3, PartyRole.CasterDps, PartyRole.OffTank, TopP3HelloWorldColor.Blue),
        new(3, PartyRole.RegenHealer, PartyRole.MeleeDpsB, TopP3HelloWorldColor.Blue),
        new(3, PartyRole.MeleeDpsA, PartyRole.MainTank, TopP3HelloWorldColor.Red),
        new(3, PartyRole.ShieldHealer, PartyRole.PhysRangedDps, TopP3HelloWorldColor.Red),
    ];

    private static readonly IReadOnlyDictionary<(int Round, PartyRole Owner), PartyRole[]> StackPartners =
        new Dictionary<(int, PartyRole), PartyRole[]>
        {
            [(1, PartyRole.OffTank)] = [PartyRole.CasterDps],
            [(1, PartyRole.MeleeDpsB)] = [PartyRole.RegenHealer],
            [(2, PartyRole.CasterDps)] = [PartyRole.MainTank],
            [(2, PartyRole.RegenHealer)] = [PartyRole.PhysRangedDps],
            [(3, PartyRole.MainTank)] = [PartyRole.MeleeDpsA],
            [(3, PartyRole.PhysRangedDps)] = [PartyRole.ShieldHealer],
            [(4, PartyRole.ShieldHealer)] = [PartyRole.MainTank, PartyRole.OffTank],
            [(4, PartyRole.MeleeDpsA)] = [PartyRole.PhysRangedDps, PartyRole.MeleeDpsB],
        };

    private static readonly IReadOnlyDictionary<(int Round, PartyRole Owner), PartyRole> LocalPartners =
        new Dictionary<(int, PartyRole), PartyRole>
        {
            [(1, PartyRole.MainTank)] = PartyRole.MeleeDpsA,
            [(1, PartyRole.PhysRangedDps)] = PartyRole.ShieldHealer,
            [(2, PartyRole.MeleeDpsA)] = PartyRole.MeleeDpsB,
            [(2, PartyRole.ShieldHealer)] = PartyRole.OffTank,
            [(3, PartyRole.OffTank)] = PartyRole.CasterDps,
            [(3, PartyRole.MeleeDpsB)] = PartyRole.RegenHealer,
        };

    public static IReadOnlyList<PartyRole> RecordingOrder => TopP3PracticeParty.RecordingOrder;

    public static float RuntimeAt(float recordingOffset)
        => PreparationSeconds + recordingOffset;

    public static TopP3HelloWorldRoundTimes RoundTimes(int round)
    {
        return round switch
        {
            1 => new(1, 19.182f, 28.066f, 29.109f, 29.154f),
            2 => new(2, 40.239f, 49.011f, 50.158f, 50.203f),
            3 => new(3, 61.293f, 70.031f, 71.219f, 71.261f),
            4 => new(4, 82.346f, 90.994f, 92.272f, 92.318f),
            _ => throw new ArgumentOutOfRangeException(nameof(round)),
        };
    }

    public static IReadOnlyList<PartyRole> RoleGroup(int round, TopP3HelloWorldRole role)
    {
        if (round is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(round));
        var group = role switch
        {
            TopP3HelloWorldRole.Defamation => 0,
            TopP3HelloWorldRole.Stack => 1,
            TopP3HelloWorldRole.LocalTether => 2,
            TopP3HelloWorldRole.RemoteTether => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
        return RoundRoleGroups[(round - 1) * 4 + group];
    }

    public static IReadOnlyList<PartyRole> RoleOrderForRound(int round)
        => new[]
        {
            TopP3HelloWorldRole.Defamation,
            TopP3HelloWorldRole.Stack,
            TopP3HelloWorldRole.LocalTether,
            TopP3HelloWorldRole.RemoteTether,
        }.SelectMany(role => RoleGroup(round, role)).ToArray();

    public static int ExpectedContactCount => ExpectedContacts.Count;
    public static IReadOnlyList<TopP3HelloWorldContactExpectation> ExpectedContactExpectations
        => ExpectedContacts;

    public static int ContactRoundFor(float now)
    {
        var recording = now >= PreparationSeconds ? now - PreparationSeconds : now;
        for (var round = 1; round <= 4; round++)
        {
            var start = round == 1 ? 0f : RoundTimes(round).TowerCastAt - 1f;
            var end = round == 4 ? EndAt : RoundTimes(round + 1).TowerCastAt - 1f;
            if (recording >= start && recording < end) return round;
        }
        return recording < RoundTimes(1).TowerCastAt ? 1 : 4;
    }

    public static IReadOnlyList<TopP3HelloWorldContactExpectation> ContactsForRound(int round)
        => ExpectedContacts.Where(contact => contact.Round == round).ToArray();

    public static IReadOnlyList<TopP3HelloWorldContactExpectation> ExpectedContactsFor(
        PartyRole receiver,
        PartyRole source,
        TopP3HelloWorldColor color)
        => ExpectedContacts.Where(contact => contact.Receiver == receiver
                                             && contact.Source == source
                                             && contact.Color == color).ToArray();
    public static int RoleOrderIndex(PartyRole role)
        => RecordingOrder.Select((candidate, index) => (candidate, index))
            .First(item => item.candidate == role).index;
    public static IReadOnlyList<PartyRole> StackPartnersFor(int round, PartyRole owner)
        => StackPartners.TryGetValue((round, owner), out var partners) ? partners : Array.Empty<PartyRole>();

    public static bool TryLocalPartner(int round, PartyRole owner, out PartyRole partner)
        => LocalPartners.TryGetValue((round, owner), out partner);
    public static Vector3 FixedPositionFor(PartyRole role) => FixedPositions[role];

    public static Vector3 PositionForRound(int round, PartyRole role)
    {
        var index = RoleOrderForRound(round)
            .Select((candidate, index) => (candidate, index))
            .First(item => item.candidate == role).index;
        var position = index switch
        {
            0 => FixedPositionFor(RecordingOrder[0]),
            1 => FixedPositionFor(RecordingOrder[1]),
            2 => FixedPositionFor(RecordingOrder[2]),
            3 => FixedPositionFor(RecordingOrder[3]),
            4 when round == 4 => new(9f, 0f, -4f),
            5 when round == 4 => new(5f, 0f, -10f),
            6 when round == 4 => new(9f, 0f, -6f),
            7 when round == 4 => new(6f, 0f, -9f),
            _ => FixedPositionFor(RecordingOrder[index]),
        };
        return round == 3 ? RotateThirdRound(position) : position;
    }

    public static IReadOnlyList<Vector3> TowerCentersFor(int round)
    {
        if (round is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(round));
        if (round != 3) return BaseTowerCenters;
        return BaseTowerCenters.Select(RotateThirdRound).ToArray();
    }

    public static IReadOnlyList<TopP3HelloWorldTower> TowersForRound(int round)
    {
        var centers = TowerCentersFor(round);
        var defam = RoleGroup(round, TopP3HelloWorldRole.Defamation);
        var stack = RoleGroup(round, TopP3HelloWorldRole.Stack);
        return
        [
            new(round, 0, TopP3HelloWorldColor.Blue, centers[0], defam[0]),
            new(round, 1, TopP3HelloWorldColor.Blue, centers[1], defam[1]),
            new(round, 2, TopP3HelloWorldColor.Red, centers[2], stack[0]),
            new(round, 3, TopP3HelloWorldColor.Red, centers[3], stack[1]),
        ];
    }

    public static Vector3 RotateThirdRound(Vector3 position)
        => new((-position.X - position.Z) / MathF.Sqrt(2f), position.Y,
            (position.X - position.Z) / MathF.Sqrt(2f));
    public static bool Inside(Vector3 position, Vector3 center, float radius)
    {
        var dx = position.X - center.X;
        var dz = position.Z - center.Z;
        return dx * dx + dz * dz <= radius * radius;
    }

    public static float DistanceXZ(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    public static bool SweptContact(
        Vector3 previousReceiver,
        Vector3 previousSource,
        Vector3 receiver,
        Vector3 source,
        float radius)
    {
        var previous = new Vector2(previousReceiver.X - previousSource.X,
            previousReceiver.Z - previousSource.Z);
        var current = new Vector2(receiver.X - source.X, receiver.Z - source.Z);
        var delta = current - previous;
        var denominator = Vector2.Dot(delta, delta);
        var t = denominator <= 0f
            ? 1f
            : Math.Clamp(-Vector2.Dot(previous, delta) / denominator, 0f, 1f);
        return Vector2.DistanceSquared(previous + delta * t, Vector2.Zero) <= radius * radius;
    }
    public static bool Contact(
        TopP3HelloWorldPosition receiver,
        TopP3HelloWorldPosition source,
        float radius)
    {
        if (receiver.Previous is not { } previousReceiver
            || source.Previous is not { } previousSource)
            return false;
        return SweptContact(previousReceiver, previousSource,
            receiver.Current, source.Current, radius);
    }
}
