using System.Collections.Generic;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;

namespace AnoMech.Core.Combat.Jobs;

/// <summary>
/// Hand-written proc/derivation chain for one job — the part the recorded catalog
/// cannot express (consumption on use, stack decrements, combo-gated grants).
/// Runs where <see cref="RecordedAbilityRuntime"/> runs: on the host for every role,
/// so the resulting statuses replicate to peers exactly like any other status.
/// </summary>
internal interface IJobStatusRules
{
    bool IsKnownAction(uint actionId);
    /// <summary>Statuses this action may grant or consume; the recorded catalog must not replay them.</summary>
    IReadOnlyList<ushort> TouchedStatuses(uint actionId, bool comboOk);
    void Apply(in JobActionContext context);
    void OnPartyAction(in JobActionContext context, SimCharacter actor) { }
    void OnMechanicHit(in JobActionContext context, SimCharacter target) { }
    void Tick(in JobActionContext context, float deltaSeconds) { }
    void ForgetRole(PartyRole role) { }
    void ResetStatusState() { }
}

/// <summary>
/// Job gauge for the local player's own character only. Only this client writes
/// its native gauge; host-owned shield resource feedback is projected locally.
/// Ordinary completed actions and timers never write another player's gauge.
/// </summary>
internal interface IJobGaugeRules
{
    void OnLocalFire(uint actionId, bool comboOk, byte level);
    void Reset(byte level);
    /// <summary>Per-frame, only inside a running practice; pause freezes these timers.</summary>
    void Tick(float deltaSeconds, byte level) { }
    bool AllowsNaturalManaRecovery => true;
}

internal static class JobRules
{
    // One host status owner and one local native gauge writer per supported job.
    private static readonly Dictionary<byte, IJobStatusRules> StatusRules = new()
    {
        [Paladin.JobId] = Paladin.Instance,
        [Samurai.JobId] = Samurai.Instance,
        [Reaper.JobId] = Reaper.Instance,
        [Machinist.JobId] = Machinist.Instance,
        [BlackMage.JobId] = BlackMage.Instance,
        [WhiteMage.JobId] = WhiteMage.Instance,
        [Sage.JobId] = Sage.Instance,
        [DarkKnight.JobId] = DarkKnight.Instance,
    };

    private static readonly Dictionary<byte, IJobGaugeRules> GaugeRules = new()
    {
        [Paladin.JobId] = Paladin.Instance,
        [Samurai.JobId] = Samurai.Instance,
        [Reaper.JobId] = Reaper.Instance,
        [Machinist.JobId] = Machinist.Instance,
        [BlackMage.JobId] = BlackMage.Instance,
        [WhiteMage.JobId] = WhiteMage.Instance,
        [Sage.JobId] = Sage.Instance,
        [DarkKnight.JobId] = DarkKnight.Instance,
    };

    internal static IJobStatusRules? StatusesFor(byte classJob)
        => StatusRules.TryGetValue(classJob, out var rules) ? rules : null;

    internal static bool IsOwnedTransition(IJobStatusRules rules, uint actionId, ushort statusId)
    {
        foreach (var owned in rules.TouchedStatuses(actionId, comboOk: true))
            if (owned == statusId) return true;
        return false;
    }

    internal static void OnLocalFire(byte classJob, uint actionId, bool comboOk, byte level)
    {
        if (GaugeRules.TryGetValue(classJob, out var rules)) rules.OnLocalFire(actionId, comboOk, level);
    }

    internal static void ResetLocalGauge(byte level)
    {
        foreach (var rules in GaugeRules.Values) rules.Reset(level);
    }

    internal static void TickLocalGauge(byte classJob, float deltaSeconds, byte level)
    {
        if (GaugeRules.TryGetValue(classJob, out var rules)) rules.Tick(deltaSeconds, level);
    }

    internal static bool Supports(byte classJob) => GaugeRules.ContainsKey(classJob);
    internal static bool AllowsNaturalManaRecovery(byte classJob)
        => !GaugeRules.TryGetValue(classJob, out var rules) || rules.AllowsNaturalManaRecovery;

    internal static bool IsAvailableAction(Lumina.Excel.Sheets.Action action, byte classJob, byte level)
    {
        if (action.IsPvP) return false;
        // Sprint (Action 3) is a general action with ClassJobLevel 0, not a job unlock.
        if (action.RowId == 3) return level > 0 && Supports(classJob);
        if (action.ClassJobLevel == 0 || action.ClassJobLevel > level) return false;
        var jobs = action.ClassJobCategory.Value;
        return classJob switch
        {
            Samurai.JobId => jobs.SAM,
            Reaper.JobId => jobs.RPR,
            Machinist.JobId => jobs.MCH,
            BlackMage.JobId => jobs.BLM,
            WhiteMage.JobId => jobs.WHM,
            Sage.JobId => jobs.SGE,
            DarkKnight.JobId => jobs.DRK,
            Paladin.JobId => jobs.PLD,
            _ => false,
        };
    }
}
