using System;
using System.Collections.Generic;
using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Geometry;
using AnoMech.Core.Game.Party;
using AnoMech.Core.Native;
using AnoMech.Multiplayer;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Lumina.Excel.Sheets;

namespace AnoMech.Core.SimObjects;

// Common base for anything in the simulated world that has a BattleChara behind it
public abstract unsafe class SimCharacter(Coordinates coordinates) : ISimObject, IPositioned
{
    private readonly List<SimVfx> vfx = [];
    private readonly List<SimStatus> statusList = [];
    
    // Host-only managed event sink. A peer deliberately leaves this unset so
    // applying a received cue can never echo a second cue back to the host.
    private Action<SimNetworkEvent>? networkEventSink;
    internal Action<SimNetworkEvent>? NetworkEventSink
    {
        get => networkEventSink;
        set => networkEventSink = value;
    }

    internal void RecordNetworkEvent(WorldEvent item)
        => networkEventSink?.Invoke(new SimNetworkWorldEvent(this, item));

    internal void RecordNetworkCast(uint actionId, byte actionType, float castSeconds,
        float omenDelay, bool interruptible, float? rotation, Vector3? position,
        GameObjectId? target)
        => networkEventSink?.Invoke(new SimNetworkCastEvent(this, actionId, actionType,
            castSeconds, omenDelay, interruptible, rotation, position, target));

    internal void RecordNetworkActionEffect(uint actionId, float animationLock, ushort spellId,
        byte animationVariation, byte actionType, byte flags, GameObjectId[] targets,
        float? rotation, Vector3? position, GameObjectId? animationTarget)
        => networkEventSink?.Invoke(new SimNetworkActionEffectEvent(this, actionId,
            animationLock, spellId, animationVariation, actionType, flags, targets,
            rotation, position, animationTarget));
    
    internal void RecordNetworkUnsupported(string description)
        => networkEventSink?.Invoke(new SimNetworkUnsupportedEvent(this, description));
    
    internal abstract BattleChara* BattleCharaPtr { get; }
    
    private protected abstract Movement Movement { get; }
    
    protected readonly Coordinates Coordinates = coordinates;

    // Obstacles this character's Movement steers around. Defaults to the shared
    // empty field (no avoidance — straight lines); PartyCreator points party
    // doppels at world.Obstacles so only bots avoid geometry.
    internal ObstacleField Obstacles { get; set; } = ObstacleField.Empty;

    public virtual bool IsActive => BattleCharaPtr != null;

    // True while the character is rooted by an in-progress action (cast bar up or
    // release animation still playing). The Movement subsystem reads this to hold
    // an active follow in place until the animation finishes. Practice LB recovery
    // is independent of the shared gauge; SimEnemy owns its own cast lock.
    internal bool LimitBreakRecovery { get; set; }
    public virtual bool AnimationLock => LimitBreakRecovery;

    public GameObjectId GameObjectId => BattleCharaPtr == null ? default : BattleCharaPtr->GetGameObjectId();
    public float HitboxRadius => BattleCharaPtr == null ? 0f : BattleCharaPtr->HitboxRadius;
    
    internal void ApplyNetworkHealthFraction(float fraction)
    {
        var native = BattleCharaPtr;
        if (native == null) return;
        var clamped = Math.Clamp(fraction, 0f, 1f);
        native->Health = (uint)MathF.Round(native->MaxHealth * clamped);
    }
    
    
    public virtual void Tick(float deltaSeconds)
    {
        RefreshPosition();
        snapshots.Record(deltaSeconds, Position);
        statusList.Update(deltaSeconds);
        vfx.Update(deltaSeconds);
        Movement.Tick(deltaSeconds);
    }

    public virtual void Despawn()
    {
        LimitBreakRecovery = false;
        statusList.Despawn();
        vfx.Despawn();
    }
    
    // -------------------------
    // Location Subsystem
    // Cached local position; event-phase collision must refresh before World.Tick.
    // Virtual so a network puppet can expose its latest owner-reported pose while
    // its native model catches up independently.
    private Vector3 position;
    public virtual Vector3 Position => position;
    public virtual float Rotation { get; private set; }

    // Damage snapshot history: the game reads your position a short moment before the omen
    // ends (維護者 2026-09-18：「橘預警線結束 0.3 秒前站進去都還不會算傷害」), while we used to
    // judge at the exact resolution frame — anyone entering late died. Logic lives in
    // PositionSnapshotBuffer so the rule is unit-testable without native access.
    private AnoMech.Core.Game.PositionSnapshotBuffer snapshots;
    public Vector3 SnapshotPosition => snapshots.Resolve(Position);

    // Position only: do not advance movement, statuses, casts, or input state.
    internal void RefreshPosition()
    {
        var native = BattleCharaPtr;
        if (native == null) return;
        position = Coordinates.ToLocal(native->Position);
        Rotation = native->Rotation;
    }

    public virtual void SetPosition(Vector3 position)
    {
        var obj = BattleCharaPtr;
        if (obj == null) return;
        var w = Coordinates.ToGlobal(position);
        obj->SetPosition(w.X, w.Y, w.Z);
        if (obj->DrawObject != null) obj->DrawObject->Object.Position = w;
        this.position = position; // early update, will be updated on next tick anyway
    }
    
    public virtual void SetRotation(float rotation)
    {
        var obj = BattleCharaPtr;
        if (obj == null) return;
        obj->SetRotation(MathUtil.NormalizeRotation(rotation));
        Rotation = rotation; // early update, will be updated on next tick anyway
    }
    
    public void SetPosition(Placement placement)
    {
        SetPosition(placement.Position);
        SetRotation(placement.Rotation);
    }
    
    public void Face(Vector3? target) => Movement.Face(target);
    public void Face(IPositioned? target) => Face(target?.Position);
    public void MoveTo(Vector3 target, float speed = 6f, float? finalRotation = null)
        => Movement.MoveTo(target, speed, finalRotation);
    public void MoveTo(Placement p) => MoveTo(p.Position);
    /// <summary>錄影軌跡回放：在 duration 秒內按取樣時間把 XYZ 一起內插到 target，準時抵達。</summary>
    public void MoveRecorded(Vector3 target, float duration, float? finalRotation = null)
        => Movement.MoveRecorded(target, duration, finalRotation);
    protected void StopMoving() => Movement.Stop();

    public void Intercept(SimTether? tether, float margin = 3f) => Movement.Intercept(tether, margin);


    // -------------------------
    // VFX Subsystem
    // -------------------------
    
    // Self-attached actor VFX keyed by path.
    // persistent: true  → tracked by sim (might crash if we try to remove vfx after game already did that)
    // persistent: false → fire-and-forget (game is responsible for duration and cleaning of vfx)
    public void AddVfx(string path, float duration = 0f, bool persistent = true)
        => AddVfxInternal(path, duration, persistent, emitNetworkEvent: true);

    // Peer-side native application uses this result to turn a failed actor-VFX
    // allocation into a run failure instead of claiming the cue was applied.
    internal bool TryAddNetworkVfx(string path, float duration, bool persistent)
        => AddVfxInternal(path, duration, persistent, emitNetworkEvent: false);

    private bool AddVfxInternal(string path, float duration, bool persistent, bool emitNetworkEvent)
    {
        if (!VfxFunctions.VfxPathExists(path) || !IsActive) return false;
        if (persistent && FindVfx(path) is {} existing)
        {
            existing.Refresh(duration);
            if (emitNetworkEvent)
                RecordNetworkEvent(new ActorVfxEvent(default, path, duration, persistent));
            return true;
        }
        var spawned = new SimVfx(this, path, duration);
        if (!spawned.IsActive) return false;
        if (persistent) vfx.Add(spawned);
        if (emitNetworkEvent)
            RecordNetworkEvent(new ActorVfxEvent(default, path, duration, persistent));
        return true;
    }
    
    public void AttachLockonVfx(uint lockonId, float duration = 0f, bool persistent = true)
    {
        if (VfxFunctions.LockonVfxIconName(lockonId) is {} iconName)
            AddVfx($"vfx/lockon/eff/{iconName}.avfx", duration, persistent);
    }

    public SimVfx? FindVfx(string path)
    {
        return vfx.Find(v => v.IsActive && v.Path == path);
    }

    
    // FIXME: minor, keep track of tethers and slots attached to character
    public bool HasTetherInSlot0(ushort tetherId)
        => BattleCharaPtr != null && VfxFunctions.GetTetherId((Character*)BattleCharaPtr, 0) == tetherId;
    public void RemoveVfx(string path)
    {
        var existing = FindVfx(path);
        if (existing == null) return;
        existing.Despawn();
        RecordNetworkEvent(new RemoveActorVfxEvent(default, path));
    }
    public SimStatus? AddStatus(ushort statusId, float duration = 0f, int stacks = 1, bool overrideStacks = false)
    {
        if (FindStatus(statusId) is {} status)
        {
            // overrideStacks: stacks is the absolute target; otherwise it's a
            // relative delta (negative consumes stacks).
            int delta = overrideStacks ? stacks - status.Stacks : stacks;
            status.Reapply(duration, delta);
            if (status.Stacks == 0)
            {
                status.Despawn();   // last stack consumed → remove the status
                return null;
            }
            return status;
        }

        // No existing status: a non-positive request has nothing to remove.
        // A full managed/native status table refuses a new key without
        // disturbing any existing status.
        if (!CanCreateStatus(statusId, default)) return null;
        var s = new SimStatus(this, statusId, duration, (ushort)stacks);
        statusList.Add(s);
        return s;
    }

    public SimStatus? AddStatusParam(
        ushort statusId,
        int param,
        float duration = 0f,
        PartyRole? sourceRole = null,
        GameObjectId sourceObject = default,
        float? nativeRemainingOverride = null)
    {
        // Raw status observations are absolute values, including a live param
        // of 0. Refresh the exact source-keyed instance so snapshots never
        // contain duplicate keys.
        if (FindStatus(statusId, sourceRole) is {} existing)
        {
            existing.ApplyNetworkState(duration, (ushort)param, nativeRemainingOverride, sourceObject);
            return existing;
        }
        if (!CanCreateStatus(statusId, sourceObject)) return null;
        var s = new SimStatus(
            this,
            statusId,
            duration,
            (ushort)param,
            sourceRole,
            sourceObject,
            nativeRemainingOverride);
        statusList.Add(s);
        return s;
    }
    private bool CanCreateStatus(ushort statusId, GameObjectId sourceObject)
    {
        var activeCount = 0;
        foreach (var status in statusList)
            if (status.IsActive) activeCount++;
        return activeCount < MpLimits.Statuses &&
            Statuses.HasCapacity((Character*)BattleCharaPtr, statusId, sourceObject);
    }

    // Peer-side replication entry point. The owner's snapshot decides
    // existence, so a Param of 0 creates and keeps the status instead of
    // reading as "no stacks left" (see StatusReplication for the full rule).
    // Statuses already tracked are refreshed in place, never re-initialised.
    internal void ApplyNetworkStatuses(
        StatusState[] statuses,
        Func<PartyRole, GameObjectId>? sourceResolver = null)
    {
        var sink = new NetworkStatusSink(this, sourceResolver);
        StatusReplication.Apply(ref sink, statuses);
    }

    private readonly struct NetworkStatusSink(
        SimCharacter owner,
        Func<PartyRole, GameObjectId>? sourceResolver) : IStatusReplicationSink
    {
        public int TrackedCount => owner.statusList.Count;

        public StatusKey TrackedKeyAt(int index)
        {
            var status = owner.statusList[index];
            return new StatusKey(status.StatusId, status.SourceRole);
        }

        public void Remove(StatusKey key) => owner.RemoveStatus(key.Id, key.SourceRole);

        public bool TryRefresh(StatusState state)
        {
            if (owner.FindStatus(state.Id, state.SourceRole) is not {} status) return false;
            status.ApplyNetworkState(
                state.Remaining,
                state.Param,
                state.NativeRemainingOverride,
                ResolveSource(state.SourceRole, status.SourceObject));
            return true;
        }

        // Same creation path a host scenario uses for a raw-param status, so
        // the peer gets the identical native status-init visual.
        public void Create(StatusState state)
        {
            var sourceObject = ResolveSource(state.SourceRole);
            if (!owner.CanCreateStatus(state.Id, sourceObject)) return;
            owner.statusList.Add(new SimStatus(
                owner,
                state.Id,
                state.Remaining,
                state.Param,
                state.SourceRole,
                sourceObject,
                state.NativeRemainingOverride));
        }

        private GameObjectId ResolveSource(PartyRole? sourceRole, GameObjectId fallback = default)
        {
            if (sourceRole is not {} role) return default;
            return sourceResolver is {} resolver ? resolver(role) : fallback;
        }

    }
    public void RemoveStatus(ushort statusId)
        => RemoveStatus(statusId, null);

    public void RemoveStatus(ushort statusId, PartyRole? sourceRole)
    {
        FindStatus(statusId, sourceRole)?.Despawn();
    }

    public SimStatus? FindStatus(ushort statusId)
        => FindStatus(statusId, null);

    /// <summary>不分來源，移除這個 id 的每一份活躍狀態。</summary>
    public void RemoveStatusAnySource(ushort statusId)
    {
        for (var i = 0; i < statusList.Count; i++)
        {
            var status = statusList[i];
            if (status.IsActive && status.StatusId == statusId) status.Despawn();
        }
    }

    public SimStatus? FindStatus(ushort statusId, PartyRole? sourceRole)
    {
        for (var i = 0; i < statusList.Count; i++)
        {
            var status = statusList[i];
            if (status.IsActive &&
                status.StatusId == statusId &&
                status.SourceRole == sourceRole)
                return status;
        }
        return null;
    }

    // Managed status state is the source of truth for simulated mechanics; the
    // native status array is only the rendering sink. Param is the raw native
    // ushort a caller asked for (stack count for stacking statuses, but 0 is a
    // legitimate value for marker statuses) — never treat 0 as "absent".
    public StatusState[] ActiveStatusSnapshot
    {
        get
        {
            var count = 0;
            foreach (var status in statusList)
                if (status.IsActive) count++;

            var snapshot = new StatusState[count];
            var index = 0;
            foreach (var status in statusList)
            {
                if (!status.IsActive) continue;
                snapshot[index++] = new StatusState(
                    status.StatusId,
                    status.Stacks,
                    status.RemainingTime,
                    status.SourceRole,
                    status.NativeRemainingOverride);
            }
            return snapshot;
        }
    }

    public bool HasStatus(ushort statusId) => FindStatus(statusId) != null;
    
    
    // -------------------------
    // Other Subsystem
    // -------------------------
    
    public virtual void PlayActionTimeline(ushort timelineId, ushort loopId = 0, ushort baseOverride = 0)
    {
        if (!PlayActionTimelineCore(timelineId, loopId, baseOverride)) return;
        // Movement uses the looping run clip with BaseOverride=RunTimelineId.
        // It is local presentation, not a mechanic cue, and must not create
        // network traffic on every movement start/stop.
        if (timelineId != 22 || baseOverride != timelineId)
            RecordNetworkEvent(new ActionTimelineEvent(default, timelineId, loopId, baseOverride));
    }

    internal void PlayActionTimelineNative(ushort timelineId, ushort loopId = 0, ushort baseOverride = 0)
        => PlayActionTimelineCore(timelineId, loopId, baseOverride);

    private bool PlayActionTimelineCore(ushort timelineId, ushort loopId, ushort baseOverride)
    {
        var chara = BattleCharaPtr;
        if (chara == null) return false;
        if (chara->Timeline.TimelineSequencer.Parent == null) return false;
        chara->Timeline.BaseOverride = baseOverride;
        chara->Timeline.PlayActionTimeline(timelineId, loopId);
        return true;
    }
    
    /// <summary>
    /// 目前 base（slot 0）正在播的 ActionTimeline id。連線時用來把**擁有者實際的動畫**
    /// 搬到別人畫面上的替身——走路／跑步／跳躍／情感動作都是不同的 id，用位移猜只能猜出
    /// 「跑或站」兩種。讀不到原生物件時回 0。
    /// </summary>
    public ushort CurrentActionTimeline
    {
        get
        {
            var chara = BattleCharaPtr;
            if (chara == null) return 0;
            return chara->Timeline.TimelineSequencer.GetSlotTimeline(0);
        }
    }

    public void ResetActionTimeline()
    {
        var bc = BattleCharaPtr;
        if (bc == null) return;
        bc->Timeline.BaseOverride = 0;
        bc->Timeline.ModelState = 0;
        bc->Timeline.AnimationState[0] = 0;
        bc->Timeline.AnimationState[1] = 0;
        // Sequencer ops need a live skeleton (Parent); guard before touching it.
        if (bc->Timeline.TimelineSequencer.Parent == null) return;
        bc->Timeline.TimelineSequencer.SetSlotTimeline(0, 0);
        RecordNetworkEvent(new ResetTimelineEvent(default));
    }

    // Despawn-only: fully stop the action-timeline sequencer before the BattleChara is
    // deleted. DeleteObjectByIndex -> Character::Terminate walks all 14 sequencer slots and
    // calls TimelineGroup::PlayAction on each; a still-live slot (mid-cast / release
    // animation) crashes on freed scheduler state (C0000005 at TimelineGroup.PlayAction;
    // dumps 20260529_193455, 20260603_221355). ResetActionTimeline only clears slot 0 (Base),
    // which is insufficient for a casting boss whose release animation occupies the
    // UpperBody/Facial/Lips slots. Sequencer ops need a live skeleton, so guard on Parent first.
    public void QuiesceActionTimeline()
    {
        var bc = BattleCharaPtr;
        if (bc == null) return;
        bc->Timeline.BaseOverride = 0;
        bc->Timeline.ModelState = 0;
        bc->Timeline.AnimationState[0] = 0;
        bc->Timeline.AnimationState[1] = 0;
        if (bc->Timeline.TimelineSequencer.Parent == null) return;
        for (uint slot = 0; slot < 14; slot++)
            bc->Timeline.TimelineSequencer.SetSlotTimeline(slot, 0);
    }
}
