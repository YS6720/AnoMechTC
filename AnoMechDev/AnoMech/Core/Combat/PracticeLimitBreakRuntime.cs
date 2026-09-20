using System;
using System.Numerics;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using AnoMech.Multiplayer;
using ActionSheet = Lumina.Excel.Sheets.Action;

namespace AnoMech.Core.Combat;

// Scenario-local LB3 coordinator. ClassJob supplies the actor's PvE LB3;
// this runtime owns the shared practice reservation and never writes the live gauge.
internal sealed unsafe class PracticeLimitBreakRuntime : IDisposable
{
    private const byte LimitBreakActionCategory = 9;
    private const float MovementEpsilonSquared = 0.0001f;

    private readonly SimWorld world;
    private readonly Action<PartyRole, uint, Vector3?, SimCharacter?>? onResolved;
    private readonly bool replica;
    private LimitBreakState? networkState;
    private readonly byte?[] networkJobs = new byte?[8];
    private readonly long[] lastRequests = new long[8];
    private long requestSequence;
    private long pendingRequest;
    private PendingCast? active;
    private readonly PendingCast?[] recovering = new PendingCast?[8];
    private bool available = true;
    private bool disposed;
    // Temporary local-only evidence for the reported D3/D4 start rejection.
    // At most 24 failures per run; remove after the native boundary is identified.
    private int startDiagnosticsRemaining = 24;
    // Temporary completion-boundary evidence; bounded per run, no external logging.
    private int lifecycleDiagnosticsRemaining = 24;

    internal PracticeLimitBreakRuntime(
        SimWorld world,
        Action<PartyRole, uint, Vector3?, SimCharacter?> onResolved)
    {
        this.world = world ?? throw new ArgumentNullException(nameof(world));
        this.onResolved = onResolved ?? throw new ArgumentNullException(nameof(onResolved));
    }

    private PracticeLimitBreakRuntime(SimWorld world)
    {
        this.world = world;
        replica = true;
        available = false;
    }

    internal static PracticeLimitBreakRuntime CreateReplica(SimWorld world) => new(world);

    internal LimitBreakState CaptureState()
    {
        byte mask = 0;
        for (var i = 0; i < recovering.Length; i++)
            if (recovering[i] != null) mask |= (byte)(1 << i);
        return new LimitBreakState(IsAvailable, active?.Role, mask,
            (long[])lastRequests.Clone(), active?.RequestId ?? 0);
    }

    internal void ApplyNetworkState(LimitBreakState state)
    {
        if (!replica || disposed) return;
        networkState = state;
        available = state.Available;
        var localRole = world.Party.PlayerRole;
        if ((uint)localRole < 8 && state.LastRequests[(int)localRole] >= pendingRequest)
            pendingRequest = 0;
        for (var i = 0; i < 8; i++)
        {
            if (world.Party.Get((PartyRole)i) is not { } member) continue;
            // A canceled cast event can arrive after an idle snapshot without
            // any casting snapshot in between. Reconcile native LB presentation
            // on every authoritative state, not only CastingRole transitions.
            var native = member.BattleCharaPtr;
            if (state.CastingRole != (PartyRole)i && native != null && native->CastInfo.IsCasting &&
                native->CastInfo.ActionType == FFXIVClientStructs.FFXIV.Client.Game.ActionType.Action &&
                TryGetValidatedAction(native->CastInfo.ActionId, out _))
                native->CastInfo.IsCasting = false;
            member.LimitBreakRecovery = (state.RecoveryMask & (1 << i)) != 0;
            if (member is SimPlayer player) player.SyncInputLock();
        }
    }

    internal bool IsAvailable => !disposed && available && pendingRequest == 0;
    // Reservation/readiness is intentionally separate from the underlying charge:
    // an accepted cast keeps the shared gauge visible until release completes.
    internal bool HasGaugeCharge => !disposed && (replica
        ? networkState is { Available: true } || networkState?.CastingRole != null
        : available || active != null);

    internal bool IsCasting(PartyRole role)
        => !disposed && (replica ? networkState?.CastingRole == role : active is { } cast && cast.Role == role);

    internal bool IsBusy(PartyRole role)
        => IsCasting(role) || (!disposed && (uint)role < 8 &&
            (replica ? ((networkState?.RecoveryMask ?? 0) & (1 << (int)role)) != 0 : recovering[(int)role] != null));

    internal uint ActionFor(PartyRole role, byte? classJob = null)
    {
        if (disposed || (uint)role >= 8) return 0;
        var member = world.Party.Get(role);
        if (member == null) return 0;
        var battleChara = member.BattleCharaPtr;
        if (battleChara == null || Plugin.ClientState.IsPvP)
            return 0;

        var jobs = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>();
        if (!jobs.TryGetRow(classJob ?? battleChara->ClassJob, out var job)) return 0;
        var actionId = job.LimitBreak3.RowId;
        return TryGetValidatedAction(actionId, out _) ? actionId : 0;
    }

    internal long BeginRequest(PartyRole role)
    {
        if (!IsAvailable || IsBusy(role) || requestSequence == long.MaxValue) return 0;
        var request = ++requestSequence;
        if (replica) pendingRequest = request;
        return request;
    }

    internal void RejectRequest(long request)
    {
        if (pendingRequest == request) pendingRequest = 0;
    }

    internal long ActiveRequest(PartyRole role)
        => replica ? networkState?.CastingRole == role ? networkState.CastingRequestId : pendingRequest
            : active?.Role == role ? active.RequestId : 0;

    internal bool ApplyRequest(PartyRole role, AbilityUseMessage request, SimCharacter? target, bool canStart = true)
    {
        if (replica || disposed || (uint)role >= 8 || request.LimitBreakRequestId <= 0) return false;
        if (request.CancelLimitBreak)
        {
            if (active is not { } pending || pending.Role != role ||
                pending.RequestId != request.LimitBreakRequestId) return false;
            CancelActive(pending);
            return true;
        }
        if (request.LimitBreakRequestId <= lastRequests[(int)role]) return false;
        lastRequests[(int)role] = request.LimitBreakRequestId;
        return canStart && TryStart(role, request.ActionId, request.Location?.ToVector(), target,
            world.Party.Get(role) is SimNetworkPuppet ? request.ClassJob : null,
            request.LimitBreakRequestId);
    }

    internal bool TryStart(
        PartyRole role,
        uint actionId,
        Vector3? location = null,
        SimCharacter? target = null,
        byte? classJob = null,
        long requestId = 0)
    {
        if (replica || disposed || !available || active != null || (uint)role >= 8 || IsBusy(role))
            return TraceStartFailure(role, actionId, "runtime-busy-or-unavailable");

        var caster = world.Party.Get(role);
        if (caster is not { IsActive: true } || !caster.IsAlive())
            return TraceStartFailure(role, actionId, "caster-inactive-or-dead");

        // A remote human's native stand-in retains its preset job. Bind the
        // authenticated role's declared job once, not the stand-in's cosmetics.
        if (classJob is { } declared &&
            (caster is not SimNetworkPuppet || networkJobs[(int)role] is { } frozen && frozen != declared))
            return TraceStartFailure(role, actionId, "remote-job-binding");
        if (ActionFor(role, classJob) != actionId || !TryGetValidatedAction(actionId, out var action))
            return TraceStartFailure(role, actionId, "action-or-shape-mismatch");

        if (action.TargetArea)
        {
            if (location is null)
                return TraceStartFailure(role, actionId, "missing-ground-location");
        }
        else if (location is not null)
        {
            return TraceStartFailure(role, actionId, "unexpected-ground-location");
        }

        if (target is { IsActive: false })
            return TraceStartFailure(role, actionId, "inactive-target");

        var cast = new SimCast(caster, world.Coordinates);
        var pending = new PendingCast(
            role,
            caster,
            actionId,
            location,
            target,
            caster.Position,
            cast,
            classJob ?? caster.BattleCharaPtr->ClassJob,
            requestId);

        // Reserve before crossing the native boundary. Any failed Start path
        // restores the reservation; successful casts keep it until release.
        available = false;
        active = pending;
        try
        {
            var aim = location ?? target?.Position;
            if (!action.TargetArea && aim is { } point)
            {
                var direction = point - caster.Position;
                if (direction.X * direction.X + direction.Z * direction.Z > 0.000001f)
                    caster.SetRotation(MathF.Atan2(direction.X, direction.Z));
            }
            if (!cast.Start(
                    actionId,
                    aim,
                    castTime: null,
                    targetId: target?.GameObjectId,
                    omenDelay: 0f,
                    omenRotate: 0f,
                    animationVariation: 0,
                    animationLock: RecoverySeconds(actionId),
                    localOmenOrigin: action.TargetArea ? location : caster.Position))
            {
                TraceStartFailure(role, actionId, "native-start-returned-false");
                CancelActive(pending);
                return false;
            }
        }
        catch
        {
            TraceStartFailure(role, actionId, "native-start-exception");
            CancelActive(pending);
            throw;
        }

        if (classJob is { } acceptedJob) networkJobs[(int)role] = acceptedJob;
        // SimCast releases instant actions synchronously. Treat that release as
        // completion, but never invoke the scenario callback merely on reserve.
        if (cast.ActionId == 0)
        {
            Complete(pending);
            return true;
        }

        if (!cast.IsCasting)
        {
            TraceStartFailure(role, actionId, "native-cast-not-active");
            CancelActive(pending);
            return false;
        }

        TraceLifecycle(pending, "started");
        return true;
    }

    private bool TraceStartFailure(PartyRole role, uint actionId, string reason)
    {
        if (startDiagnosticsRemaining <= 0) return false;
        startDiagnosticsRemaining--;
        var member = (uint)role < 8 ? world.Party.Get(role) : null;
        var native = member == null ? null : member.BattleCharaPtr;
        CrashTrace.Log($"[LB起手診斷] reason={reason} role={role} action={actionId}"
            + $" replica={replica} available={available} active={active?.Role}"
            + $" actor={member?.GetType().Name} job={(native == null ? -1 : native->ClassJob)}"
            + $" nativeCasting={native != null && native->CastInfo.IsCasting}"
            + $" nativeAction={(native == null ? 0u : native->CastInfo.ActionId)}"
            + $" castTime={(native == null ? 0f : native->CastInfo.CurrentCastTime):F3}/{(native == null ? 0f : native->CastInfo.TotalCastTime):F3}");
        return false;
    }

    internal void Refill()
    {
        if (replica || disposed || active != null) return;
        available = true;
    }

    internal void Cancel(PartyRole role)
    {
        if (disposed || active is not { } pending || pending.Role != role)
            return;
        CancelActive(pending);
    }

    internal void Tick(float deltaSeconds)
    {
        if (replica || disposed) return;
        for (var i = 0; i < recovering.Length; i++)
        {
            if (recovering[i] is not { } recovery) continue;
            recovery.Cast.Tick(deltaSeconds);
            if (recovery.Caster.IsActive && recovery.Caster.IsAlive() && recovery.Cast.IsBusy)
                continue;
            SetRecovery(recovery, false);
            recovering[i] = null;
        }

        if (active is not { } pending)
            return;

        // Position change is deliberate here: SimPlayer.IsMoving also includes
        // this action's input latch, so using it would cancel every player LB.
        if (!pending.Caster.IsActive || !pending.Caster.IsAlive() || HasMoved(pending) ||
            !pending.Cast.IsManagedCasting)
        {
            CancelActive(pending);
            return;
        }

        // Native CastInfo can clear on the last client frame before our tick.
        // Only the scenario clock and explicit cancellation own this LB's outcome.
        pending.Elapsed += deltaSeconds;
        pending.Cast.TickOwned(deltaSeconds, pending.Elapsed);
        if (active is null || !ReferenceEquals(active, pending))
            return;

        if (pending.Cast.ActionId == 0)
            Complete(pending);
        else if (!pending.Cast.IsManagedCasting)
            CancelActive(pending);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (active is { } pending)
        {
            pending.Cast.Despawn();
            active = null;
        }
        if (replica)
            foreach (var member in world.Party.AllMembers())
            {
                member.LimitBreakRecovery = false;
                if (member is SimPlayer player) player.SyncInputLock();
            }
        for (var i = 0; i < recovering.Length; i++)
        {
            if (recovering[i] is { } recovery) SetRecovery(recovery, false);
            recovering[i] = null;
        }
        available = false;
    }

    private void Complete(PendingCast pending)
    {
        if (disposed || !ReferenceEquals(active, pending)) return;
        TraceLifecycle(pending, "completed");
        active = null;
        available = false;
        recovering[(int)pending.Role] = pending;
        SetRecovery(pending, true);
        ApplyTankStatus(pending);
        onResolved!(pending.Role, pending.ActionId, pending.Location, pending.Target);
    }

    private static void SetRecovery(PendingCast pending, bool locked)
    {
        pending.Caster.LimitBreakRecovery = locked;
        if (pending.Caster is SimPlayer player) player.SyncInputLock();
    }

    // Post-release lock, not cast time or tank mitigation duration. Action has no
    // animation-lock column. Tank/melee/healer values match local ActionEffect
    // recordings; ranged/caster values: https://ffxiv.consolegameswiki.com/wiki/Limit_Break
    private static float RecoverySeconds(uint actionId) => actionId switch
    {
        199 or 4240 or 4241 or 17105 => 3.86f,
        205 or 4246 or 7862 or 34867 or 208 or 4247 or 4248 or 24859 => 8.1f,
        _ => 3.7f, // Remaining ClassJob.LimitBreak3 actions: melee / physical ranged.
    };

    private void CancelActive(PendingCast pending)
    {
        if (!ReferenceEquals(active, pending)) return;
        TraceLifecycle(pending, "canceled");
        pending.Cast.Despawn();
        active = null;
        available = true;
    }

    private void TraceLifecycle(PendingCast pending, string phase)
    {
        if (lifecycleDiagnosticsRemaining-- <= 0) return;
        var native = pending.Caster.BattleCharaPtr;
        CrashTrace.Log($"[LB結算診斷] phase={phase} role={pending.Role} action={pending.ActionId}"
            + $" elapsed={pending.Elapsed:F3} alive={pending.Caster.IsAlive()} moved={HasMoved(pending)}"
            + $" active={pending.Caster.IsActive} nativeCasting={native != null && native->CastInfo.IsCasting}"
            + $" nativeAction={(native == null ? 0u : native->CastInfo.ActionId)}"
            + $" nativeTime={(native == null ? 0f : native->CastInfo.CurrentCastTime):F3}/{(native == null ? 0f : native->CastInfo.TotalCastTime):F3}");
    }

    private static bool HasMoved(PendingCast pending)
    {
        var delta = pending.Caster.Position - pending.StartPosition;
        return delta.LengthSquared() > MovementEpsilonSquared;
    }

    private void ApplyTankStatus(PendingCast pending)
    {
        var battleChara = pending.Caster.BattleCharaPtr;
        if (battleChara == null ||
            !TankLimitBreak.TryGetStatus(
                pending.ActionId,
                pending.ClassJob,
                out var statusId,
                out var duration))
            return;

        var action = Plugin.DataManager.GetExcelSheet<ActionSheet>().GetRow(pending.ActionId);
        var radius = (float)action.EffectRange;
        var sourceObject = pending.Caster.GameObjectId;
        var radiusSquared = radius * radius;
        foreach (var member in world.Party.ActiveMembers())
        {
            if (radius > 0f && Vector3.DistanceSquared(pending.Caster.Position, member.Position) > radiusSquared)
                continue;
            member.AddStatusParam(statusId, 0, duration, pending.Role, sourceObject);
        }
    }

    private static bool TryGetValidatedAction(uint actionId, out ActionSheet action)
    {
        action = default;
        if (actionId == 0) return false;

        var sheet = Plugin.DataManager.GetExcelSheet<ActionSheet>();
        if (!sheet.TryGetRow(actionId, out action) ||
            action.ActionCategory.RowId != LimitBreakActionCategory)
            return false;

        var range = (float)action.EffectRange;
        var width = (float)action.XAxisModifier;
        if (!float.IsFinite(range) || range < 0f || !float.IsFinite(width) || width < 0f)
            return false;

        // Only accept shapes that the simulator can represent from Lumina;
        // ClassJob supplies the job binding without consulting native PvP mode.
        return action.CastType switch
        {
            1 => true,
            2 or 3 or 5 or 6 or 10 or 13 => range > 0f,
            4 or 8 or 11 or 12 => range > 0f && width > 0f,
            _ => false,
        };
    }

    private sealed class PendingCast
    {
        internal readonly PartyRole Role;
        internal readonly SimCharacter Caster;
        internal readonly uint ActionId;
        internal readonly Vector3? Location;
        internal readonly SimCharacter? Target;
        internal readonly Vector3 StartPosition;
        internal readonly SimCast Cast;
        internal readonly byte ClassJob;
        internal readonly long RequestId;
        internal float Elapsed;

        internal PendingCast(
            PartyRole role,
            SimCharacter caster,
            uint actionId,
            Vector3? location,
            SimCharacter? target,
            Vector3 startPosition,
            SimCast cast,
            byte classJob,
            long requestId)
        {
            Role = role;
            Caster = caster;
            ActionId = actionId;
            Location = location;
            Target = target;
            StartPosition = startPosition;
            Cast = cast;
            ClassJob = classJob;
            RequestId = requestId;
        }
    }
}
