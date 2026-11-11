using System.Collections.Generic;
using System.Linq;
using AnoMech.Core.Game.Party;
using AnoMech.Scenarios.Top;

namespace AnoMech.Scenarios.Umad.P3BlackHole;

// One Thunder III set's two hits, planned for whichever tank slot ends up bot-driven. No
// "auto": every consumer switches on all four cases.
public enum ThunderIIIAssignment
{
    MtInvulnsBoth,  // MT stands closest for both hits in the set and pops an invuln before the first
    OtInvulnsBoth,  // mirror of MtInvulnsBoth
    ShareMtFirst,   // hits split -- MT takes the first (needs its own + party mitigation, no invuln), OT takes the second
    ShareOtFirst,   // mirror of ShareMtFirst
}

// Shared so the Ai and the scenario agree on the role assignment for a plan.
public static class ThunderIIIPlanning
{
    // Who stands closest to Exdeath for the first hit (targeting is party.Find.Closest) and,
    // for a Share plan, who swaps in for the second.
    public static (PartyRole First, PartyRole? Second) Roles(ThunderIIIAssignment plan) => plan switch
    {
        ThunderIIIAssignment.MtInvulnsBoth => (PartyRole.MainTank, null),
        ThunderIIIAssignment.OtInvulnsBoth => (PartyRole.OffTank, null),
        ThunderIIIAssignment.ShareMtFirst  => (PartyRole.MainTank, PartyRole.OffTank),
        ThunderIIIAssignment.ShareOtFirst  => (PartyRole.OffTank, PartyRole.MainTank),
        _                                   => throw new System.ArgumentOutOfRangeException(nameof(plan), plan, null),
    };
}

public sealed class UmadP3BlackHoleStateOverrides
{
    // --- Fight-wide: one roll the whole sim shares -------------------------------------
    // null = random. Only SlapAttacks[0] has (debug-only) UI; the rest are pinned by tests.
    public uint?[] SlapAttacks { get; } = new uint?[3];               // ActionId.SlapHappy_Left / .SlapHappy_Right
    public Direction?[] KefkaPositions { get; } = new Direction?[5];
    public PartyRole[]?[] ConeTargets { get; } = new PartyRole[]?[3]; // one role for a Left slap, three for a Right
    public uint? ImplosionAttack { get; set; }                        // ActionId.LatitudinalImplosion / .LongitudinalImplosion
    public Direction?[] BlackHoleDirections { get; } = new Direction?[4];
    public int? MiniBlackHoleInitialAngle { get; set; }               // 0 / 1
    public int? MiniBlackHoleChirality { get; set; }                  // -1 / +1
    public PartyRole[]? StackTargets { get; set; }                    // in stack order
    public PartyRole? EdictTarget { get; set; }
    public bool SeatLinesInRoleOrder { get; set; }                    // false = roll; true = slot i holds PartyRole i, overriding the per-seat requests

    // Defaults: MT solo-tanks Set 1 behind an invuln, Set 2 is shared MT-first.
    public ThunderIIIAssignment ThunderSet1 { get; set; } = ThunderIIIAssignment.MtInvulnsBoth;
    public ThunderIIIAssignment ThunderSet2 { get; set; } = ThunderIIIAssignment.ShareMtFirst;
    // YAN(211) only: System = the plugin places the head markers, Manual = read the ones players placed.
    public MarkerMode Markers { get; set; } = MarkerMode.System;

    // --- Per player: everyone has their own ---------------------------------------------
    // 1/2/3 = First/Second/Third in line. Line and Accretion are solved together against the
    // fight's own slot layout, so a request the layout can't satisfy is dropped and logged.
    public PerRoleSetting<int> LineNumber { get; set; } = new();
    // Tanks and third-in-line never carry Accretion in the real fight.
    public PerRoleSetting<bool> Accretion { get; set; } = new();
    // Debug-only UI: aim every first-slap cone at this seat.
    public PerRoleSetting<bool> FirstSlapAllOnMe { get; set; } = new();

    // What the run will try to seat: one line and/or Accretion per seat that asked.
    public Dictionary<PartyRole, (int? Line, bool? Accretion)> Requests(PartyRole localPlayerRole)
    {
        var requests = new Dictionary<PartyRole, (int? Line, bool? Accretion)>();
        foreach (var (role, line) in LineNumber.Resolve(localPlayerRole))
            if (line is >= 1 and <= 3)
                requests[role] = (line, requests.GetValueOrDefault(role).Accretion);
        foreach (var (role, accretion) in Accretion.Resolve(localPlayerRole))
            requests[role] = (requests.GetValueOrDefault(role).Line, accretion);
        return requests;
    }

    // Exactly one healer and exactly one DPS carry Accretion, and a tank never does; the three
    // lines hold at most three seats each. Anything the slot layout can't seat is named here
    // before the run rather than dropped during it.
    public SettingsConflicts Validate()
    {
        var conflicts = new SettingsConflicts();
        if (!PerRole.SeatsActive) return conflicts;

        // Exactly one healer and exactly one DPS hold Accretion; a tank never does.
        static bool IsHealer(PartyRole role) => !role.IsDps() && !role.IsTank();
        var wants = PerRole.All.Where(r => Accretion[r] == true).ToList();
        var refuses = PerRole.All.Where(r => Accretion[r] == false).ToList();
        var healersWanting = wants.Where(IsHealer).ToList();
        var dpsWanting = wants.Where(r => r.IsDps()).ToList();
        var healersRefusing = refuses.Where(IsHealer).ToList();
        var dpsRefusing = refuses.Where(r => r.IsDps()).ToList();

        conflicts.Forbidden(wants.Where(r => r.IsTank()).ToList(), "拿到 Accretion（只會給一名補師和一名 DPS）");
        if (healersWanting.Count > 1)
            conflicts.Add($"{SettingsConflicts.Seats(healersWanting)} 都要 Accretion，但只有一名補師會拿到。");
        if (dpsWanting.Count > 1)
            conflicts.Add($"{SettingsConflicts.Seats(dpsWanting)} 都要 Accretion，但只有一名 DPS 會拿到。");
        if (healersRefusing.Count == 2)
            conflicts.Add($"{SettingsConflicts.Seats(healersRefusing)} 都不要 Accretion，但一定有一名補師拿到。");
        if (dpsRefusing.Count == 4)
            conflicts.Add($"{SettingsConflicts.Seats(dpsRefusing)} 都不要 Accretion，但一定有一名 DPS 拿到。");

        foreach (var line in new[] { 1, 2, 3 })
            conflicts.AtMost(line == 3 ? 2 : 3, PerRole.All.Where(r => LineNumber[r] == line).ToList(),
                             line switch { 1 => "第一順位", 2 => "第二順位", _ => "第三順位" });

        // Backstop for anything the named rules miss: line and Accretion interact through the
        // same eight slots, so a set that passes each rule alone can still be unseatable (three
        // supports all asking to be second in line, say). The argument is unused here, since
        // Resolve reads the seats while hosting.
        if (!conflicts.Any && !UmadP3BlackHoleState.CanSeat(Requests(PartyRole.MainTank)))
            conflicts.Add("這組順位與 Accretion 指定無法在同一輪同時成立。");
        return conflicts;
    }
}
