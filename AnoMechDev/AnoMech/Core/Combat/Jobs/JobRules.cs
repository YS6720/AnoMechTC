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
    void Apply(uint actionId, bool comboOk, SimCharacter player, PartyRole sourceRole);
}

/// <summary>
/// Job gauge for the local player's own character only. Gauges live in this client's
/// memory; nobody else can write them and nobody needs to read them over the wire.
/// Runs on every client (host or peer) from its own UseAction, never replicated.
/// </summary>
internal interface IJobGaugeRules
{
    void OnLocalFire(uint actionId, bool comboOk);
    void Reset();
}

internal static class JobRules
{
    // 武士（2026-09-16）尚未定案：天道居合被拒原因、明鏡止水充能顯示都還沒收斂，
    // 維護者裁示先告一段落。只在開發者版啟用，公開版不帶半成品。
    private static readonly Dictionary<byte, IJobStatusRules> StatusRules = new()
    {
        [Paladin.JobId] = Paladin.Instance,
    };

    private static readonly Dictionary<byte, IJobGaugeRules> GaugeRules = new()
    {
    };

    internal static IJobStatusRules? StatusesFor(byte classJob)
        => StatusRules.TryGetValue(classJob, out var rules) ? rules : null;

    internal static bool IsOwnedTransition(IJobStatusRules rules, uint actionId, ushort statusId)
    {
        foreach (var owned in rules.TouchedStatuses(actionId, comboOk: true))
            if (owned == statusId) return true;
        return false;
    }

    internal static void OnLocalFire(byte classJob, uint actionId, bool comboOk)
    {
        if (GaugeRules.TryGetValue(classJob, out var rules)) rules.OnLocalFire(actionId, comboOk);
    }

    internal static void ResetLocalGauge()
    {
        foreach (var rules in GaugeRules.Values) rules.Reset();
    }
}
