using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game.Party;

namespace AnoMech.Scenarios.Top.P3HelloWorld;

/// <summary>One immutable permutation of the recorded duties and rot colors.</summary>
public sealed class TopP3HelloWorldPattern
{
    private readonly Dictionary<(int, TopP3HelloWorldRole), IReadOnlyList<PartyRole>> groups = [];
    private readonly Dictionary<int, IReadOnlyList<PartyRole>> orders = [];
    private readonly Dictionary<int, IReadOnlyList<TopP3HelloWorldTower>> towers = [];
    private readonly Dictionary<int, IReadOnlyList<TopP3HelloWorldContactExpectation>> contacts = [];
    private readonly Dictionary<(int, PartyRole), Vector3> positions = [];
    private readonly Dictionary<(int, PartyRole), Vector3> departures = [];
    private readonly Dictionary<(int, PartyRole), IReadOnlyList<PartyRole>> stackPartners = [];
    private readonly Dictionary<(int, PartyRole), PartyRole> localPartners = [];
    private readonly Dictionary<PartyRole, IReadOnlyList<TopP3HelloWorldRoutePoint>> routes = [];

    public TopP3HelloWorldPattern(
        PartyRole playerRole = PartyRole.MainTank,
        TopP3HelloWorldRole? startingRole = null,
        TopP3HelloWorldColor defamationColor = TopP3HelloWorldColor.Blue)
    {
        if (defamationColor is not (TopP3HelloWorldColor.Blue or TopP3HelloWorldColor.Red))
            throw new ArgumentOutOfRangeException(nameof(defamationColor));
        if (startingRole.HasValue && !Enum.IsDefined(startingRole.Value))
            throw new ArgumentOutOfRangeException(nameof(startingRole));
        DefamationColor = defamationColor;
        var kinds = Enum.GetValues<TopP3HelloWorldRole>();
        var original = kinds.Single(kind => TopP3HelloWorldRules.RoleGroup(1, kind).Contains(playerRole));
        var shift = startingRole.HasValue ? ((int)original - (int)startingRole.Value + 4) % 4 : 0;
        var mappedRoles = new Dictionary<PartyRole, PartyRole>();
        var canonicalRoles = new Dictionary<PartyRole, PartyRole>();
        foreach (var kind in kinds)
        {
            var source = TopP3HelloWorldRules.RoleGroup(1, kind);
            var destination = TopP3HelloWorldRules.RoleGroup(1, (TopP3HelloWorldRole)(((int)kind + shift) % 4));
            for (var index = 0; index < source.Count; index++)
            {
                mappedRoles.Add(source[index], destination[index]);
                canonicalRoles.Add(destination[index], source[index]);
            }
        }

        var expectations = TopP3HelloWorldRules.ExpectedContactExpectations.Select(contact => contact with
        {
            Receiver = mappedRoles[contact.Receiver],
            Source = mappedRoles[contact.Source],
            Color = MapColor(contact.Color),
        }).ToArray();
        ExpectedContactExpectations = Array.AsReadOnly(expectations);
        for (var round = 1; round <= 4; round++)
        {
            foreach (var kind in kinds)
                groups.Add((round, kind), Array.AsReadOnly(TopP3HelloWorldRules.RoleGroup(round, kind)
                    .Select(role => mappedRoles[role]).ToArray()));
            orders.Add(round, Array.AsReadOnly(TopP3HelloWorldRules.RoleOrderForRound(round)
                .Select(role => mappedRoles[role]).ToArray()));
            towers.Add(round, Array.AsReadOnly(TopP3HelloWorldRules.TowersForRound(round)
                .Select(tower => tower with { Owner = mappedRoles[tower.Owner], Color = MapColor(tower.Color) }).ToArray()));
            contacts.Add(round, Array.AsReadOnly(expectations.Where(contact => contact.Round == round).ToArray()));
            foreach (var (role, canonical) in canonicalRoles)
            {
                positions.Add((round, role), TopP3HelloWorldRules.PositionForRound(round, canonical));
                departures.Add((round, role), TopP3HelloWorldRules.PostContactPositionFor(round, canonical));
                stackPartners.Add((round, role), Array.AsReadOnly(TopP3HelloWorldRules.StackPartnersFor(round, canonical)
                    .Select(partner => mappedRoles[partner]).ToArray()));
                if (TopP3HelloWorldRules.TryLocalPartner(round, canonical, out var partner))
                    localPartners.Add((round, role), mappedRoles[partner]);
            }
        }
        foreach (var (role, canonical) in canonicalRoles)
            routes.Add(role, Array.AsReadOnly(TopP3HelloWorldRules.RuntimeRouteFor(canonical).ToArray()));
    }

    public TopP3HelloWorldColor DefamationColor { get; }
    public TopP3HelloWorldColor StackColor => DefamationColor == TopP3HelloWorldColor.Blue
        ? TopP3HelloWorldColor.Red : TopP3HelloWorldColor.Blue;
    public IReadOnlyList<TopP3HelloWorldContactExpectation> ExpectedContactExpectations { get; }
    public IReadOnlyList<PartyRole> RoleGroup(int round, TopP3HelloWorldRole kind) => groups[(round, kind)];
    public IReadOnlyList<PartyRole> RoleOrderForRound(int round) => orders[round];
    public IReadOnlyList<TopP3HelloWorldTower> TowersForRound(int round) => towers[round];
    public IReadOnlyList<TopP3HelloWorldContactExpectation> ContactsForRound(int round) => contacts[round];
    public Vector3 PositionForRound(int round, PartyRole role) => positions[(round, role)];
    public IReadOnlyList<TopP3HelloWorldRoutePoint> RuntimeRouteFor(PartyRole role) => routes[role];
    public Vector3 PostContactPositionFor(int round, PartyRole role) => departures[(round, role)];
    public IReadOnlyList<PartyRole> StackPartnersFor(int round, PartyRole owner) => stackPartners[(round, owner)];
    public bool TryLocalPartner(int round, PartyRole owner, out PartyRole partner)
        => localPartners.TryGetValue((round, owner), out partner);
    public Vector3 TetherPositionFor(int round, TopP3HelloWorldRole kind, PartyRole role)
        => RoleGroup(round, kind).Contains(role)
            ? PostContactPositionFor(round, role) : PositionForRound(round, role);
    public Vector3 ContactPositionFor(int round, PartyRole role)
    {
        foreach (var contact in ContactsForRound(round))
            if (contact.Receiver == role)
                return PositionForRound(round, contact.Source);
        return round == 4 && RoleGroup(round, TopP3HelloWorldRole.LocalTether).Contains(role)
            ? LocalBreakPosition(round) : PositionForRound(round, role);
    }
    public Vector3 AoePositionFor(int round, PartyRole role) => PositionForRound(round, role);
    public Vector3 LocalBreakPosition(int round)
    {
        var pair = RoleGroup(round, round == 4
            ? TopP3HelloWorldRole.LocalTether : TopP3HelloWorldRole.Defamation);
        return (PositionForRound(round, pair[0]) + PositionForRound(round, pair[1])) * 0.5f;
    }

    private TopP3HelloWorldColor MapColor(TopP3HelloWorldColor color)
        => color == TopP3HelloWorldColor.Blue ? DefamationColor : StackColor;
}
