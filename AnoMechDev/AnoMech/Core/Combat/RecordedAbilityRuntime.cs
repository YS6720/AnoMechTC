using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using AnoMech.Core.Combat.Jobs;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;

namespace AnoMech.Core.Combat;

/// <summary>
/// Host/single-player owner for all recorded action outcomes. A request carries
/// only an authenticated role, action identity, declared job/level and an already
/// resolved target; this class alone decides combo eligibility and status writes.
/// </summary>
internal sealed class RecordedAbilityRuntime
{
    private const uint SprintActionId = 3;
    private const ushort SprintStatusId = 50;
    private const int SprintParam = 30;
    private const float SprintDuration = 10f;

    private readonly Game.Game game;
    private readonly RecordedAbilityCatalog catalog;
    private readonly RecordedAbilityQueue<PendingOutcome> queue = new();
    private readonly List<RecordedAbilityQueueEntry<PendingOutcome>> due = new();
    private readonly Dictionary<PartyRole, RoleState> roles = new();
    private readonly Dictionary<StatusVersionKey, long> statusVersions = new();
    private readonly Dictionary<uint, uint> prereqOf = new();
    private readonly HashSet<uint> startsCombo = new();
    private long generation;
    private long nextVersion;

    internal RecordedAbilityRuntime(Game.Game game)
        : this(game, RecordedAbilityCatalog.LoadEmbedded())
    {
    }

    internal RecordedAbilityRuntime(Game.Game game, RecordedAbilityCatalog catalog)
    {
        this.game = game ?? throw new ArgumentNullException(nameof(game));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        generation = game.ScenarioDispatchGeneration;
    }

    internal void ConfigureCombos(IReadOnlyDictionary<uint, uint> prereq, IReadOnlySet<uint> starts)
    {
        if (prereq is null) throw new ArgumentNullException(nameof(prereq));
        if (starts is null) throw new ArgumentNullException(nameof(starts));
        prereqOf.Clear();
        foreach (var pair in prereq)
            prereqOf[pair.Key] = pair.Value;
        startsCombo.Clear();
        foreach (var action in starts)
            startsCombo.Add(action);
    }

    internal bool TryUse(PartyRole role, uint actionId, byte classJob, byte level, SimCharacter? target)
    {
        if (!TryEnterCurrentRun()) return false;
        if (game.IsNetworkPeer || game.Paused)
            return false;
        if ((uint)role >= 8 || classJob == 0 || level == 0) return false;
        var caster = game.World.Party.Get(role);
        if (!IsUsableActor(caster)) return false;

        var observed = catalog.IsObservedAction(classJob, actionId, level);
        var jobRules = JobRules.StatusesFor(classJob);
        var explicitDefault = actionId == SprintActionId ||
            (jobRules?.IsKnownAction(actionId) == true);
        if (!observed && !explicitDefault) return false;

        var rules = catalog.FindExecutableRules(classJob, actionId, level);
        var removals = catalog.FindExecutableRemovals(classJob, actionId, level);
        if (!TargetsAreValid(rules, removals, target)) return false;

        if (!roles.TryGetValue(role, out var state))
        {
            state = new RoleState(caster, role, classJob, level);
            roles.Add(role, state);
        }
        else if (!ReferenceEquals(state.Caster, caster) || state.ClassJob != classJob || state.Level != level)
        {
            // The declaration is frozen for this authenticated role/run. A
            // replacement actor must retire the role before it can submit again.
            return false;
        }

        var prerequisite = prereqOf.GetValueOrDefault(actionId);
        var hasPrerequisite = prereqOf.ContainsKey(actionId);
        var comboOk = RecordedAbilityEligibility.IsComboReady(prerequisite, state.Combo, state.ComboAge);
        UpdateCombo(state, actionId, comboOk, hasPrerequisite);

        if (jobRules?.IsKnownAction(actionId) == true)
            QueueJob(jobRules, actionId, comboOk, state);

        if (actionId == SprintActionId)
        {
            // Sprint is an explicit long-standing default. Do not also replay
            // the sampled catalog row for status 50.
            QueueApply(state, state.Caster, SprintStatusId, SprintParam, SprintDuration, 0f);
        }

        foreach (var rule in rules)
        {
            if (rule.ActionId == SprintActionId && rule.StatusId == SprintStatusId) continue;
            if (jobRules != null && JobRules.IsOwnedTransition(jobRules, actionId, rule.StatusId)) continue;
            var recipient = rule.TargetKind == RecordedAbilityTargetKind.Self ? state.Caster : target!;
            QueueApply(state, recipient, rule.StatusId, rule.Param!.Value, rule.DurationSeconds, rule.DelaySeconds);
        }
        foreach (var removal in removals)
        {
            if (jobRules != null && JobRules.IsOwnedTransition(jobRules, actionId, removal.StatusId)) continue;
            var recipient = removal.TargetKind == RecordedAbilityTargetKind.Self ? state.Caster : target!;
            QueueRemove(state, recipient, removal.StatusId, removal.DelaySeconds);
        }
        // Preserve grant-before-consume ordering when reliable inputs arrive in
        // one framework batch. Zero-delay outcomes must not invalidate each
        // other before the first transition has actually run.
        ApplyDueOutcomes(0f);
        return true;
    }

    internal void Tick(float realSeconds)
    {
        if (!TryEnterCurrentRun()) return;
        if (game.IsNetworkPeer) return;
        if (game.Paused) return; // native timers and this queue both freeze on pause
        if (!float.IsFinite(realSeconds) || realSeconds < 0f)
            return;
        if (realSeconds == 0f) return;


        foreach (var state in roles.Values)
        {
            state.ComboAge = MathF.Min(30f, state.ComboAge + realSeconds);
            if (state.ComboAge >= 30f) state.Combo = 0;
        }

        ApplyDueOutcomes(realSeconds);
    }

    private void ApplyDueOutcomes(float realSeconds)
    {
        if (queue.Drain(realSeconds, generation, due) == 0) return;
        foreach (var entry in due)
        {
            var outcome = entry.Value;
            if (!IsCurrent(outcome)) continue;
            try
            {
                switch (outcome.Kind)
                {
                    case PendingKind.Apply:
                        var remaining = outcome.Duration - MathF.Max(0f, queue.Time - entry.Due);
                        if (remaining > 0f)
                            outcome.Recipient.AddStatusParam(outcome.StatusId, outcome.Param, remaining,
                                outcome.SourceRole, outcome.Caster.GameObjectId);
                        else
                            outcome.Recipient.RemoveStatus(outcome.StatusId, outcome.SourceRole);
                        break;
                    case PendingKind.Remove:
                        outcome.Recipient.RemoveStatus(outcome.StatusId, outcome.SourceRole);
                        break;
                    case PendingKind.Job:
                        outcome.Rules!.Apply(outcome.ActionId, outcome.ComboOk, outcome.Caster, outcome.SourceRole);
                        break;
                }
            }
            catch (Exception ex)
            {
                // A stale native actor or status slot must not tear down the host
                // session. The request itself was already accepted atomically.
                CrashTrace.Log($"[AbilityRuntime] outcome ignored: {ex.Message}");
            }
        }
    }

    internal void Reset()
    {
        queue.Clear();
        due.Clear();
        roles.Clear();
        statusVersions.Clear();
        nextVersion = 0;
        generation = game.ScenarioDispatchGeneration;
    }

    internal void ForgetRole(PartyRole role)
    {
        roles.TryGetValue(role, out var retired);
        roles.Remove(role);
        foreach (var key in new List<StatusVersionKey>(statusVersions.Keys))
            if (key.SourceRole == role || (retired is not null && ReferenceEquals(key.Target, retired.Caster)))
                statusVersions.Remove(key);
    }

    private bool TryEnterCurrentRun()
    {
        if (game.ScenarioDispatchGeneration != generation || !game.HasActivePractice)
            Reset();
        return game.HasActivePractice;
    }

    private bool TargetsAreValid(IReadOnlyList<RecordedAbilityRule> rules,
        IReadOnlyList<RecordedAbilityRemoval> removals, SimCharacter? target)
    {
        foreach (var rule in rules)
        {
            var hasTarget = target is { IsActive: true };
            var targetIsPlayer = target is ISimPartyMember;
            if (!RecordedAbilityEligibility.IsTargetAllowed(rule.TargetKind, rule.TargetCategory,
                    hasTarget, targetIsPlayer))
                return false;
            if (rule.TargetKind == RecordedAbilityTargetKind.Target && target is ISimPartyMember { Dead: true })
                return false;
        }
        foreach (var removal in removals)
        {
            var hasTarget = target is { IsActive: true };
            var targetIsPlayer = target is ISimPartyMember;
            if (!RecordedAbilityEligibility.IsTargetAllowed(removal.TargetKind, removal.TargetCategory,
                    hasTarget, targetIsPlayer))
                return false;
            if (removal.TargetKind == RecordedAbilityTargetKind.Target && target is ISimPartyMember { Dead: true })
                return false;
        }
        return true;
    }

    private static bool IsUsableActor([NotNullWhen(true)] SimCharacter? actor)
        => actor is { IsActive: true } && actor is not ISimPartyMember { Dead: true };

    private void UpdateCombo(RoleState state, uint actionId, bool comboOk, bool hasPrerequisite)
    {
        // The caller applies the same reverse ActionCombo metadata that the
        // client uses. Actions without combo metadata leave the current chain
        // alone, preserving the existing RotationSim behavior.
        if (comboOk && startsCombo.Contains(actionId))
        {
            state.Combo = actionId;
            state.ComboAge = 0f;
        }
        else if (hasPrerequisite || startsCombo.Contains(actionId))
        {
            state.Combo = 0;
            state.ComboAge = 30f;
        }
    }

    private void QueueJob(IJobStatusRules rules, uint actionId, bool comboOk, RoleState state)
    {
        var touched = rules.TouchedStatuses(actionId, comboOk);
        if (touched.Count == 0) return;
        var stamps = new StatusStamp[touched.Count];
        for (var i = 0; i < touched.Count; i++)
        {
            var key = new StatusVersionKey(state.Caster, touched[i], state.Role);
            stamps[i] = new(key, NextVersion(key));
        }
        queue.Add(0f, generation, stamps[^1].Version,
            PendingOutcome.Job(rules, state.Caster, state.Role, actionId, comboOk, stamps));
    }

    private void QueueApply(RoleState state, SimCharacter recipient, ushort statusId,
        int param, float duration, float delay)
    {
        var key = new StatusVersionKey(recipient, statusId, state.Role);
        var version = NextVersion(key);
        queue.Add(delay, generation, version,
            PendingOutcome.Apply(state.Caster, recipient, state.Role, statusId, param, duration, key, version));
    }

    private void QueueRemove(RoleState state, SimCharacter recipient, ushort statusId, float delay)
    {
        var key = new StatusVersionKey(recipient, statusId, state.Role);
        var version = NextVersion(key);
        queue.Add(delay, generation, version,
            PendingOutcome.Remove(state.Caster, recipient, state.Role, statusId, key, version));
    }

    private long NextVersion(StatusVersionKey key)
    {
        var version = ++nextVersion;
        statusVersions[key] = version;
        return version;
    }

    private bool IsCurrent(PendingOutcome outcome)
    {
        if (game.ScenarioDispatchGeneration != generation || !game.HasActivePractice || game.Paused)
            return false;
        if (!roles.TryGetValue(outcome.SourceRole, out var state) ||
            !ReferenceEquals(state.Caster, outcome.Caster) ||
            !ReferenceEquals(game.World.Party.Get(outcome.SourceRole), outcome.Caster) ||
            !IsUsableActor(outcome.Caster))
            return false;
        if (!IsUsableActor(outcome.Recipient)) return false;
        if (outcome.Recipient is ISimPartyMember targetMember &&
            !ReferenceEquals(game.World.Party.Get(targetMember.Role), outcome.Recipient))
            return false;
        if (outcome.Kind == PendingKind.Job)
        {
            foreach (var stamp in outcome.Stamps!)
                if (!statusVersions.TryGetValue(stamp.Key, out var current) || current != stamp.Version)
                    return false;
            return true;
        }
        return statusVersions.TryGetValue(outcome.Key, out var version) && version == outcome.Version;
    }

    private enum PendingKind
    {
        Apply,
        Remove,
        Job,
    }

    private readonly record struct StatusVersionKey(SimCharacter Target, ushort StatusId, PartyRole SourceRole);
    private readonly record struct StatusStamp(StatusVersionKey Key, long Version);

    private sealed class RoleState
    {
        internal RoleState(SimCharacter caster, PartyRole role, byte classJob, byte level)
        {
            Caster = caster;
            Role = role;
            ClassJob = classJob;
            Level = level;
        }

        internal SimCharacter Caster { get; }
        internal PartyRole Role { get; }
        internal byte ClassJob { get; }
        internal byte Level { get; }
        internal uint Combo { get; set; }
        internal float ComboAge { get; set; }
    }

    private sealed class PendingOutcome
    {
        private PendingOutcome(PendingKind kind, SimCharacter caster, SimCharacter recipient,
            PartyRole sourceRole, ushort statusId, int param, float duration, uint actionId,
            bool comboOk, StatusVersionKey key, long version, StatusStamp[]? stamps)
        {
            Kind = kind;
            Caster = caster;
            Recipient = recipient;
            SourceRole = sourceRole;
            StatusId = statusId;
            Param = param;
            Duration = duration;
            ActionId = actionId;
            ComboOk = comboOk;
            Key = key;
            Version = version;
            Stamps = stamps;
        }

        internal PendingKind Kind { get; }
        internal SimCharacter Caster { get; }
        internal SimCharacter Recipient { get; }
        internal PartyRole SourceRole { get; }
        internal ushort StatusId { get; }
        internal int Param { get; }
        internal float Duration { get; }
        internal uint ActionId { get; }
        internal bool ComboOk { get; }
        internal StatusVersionKey Key { get; }
        internal long Version { get; }
        internal StatusStamp[]? Stamps { get; }
        internal IJobStatusRules? Rules { get; init; }

        internal static PendingOutcome Apply(SimCharacter caster, SimCharacter recipient, PartyRole sourceRole,
            ushort statusId, int param, float duration, StatusVersionKey key, long version)
            => new(PendingKind.Apply, caster, recipient, sourceRole, statusId, param, duration,
                0, false, key, version, null);

        internal static PendingOutcome Remove(SimCharacter caster, SimCharacter recipient, PartyRole sourceRole,
            ushort statusId, StatusVersionKey key, long version)
            => new(PendingKind.Remove, caster, recipient, sourceRole, statusId, 0, 0f,
                0, false, key, version, null);

        internal static PendingOutcome Job(IJobStatusRules rules, SimCharacter caster, PartyRole sourceRole,
            uint actionId, bool comboOk, StatusStamp[] stamps)
            => new(PendingKind.Job, caster, caster, sourceRole, 0, 0, 0f,
                actionId, comboOk, default, 0, stamps) { Rules = rules };
    }
}
