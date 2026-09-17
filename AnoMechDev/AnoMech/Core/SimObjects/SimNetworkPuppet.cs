using System;
using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Party;
using AnoMech.Core.Native;
using AnoMech.Multiplayer;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace AnoMech.Core.SimObjects;

// Same verified CharacterManager allocation as SimPartyNpc, but not its subtype:
// AI-only revival and path planning must not acquire a remote player's slot.
public sealed unsafe class SimNetworkPuppet : SimNpc, ISimPartyMember
{
    private NetworkPoseSmoother display;
    private Vector3 networkPosition;
    private float networkRotation;
    private bool hasNetworkPose;
    private bool runningVisual;
    private protected override Movement Movement => field ??= new PuppetMovement(this);
    public PartyRole Role { get; set; }
    public bool Dead { get; private set; }
    public byte ClassJob { get; }
    public string DisplayName { get; }
    public bool IsMoving { get; private set; }
    public bool IsActing { get; private set; }
    internal event Action<WorldEvent>? NetworkControl;
    // The owner left mid-run. From here the slot behaves like an AI stand-in: scheduled
    // AI moves drive the native model from the last verified pose, and Position/Rotation
    // read the model instead of a network sample that will never arrive again. The other
    // seven keep playing; before 2026-09-17 a single drop ended the run for the whole room.
    internal bool Orphaned { get; private set; }

    internal void Orphan()
    {
        if (Orphaned) return;
        if (hasNetworkPose)
        {
            display.Snap(networkPosition, networkRotation);
            base.SetPosition(networkPosition);
            base.SetRotation(networkRotation);
        }
        hasNetworkPose = false;
        IsMoving = false;
        IsActing = false;
        Orphaned = true;
    }

    internal SimNetworkPuppet(int index, Coordinates coordinates, PartyRole role, byte classJob, string name)
        : base(index, coordinates)
    {
        Role = role;
        ClassJob = classJob;
        DisplayName = name;
    }

    // Latest verified owner pose is the mechanic position and facing. No
    // extrapolation, catch-up speed, or AI can change that authoritative value:
    // Position/Rotation below report this sample the moment it lands, while only
    // the native model is allowed to walk toward it across frames.
    // Apply immediately even while Game.Paused (the network pump still runs).
    public void ApplyNetworkPose(MpPose pose, bool moving, bool acting)
    {
        var position = pose.Position.ToVector();
        networkPosition = position;
        networkRotation = pose.Rotation;
        hasNetworkPose = true;
        IsMoving = !Dead && moving;
        IsActing = !Dead && acting;
        // A snapped sample (first pose, teleport-sized jump) has no interpolation
        // to run, so it reaches the model here instead of on the next frame.
        if (display.Accept(position, pose.Rotation))
            PushDisplayPose();
    }

    // Per-frame display step, driven by the framework pump so remote characters
    // keep moving smoothly between owner samples — and while the local
    // game is paused, exactly like ApplyNetworkPose. Mechanics never see this pose.
    internal void UpdateVisualPose(float deltaSeconds)
    {
        if (!hasNetworkPose || Orphaned)
            return;
        if (display.Advance(deltaSeconds))
            PushDisplayPose();
        if (Dead)
            return;
        // IsMoving includes action use on the real client. Cosmetic locomotion
        // follows sampled displacement instead; teleports do not start a run clip
        // and a single stationary sample does not flick the clip to idle.
        var locomotion = display.Locomotion;
        if (runningVisual == locomotion)
            return;
        runningVisual = locomotion;
        PlayVisualTimeline(runningVisual ? (ushort)22 : (ushort)0, runningVisual ? (ushort)22 : (ushort)0);
    }

    private void PushDisplayPose()
    {
        base.SetPosition(display.Position);
        base.SetRotation(display.Rotation);
    }

    // Discontinuities (KO, revive, host teleport) must not leave the model
    // catching up to a pose the owner already left.
    private void SnapDisplayToOwner()
    {
        if (!hasNetworkPose)
            return;
        display.Snap(networkPosition, networkRotation);
        PushDisplayPose();
    }

    // The owner's own client is the authority for where this character is; the
    // native model may still be a display frame behind it. Before the first
    // network pose there is nothing to report but the spawned native pose.
    public override Vector3 Position => hasNetworkPose ? networkPosition : base.Position;
    public override float Rotation => hasNetworkPose ? networkRotation : base.Rotation;

    // Scripted teleports/facing are explicit Host effects, not delayed pose
    // echoes. The receiver applies them only to the addressed local owner.
    public override void SetPosition(Vector3 position)
    {
        if (Orphaned) { base.SetPosition(position); return; }
        networkRotation = Rotation;
        networkPosition = position;
        hasNetworkPose = true;
        SnapDisplayToOwner();
        NetworkControl?.Invoke(new TeleportEvent(Role, MpVector.From(position)));
    }

    public override void SetRotation(float rotation)
    {
        if (Orphaned) { base.SetRotation(rotation); return; }
        networkPosition = Position;
        networkRotation = rotation;
        hasNetworkPose = true;
        SnapDisplayToOwner();
        NetworkControl?.Invoke(new FaceEvent(Role, rotation));
    }

    public void Knockback(Vector3 source, float distance, float speed)
    {
        if (Dead)
            return;
        PlayVisualTimeline(156, 0);
        NetworkControl?.Invoke(new KnockbackEvent(Role, MpVector.From(source), distance, speed));
    }

    public void OnKilled()
    {
        if (Dead)
            return;
        Dead = true;
        IsMoving = IsActing = runningVisual = false;
        StopMoving();
        SnapDisplayToOwner();
        var native = BattleCharaPtr;
        if (native == null)
            return;
        native->Health = native->Mana = 0;
        native->Mode = CharacterModes.Dead;
        this.PlayKoActionTimeline();
    }

    public void RestoreNetworkAlive()
    {
        if (!Dead)
            return;
        Dead = false;
        SnapDisplayToOwner();
        var native = BattleCharaPtr;
        if (native == null)
            return;
        native->Health = native->MaxHealth;
        native->Mana = native->MaxMana;
        native->Mode = CharacterModes.Normal;
        ResetActionTimeline();
        PlayActionTimeline(77);
    }

    // Cosmetic locomotion must not produce another replicated action cue.
    // This is the same validated Timeline API used by SimCharacter, no hook.
    private void PlayVisualTimeline(ushort timeline, ushort baseOverride)
    {
        var native = BattleCharaPtr;
        if (native == null || native->Timeline.TimelineSequencer.Parent == null)
            return;
        native->Timeline.BaseOverride = baseOverride;
        native->Timeline.PlayActionTimeline(timeline, 0);
    }

    private sealed class PuppetMovement(SimNetworkPuppet parent) : Movement(parent)
    {
        // This intentional control gate mirrors PlayerMovement. Real forced
        // movement is delivered as a reliable Host event to the owning client.
        // Once orphaned the gate opens and the slot walks like an AI stand-in.
        public override void MoveTo(Vector3 target, float speed = 6f, float? finalRotation = null,
            ushort timeline = RunTimelineId, bool baseOverride = true)
        {
            if (parent.Orphaned) base.MoveTo(target, speed, finalRotation, timeline, baseOverride);
        }

        public override void MoveRecorded(Vector3 target, float duration, float? finalRotation = null)
        {
            if (parent.Orphaned) base.MoveRecorded(target, duration, finalRotation);
        }
    }
}
