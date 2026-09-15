using System.Collections.Generic;
using System.Linq;
using AnoMech.Core;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using AnoMech.Scenarios.Top.P3Monitors;

namespace AnoMech.Scenarios.Top.P5Sigma;

public sealed class TopP5SigmaMoogleAi : TopP5SigmaTuuuflessAi
{
    public override string Name => "打法 莫古力";
    public override string? Group => "陸服";
    protected override float WaveCannonMoveAt => 22.3f;

    private PartyRole[] Pair(int index) => state.Order.List.Skip(index * 2).Take(2)
        .OrderBy(TopP3MonitorRules.PriorityIndex).ToArray();

    protected override IAiMove LineupNextToOmegaM() => AiMove.Create(
        new(-6, 6), new(-6, 8), new(-2, 6), new(-2, 8),
        new(2, 6), new(2, 8), new(6, 6), new(6, 8))
        .Assignments(Enumerable.Range(0, 4).SelectMany(Pair).ToArray())
        .ApplyPositions(state.NewNorthA.Apply);

    protected override IReadOnlyList<PartyRole> WaveCannonAssignments()
    {
        var full = Enumerable.Range(0, 4).Where(i => i != state.FirstMissing / 2 && i != state.SecondMissing / 2).ToArray();
        var a = Pair(full[0]);
        var b = Pair(full[1]);
        return [a[0], state.HalfPair(1).baiting, b[1], state.HalfPair(0).marked,
            a[1], state.HalfPair(1).marked, b[0], state.HalfPair(0).baiting];
    }

    protected override RoleList BuildMarkingsOrder(SimWorld world)
    {
        var candidates = TopP3MonitorRules.Priority.Where(r => !state.HelloWorldTargets.List.Contains(r)).ToArray();
        var attacks = candidates.Where(state.DynamisTargets.List.Contains).ToArray();
        var idle = candidates.Where(r => !attacks.Take(4).Contains(r))
            .OrderBy(r => state.DynamisTargets.List.Contains(r) ? 0 : 1).ToArray();
        return new(world.Party, [attacks[1], attacks[0], attacks[2], idle[1], idle[0], attacks[3],
            state.HelloWorldTargets[0], state.HelloWorldTargets[1]]);
    }

    protected override Dictionary<PartyRole, Sign> MarkerMapping()
    {
        var signs = new Dictionary<PartyRole, Sign>
        {
            [markingsOrder[1]] = Sign.Attack1, [markingsOrder[0]] = Sign.Attack2,
            [markingsOrder[2]] = Sign.Attack3, [markingsOrder[5]] = Sign.Attack4,
        };
        var attack = 0;
        var ignore = 0;
        foreach (var role in markingsOrder.List.Skip(3).Take(2).OrderBy(TopP3MonitorRules.PriorityIndex))
            signs[role] = state.DynamisTargets.List.Contains(role)
                ? (attack++ == 0 ? Sign.Attack5 : Sign.Attack6)
                : (ignore++ == 0 ? Sign.Ignore1 : Sign.Ignore2);
        return signs;
    }

    protected override (Sign Sign, int Slot)[] HandPlacedPlan =>
        MarkerMapping().Select(p => (p.Value, System.Array.IndexOf(markingsOrder.List, p.Key))).ToArray();

    protected override IAiMove HelloWorldPositions() => AiMove.Create(
        new(13.5f, -14.2f), new(19.5f, 0), new(-13.5f, -14.2f),
        new(-4, 19), new(4, 19), new(-19.5f, 0), new(0, 10), new(-10, 0))
        .Assignments(markingsOrder.List).ApplyPositions(p => p.MultiplyX(-state.SpinnerRotation.Mul), state.NewNorthB.Apply);
}
