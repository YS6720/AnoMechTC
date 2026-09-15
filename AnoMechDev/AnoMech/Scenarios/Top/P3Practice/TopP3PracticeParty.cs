using System;
using System.Collections.Generic;
using AnoMech.Core.Game.Party;

namespace AnoMech.Scenarios.Top.P3Practice;

/// <summary>
/// The fixed eight-job rehearsal party used by the P3 intermission.  The array is
/// deliberately indexed by <see cref="PartyRole"/>, not by the P01 label order:
/// this keeps PartyCreator's slot contract intact while the selector presents the
/// recorded P01..P08 order.
/// </summary>
public static class TopP3PracticeParty
{
    public static readonly IReadOnlyList<PartyRole> RecordingOrder =
    [
        PartyRole.ShieldHealer, // P01 SGE
        PartyRole.MainTank,     // P02 DRK
        PartyRole.CasterDps,    // P03 BLM
        PartyRole.OffTank,      // P04 PLD
        PartyRole.RegenHealer,  // P05 WHM
        PartyRole.PhysRangedDps,// P06 MCH
        PartyRole.MeleeDpsA,    // P07 SAM
        PartyRole.MeleeDpsB,    // P08 RPR
    ];

    public static readonly string[] RoleSelectorLabels =
    [
        "自動",
        "P01 SGE",
        "P02 DRK",
        "P03 BLM",
        "P04 PLD",
        "P05 WHM",
        "P06 MCH",
        "P07 SAM",
        "P08 RPR",
    ];

    // ClassJob row ids are the native ClassJob sheet ids.  The equipment rows are
    // the existing level-90 AF stand-ins; only the job identity is mechanic-visible.
    private static readonly PartyMemberPreset[] Standard =
    [
        new("P02 DRK", 32, 90, 2899, 3222, 3684, 3460, 3891), // tank set
        new("P04 PLD", 19, 90, 2897, 3220, 3682, 3458, 3889), // tank set
        new("P05 WHM", 24, 90, 2902, 3225, 3687, 3463, 3894), // healer set
        new("P01 SGE", 40, 90, 2905, 3228, 3689, 3466, 3897), // healer set
        new("P07 SAM", 34, 90, 2900, 3223, 3685, 3461, 3892), // melee set
        new("P08 RPR", 39, 90, 2898, 3221, 3683, 3459, 3890), // melee set
        new("P06 MCH", 31, 90, 2901, 3224, 3686, 3462, 3893), // ranged set
        new("P03 BLM", 25, 90, 2903, 3226, 3690, 3464, 3895), // caster set
    ];

    public static PartyRole? RoleForSelectorIndex(int index)
    {
        if (index == 0) return null;
        if (index < 0 || index > RecordingOrder.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return RecordingOrder[index - 1];
    }

    public static int SelectorIndex(PartyRole? role)
    {
        if (role is not { } value) return 0;
        for (var i = 0; i < RecordingOrder.Count; i++)
            if (RecordingOrder[i] == value) return i + 1;
        throw new ArgumentOutOfRangeException(nameof(role));
    }

    /// <summary>Maps Auto to the corresponding recorded slot for the player's job.</summary>
    public static PartyRole RoleForJob(uint classJob) => classJob switch
    {
        40 => PartyRole.ShieldHealer, // SGE P01
        32 => PartyRole.MainTank,     // DRK P02
        25 => PartyRole.CasterDps,    // BLM P03
        19 => PartyRole.OffTank,      // PLD P04
        24 => PartyRole.RegenHealer,  // WHM P05
        31 => PartyRole.PhysRangedDps,// MCH P06
        34 => PartyRole.MeleeDpsA,    // SAM P07
        39 => PartyRole.MeleeDpsB,    // RPR P08
        _ => PartyPresets.SkipRoleForJob(classJob),
    };

    /// <summary>
    /// Returns the eight fixed job slots with exactly one null player slot.  The
    /// player job is not replaced by the rehearsal job when Auto resolves it.
    /// </summary>
    public static IReadOnlyList<PartyMemberPreset?> ForPlayerJob(
        uint classJob,
        PartyRole? roleOverride = null)
        => ForRole(roleOverride ?? RoleForJob(classJob));

    public static IReadOnlyList<PartyMemberPreset?> ForRole(PartyRole skip)
    {
        var result = new PartyMemberPreset?[Standard.Length];
        for (var i = 0; i < Standard.Length; i++)
            result[i] = (PartyRole)i == skip ? null : Standard[i];
        return result;
    }

    public static string PositionLabel(PartyRole role)
        => RoleSelectorLabels[SelectorIndex(role)];

    public static uint JobForRole(PartyRole role)
        => Standard[(int)role].ClassJob;
}
