using System;
using System.Numerics;
using AnoMech.Multiplayer;

namespace AnoMech.Core.SimObjects;

// Display-side pose smoothing for a remote party puppet.
//
// 維護者 poses arrive at MpLimits.PoseHz. Display frames and packet arrival do not
// share a clock, so writing each sample directly still exposes packet jitter.
// This struct interpolates only the display; mechanics read the latest raw sample.
//
// Deliberate properties:
//  - No extrapolation. The display never passes the newest sample, so a late or
//    missing packet holds the character still instead of guessing a future pose.
//  - Bounded lag. The display is at most InterpolationWindowSeconds behind the
//    authoritative pose (25 ms; ~0.15 m for a 6 m/s run), and exactly that in
//    steady motion — the display speed matches the owner's speed with a fixed offset.
//  - Snap, never chase, on discontinuities: first sample, teleport-sized jumps,
//    KO/revive and explicit host teleports all reset the display so nothing
//    rubber-bands across the arena.
internal struct NetworkPoseSmoother
{
    // Follow the character cadence, not the slower complete-world snapshots.
    public const float SnapshotIntervalSeconds = 1f / MpLimits.PoseHz;

    // 視窗＝能容忍的到達抖動。原本 3 個取樣（25 ms）：只要連續三個 pose 晚到，
    // 顯示就停住，玩家看到的是「別人偶爾頓一下」。經 Cloudflare Tunnel 的連線抖動
    // 本來就比區網大，2026-09-15 維護者回報偶發嚴重卡頓。
    //
    // 放寬到 6 個取樣（50 ms）：抖動容忍加倍，代價是顯示固定落後 50 ms（跑動 6 m/s
    // 約 0.3 m）。機制判定不受影響——判定讀最新的原始取樣，不是這裡的顯示值。
    // 再往上加沒有意義：落後超過一個 GCD 的位置對練機制反而有害。
    // 仍然不外插：晚到就停住，絕不猜未來位置（猜錯會橡皮筋，比頓一下更糟）。
    public const float InterpolationWindowSeconds = 6f * SnapshotIntervalSeconds;

    // A 2 m jump in one character sample is not ordinary locomotion. Snap instead
    // of drawing a streak across the arena.
    public const float TeleportDistanceSquared = 4f;

    // 動畫不再由位移猜（2026-09-18）：擁有者送出自己實際在播的 ActionTimeline，
    // 走路／跑步／跳躍／情感動作各自是不同的 id，位移只能猜出「跑或站」兩種。
    private Vector3 target;
    private float targetRotation;
    private float remainingSeconds;
    private bool started;

    // Pose to show. Only meaningful once a sample has been accepted.
    public Vector3 Position { get; private set; }
    public float Rotation { get; private set; }

    /// <summary>
    /// Takes a new authoritative sample. Returns true when the display was snapped
    /// onto it (first sample or teleport-sized jump) and must be pushed out now.
    /// </summary>
    public bool Accept(Vector3 position, float rotation)
    {
        var travelSquared = Vector3.DistanceSquared(target, position);
        if (!started || travelSquared >= TeleportDistanceSquared)
        {
            Snap(position, rotation);
            return true;
        }

        var normalizedRotation = MathUtil.NormalizeRotation(rotation);
        // Duplicate snapshots must not restart the arrival deadline.
        if (position == target && normalizedRotation == targetRotation)
            return false;

        target = position;
        targetRotation = normalizedRotation;
        remainingSeconds = InterpolationWindowSeconds;
        return false;
    }

    /// <summary>Drops any pending interpolation and puts the display on this pose.</summary>
    public void Snap(Vector3 position, float rotation)
    {
        started = true;
        target = Position = position;
        targetRotation = Rotation = MathUtil.NormalizeRotation(rotation);
        remainingSeconds = 0f;
    }

    /// <summary>
    /// Advances the display by one frame. Returns true when the display pose moved
    /// and has to be written to the native model.
    /// </summary>
    public bool Advance(float deltaSeconds)
    {
        // Also rejects NaN: nothing is done unless time actually passed.
        if (!started || !(deltaSeconds > 0f))
            return false;

        if (remainingSeconds <= 0f)
            return false;

        if (deltaSeconds >= remainingSeconds)
        {
            remainingSeconds = 0f;
            Position = target;
            Rotation = targetRotation;
            return true;
        }

        // Fraction of the *remaining* gap covered by this frame, so the display
        // lands exactly on the sample when the window elapses and never overshoots.
        var alpha = deltaSeconds / remainingSeconds;
        remainingSeconds -= deltaSeconds;
        Position += (target - Position) * alpha;
        // Shortest arc: the delta is wrapped into (-pi, pi] before it is scaled,
        // so a facing that crosses +/-pi turns the short way.
        Rotation = MathUtil.NormalizeRotation(
            Rotation + MathUtil.NormalizeRotation(targetRotation - Rotation) * alpha);
        return true;
    }
}
