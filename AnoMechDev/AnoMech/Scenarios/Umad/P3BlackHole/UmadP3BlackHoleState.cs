using System;
using System.Collections.Generic;
using System.Linq;
using AnoMech.Core;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using AnoMech.Scenarios.Top;
using static AnoMech.Scenarios.Umad.UmadConstants;

namespace AnoMech.Scenarios.Umad.P3BlackHole;

// Per-run randomized assignments the scenario and AI consume. Filled in the ctor
// (apply override if set, otherwise pick at random) so Run stays deterministic for
// the duration of one play. See UmadP4KefkaSaysState for the canonical shape.
// 我方：座位求解的純邏輯（SlotLine／CanSeat／TrySeatRoles／SlotAllows）在 UmadP3BlackHoleState.Seating.cs，
// 原樣搬出，讓 Tests 能以 Compile Include 連結；本檔其餘照上游。
public sealed partial class UmadP3BlackHoleState
{
    private readonly Rng rng = Rng.Detached;

    public UmadP3BlackHoleScenarioObjects ScenarioObjects { get; }

    public RoleList Roles { get; }
    public RoleList StackTargets { get; }
    public RoleList EdictTargets { get; }
    
    public uint ImplosionAttack { get; }
    public IReadOnlyList<uint> SlapAttacks { get; }
    public IReadOnlyList<RoleList> ConeTargets { get; }
    
    public IReadOnlyList<Direction> KefkaPosition { get; }
    
    public IReadOnlyList<Direction> BlackHoleDirections { get; }
    
    public int MiniBlackHoleInitialAngle { get; }
    public int MiniBlackHoleChirality { get; }

    // Read by both the Ai (positioning) and the scenario (mitigation plan); both must agree.
    public ThunderIIIAssignment ThunderSet1 { get; }
    public ThunderIIIAssignment ThunderSet2 { get; }
    // YAN(211)'s head markers: placed by the Ai, or read from the ones players placed.
    public MarkerMode Markers { get; }
    // Who tanks which boss, read by the scenario's boss timelines at event time. The recording
    // has MT on Chaos and OT on Exdeath; YAN(211) puts MT on Exdeath, so its Ai sets this in Run,
    // before the scenario schedules the bosses.
    public PartyRole ChaosTank { get; internal set; } = PartyRole.MainTank;
    public PartyRole ExdeathTank => ChaosTank == PartyRole.MainTank ? PartyRole.OffTank : PartyRole.MainTank;

    // The seat the debug first-slap option aims at, and whether anyone asked for it.
    private readonly PartyRole subject;
    private readonly bool slapAllOnSubject;

    public UmadP3BlackHoleState(SimWorld world, UmadP3BlackHoleStateOverrides overrides)
    {
        rng = world.Rng;
        var party = world.Party;
        // Only the debug "aim every cone at me" option needs a single subject; the first seat
        // asking for it wins.
        var slapSeats = overrides.FirstSlapAllOnMe.Resolve(party.PlayerRole);
        subject = slapSeats.FirstOrDefault(s => s.Value).Role;
        slapAllOnSubject = slapSeats.Any(s => s.Value);
        ScenarioObjects = new UmadP3BlackHoleScenarioObjects(world);
        Roles = BuildRoles(party, overrides);
        ThunderSet1 = overrides.ThunderSet1;
        ThunderSet2 = overrides.ThunderSet2;
        Markers = overrides.Markers;

        StackTargets = new RoleList(party, overrides.StackTargets ?? rng.Shuffle(rng.NextSupportRole(), rng.NextDpsRole()));
        EdictTargets = new RoleList(party, [overrides.EdictTarget ?? rng.NextRole(), rng.NextRole()]);
        ImplosionAttack = overrides.ImplosionAttack ?? rng.NextObj(ActionId.LatitudinalImplosion, ActionId.LongitudinalImplosion);
        SlapAttacks = Enumerable.Range(0, 3).Select(i => overrides.SlapAttacks[i] ?? NextSlap()).ToList();
        KefkaPosition = Enumerable.Range(0, 5).Select(i => overrides.KefkaPositions[i] ?? rng.NextDirection()).ToList();
        ConeTargets = Enumerable.Range(0, 3)
                                .Select(i => overrides.ConeTargets[i] is { } forced
                                                 ? new RoleList(party, forced)
                                                 : NextConeTargets(party, SlapAttacks[i], i == 0 && slapAllOnSubject))
                                .ToList();
        BlackHoleDirections = Enumerable.Range(0, 4).Select(i => overrides.BlackHoleDirections[i] ?? rng.NextCardinal()).ToList();
        MiniBlackHoleInitialAngle = overrides.MiniBlackHoleInitialAngle ?? rng.NextInt(2);
        MiniBlackHoleChirality = overrides.MiniBlackHoleChirality ?? rng.NextSign();
    }

    // Every seat's line/Accretion request solved together against the fight's own slot layout:
    // supports fill slots 0-3 and DPS 4-7 before the coin-flip Swap(3,7), and pre-slot 3 is
    // never a tank. Both Accretion slots (3 and 7) sit in different blocks, so which seats can
    // hold Accretion depends on that swap -- hence solving for both and taking the first that
    // satisfies everyone.
    private RoleList BuildRoles(SimParty party, UmadP3BlackHoleStateOverrides overrides)
    {
        if (overrides.SeatLinesInRoleOrder)
            return new RoleList(party, PerRole.All);

        var requests = overrides.Requests(party.PlayerRole);

        var swapFirst = rng.NextBool();
        foreach (var swap in new[] { swapFirst, !swapFirst })
            if (TrySeatRoles(swap, requests) is { } solved)
                return new RoleList(party, solved);

        // Nothing seats everyone as asked (two seats on the same line with only one slot for
        // it, a tank asking for Accretion). Drop requests from the last seat back until it
        // solves; an empty request set always does.
        var ordered = requests.Keys.OrderBy(r => (int)r).ToList();
        for (var drop = ordered.Count - 1; drop >= 0; drop--)
        {
            var dropped = ordered[drop];
            requests.Remove(dropped);
            DiagnosticLog.Warn($"[UmadP3BlackHole] Can't satisfy every line/Accretion request -- dropping {dropped}'s and leaving that seat to the roll.");
            foreach (var swap in new[] { swapFirst, !swapFirst })
                if (TrySeatRoles(swap, requests) is { } relaxed)
                    return new RoleList(party, relaxed);
        }
        return RoleList.RandomRoleStable(rng, party);
    }

    // Backtracking fill of the eight final slots. A seat with no request goes anywhere its role
    // is allowed; a seat with one only goes where the line and Accretion match.
    private PartyRole[]? TrySeatRoles(bool swap, Dictionary<PartyRole, (int? Line, bool? Accretion)> requests)
        // Shuffled so unforced seats still vary per run.
        => TrySeatRoles(swap, requests, rng.Shuffle(PerRole.All).ToArray());

    private uint NextSlap()
    {
        return rng.NextObj(ActionId.SlapHappy_Left, ActionId.SlapHappy_Right);
    }

    // Left = one shocking-impact stack target; Right = three shockwave cones. When
    // allOnPlayer is set, every target slot collapses onto the player so the cones
    // all land on the practising player.
    private RoleList NextConeTargets(SimParty party, uint slap, bool allOnPlayer)
    {
        if (slap == ActionId.SlapHappy_Left)
            return new RoleList(party, [allOnPlayer ? subject : rng.NextRole()]);

        return allOnPlayer
                   ? new RoleList(party, [subject, subject, subject])
                   : new RoleList(party, [rng.NextDpsRole(), rng.NextTankRole(), rng.NextHealerRole()]);
    }
}
