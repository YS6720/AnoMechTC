using AnoMech.Core.Game;
using AnoMech.Multiplayer;
using AnoMech.Pointers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Numerics;

namespace AnoMech.Core.SimObjects;

// Drives a single simulated cast on a SimCharacter's BattleChara. Writing
// CastInfo and ticking CurrentCastTime ourselves (instead of calling
// Character::StartCast) is what lets the simulator replay arbitrary boss
// abilities; on completion SimCast fires a synthetic ActionEffectHandler.Receive
// with a server-shaped header so the release animation/VFX play. It owns the
// post-release animation lock that roots the caster.
//
// One SimCast per caster, constructed once and reused. Start() begins a cast (or
// fires instantly when the cast time resolves to <= 0). The
// owning SimEnemy reads IsCasting/Progress/ActionId for the cast-bar HUD and
// IsBusy to decide when to root a following boss. All target coordinates handled
// here are world-space — the SimEnemy adapter converts from scenario-local.
public sealed unsafe class SimCast : ISimObject
{
    private readonly SimCharacter parent;
    private readonly Coordinates coordinates;

    private bool casting;
    private bool recordedCasting;
    private float elapsed;
    private float total;
    private float fireDelay;
    private float fireDelayElapsed;
    private Vector3? targetLocation;   // scenario-local coords
    private GameObjectId? targetId;
    private byte animationVariation;

    private float animationLock;
    private float remainingAnimationLock;

    private static SimCharacter? localCastBarOwner;
    private static SimCast? localCastBarClock;
    private static uint localCastBarAction;
    private static uint localCastBarAddon;

    public bool IsCasting => parent.BattleCharaPtr != null && parent.BattleCharaPtr->CastInfo.IsCasting;
    internal bool IsManagedCasting => casting;

    public uint ActionId { get; private set; }
    public float Progress => total <= 0f ? 0f : Math.Clamp(elapsed / total, 0f, 1f);

    // True while the cast bar is up or the release animation is still playing. A
    // following boss roots itself while busy so the action animation finishes in
    // place instead of sliding.
    public bool IsBusy => IsCasting || remainingAnimationLock > 0f;

    // SimCast is a persistent subsystem of its caster: the owning SimEnemy holds it
    // as a direct field and ticks/despawns it explicitly, never reaping it by
    // liveness. Always active while it exists.
    public bool IsActive => true;

    internal SimCast(SimCharacter parent, Coordinates coordinates)
    {
        this.parent = parent;
        this.coordinates = coordinates;
    }

    // Begins a cast of `actionId`. When castSeconds resolves to <= 0 (passed
    // explicitly or read as Cast100ms=0 from the sheet) the action fires
    // immediately with no cast bar. localTargetLocation (scenario-local, converted
    // to world only where native fields demand it) drives the AOE landing point and
    // the pre-fire facing snap; targetId, if set, makes the packet carry NumTargets=1
    // (some actions only animate on the caster when an entity target is delivered).
    // omenRotate is an offset added to the caster's facing (0 = aligned with parent.Rotation).
    // localOmenOrigin overrides only the cast telegraph; release still aims at localTargetLocation.
    public bool Start(uint actionId, Vector3? localTargetLocation, float? castTime, GameObjectId? targetId, float omenDelay, float omenRotate, byte animationVariation, float animationLock, float? fireDelay = null, Vector3? localOmenOrigin = null)
    {
        var chara = parent.BattleCharaPtr;
        if (chara == null) return false;


        if (castTime == null)
        {
            var actionSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();

            if (!actionSheet.TryGetRow(actionId, out var action))
            {
                Plugin.Log.Warning($"[SimCast.Start] Action Row {actionId} not found");
                return false;
            }

            castTime = action.Cast100ms / 10f;
        }

        recordedCasting = false;
        this.animationLock = animationLock;
        var castTimeValue = castTime.Value;


        // Instant actions (castTimeValue <= 0): retail sends only the ActionEffect, never a
        // StartCasting packet (verified against the Dancing Mad replay — 0 cast packets for the
        // auto-attack 0xC252). Dispatching a cast-begin (HandleActorCastPacket) on the same frame
        // as the release clobbers the action's body animation — invisible on VFX/cast abilities,
        // but it's the whole show for a VFX-less auto-attack, so the boss never swings. Skip the
        // cast packet entirely for instants and fire the effect directly below.
        if (castTimeValue > 0)
        {
            var target = targetId ?? chara->GetGameObjectId();
            NativeCast(actionId, ActionType.Action, omenDelay, castTimeValue, false, parent.Rotation + omenRotate, localOmenOrigin ?? localTargetLocation, target);
            total = chara->CastInfo.TotalCastTime;
            if (parent is SimPlayer) localCastBarClock = this;
        }
        else
        {
            total = 0f;
        }

        elapsed = 0f;

        casting = true;
        targetLocation = localTargetLocation;
        this.targetId = targetId;
        this.animationVariation = animationVariation;
        ActionId = actionId;
        this.fireDelay = fireDelay ?? 0;
        
        if (castTimeValue <= 0)
        {
            FaceTarget(chara);
            FireActionEffect(chara, actionId, ActionType.Action, animationLock, targetLocation, targetId, animationVariation);
            ResetCastState();
        }
        
        return true;
    }

    public void NativeCast(uint actionId, ActionType actionType, float omenDelay, float castTime, bool interruptible, float? rotation = null, Vector3? position = null, GameObjectId? targetId = null, GameObjectId? ballistaId = null)
    {
        NativeCast(parent, coordinates, actionId, actionType, omenDelay, castTime, interruptible,
            rotation, position, targetId, ballistaId);
    }

    internal static void NativeCast(SimCharacter parent, Coordinates coordinates, uint actionId,
        ActionType actionType, float omenDelay, float castTime, bool interruptible,
        float? rotation = null, Vector3? position = null, GameObjectId? targetId = null,
        GameObjectId? ballistaId = null)
    {
        var omenDelayByte = (byte)(omenDelay * 10);

        var rot = rotation ?? parent.Rotation;
        var qRotation = MathUtil.QuantizeRotation(rot);

        var animationTargetId = targetId == null ? 0xE0000000 : targetId.Value.ObjectId;
        var ballistaTargetId = ballistaId == null ? 0xE0000000 : ballistaId.Value.ObjectId;

        var localPos = position ?? parent.Position;
        var globalPos = coordinates.ToGlobal(localPos);

        var qPosX = MathUtil.QuantizePosition(globalPos.X);
        var qPosY = MathUtil.QuantizePosition(globalPos.Y);
        var qPosZ = MathUtil.QuantizePosition(globalPos.Z);

        var actorCastPacket = new ActorCastPacket
        {
            ActionId = (ushort)actionId,
            ActionType = (byte)actionType,
            OmenDelay = omenDelayByte,
            ActionId_2 = actionId,
            CastTime = castTime,
            TargetEntityId = animationTargetId,
            RotationInt = qRotation,
            Interruptible = interruptible,
            BallistaEntityId = ballistaTargetId,
            PositionX = qPosX,
            PositionY = qPosY,
            PositionZ = qPosZ,
        };

        if (parent.NetworkEventSink is not null)
        {
            if (ballistaId is not null)
                parent.RecordNetworkUnsupported("cast ballista target");
            parent.RecordNetworkCast(actionId, (byte)actionType, castTime, omenDelay,
                interruptible, rot, localPos, targetId);
        }
        PacketDispatcherPointers.HandleActorCastPacket(parent.GameObjectId.ObjectId, &actorCastPacket);
        // The client's ActorCast receiver does not initialize its own player.
        // Simulated local casts have no ActionManager/server start to do that,
        // so own their native cast clock just as the scenario owns completion.
        if (parent is SimPlayer && parent.BattleCharaPtr is var chara && chara != null)
        {
            ref var cast = ref chara->CastInfo;
            cast.ActionType = actionType;
            cast.ActionId = actionId;
            cast.SourceSequence = 0;
            cast.TargetId = targetId ?? new GameObjectId { ObjectId = 0xE0000000 };
            cast.TargetLocation = globalPos;
            cast.Rotation = rot;
            cast.CurrentCastTime = 0f;
            cast.BaseCastTime = castTime;
            cast.TotalCastTime = castTime;
            cast.Interruptible = interruptible;
            cast.IsCasting = true;
            if (castTime > 0f)
            {
                // TC OpenCastBar publishes the normal HUD only; verified native
                // body does not write ActionManager's cast state or send actions.
                ActionManager.Instance()->OpenCastBar(chara, actionType, actionId, actionId, 0, 0f, castTime);
                localCastBarOwner = parent;
                localCastBarClock = null; // replicated casts use their native clock
                localCastBarAction = actionId;
                var hud = AgentHUD.Instance();
                localCastBarAddon = hud == null ? 0 : hud->CastBarAddonId;
            }
        }
    }

    // Recorded releases have their own observed timestamp; do not let Tick also
    // synthesize a second release when the cast bar reaches its nominal end.
    public void BeginRecordedCast(uint actionId, ActionType actionType, float castTime,
        float omenDelay, float? rotation, Vector3? position, GameObjectId? targetId)
    {
        if (parent.BattleCharaPtr == null) return;
        ResetCastState();
        NativeCast(actionId, actionType, omenDelay, castTime, false, rotation, position, targetId);
        recordedCasting = true;
        total = castTime;
        elapsed = 0f;
        ActionId = actionId;
    }

    public void NativeActionEffect(uint actionId, float animationLock, ushort spellId, byte animationVariaton, ActionType actionType, byte flags, float? rotation = null, Vector3? position = null, GameObjectId? animationTargetId = null, GameObjectId? actionTargetId = null, GameObjectId? ballistaId = null)
    {
        const uint NullObjectId = 0xE0000000;

        var chara = parent.BattleCharaPtr;

        if (chara == null)
        {
            return;
        }

        var nullActionTarget = actionTargetId == null;

        var animationTarget = animationTargetId == null ? new GameObjectId { ObjectId = NullObjectId, Type = 0 } : animationTargetId!.Value;
        var actionTarget = nullActionTarget ? new GameObjectId { ObjectId = NullObjectId, Type = 0 } : actionTargetId!.Value;
        var ballistaTarget = ballistaId == null ? NullObjectId : ballistaId.Value.ObjectId;

        var rot = rotation ?? parent.Rotation;
        var qRotation = MathUtil.QuantizeRotation(rot);

        var localPos = position ?? Vector3.Zero;
        var globalPos = coordinates.ToGlobal(localPos);

        var header = new ActionEffectHandler.Header
        {
            AnimationTargetId = animationTarget,
            ActionId = actionId,
            GlobalSequence = 0,
            AnimationLock = animationLock,
            BallistaEntityId = ballistaTarget,
            SourceSequence = 0,
            RotationInt = qRotation,
            SpellId = spellId,
            AnimationVariation = animationVariaton,
            // 台服 CS 的 ActionEffectHandler.Header.ActionType 已是 ActionType enum
            // （ActorCastPacket 那邊仍是 byte，故上面 147 行保留轉型）
            ActionType = actionType,
            Flags = flags,
            NumTargets = (byte)(nullActionTarget ? 0 : 1)
        };

        if (parent.NetworkEventSink is not null)
        {
            if (ballistaId is not null)
                parent.RecordNetworkUnsupported("action-effect ballista target");
            parent.RecordNetworkActionEffect(actionId, animationLock, spellId,
                animationVariaton, (byte)actionType, flags,
                nullActionTarget ? [] : [actionTargetId!.Value], rot, localPos,
                animationTargetId);
        }

        var targetEffects = new ActionEffectHandler.TargetEffects();

        ActionEffectHandler.Receive(
            parent.GameObjectId.ObjectId,
            (Character*)chara,
            &globalPos,
            &header,
            &targetEffects,
            &actionTarget
            );
        CompleteLocalCast(parent, chara, actionId, actionType);

        remainingAnimationLock = animationLock;
    }

    // Separate recorded-packet overload: legacy single-target scenarios retain
    // their established behavior. Effects remain zeroed: replay visuals only,
    // never reapply the recording's real damage/status payloads to the client.
    public void NativeActionEffect(uint actionId, float animationLock, ushort spellId,
        byte animationVariation, ActionType actionType, byte flags,
        ReadOnlySpan<GameObjectId> actionTargets, float? rotation, Vector3? position,
        GameObjectId? animationTargetId, GameObjectId? ballistaId)
    {
        NativeActionEffect(parent, coordinates, actionId, animationLock, spellId,
            animationVariation, actionType, flags, actionTargets, rotation, position,
            animationTargetId, ballistaId);
        remainingAnimationLock = animationLock;
        if (recordedCasting && ActionId == actionId) ResetCastState();
    }

    internal static void NativeActionEffect(SimCharacter parent, Coordinates coordinates,
        uint actionId, float animationLock, ushort spellId, byte animationVariation,
        ActionType actionType, byte flags, ReadOnlySpan<GameObjectId> actionTargets,
        float? rotation, Vector3? position, GameObjectId? animationTargetId,
        GameObjectId? ballistaId)
    {
        if (actionTargets.Length > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(actionTargets));
        var chara = parent.BattleCharaPtr;
        if (chara == null) return;
        var manager = CharacterManager.Instance();
        Span<GameObjectId> targets = stackalloc GameObjectId[Math.Max(actionTargets.Length, 1)];
        var count = 0;
        foreach (var target in actionTargets)
            if (manager != null && manager->LookupBattleCharaByEntityId(target.ObjectId) != null)
                targets[count++] = target;
        if (count == 0) targets[0] = new GameObjectId { ObjectId = 0xE0000000 };

        var globalPos = position is { } p ? coordinates.ToGlobal(p) : Vector3.Zero;
        var header = new ActionEffectHandler.Header
        {
            AnimationTargetId = animationTargetId ?? new GameObjectId { ObjectId = 0xE0000000 },
            ActionId = actionId,
            // Source/global sequence numbers belong to the recorded connection,
            // not this client session; retain them in evidence, not dispatch.
            GlobalSequence = 0,
            AnimationLock = animationLock,
            BallistaEntityId = ballistaId?.ObjectId ?? 0xE0000000,
            SourceSequence = 0,
            RotationInt = MathUtil.QuantizeRotation(rotation ?? parent.Rotation),
            SpellId = spellId,
            AnimationVariation = animationVariation,
            ActionType = actionType,
            Flags = flags,
            NumTargets = (byte)count
        };
        if (parent.NetworkEventSink is not null)
        {
            if (ballistaId is not null)
                parent.RecordNetworkUnsupported("recorded action-effect ballista target");
            parent.RecordNetworkActionEffect(actionId, animationLock, spellId,
                animationVariation, (byte)actionType, flags, targets[..count].ToArray(),
                rotation ?? parent.Rotation, position, animationTargetId);
        }
        Span<ActionEffectHandler.TargetEffects> effects =
            stackalloc ActionEffectHandler.TargetEffects[Math.Max(count, 1)];
        effects.Clear();
        fixed (GameObjectId* targetPtr = targets)
        fixed (ActionEffectHandler.TargetEffects* effectPtr = effects)
            ActionEffectHandler.Receive(parent.GameObjectId.ObjectId, (Character*)chara,
                &globalPos, &header, effectPtr, targetPtr);
        CompleteLocalCast(parent, chara, actionId, actionType);
    }

    // Mark/project after ActionManager.Update, then re-project at the actual HUD
    // consumer boundary so native HUD writers cannot replace the practice clock.
    internal static void UpdateLocalCastBar()
    {
        if (localCastBarOwner == null) return;
        var stage = AtkStage.Instance();
        if (stage != null) ProjectLocalCastBar(stage->GetNumberArrayData(NumberArrayType.CastBar));
    }

    internal static void OnLocalCastBarUpdate(AddonEvent type, AddonArgs args)
    {
        if (localCastBarOwner == null || args is not AddonRequestedUpdateArgs request ||
            request.NumberArrayData == 0 || request.Addon == 0 ||
            ((AtkUnitBase*)request.Addon.Address)->Id != localCastBarAddon) return;
        ProjectLocalCastBar(((NumberArrayData**)request.NumberArrayData)[(int)NumberArrayType.CastBar]);
    }

    private static void ProjectLocalCastBar(NumberArrayData* numbers)
    {
        if (localCastBarOwner is not { } owner) return;
        var chara = owner.BattleCharaPtr;
        var clock = localCastBarClock;
        if (chara == null || (clock != null
                ? !clock.casting || clock.ActionId != localCastBarAction
                : !chara->CastInfo.IsCasting || chara->CastInfo.ActionId != localCastBarAction))
        {
            CloseLocalCastBar(owner);
            return;
        }
        if (numbers == null || numbers->Size < 6) return;
        var duration = clock?.total ?? chara->CastInfo.TotalCastTime;
        var time = Math.Clamp(clock?.elapsed ?? chara->CastInfo.CurrentCastTime, 0f, duration);
        // TC OpenCastBar uses centiseconds at 2/3 and integer percent at 4.
        numbers->SetValue(2, (int)(time * 100f), force: true);
        numbers->SetValue(3, (int)(duration * 100f), force: true);
        numbers->SetValue(4, duration > 0f ? (int)(time / duration * 100f) : 0, force: true);
        // TC _CastBar skips its countdown refresh when array[5] is interrupted.
        // An owned cast ends only through release/Despawn, not native expiry.
        if (clock != null) numbers->SetValue(5, 0, force: true);
    }

    internal static void CloseLocalCastBar(SimCharacter owner)
    {
        if (!ReferenceEquals(localCastBarOwner, owner)) return;
        var hud = AgentHUD.Instance();
        var stage = AtkStage.Instance();
        if (hud != null && stage != null && localCastBarAddon != 0 &&
            hud->CastBarAddonId == localCastBarAddon && stage->RaptureAtkUnitManager != null)
        {
            var addon = stage->RaptureAtkUnitManager->GetAddonById((ushort)localCastBarAddon);
            if (addon != null) addon->Close(true);
            hud->CastBarAddonId = 0;
        }
        localCastBarOwner = null;
        localCastBarClock = null;
        localCastBarAddon = 0;
        localCastBarAction = 0;
    }

    private static void CompleteLocalCast(SimCharacter parent, BattleChara* chara,
        uint actionId, ActionType actionType)
    {
        // Pair local synthetic start/finish even when the native receiver leaves
        // CastInfo untouched; an unrelated effect must not cancel this cast.
        if (parent is SimPlayer && chara->CastInfo.ActionId == actionId &&
            chara->CastInfo.ActionType == actionType)
        {
            CloseLocalCastBar(parent);
            chara->CastInfo.IsCasting = false;
            chara->CastInfo.ActionId = 0;
            chara->CastInfo.ActionType = 0;
        }
    }

    public void Tick(float deltaSeconds) => Advance(deltaSeconds, null);

    internal void TickOwned(float deltaSeconds, float castElapsed) => Advance(deltaSeconds, castElapsed);

    private void Advance(float deltaSeconds, float? castElapsed)
    {
        if (remainingAnimationLock > 0f)
        {
            remainingAnimationLock = MathF.Max(0f, remainingAnimationLock - deltaSeconds);
        }
        if (recordedCasting)
        {
            var recordedChara = parent.BattleCharaPtr;
            if (recordedChara == null) ResetCastState();
            else elapsed = recordedChara->CastInfo.CurrentCastTime;
            return;
        }

        if (!casting)
        {
            return;
        }

        var chara = parent.BattleCharaPtr;
        if (chara == null)
        {
            casting = false;
            return;
        }

        elapsed = castElapsed ?? chara->CastInfo.CurrentCastTime;
        if (castElapsed != null)
            chara->CastInfo.CurrentCastTime = MathF.Min(elapsed, total);

        if (elapsed >= total)
        {
            var fire = true;

            if (fireDelay > 0)
            {
                fireDelayElapsed += deltaSeconds;

                if (fireDelayElapsed < fireDelay)
                {
                    fire = false;
                }
            }

            if (fire)
            {
                FaceTarget(chara);
                FireActionEffect(chara, ActionId, ActionType.Action, animationLock, targetLocation, targetId, animationVariation);
                ResetCastState();
            }
        }
    }

    // Teardown for caster despawn: drop the telegraph, stop any pending delayed
    // spawn, and clear CastInfo. Clearing CastInfo matters because despawn deletes
    // the BattleChara via DeleteObjectByIndex -> Character::Terminate, whose
    // scheduler teardown reads a still-live cast/action timeline and crashes on
    // freed state (C0000005 at TimelineGroup.PlayAction; see crash dump
    // 20260529_193455).
    public void Despawn()
    {
        CloseLocalCastBar(parent);
        var chara = parent.BattleCharaPtr;
        if (chara != null)
        {
            chara->CastInfo.IsCasting = false;
            chara->CastInfo.ActionId = 0;
            chara->CastInfo.ActionType = 0;
        }
        casting = false;
        recordedCasting = false;
    }

    private void ResetCastState()
    {
        if (ReferenceEquals(localCastBarClock, this)) CloseLocalCastBar(parent);
        casting = false;
        recordedCasting = false;
        targetLocation = null;
        targetId = null;
        animationVariation = 0;
        ActionId = 0;
        fireDelay = 0;
        fireDelayElapsed = 0;
    }

    // targetLocation is stored scenario-local; lift to world only for native
    // ActionEffect delivery (Receive / CastInfo expect world coords).
    private Vector3? WorldTargetLocation => targetLocation is { } loc ? coordinates.ToGlobal(loc) : null;

    // Targeted casts (ground location now; entity targets later) snap to face the target
    // on the final tick so the release animation plays in the intended direction even if
    // the target moved during the cast. FireActionEffect snapshots Rotation into the
    // packet header, so this must run first.
    private void FaceTarget(BattleChara* chara)
    {
        if (WorldTargetLocation is not { } loc) return;
        var dx = loc.X - chara->Position.X;
        var dz = loc.Z - chara->Position.Z;
        if (dx * dx + dz * dz < 1e-6f) return;
        chara->Rotation = MathUtil.NormalizeRotation(MathF.Atan2(dx, dz));
    }

    // Mimics the server's ActionEffect packet so the game plays the action's release
    // animation/VFX on the caster. When deliverTo is set, the packet carries
    // NumTargets=1 with that GameObjectId and a zeroed no-op effect block; some
    // actions only animate on the caster if the engine sees at least one target to
    // deliver to. When deliverTo is null, NumTargets=0 (used for self-targeted
    // casts and cast releases without an entity target) — the release animation
    // still plays.
    private void FireActionEffect(BattleChara* chara, uint actionId, ActionType actionType, float animationLock, Vector3? localTargetLocation = null, GameObjectId? deliverTo = null, byte animationVariation = 0)
    {
        if (deliverTo is { } id)
        {
            var characterManager = CharacterManager.Instance();
            var deliverToId = id.ObjectId;

            if (characterManager == null || characterManager->LookupBattleCharaByEntityId(deliverToId) == null)
            {
                Plugin.Log.Warning(
                    $"FireActionEffect: target {deliverToId:X} for action {actionId:X} on caster {chara->EntityId:X} not in CharacterManager._battleCharas; dropping deliverTo to avoid ApplyAll null-deref");
                deliverTo = null;
            }
        }

        var pos = localTargetLocation ?? parent.Position;
        NativeActionEffect(actionId, animationLock, (ushort)actionId, animationVariation, actionType, 0, chara->Rotation, pos, deliverTo, deliverTo);
    }
}
