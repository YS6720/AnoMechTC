using System;
using System.Collections.Generic;
using System.Linq;
using AnoMech.Core;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;

namespace AnoMech.Scenarios.Top.P5Omega;

public class TopP5OmegaAi : IScenarioAi<TopP5OmegaState>
{
    public virtual string Name => "Standard";
    public virtual string? Group => null;

    protected TopP5OmegaState state = null!;
    private readonly Random rng = new Random();

    protected RoleList? helloWorld1;
    protected RoleList? helloWorld2;

    public void Run(TopP5OmegaState s, SimWorld world)
    {
        state = s;
        PrepareState();
        helloWorld1 = solveHelloWorld1(world.Party);
        var ai = new AiManager(world, deferToArrival: true);
        ai.Move(0f, InitialPositions);
        ai.Move(20f, Dodge(0), arrivalTime: 24f);
        ai.Move(24f, Dodge(1), arrivalTime: 28f);
        if (state.Markers == MarkerMode.System)
            ai.Automarker(28f, () => HelloWorldMarkers(helloWorld1));
        else
            world.Events.Add(31f, () => helloWorld1 = ReadHandPlacedSigns(world.Party, helloWorld1));
        ai.Move(32f, HelloWorld1Pos, jitter: 0.1f, arrivalTime: 41f);
        world.Events.Add(46f, () => helloWorld2 = solveHelloWorld2(world.Party));
        if (state.Markers == MarkerMode.System)
            ai.Automarker(47f, () => HelloWorldMarkers(helloWorld2));
        else
            world.Events.Add(52f, () => helloWorld2 = ReadHandPlacedSigns(world.Party, helloWorld2));
        ai.Move(48f, GatherMiddle);
        ai.Move(53f, HelloWorld2Pos, arrivalTime: 57f);
        ai.Move(62f, InitialPositions);
    }

    private IAiMove InitialPositions()
    {
        return AiMove.Create(
            new(-2.10f, -5.08f),
            new(2.10f, -5.08f),
            new(-0.7f, 5.7f),
            new(-0.7f, 6.5f),
            new(-0.7f, 7.3f),
            new(0.7f, 5.7f),
            new(0.7f, 6.5f),
            new(0.7f, 7.3f)
        ).NaturalOrder();
    }



    private Func<IAiMove> Dodge(int attack)
    {
        return () => AiMove.All(new(0, -1f))
                           .ApplyPositions(
                               AdjustSafeCardinal(attack),
                               AdjustSafeSpot(attack)
                           );
    }

    protected virtual RoleList? ReadHandPlacedSigns(SimParty party, RoleList? fallback) => fallback == null
        ? null
        : HandPlacedSigns.Reorder(party, fallback, HandPlacedPlan, [fallback[0], fallback[1]]);

    protected virtual (Sign Sign, int Slot)[] HandPlacedPlan =>
    [
        (Sign.Bind1, 2), (Sign.Bind2, 3),
        (Sign.Attack1, 4), (Sign.Attack2, 5), (Sign.Attack3, 6), (Sign.Attack4, 7),
    ];

    protected virtual Dictionary<PartyRole, Sign> HelloWorldMarkers(RoleList? list)
    {
        if (list == null) return [];
        return new Dictionary<PartyRole, Sign>() {
            [list[0]] = Sign.Triangle,
            [list[1]] = Sign.Cross,
            [list[2]] = Sign.Bind1,
            [list[3]] = Sign.Bind2,
            [list[4]] = Sign.Attack1,
            [list[5]] = Sign.Attack2,
            [list[6]] = Sign.Attack3,
            [list[7]] = Sign.Attack4,
        };
    }

    protected virtual IAiMove HelloWorld1Pos()
    {
        return AiMove.Create(
            new(10f, 0),
            new(.2f, -10),
            new(-10, -10),
            new(-10, 10),
            new(0.2f, -19),
            new(19, -4),
            new(19, 4),
            new(0.2f, 19)
        )
        .Assignments(helloWorld1?.List)
        .ApplyPositions(AdjustForSafeMonitorSide);
    }

    private IAiMove GatherMiddle()
    {
        return AiMove.Create(
            new (0, 0),
            new (0, 0),
            new(0, -3f),
            new(0, -3f),
            new (0, 0),
            new (0, 0),
            new (0, 0),
            new (0, 0)
        )
        .Assignments(helloWorld2?.List)
        .ApplyPositions(state.BettleSpawnDirection.Apply);
    }

    private IAiMove HelloWorld2Pos()
    {
        return AiMove.Create(
            new (0, 10),
            new (10, 0),
            new (-9.5f, -16.5f),
            new (9.5f, -16.5f),
            new (-19f, 0),
            new (-4, 19f),
            new (4, 19f),
            new (19f, 0)
        )
        .Assignments(helloWorld2?.List)
        .ApplyPositions(state.BettleSpawnDirection.Apply);
    }

    Action<IAiPositions> AdjustSafeCardinal(int attack)
    {
        var startDirection = state.AttackDirections[attack * 2];
        var adjustmentToSafeVertical = startDirection.Index() % 4 == 1 ? -1 : +1;
        var safeAdjustment = state.FirstWaveCannonFront ? -1 : +1;
        var doubleAdjustment = attack == 0 ? 1 : -1;
        var safe = startDirection.Rotate(adjustmentToSafeVertical * safeAdjustment * doubleAdjustment);
        return safe.Apply;
    }

    Action<IAiPositions> AdjustSafeSpot(int attack)
    {
        var attackf = state.OmegaAttacks[attack * 2];
        var attackm = state.OmegaAttacks[attack * 2 + 1];
        float mul;
        if (attackf == OmegaAttack.Legs)
            mul = attackm == OmegaAttack.Sword ? 2.5f : -2.5f;
        else
            mul = attackm == OmegaAttack.Sword ? -17f : -10f;
        return move => move.Multiply(mul);
    }

    private void AdjustForSafeMonitorSide(IAiPositions move)
    {
        move.MultiplyX(state.MonitorSide.Mul);
    }

    /// <summary>打法專屬的盤面調整；預設不動（Standard 維持原本的抽法）。</summary>
    protected virtual void PrepareState() { }

    protected virtual RoleList solveHelloWorld1(SimParty party)
    {
        var monitorTarget = monitorTargets();
        var jumpTargets = RoleList.AllExcept(party, monitorTarget.ElementAtOrDefault(0),
                                             monitorTarget.ElementAtOrDefault(1),
                                             state.HelloWorldTargets[0], state.HelloWorldTargets[1]);
        // 監視器可能湊不到兩人（玩家漏接／死亡），缺口由 Assign 從剩下的人補滿 8 格。
        return new RoleList(party, TopP5OmegaRoleRules.Assign(
            fixedSlots: [state.HelloWorldTargets[0], state.HelloWorldTargets[1]],
            preferred: monitorTarget,
            preferredCount: 2,
            fallback: jumpTargets.List,
            everyone: [.. Enum.GetValues<PartyRole>()]));
    }

    protected virtual RoleList solveHelloWorld2(SimParty party)
    {
        List<PartyRole> freeAgents = [];
        List<PartyRole> tethers = [];
        foreach (var role in Enum.GetValues<PartyRole>())
        {
           if  (role == state.HelloWorldTargets[2] || role == state.HelloWorldTargets[3])
               continue;
           // 第 3 層要玩家實際接到才有：打得不完美時這個名單就湊不到兩人。
           if (party.Get(role)?.FindStatus(TopConstants.StatusId.QuickeningDynamis) is { Stacks: 3 })
               tethers.Add(role);
           else
               freeAgents.Add(role);
        }
        return new RoleList(party, TopP5OmegaRoleRules.Assign(
            fixedSlots: [state.HelloWorldTargets[2], state.HelloWorldTargets[3]],
            preferred: [.. tethers.Shuffle()],
            preferredCount: 2,
            fallback: [.. freeAgents.Shuffle()],
            everyone: [.. Enum.GetValues<PartyRole>()]));
    }

    private List<PartyRole> monitorTargets()
    {
        List<PartyRole> mustTakeMonitor = [];
        List<PartyRole> canTakeMonitor = [];
        foreach (var role in Enum.GetValues<PartyRole>())
        {
            if (state.HelloWorldTargets[0] == role || state.HelloWorldTargets[1] == role)
                continue;
            if (!state.DoubleDynamicTargets.Contains(role))
                continue;

            if (state.HelloWorldTargets[2] == role || state.HelloWorldTargets[3] == role)
                mustTakeMonitor.Add(role);
            else
                canTakeMonitor.Add(role);
        }

        return [.. TopP5OmegaRoleRules
            .Monitors([.. mustTakeMonitor.Shuffle()], [.. canTakeMonitor.Shuffle()])
            .Shuffle()];
    }
}
