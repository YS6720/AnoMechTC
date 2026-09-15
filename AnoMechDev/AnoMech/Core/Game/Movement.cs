using System;
using System.Numerics;
using AnoMech.Core.SimObjects;

namespace AnoMech.Core.Game;

internal class Movement(SimCharacter parent)
{
    protected const ushort RunTimelineId = 22;
    private const ushort KnockbackTimelineId = 156;

    // After a follower reaches its target it waits this long before chasing again,
    // so it doesn't twitch back into motion the instant the target drifts.
    private const float FollowArrivalCooldown = 0.5f;

    private Vector3? destination;
    private float speed;
    private float? finalRotation;
    private bool faceTravel = true;
    private bool avoid = true;
    private ushort timelineId;
    private bool timelineBaseOverride;
    private bool animActive;

    private SimCharacter? followTarget;
    private float followCooldown;

    private SimTether? interceptTether;
    private float interceptMargin = 3f;   // park this many yards short of either tether endpoint
    private bool internalReissue;

    // ── 錄影軌跡回放（opt-in，只有 RecordedScenario 的 npcmove 走這條）──────────
    // 一般 locomotion 是「用速度走向目標」：Tick 只在 XZ 推進、Y 保留 cur.Y，
    // 抵達那一刻才一次套上 dest.Y。錄影回放的事實是**取樣時刻**，不是速度——
    // 於是純垂直段（XZ 速度 0）會在第一個 tick 就被當成「已到達」跳到終點高度，
    // 斜向段的高度則整段憋到抵達才跳（BLIND-ASTRA-07）。位置計算整段委託給
    // TimedMotion（唯一來源，離線可驗）。
    private TimedMotion? recorded;

    public bool IsMoving => destination != null || recorded != null;

    public virtual void MoveTo(Vector3 t, float sp = 6f, float? finalRot = null, ushort tl = RunTimelineId, bool baseOverride = true)
        => InternalMoveTo(t, sp, finalRot, tl, baseOverride);

    /// <summary>
    /// 錄影軌跡回放：在 <paramref name="duration"/> 秒內按錄影時間把 XYZ 一起內插到
    /// <paramref name="target"/>，準時抵達。**不繞障礙**——這是重播已經發生過的事，
    /// 不是決策。位置只跟錄影時間有關，動畫鎖定只會抑制跑步動畫、不會讓位置偏離
    /// 已觀測軌跡；轉向、動畫與停止的生命週期沿用既有 locomotion 那一套。
    /// 一般 <see cref="MoveTo"/>／玩家／peer puppet 的語意完全不受影響。
    /// </summary>
    public virtual void MoveRecorded(Vector3 target, float duration, float? finalRot = null)
    {
        if (!parent.IsAlive()) return;   // dead characters don't move
        followTarget = null;
        followCooldown = 0f;
        interceptTether = null;
        destination = null;              // 與一般 locomotion 互斥，後下的指令贏
        finalRotation = null;
        recorded = new TimedMotion(parent.Position, target, duration, finalRot);
        timelineId = RunTimelineId;
        timelineBaseOverride = true;
        // 只有真的在水平移動才播跑步循環：純高度變化不是「跑」，而引擎的 locomotion
        // 又是從移動狀態推 base slot 的（見 StartAnim）。
        if (!animActive && !parent.AnimationLock && recorded.HasHorizontalTravel) StartAnim();
    }

    public void Follow(SimCharacter? target, float speed = 6f)
    {
        if (!target.IsAlive())
        {
            followTarget = null;
            Stop();
            return;
        }
        followTarget = target;
        followCooldown = 0f;
        interceptTether = null;
        recorded = null;                 // 後下的追蹤指令取代錄影回放
        this.speed = MathF.Max(0f, speed);
    }

    // Walk to the nearest point on the tether line and keep tracking it: TickIntercept
    // re-projects every frame so a tether whose endpoints drift is still met. `margin`
    // is how many yards short of either endpoint to park.
    public void Intercept(SimTether? tether, float margin = 3f)
    {
        followTarget = null;
        recorded = null;
        interceptTether = tether;
        interceptMargin = margin;
        RetargetIntercept();
    }

    // Re-project the parent onto the tether segment and re-issue the move. Self-cancels
    // (clears tracking, issues no move) if the tether is gone or an endpoint died, so any
    // in-flight move just finishes.
    private void RetargetIntercept()
    {
        var margin = interceptMargin;
        if (interceptTether is not { A: { } a, B: { } b } || !a.IsAlive() || !b.IsAlive())
        {
            interceptTether = null;
            return;
        }
        var src = new Vector2(a.Position.X, a.Position.Z);
        var seg = new Vector2(b.Position.X, b.Position.Z) - src;
        var len = seg.Length();
        if (len < 1e-6f) { ReissueInterceptMove(new Vector3(src.X, parent.Position.Y, src.Y)); return; }
        var rel = new Vector2(parent.Position.X, parent.Position.Z) - src;
        // Park `margin` yards short of either endpoint instead of standing right on it.
        // On a segment under 2*margin long the two insets cross, so settle on the midpoint.
        var inset = margin / len;
        var (tMin, tMax) = 2f * inset >= 1f ? (0.5f, 0.5f) : (inset, 1f - inset);
        var t = Math.Clamp(Vector2.Dot(rel, seg) / (len * len), tMin, tMax);
        // Slide the parked point along the tether line to the nearest spot clear of
        // obstacles, so the bot lands on the grab corridor instead of being parked
        // perpendicular off it when a black hole sits on the line.
        var target = parent.Obstacles.NearestClearOnSegment(src, src + seg, t, tMin, tMax);
        ReissueInterceptMove(new Vector3(target.X, parent.Position.Y, target.Y));
    }

    private void ReissueInterceptMove(Vector3 target)
    {
        internalReissue = true;
        try { MoveTo(target); }
        finally { internalReissue = false; }
    }

    // Keep the intercept aimed at the moving tether each frame. Exception: once the
    // tether is attached to this character (it has become an endpoint) we stop
    // re-projecting — that would collapse the target onto ourselves and freeze us
    // mid-approach — and let the in-flight move finish on its own. Also stops once the
    // move completes or the tether goes inactive.
    private void TickIntercept()
    {
        if (interceptTether is not { } tether) return;
        if (!IsMoving || !tether.IsActive
            || ReferenceEquals(tether.A, parent) || ReferenceEquals(tether.B, parent))
        {
            interceptTether = null;
            return;
        }
        RetargetIntercept();
    }

    public void Knockback(Vector3 source, float distance, float kbSpeed)
    {
        var kbDestination = parent.Placement().Face(source).MoveForward(-distance).Position;
        // Knockback is forced movement: don't steer around or stop short of obstacles.
        InternalMoveTo(kbDestination, kbSpeed, tl: KnockbackTimelineId, baseOverride: false, faceTravel: false, avoid: false);

    }

    // Shared move entry for MoveTo (locomotion) and Knockback (one-shot action).
    // `baseOverride` selects the animation mechanism in StartAnim: true for a
    // looping locomotion clip (run/walk), false for a one-shot action timeline
    // (knockback). See StartAnim for why the two need different handling.
    private void InternalMoveTo(
        Vector3 moveDestination, float sp = 6f, float? finalRot = null, ushort tl = RunTimelineId, bool baseOverride = true,
        bool faceTravel = true, bool avoid = true, bool preserveTracking = false)
    {
        if (!parent.IsAlive()) return;   // dead characters don't move
        // A new dodge or knockback supersedes the previous chase. Internal tracking
        // updates keep their target and still use the virtual player movement gate.
        if (!internalReissue && !preserveTracking)
        {
            followTarget = null;
            followCooldown = 0f;
            interceptTether = null;
        }
        recorded = null;                 // 新的 locomotion／擊退取代錄影回放
        destination = moveDestination;
        speed = MathF.Max(0f, sp);
        finalRotation = finalRot;
        this.faceTravel = faceTravel;
        this.avoid = avoid;
        var sameAnim = animActive && timelineId == tl;
        timelineId = tl;
        timelineBaseOverride = baseOverride;
        if (!sameAnim) StartAnim();
    }

    public void Tick(float deltaSeconds)
    {
        // 錄影回放與一般 locomotion 互斥，不吃 follow／intercept／避障，而且
        // **依錄影時間推進**：動畫鎖定（讀條／釋放）只抑制跑步動畫，位置不能因此
        // 停表偏離已觀測軌跡。
        if (recorded is { } motion)
        {
            TickRecorded(motion, deltaSeconds);
            return;
        }
        if (parent.AnimationLock)
        {
            StopAnim();
            return;
        }

        TickFollow(deltaSeconds);
        TickIntercept();

        if (destination is not { } dest) return;

        // Re-assert the movement animation if a move is still pending but its
        // animation was stopped while we were animation-locked. Follow re-issues
        // via InternalMoveTo every tick (which restarts the anim itself), but a
        // one-shot MoveTo/Knockback won't, so re-assert here or it would slide the
        // rest of the way unanimated.
        if (!animActive) StartAnim();

        var cur = parent.Position;

        // Park-at-edge: if the destination lies inside an obstacle, retarget to the
        // nearest boundary point so the bot stops at the edge instead of orbiting an
        // unreachable center. Skipped for forced movement (knockback).
        var dest2 = new Vector2(dest.X, dest.Z);
        if (avoid) dest2 = parent.Obstacles.ClampOutside(dest2);

        var dx = dest2.X - cur.X;
        var dz = dest2.Y - cur.Z;
        var distSq = dx * dx + dz * dz;
        var step = speed * deltaSeconds;

        if (distSq <= step * step || step <= 0f)
        {
            var rot = finalRotation ?? (faceTravel && distSq > 1e-6f ? MathF.Atan2(dx, dz) : parent.Rotation);
            parent.SetPosition(new Placement(new Vector3(dest2.X, dest.Y, dest2.Y), rot));
            finalRotation = null;
            Stop();
        }
        else
        {
            var dist = MathF.Sqrt(distSq);
            var desired = new Vector2(dx / dist, dz / dist);
            // Steer around obstacles; a no-op (returns `desired`) when the field is
            // empty, nothing blocks, or this is forced movement.
            var heading = avoid ? parent.Obstacles.Steer(new Vector2(cur.X, cur.Z), desired, dist) : desired;
            var next = new Vector3(cur.X + heading.X * step, cur.Y, cur.Z + heading.Y * step);
            parent.SetPosition(new Placement(next, faceTravel ? MathF.Atan2(heading.X, heading.Y) : parent.Rotation));
        }
    }

    // Recorded playback: position is a pure function of recorded time, so TimedMotion
    // owns the computation and every tick advances X, Y and Z together. The clock keeps
    // running through an animation lock — the observed trajectory does not pause because
    // the actor started a cast — and the lock only suppresses the run clip.
    private void TickRecorded(TimedMotion motion, float deltaSeconds)
    {
        if (parent.AnimationLock) StopAnim();
        else if (!animActive && motion.HasHorizontalTravel) StartAnim();
        var sample = motion.Advance(deltaSeconds);
        parent.SetPosition(new Placement(sample.Position, sample.Rotation ?? parent.Rotation));
        if (!sample.Arrived) return;
        recorded = null;
        StopAnim();
    }

    private void TickFollow(float deltaSeconds)
    {
        if (followTarget == null) return;
        if (!followTarget.IsAlive())
        {
            followTarget = null;
            followCooldown = 0f;
            Stop();
            return;
        }

        // Arrived last frame: sit out the cooldown facing the target, don't chase yet.
        if (followCooldown > 0f)
        {
            followCooldown -= deltaSeconds;
            Face(followTarget.Position);
            return;
        }

        var stopDist = parent.HitboxRadius + followTarget.HitboxRadius;
        if (parent.Placement().DistanceSq(followTarget) <= stopDist * stopDist)
        {
            // Start the cooldown only on the frame we actually arrive (were moving).
            if (IsMoving) followCooldown = FollowArrivalCooldown;
            Stop();
            Face(followTarget.Position);
        }
        else
        {
            InternalMoveTo(followTarget.Position, speed, preserveTracking: true);
        }
    }

    public void Stop()
    {
        destination = null;
        interceptTether = null;
        recorded = null;
        StopAnim();
    }


    // Two distinct animation mechanisms, selected by timelineBaseOverride:
    //   * Locomotion (run/walk, 22): the engine recomputes the base slot from the
    //     character's movement state every frame, and a SetPosition-driven doppel
    //     reads as velocity 0 -> idle, which clobbers a plain PlayActionTimeline
    //     (the model just slides). BaseOverride is the one field the engine forces
    //     instead, so we pass timelineId there to keep the run loop applied.
    //   * One-shot action (knockback, 156): an action timeline isn't derived from
    //     movement state, so it plays through with baseOverride 0; ResetActionTimeline
    //     (StopAnim) returns it to idle on arrival. Passing it as BaseOverride would
    //     loop the pose forever (the original "knockback stuck" bug).
    protected void StartAnim()
    {
        parent.PlayActionTimeline(timelineId, baseOverride: timelineBaseOverride ? timelineId : (ushort)0);
        animActive = true;
    }

    protected void StopAnim()
    {
        if (!animActive) return;
        parent.ResetActionTimeline();
        animActive = false;
    }

    public void Face(Vector3? t)
    {
        if(t is not {} target) return;
        var dx = target.X - parent.Position.X;
        var dz = target.Z - parent.Position.Z;
        if (dx * dx + dz * dz < 1e-6f) return;
        parent.SetRotation(MathF.Atan2(dx, dz));
    }
}

internal sealed class PlayerMovement(SimCharacter parent) : Movement(parent)
{
    public override void MoveTo(Vector3 t, float sp = 6f, float? finalRot = null, ushort tl = RunTimelineId, bool baseOverride = true)
    {
        // NO-OP - player cannot be moved like this
    }

    public override void MoveRecorded(Vector3 target, float duration, float? finalRot = null)
    {
        // NO-OP - the player's own position is never replayed
    }
}
