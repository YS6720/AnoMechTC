using System;
using System.Collections.Generic;
using System.Linq;
using AnoMech.Core;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using static AnoMech.Scenarios.Umad.UmadConstants;

namespace AnoMech.Scenarios.Umad.P2Forsaken;

// 我方：上游 1646a70 原本在這裡的 EndAttack 搬到 UmadP2ForsakenEndAttack.cs（內容不改），
// 這個檔只剩狀態，Tests 可以直接連結。

// Per-run randomized assignments the scenario and AI consume. Filled in the ctor
// (apply override if set, otherwise pick at random) so Run stays deterministic
// for the duration of one play. See TopP5DeltaState for the canonical shape.
public sealed class UmadP2ForsakenState
{
    private readonly Rng rng = Rng.Detached;

    public EndAttack[] EndAttacks { get; }
    
    public Direction NewNorth { get; }

    public int Rotation { get; }

    public const int TowerRounds = 8;

    // Each tower's two new lockons, dealt to its soakers in PartyRole order; indexed as
    // UmadP2ForsakenStateOverrides.TowerLockons.
    public uint[][] TowerLockons { get; } = [];
    
    public Dictionary<PartyRole, uint> Lockons = [];

    public UmadP2ForsakenState(Rng rng, SimParty party, UmadP2ForsakenStateOverrides overrides)
    {
        this.rng = rng;
        EndAttacks = overrides.EndAttacks.Select(e => e ?? NextEnd()).ToArray();
        NewNorth = overrides.NewNorth ?? rng.NextDirection();
        Rotation = overrides.Rotation ?? rng.NextSign();

        var supportLockon = overrides.SupportLockon ?? rng.NextObj(LockonId.ForsakenChariot, LockonId.ForsakenCone);
        var dpsLockon = supportLockon == LockonId.ForsakenChariot ? LockonId.ForsakenCone : LockonId.ForsakenChariot;

        List<PartyRole> supports = [PartyRole.MainTank, PartyRole.OffTank, PartyRole.RegenHealer, PartyRole.ShieldHealer];
        List<PartyRole> dps = [PartyRole.MeleeDpsA, PartyRole.MeleeDpsB, PartyRole.PhysRangedDps, PartyRole.CasterDps];
        supports.ForEach(role => Lockons[role] = supportLockon);
        dps.ForEach(role => Lockons[role] = dpsLockon);
        Lockons[overrides.SupportStackRole ?? rng.NextObj(supports.ToArray())] = LockonId.ForsakenStack;
        Lockons[overrides.DpsStackRole ?? rng.NextObj(dps.ToArray())] = LockonId.ForsakenStack;
        DiagnosticLog.Info($"Lockon assigments: {string.Join(",", Enum.GetValues<PartyRole>().Select(r => Lockons[r]))}");

        TowerLockons = Enumerable.Range(0, TowerRounds)
            .SelectMany(round => rng.Shuffle(RoundLockons(round)).Chunk(2))
            .Select((dealt, tower) => overrides.TowerLockons[tower] ?? dealt)
            .ToArray();
    }

    // The four lockons a round's towers hand out between them.
    public static uint[] RoundLockons(int round) => round switch
    {
        6 => [LockonId.ForsakenStack, LockonId.ForsakenStack, LockonId.ForsakenCone, LockonId.ForsakenStack],
        _ when round % 2 == 0 => [LockonId.ForsakenCone, LockonId.ForsakenCone, LockonId.ForsakenChariot, LockonId.ForsakenChariot],
        _ => [LockonId.ForsakenStack, LockonId.ForsakenStack, LockonId.ForsakenCone, LockonId.ForsakenChariot],
    };

    public (Direction, Direction) GetTowers(int index)
    {
        var north =  NewNorthAt(index);
        return (north.Rotate(-1), north.Rotate(1));
    }

    public Direction NewNorthAt(int index)
    {
        return NewNorth.Rotate(index*Rotation);
    }
    
    private EndAttack NextEnd()
    {
        return rng.NextObj(EndAttack.FuturesEnd, EndAttack.PastsEnd);
    }

}
