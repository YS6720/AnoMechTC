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

    // Three samples cover a skipped arrival when packet and display clocks drift.
    // Longer gaps still hold rather than extrapolate.
    public const float InterpolationWindowSeconds = 3f * SnapshotIntervalSeconds;

    // A 2 m jump in one character sample is not ordinary locomotion. Snap instead
    // of drawing a streak across the arena.
    public const float TeleportDistanceSquared = 4f;

    // 1 cm of travel per sample: below this the owner is standing still.
    public const float LocomotionDistanceSquared = 0.0001f;

    // Animation grace follows display time, not packet frequency. At 30 FPS a
    // 25 ms hold would expire every frame despite continuously arriving poses.
    // Preserve the existing 50 ms grace while position interpolation runs faster.
    public const float LocomotionHoldSeconds = 0.05f;

    private Vector3 target;
    private float targetRotation;
    private float remainingSeconds;
    private float locomotionHold;
    private bool started;

    // Pose to show. Only meaningful once a sample has been accepted.
    public Vector3 Position { get; private set; }
    public float Rotation { get; private set; }

    // True while the character should be playing its run clip.
    public bool Locomotion => locomotionHold > 0f;

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
        if (travelSquared > LocomotionDistanceSquared)
            locomotionHold = LocomotionHoldSeconds;
        return false;
    }

    /// <summary>Drops any pending interpolation and puts the display on this pose.</summary>
    public void Snap(Vector3 position, float rotation)
    {
        started = true;
        target = Position = position;
        targetRotation = Rotation = MathUtil.NormalizeRotation(rotation);
        remainingSeconds = 0f;
        locomotionHold = 0f;
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

        if (locomotionHold > 0f)
            locomotionHold = MathF.Max(0f, locomotionHold - deltaSeconds);

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
