using AnoMech.Compat.Server;
using AnoMech.Core.EnemyActions;
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
        confusedChaseHold = MathF.Max(0f, confusedChaseHold - deltaSeconds);
    }

    public virtual void Despawn()
    {
        LimitBreakRecovery = false;
        statusList.Despawn();
        vfx.Despawn();
        if (ActorControl.Confused) ActorControl.SetConfused(false);
        confusedChaseHold = 0f;
    }

    // Set by whoever seats or spawns the character in a world.
    internal SimEvents? SimEvents { get; set; }
    
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
    // 上游 1646a70 同名：正在攔截連線、目前要走去的位置（無則 null）。
    public bool IsIntercepting => Movement.IsIntercepting;
    public Vector3? MoveDestination => Movement.Destination;

    // An AI order (bots and enemies; the local player isn't walked by it); a mechanic that takes
    // control uses ForceFollow. A null or dead target releases it.
    public void Follow(SimCharacter? target = null, float speed = 6f) => Movement.Follow(target, speed);

    // ── 強制位移：上游 1646a70 SimCharacter 的 ForcedMove 系列，只搬概念、接我們的 Movement ──
    // 隊員走各自既有的擊退（玩家／bot／連線替身），其他角色走 Movement。KO 的角色不動。

    // Away from `source` by a Knockback sheet row.
    public void Knockback(Vector3 source, uint knockbackRow)
    {
        if (KnockbackLookup.Row(knockbackRow) is { } row) Slide(source, row.Distance, row.Speed);
    }

    // A scripted push away from `source`, which no Knockback row describes.
    public void Push(Vector3 source, float distance, float durationSeconds)
        => Slide(source, distance, distance / MathF.Max(durationSeconds, MinPushSeconds));

    private const float MinPushSeconds = 0.01f;

    private void Slide(Vector3 source, float distance, float speed)
    {
        if (!this.IsAlive()) return;
        if (this is ISimPartyMember member) member.Knockback(source, distance, speed);
        else Movement.Knockback(source, distance, speed);
    }

    // 上游的 carry 整段約 0.9 秒落定（Trajectory.Carry）；這段期間不派送一般移動，免得和原生 warp 搶位置。
    private const float CarrySettledSeconds = 0.9f;

    // Replay: ActorControl 0xF1 on the carried character (UMAD P1 arrows). Player and bot alike.
    public void CarryTo(Vector3 destination, ushort? timeline = null)
    {
        if (!this.IsAlive()) return;
        Movement.Hold(CarrySettledSeconds);
        ActorControl.CarryTo(Coordinates.ToGlobal(destination), Rotation, timeline);
    }

    // 混亂這類強制追隨：本機玩家要原生 forced follow（ServerNative.ForcedFollow），其他角色走 Movement.Follow。
    // 連線房主端無法把它複寫給隊友 → 記 unsupported（整輪失敗關閉）。A null or dead target releases it.
    public void ForceFollow(SimCharacter? target, float speed = 6f)
    {
        if (!this.IsAlive() && target is not null) return;
        if (NetworkEventSink is not null)
        {
            RecordNetworkUnsupported("forced follow");
            return;
        }
        var to = target.IsAlive() ? target : null;
        if (this is SimPlayer) ServerNative.ForcedFollow(to?.GameObjectId.ObjectId);
        else Movement.Follow(to, speed);
    }

    // 上游 1646a70 同名：A confused character (ActorControl.Confused) chases its nearest ally
    // (SimParty); this stands the chase still for a while, as a mechanic's own pause. A forced
    // move in flight (carry) holds it too.
    private float confusedChaseHold;
    public void HoldConfusedChase(float seconds) => confusedChaseHold = MathF.Max(confusedChaseHold, seconds);
    internal bool ConfusedChaseHeld => confusedChaseHold > 0f || Movement.IsHeld;

    // The server's ActorControl packets for this character, by meaning.
    public ActorControl ActorControl => field ??= new ActorControl(this);
    // 上游 1646a70 同名。
    public void VoiceLine(uint voiceLineId) => ActorControl.PlayVoiceLine(voiceLineId);


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
    
    // 頭標預設 fire-and-forget（D799 a）：上游 1646a70 的 AttachLockonVfx 只送 ActorControl.HeadMarker，
    // 不持有 VFX。B10a 國際服實測（UMAD 預設持有的頭標）崩潰 dump 堆疊＝SimVfx.Despawn → RtlFreeHeap c0000374。
    public void AttachLockonVfx(uint lockonId, float duration = 0f, bool persistent = false)
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
        => AddStatus(statusId, duration, stacks, overrideStacks, hidden: false, appliedBy: null);

    // 上游 1646a70：appliedBy＝套用它的招式（給 StatusAdded）；hidden＝新狀態先不進原生狀態列，等 SimStatus.Reveal
    // （已顯示的狀態被刷新時不理會）。連線狀態快照沒有「隱藏中」這個資訊，房主端記 unsupported（失敗關閉）。
    internal SimStatus? AddStatus(ushort statusId, float duration, int stacks, bool overrideStacks, bool hidden,
        (EnemyAction Action, SimCharacter Caster)? appliedBy)
    {
        if (FindStatus(statusId) is {} status)
        {
            var previous = status.Stacks;
            // overrideStacks: stacks is the absolute target; otherwise it's a
            // relative delta (negative consumes stacks).
            int delta = overrideStacks ? stacks - status.Stacks : stacks;
            status.Reapply(duration, delta);
            if (status.Stacks == 0)
            {
                status.Despawn();   // last stack consumed → remove the status
                return null;
            }
            // Consuming a stack or holding an override where it is applies nothing.
            if (overrideStacks ? status.Stacks > previous : stacks > 0) RaiseStatusAdded(status, previous, appliedBy);
            return status;
        }

        // No existing status: a non-positive request has nothing to remove.
        // A full managed/native status table refuses a new key without
        // disturbing any existing status.
        if (!CanCreateStatus(statusId, default)) return null;
        if (hidden && NetworkEventSink is not null)
            RecordNetworkUnsupported("hidden status");
        var s = new SimStatus(this, statusId, duration, (ushort)stacks, hidden: hidden);
        statusList.Add(s);
        if (stacks > 0) RaiseStatusAdded(s, 0, appliedBy);
        return s;
    }

    private void RaiseStatusAdded(SimStatus status, ushort previousStacks, (EnemyAction Action, SimCharacter Caster)? appliedBy = null)
        => SimEvents?.Raise(new StatusAdded(this, status.StatusId, status.Stacks, previousStacks, appliedBy?.Action, appliedBy?.Caster));

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
        RaiseStatusAdded(s, 0);
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

    /// <summary>不分來源，有沒有這個 id 的活躍狀態（含尚未顯示的 Hidden，同上游 AnyActiveStatusRow）。</summary>
    public bool HasActiveStatusAnySource(ushort statusId)
    {
        for (var i = 0; i < statusList.Count; i++)
        {
            var status = statusList[i];
            if (status.IsActive && status.StatusId == statusId) return true;
        }
        return false;
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
