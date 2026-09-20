using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
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
    private const uint ReprisalActionId = 7535;
    private const ushort ReprisalStatusId = 1193;

    private readonly Game.Game game;
    private readonly RecordedAbilityCatalog catalog;
    private readonly RecordedAbilityQueue<PendingOutcome> queue = new();
    private readonly List<RecordedAbilityQueueEntry<PendingOutcome>> due = new();
    private readonly Dictionary<PartyRole, RoleState> roles = new();
    private readonly Dictionary<StatusVersionKey, long> statusVersions = new();
    private readonly Dictionary<uint, uint> prereqOf = new();
    private readonly HashSet<uint> startsCombo = new();
    private readonly HashSet<(SimCharacter Target, uint ActionId)> mechanicHits = new();
    private long generation;
    private long nextVersion;
    private long nextResourceRevision;

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

    internal bool TryUse(PartyRole role, uint actionId, byte classJob, byte level, SimCharacter? target,
        out bool comboOk, Vector3? location = null)
    {
        comboOk = false;
        if (!TryEnterCurrentRun()) return false;
        if (game.IsNetworkPeer || game.Paused)
            return false;
        if ((uint)role >= 8 || classJob == 0 || level == 0) return false;
        var caster = game.World.Party.Get(role);
        if (!IsUsableActor(caster)) return false;

        var observed = catalog.IsObservedAction(classJob, actionId, level);
        var jobRules = JobRules.StatusesFor(classJob);
        var reprisal = actionId == ReprisalActionId && TankLimitBreak.IsTank(classJob) && level >= 22;
        var tankLimitBreak = TankLimitBreak.TryGetStatus(actionId, classJob, out var lbStatus, out var lbDuration);
        var supported = jobRules != null && actionId != SprintActionId && !tankLimitBreak;
        if (supported)
        {
            var action = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(actionId);
            if (action is not { } row || !JobRules.IsAvailableAction(row, classJob, level))
                return false;
            if (row.TargetArea)
            {
                if (location is not { } point || !float.IsFinite(point.X) || !float.IsFinite(point.Y) ||
                    !float.IsFinite(point.Z) || caster.Placement().DistanceSq(point) > row.Range * row.Range)
                    return false;
                target = caster;
            }
            else if (location != null || !TryResolveJobTarget(row, caster, ref target)) return false;
        }
        var explicitDefault = actionId == SprintActionId || reprisal || tankLimitBreak || supported;
        if (!observed && !explicitDefault) return false;

        var rules = catalog.FindExecutableRules(classJob, actionId, level);
        var removals = catalog.FindExecutableRemovals(classJob, actionId, level);
        if (!TargetsAreValid(rules, removals, target)) return false;

        if (!roles.TryGetValue(role, out var state))
        {
            state = new RoleState(caster, role, classJob, level) { ResourceRevision = ++nextResourceRevision };
            roles.Add(role, state);
        }
        else if (!ReferenceEquals(state.Caster, caster) || state.ClassJob != classJob || state.Level != level)
        {
            // The declaration is frozen for this authenticated role/run. A
            // replacement actor must retire the role before it can submit again.
            return false;
        }
        if (classJob == Sage.JobId && actionId is 24304 or 24316 && state.Addersting == 0)
            return false;
        var context = Context(state, actionId, false, target);
        var consumeSwiftcast = supported && ConsumesSwiftcast(context, classJob);
        var consumeThinAir = classJob == WhiteMage.JobId && context.Find(1217) is not null &&
            Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRow(actionId).PrimaryCostType == 3;
        var roleStatus = supported ? ApplyRoleAction(context) : (ushort)0;

        var prerequisite = prereqOf.GetValueOrDefault(actionId);
        var hasPrerequisite = prereqOf.ContainsKey(actionId);
        comboOk = RecordedAbilityEligibility.IsComboReady(prerequisite, state.Combo, state.ComboAge);
        UpdateCombo(state, actionId, comboOk, hasPrerequisite);

        if (jobRules?.IsKnownAction(actionId) == true)
            QueueJob(jobRules, actionId, comboOk, state, target);

        if (actionId == SprintActionId)
        {
            // Sprint is an explicit long-standing default. Do not also replay
            // the sampled catalog row for status 50.
            QueueApply(state, state.Caster, SprintStatusId, SprintParam, SprintDuration, 0f);
        }

        if (reprisal)
        {
            // Self-centred AoE; include the enemy's hitbox (P6's boss is much
            // larger than the sheet radius). Never write helper/non-targetable actors.
            var radius = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>()
                .GetRow(ReprisalActionId).EffectRange;
            foreach (var child in game.World.Children)
            {
                if (child is not SimEnemy { IsActive: true, Targetable: true } enemy) continue;
                var reach = radius + enemy.HitboxRadius;
                if (state.Caster.Placement().DistanceSq(enemy) > reach * reach) continue;
                // Lv90 recording: status 1193, param 0, ~10s; Enhanced Reprisal
                // at Lv98 extends to 15s. This state is visual, not damage reduction.
                QueueApply(state, enemy, ReprisalStatusId, 0, level >= 98 ? 15f : 10f, 0f);
            }
        }
        if (tankLimitBreak)
        {
            var radius = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>()
                .GetRow(actionId).EffectRange;
            foreach (var member in game.World.Party.ActiveMembers())
                if (state.Caster.Placement().DistanceSq(member) <= radius * radius)
                    QueueApply(state, member, lbStatus, 0, lbDuration, 0f);
        }

        foreach (var rule in rules)
        {
            if (rule.ActionId == SprintActionId && rule.StatusId == SprintStatusId) continue;
            if (reprisal && rule.StatusId == ReprisalStatusId || tankLimitBreak && rule.StatusId == lbStatus) continue;
            if (jobRules != null && JobRules.IsOwnedTransition(jobRules, actionId, rule.StatusId)) continue;
            if (supported && (rule.StatusId == roleStatus || rule.StatusId is 167 or 1217)) continue;
            var recipient = rule.TargetKind == RecordedAbilityTargetKind.Self ? state.Caster : target!;
            QueueApply(state, recipient, rule.StatusId, rule.Param!.Value, rule.DurationSeconds, rule.DelaySeconds);
        }
        foreach (var removal in removals)
        {
            if (jobRules != null && JobRules.IsOwnedTransition(jobRules, actionId, removal.StatusId)) continue;
            if (supported && (removal.StatusId == roleStatus || removal.StatusId is 167 or 1217)) continue;
            var recipient = removal.TargetKind == RecordedAbilityTargetKind.Self ? state.Caster : target!;
            QueueRemove(state, recipient, removal.StatusId, removal.DelaySeconds);
        }
        // Preserve grant-before-consume ordering when reliable inputs arrive in
        // one framework batch. Zero-delay outcomes must not invalidate each
        // other before the first transition has actually run.
        ApplyDueOutcomes(0f);
        if (consumeSwiftcast) context.Remove(167);
        if (consumeThinAir) context.Remove(1217);
        foreach (var owner in roles.Values)
        {
            if (!IsUsableActor(owner.Caster)) continue;
            JobRules.StatusesFor(owner.ClassJob)?.OnPartyAction(
                Context(owner, actionId, comboOk, target), caster);
        }
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

        mechanicHits.Clear();
        foreach (var state in roles.Values)
        {
            state.ComboAge = MathF.Min(30f, state.ComboAge + realSeconds);
            if (state.ComboAge >= 30f) state.Combo = 0;
            if (IsUsableActor(state.Caster))
                JobRules.StatusesFor(state.ClassJob)?.Tick(Context(state), realSeconds);
            else if (state.Addersting != 0 || state.DarkArts)
            {
                state.Addersting = 0;
                state.DarkArts = false;
                PublishJobResource(state);
            }
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
                        outcome.Rules!.Apply(outcome.Context);
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
        foreach (var state in roles.Values)
            JobRules.StatusesFor(state.ClassJob)?.ResetStatusState();
        mechanicHits.Clear();
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
        // Keep the same base-action identity used by TC ActionCombo and the
        // local native combo state, without changing the action being executed.
        var comboAction = Machinist.ComboActionId(actionId);
        if (comboOk && startsCombo.Contains(comboAction))
        {
            state.Combo = comboAction;
            state.ComboAge = 0f;
        }
        else if (hasPrerequisite || startsCombo.Contains(comboAction))
        {
            state.Combo = 0;
            state.ComboAge = 30f;
        }
    }

    private static bool ConsumesSwiftcast(in JobActionContext context, byte classJob)
    {
        if (context.Find(167) is null) return false;
        var action = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRow(context.ActionId);
        if (action.ActionCategory.RowId != 2 || action.Cast100ms == 0) return false;
        if (classJob == BlackMage.JobId &&
            (context.ActionId == 152 && context.Find(165) is not null ||
             context.ActionId == 7422 && context.Level >= 80 ||
             context.ActionId == 16505 && context.Level >= 100)) return false;
        if (classJob == Paladin.JobId && context.ActionId is 7384 or 16458 &&
            (context.Find(2673) is not null || context.Find(1368) is not null)) return false;
        if (classJob == Reaper.JobId && context.ActionId == 24386 && context.Find(2845) is not null)
            return false;
        return true;
    }

    private static ushort ApplyRoleAction(in JobActionContext context)
    {
        // Role actions use the same authenticated job/level sheet gate as job actions.
        var (statusNumber, seconds) = context.ActionId switch
        {
            7531 => (1191, 20f), // Rampart
            7542 => (84, 20f),   // Bloodbath
            7546 => (1250, 10f), // True North
            7548 => (1209, 6f),  // Arm's Length
            7549 => (1195, context.Level >= 98 ? 15f : 10f),
            7560 => (1203, context.Level >= 98 ? 15f : 10f),
            7561 => (167, 10f),  // Swiftcast
            7562 => (1204, 21f), // Lucid Dreaming
            7559 => (160, 6f),   // Surecast
            7557 => (1199, 30f), // Peloton
            _ => (0, 0f),
        };
        var status = (ushort)statusNumber;
        if (status == 0) return 0;
        if (context.ActionId == 7557) context.GrantParty(status, 0, seconds, 30f);
        else context.Grant(status, 0, seconds,
            context.ActionId is 7549 or 7560 ? context.Target : null);
        return status;
    }

    private JobActionContext Context(RoleState state, uint actionId = 0, bool comboOk = false,
        SimCharacter? target = null)
        => new(this, actionId, comboOk, state.Caster, state.Role, state.Level, target, game.World);

    private bool TryResolveJobTarget(Lumina.Excel.Sheets.Action action, SimCharacter caster,
        ref SimCharacter? target)
    {
        // Self-centred abilities ignore the currently selected enemy. A friendly
        // spell can fall back to self, just like the native action dispatcher.
        if (action.CanTargetSelf &&
            (target == null || !action.CanTargetParty && !action.CanTargetHostile ||
                target is not ISimPartyMember && !action.CanTargetHostile))
            target = caster;
        if (target == null)
            return action.TargetArea || action.CanTargetSelf;
        if (target is ISimPartyMember member)
        {
            if (!ReferenceEquals(game.World.Party.Get(member.Role), target)) return false;
            if (ReferenceEquals(caster, target) ? !action.CanTargetSelf : !action.CanTargetParty)
                return false;
            if (member.Dead) return false;
        }
        else if (!action.CanTargetHostile || target is not SimEnemy { IsActive: true, Targetable: true })
            return false;
        var range = action.Range < 0 ? 3f : action.Range;
        var reach = range + caster.HitboxRadius + target.HitboxRadius;
        return ReferenceEquals(caster, target) || caster.Placement().DistanceSq(target) <= reach * reach;
    }

    internal void ApplyJobStatus(SimCharacter caster, PartyRole role, SimCharacter recipient,
        ushort statusId, int param, float duration)
    {
        if (!roles.TryGetValue(role, out var state) || !ReferenceEquals(state.Caster, caster) ||
            !IsUsableActor(recipient)) return;
        var key = new StatusVersionKey(recipient, statusId, role);
        NextVersion(key);
        recipient.AddStatusParam(statusId, param, duration, role, caster.GameObjectId);
    }

    internal void RemoveJobStatus(PartyRole role, SimCharacter recipient, ushort statusId)
    {
        if (!roles.ContainsKey(role)) return;
        NextVersion(new StatusVersionKey(recipient, statusId, role));
        recipient.RemoveStatus(statusId, role);
    }

    internal void ChangeJobResource(PartyRole role, JobResource resource, int amount)
    {
        if (!roles.TryGetValue(role, out var state) || !IsUsableActor(state.Caster)) return;
        if (resource == JobResource.Addersting && state.ClassJob == Sage.JobId)
        {
            var value = (byte)Math.Clamp(state.Addersting + amount, 0, 3);
            if (value == state.Addersting) return;
            state.Addersting = value;
        }
        else if (resource == JobResource.DarkArts && state.ClassJob == DarkKnight.JobId)
        {
            var value = Math.Clamp((state.DarkArts ? 1 : 0) + amount, 0, 1) != 0;
            if (value == state.DarkArts) return;
            state.DarkArts = value;
        }
        else if (resource == JobResource.Kenki && state.ClassJob == Samurai.JobId && amount == 10)
        {
            if (state.KenkiGained > int.MaxValue - amount) return;
            state.KenkiGained += amount;
        }
        else return;
        PublishJobResource(state);
    }

    private void PublishJobResource(RoleState state)
    {
        state.ResourceRevision = ++nextResourceRevision;
        if (state.Role == game.World.Party.PlayerRole)
            LocalJobResources.ApplyResourceFeedback(state.ClassJob, state.ResourceRevision,
                state.Addersting, state.DarkArts, state.KenkiGained);
    }

    internal Multiplayer.JobResourceState? JobResourceState(PartyRole role)
        => roles.TryGetValue(role, out var state) && state.ClassJob is Sage.JobId or DarkKnight.JobId or Samurai.JobId
            ? new Multiplayer.JobResourceState(state.ClassJob, state.ResourceRevision, state.Addersting, state.DarkArts, state.KenkiGained)
            : null;

    internal void NotifyMechanicHit(SimCharacter target, uint actionId)
    {
        if (!TryEnterCurrentRun() || game.IsNetworkPeer || game.Paused ||
            !game.World.Map.IsInInstance || !IsUsableActor(target) || target is not ISimPartyMember member ||
            !ReferenceEquals(game.World.Party.Get(member.Role), target) ||
            !mechanicHits.Add((target, actionId))) return;
        foreach (var state in roles.Values)
            if (IsUsableActor(state.Caster))
                JobRules.StatusesFor(state.ClassJob)?.OnMechanicHit(Context(state), target);
    }

    private void QueueJob(IJobStatusRules rules, uint actionId, bool comboOk, RoleState state, SimCharacter? target)
    {
        var touched = rules.TouchedStatuses(actionId, comboOk);
        var stamps = touched.Count == 0 ? Array.Empty<StatusStamp>() : new StatusStamp[touched.Count];
        for (var i = 0; i < touched.Count; i++)
        {
            var key = new StatusVersionKey(state.Caster, touched[i], state.Role);
            stamps[i] = new(key, NextVersion(key));
        }
        queue.Add(0f, generation, ++nextVersion,
            PendingOutcome.Job(rules, state.Caster, state.Role, actionId, comboOk, stamps,
                Context(state, actionId, comboOk, target)));
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
        internal byte Addersting { get; set; }
        internal bool DarkArts { get; set; }
        internal int KenkiGained { get; set; }
        internal long ResourceRevision { get; set; }
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
        internal JobActionContext Context { get; init; }

        internal static PendingOutcome Apply(SimCharacter caster, SimCharacter recipient, PartyRole sourceRole,
            ushort statusId, int param, float duration, StatusVersionKey key, long version)
            => new(PendingKind.Apply, caster, recipient, sourceRole, statusId, param, duration,
                0, false, key, version, null);

        internal static PendingOutcome Remove(SimCharacter caster, SimCharacter recipient, PartyRole sourceRole,
            ushort statusId, StatusVersionKey key, long version)
            => new(PendingKind.Remove, caster, recipient, sourceRole, statusId, 0, 0f,
                0, false, key, version, null);

        internal static PendingOutcome Job(IJobStatusRules rules, SimCharacter caster, PartyRole sourceRole,
            uint actionId, bool comboOk, StatusStamp[] stamps, JobActionContext context)
            => new(PendingKind.Job, caster, caster, sourceRole, 0, 0, 0f,
                actionId, comboOk, default, 0, stamps) { Rules = rules, Context = context };
    }
}
