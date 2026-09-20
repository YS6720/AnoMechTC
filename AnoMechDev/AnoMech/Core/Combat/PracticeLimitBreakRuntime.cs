using System;
using System.Numerics;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using ActionSheet = Lumina.Excel.Sheets.Action;

namespace AnoMech.Core.Combat;

// Scenario-local LB3 coordinator. ClassJob supplies the actor's PvE LB3;
// this runtime owns the shared practice reservation and never writes the live gauge.
internal sealed unsafe class PracticeLimitBreakRuntime : IDisposable
{
    private const byte LimitBreakActionCategory = 9;
    private const float MovementEpsilonSquared = 0.0001f;

    private readonly SimWorld world;
    private readonly Action<PartyRole, uint, Vector3?, SimCharacter?> onResolved;
    private PendingCast? active;
    private readonly PendingCast?[] recovering = new PendingCast?[8];
    private bool available = true;
    private bool disposed;

    internal PracticeLimitBreakRuntime(
        SimWorld world,
        Action<PartyRole, uint, Vector3?, SimCharacter?> onResolved)
    {
        this.world = world ?? throw new ArgumentNullException(nameof(world));
        this.onResolved = onResolved ?? throw new ArgumentNullException(nameof(onResolved));
    }

    internal bool IsAvailable => !disposed && available;

    internal bool IsCasting(PartyRole role)
        => !disposed && active is { } cast && cast.Role == role;

    internal bool IsBusy(PartyRole role)
        => IsCasting(role) || (!disposed && (uint)role < 8 && recovering[(int)role] != null);

    internal uint ActionFor(PartyRole role)
    {
        if (disposed || (uint)role >= 8) return 0;
        var member = world.Party.Get(role);
        if (member == null) return 0;
        var battleChara = member.BattleCharaPtr;
        if (battleChara == null || Plugin.ClientState.IsPvP)
            return 0;

        var jobs = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>();
        if (!jobs.TryGetRow(battleChara->ClassJob, out var job)) return 0;
        var actionId = job.LimitBreak3.RowId;
        return TryGetValidatedAction(actionId, out _) ? actionId : 0;
    }

    internal bool TryStart(
        PartyRole role,
        uint actionId,
        Vector3? location = null,
        SimCharacter? target = null)
    {
        if (disposed || !available || active != null || (uint)role >= 8 || IsBusy(role))
            return false;

        var caster = world.Party.Get(role);
        if (caster is not { IsActive: true } || !caster.IsAlive())
            return false;

        if (ActionFor(role) != actionId || !TryGetValidatedAction(actionId, out var action))
            return false;

        if (action.TargetArea)
        {
            if (location is null)
                return false;
        }
        else if (location is not null)
        {
            return false;
        }

        if (target is { IsActive: false })
            return false;

        var cast = new SimCast(caster, world.Coordinates);
        var pending = new PendingCast(
            role,
            caster,
            actionId,
            location,
            target,
            caster.Position,
            cast);

        // Reserve before crossing the native boundary. Any failed Start path
        // restores the reservation; successful casts keep it until release.
        available = false;
        active = pending;
        try
        {
            if (!cast.Start(
                    actionId,
                    location ?? target?.Position,
                    castTime: null,
                    targetId: target?.GameObjectId,
                    omenDelay: 0f,
                    omenRotate: 0f,
                    animationVariation: 0,
                    animationLock: RecoverySeconds(actionId)))
            {
                CancelActive(pending);
                return false;
            }
        }
        catch
        {
            CancelActive(pending);
            throw;
        }

        // SimCast releases instant actions synchronously. Treat that release as
        // completion, but never invoke the scenario callback merely on reserve.
        if (cast.ActionId == 0)
        {
            Complete(pending);
            return true;
        }

        if (!cast.IsCasting)
        {
            CancelActive(pending);
            return false;
        }

        return true;
    }

    internal void Refill()
    {
        if (disposed || active != null) return;
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
        if (disposed) return;
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
            !pending.Cast.IsCasting)
        {
            CancelActive(pending);
            return;
        }

        // SimCast reads the native cast clock. Drive that clock from scenario
        // delta so pause/time scaling cannot complete the LB in wall-clock time.
        pending.Elapsed += deltaSeconds;
        var native = pending.Caster.BattleCharaPtr;
        native->CastInfo.CurrentCastTime = MathF.Min(pending.Elapsed, native->CastInfo.TotalCastTime);
        pending.Cast.Tick(deltaSeconds);
        if (active is null || !ReferenceEquals(active, pending))
            return;

        if (pending.Cast.ActionId == 0)
            Complete(pending);
        else if (!pending.Cast.IsCasting)
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
        active = null;
        available = false;
        recovering[(int)pending.Role] = pending;
        SetRecovery(pending, true);
        ApplyTankStatus(pending);
        onResolved(pending.Role, pending.ActionId, pending.Location, pending.Target);
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
        pending.Cast.Despawn();
        active = null;
        available = true;
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
                (byte)battleChara->ClassJob,
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
        internal float Elapsed;

        internal PendingCast(
            PartyRole role,
            SimCharacter caster,
            uint actionId,
            Vector3? location,
            SimCharacter? target,
            Vector3 startPosition,
            SimCast cast)
        {
            Role = role;
            Caster = caster;
            ActionId = actionId;
            Location = location;
            Target = target;
            StartPosition = startPosition;
            Cast = cast;
        }
    }
}
