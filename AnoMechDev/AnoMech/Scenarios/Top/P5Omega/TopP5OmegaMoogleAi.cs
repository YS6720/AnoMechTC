using System.Collections.Generic;
using System.Linq;
using AnoMech.Core;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using AnoMech.Scenarios.Top.P3Monitors;

namespace AnoMech.Scenarios.Top.P5Omega;

public sealed class TopP5OmegaMoogleAi : TopP5OmegaAi
{
    public override string Name => "打法 莫古力";
    public override string? Group => "陸服";
    private bool secondRound;

    protected override RoleList solveHelloWorld1(SimParty party)
    {
        secondRound = false;
        var free = TopP3MonitorRules.Priority.Where(r => !state.HelloWorldTargets.List.Take(2).Contains(r)).ToArray();
        var monitors = free.Where(state.DoubleDynamicTargets.List.Contains)
            .OrderBy(r => state.HelloWorldTargets.List.Skip(2).Contains(r) ? 0 : 1)
            .Take(2).ToArray();
        var jumps = free.Where(r => !monitors.Contains(r))
            .OrderBy(r => state.DoubleDynamicTargets.List.Contains(r) ? 1 : 0).ToArray();
        return new(party, [state.HelloWorldTargets[0], state.HelloWorldTargets[1], .. monitors, .. jumps]);
    }

    protected override RoleList solveHelloWorld2(SimParty party)
    {
        secondRound = true;
        var free = TopP3MonitorRules.Priority.Where(r => !state.HelloWorldTargets.List.Skip(2).Contains(r)).ToArray();
        var tethers = free.Where(r => party.Get(r)?.FindStatus(TopConstants.StatusId.QuickeningDynamis) is { Stacks: 3 }).ToArray();
        var jumps = free.Where(r => !tethers.Contains(r)).ToArray();
        return new(party, [state.HelloWorldTargets[2], state.HelloWorldTargets[3], .. tethers, .. jumps]);
    }

    protected override Dictionary<PartyRole, Sign> HelloWorldMarkers(RoleList? list)
    {
        if (list == null) return [];
        var mapping = new Dictionary<PartyRole, Sign>();
        if (secondRound)
        {
            mapping[list[2]] = Sign.Ignore1;
            mapping[list[3]] = Sign.Ignore2;
            for (var i = 0; i < 4; i++) mapping[list[i + 4]] = (Sign)((int)Sign.Attack1 + i);
            return mapping;
        }
        var attack = 0;
        var ignore = 0;
        var bind = 0;
        Sign[] binds = [Sign.Bind1, Sign.Bind2, Sign.Bind3, Sign.Square];
        foreach (var role in TopP3MonitorRules.Priority)
        {
            if (state.HelloWorldTargets.List.Take(2).Contains(role)) continue;
            mapping[role] = !state.DoubleDynamicTargets.List.Contains(role)
                ? (Sign)((int)Sign.Attack1 + attack++)
                : state.HelloWorldTargets.List.Skip(2).Contains(role)
                    ? (ignore++ == 0 ? Sign.Ignore1 : Sign.Ignore2)
                    : binds[bind++];
        }
        return mapping;
    }

    protected override RoleList? ReadHandPlacedSigns(SimParty party, RoleList? fallback) => fallback == null ? null :
        HandPlacedSigns.Reorder(party, fallback,
            HelloWorldMarkers(fallback).Select(p => (p.Value, System.Array.IndexOf(fallback.List, p.Key))).ToArray(),
            [fallback[0], fallback[1]]);

    protected override IAiMove HelloWorld1Pos() => AiMove.Create(
        new(10, 0), new(.2f, 10), new(-10, 10), new(-10, -10),
        new(.2f, 19), new(19, 4), new(19, -4), new(.2f, -19))
        .Assignments(helloWorld1?.List).ApplyPositions(p => p.Multiply(state.MonitorSide.Mul));
}
