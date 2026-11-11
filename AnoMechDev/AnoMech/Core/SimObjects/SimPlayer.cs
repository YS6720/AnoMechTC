using AnoMech.Compat.Server;
using System;
using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Party;
using AnoMech.Core.Native;
using AnoMech.Multiplayer;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace AnoMech.Core.SimObjects;

public sealed unsafe class SimPlayer(Coordinates coordinates) : SimCharacter(coordinates), ISimPartyMember
{
    private const ushort StunStatusId = 896;  // "Down for the Count" (896) — IsPermanent + LockControl variant.

    // The player's HP bar (real bc->Health) is touched only on a scenario KO — dropped to a 1-HP
    // sliver here (from OnKilled on a real death, and from Game.Kill for the godmode preview),
    // restored in RestoreHpBar (revive / godmode heal-back).
    public void DropHpBar()
    {
        var bc = BattleCharaPtr;
        if (bc != null) bc->Health = 1;
    }

    public void RestoreHpBar()
    {
        var bc = BattleCharaPtr;
        if (bc != null && bc->Health < bc->MaxHealth) bc->Health = bc->MaxHealth;
    }

    public PartyRole Role { get; set; }
    public bool Dead { get; private set; }

    // Player activity for stillness/movement mechanics (e.g. Pyretic, Acceleration Bomb).
    // IsMoving = locomotion input (the engine's own RMIWalk movement sample, the same signal
    // bossmod keys off) OR jumping OR using any action — all three "break" a don't-move mechanic
    // in real FFXIV, so all three count here. IsActing = IsMoving OR auto-attacking, i.e. the
    // strictly-broader "is the player doing something" trigger. Both are re-sampled each tick and
    // forced false while KO'd. Scenarios read these on Party.Player at the mechanic's resolve time.
    public bool IsMoving { get; private set; }
    public bool IsActing { get; private set; }

    internal override BattleChara* BattleCharaPtr => (BattleChara*)(Plugin.ObjectTable.LocalPlayer?.Address ?? 0);

    // 打法直接對玩家下的移動（MoveTo 對玩家是 no-op）交給引導線；SimWorld.CreateParty 接到 Guide。
    internal Action<Vector3>? ScriptedMove { get; set; }

    private protected override PlayerMovement Movement => field ??= new PlayerMovement(this, t => ScriptedMove?.Invoke(t));

    public void Knockback(Vector3 source, float distance, float speed) => Movement.Knockback(source, distance, speed);

    // The player's input lock is a pure function of its own state, re-derived
    // every tick: movement is frozen while KO'd, force-slid, or in LB recovery;
    // actions are blocked while KO'd or in LB recovery. base.Tick advances Movement
    // first, so a slide that arrives this frame has already cleared IsMoving.
    public override void Tick(float deltaSeconds)
    {
        base.Tick(deltaSeconds);
        // Activity includes Passage's activation; only movement or another action cancels it.
        if (SampleActivity()) Combat.Jobs.Paladin.CancelChanneled(this);
        SyncInputLock();
        ServerNative.SyncControlAffliction(!Dead && HasActiveStatusAnySource(ConfusedStatusId));
    }

    // D788 a：UMAD 的混亂（上游 UmadConstants.StatusId.Confused；共用核心不能引用台服被 Compile Remove 的
    // UMAD 常數）。上游以任一 active 狀態的 LockControl 舉 63；我方只看這一個狀態，免得 KO 狀態 896 等
    // 也鎖住所有國際版場景。起訖＝狀態的 active 期間（含 Hidden，同上游）。
    private const ushort ConfusedStatusId = 0x503;

    private bool SampleActivity()
    {
        var hooks = Plugin.PlayerInputHooks;
        // Drain the action latch every frame — even while dead — so a stale press can't carry over.
        var actedThisFrame = hooks.PollActionUsed(out var interruptsChannel);
        if (Dead)
        {
            IsMoving = false;
            IsActing = false;
            return false;
        }
        var moving = hooks.MovementInputActive || hooks.IsJumping;
        IsMoving = moving || actedThisFrame;
        IsActing = IsMoving || hooks.IsAutoAttacking;
        return moving || interruptsChannel;
    }

    internal SelfPoseMessage SampleNetworkPose(bool sampleActivity = false)
    {
        RefreshPosition();
        // Normal Tick already consumed the action latch. Sample it here only
        // while paused, when the network pump still runs but Game.Tick does not.
        if (sampleActivity) SampleActivity();
        return new SelfPoseMessage(new MpPose(MpVector.From(Position), Rotation), IsMoving, IsActing,
            CurrentActionTimeline);
    }

    internal void ApplyNetworkState(bool dead, float hpFraction)
    {
        var native = BattleCharaPtr;
        if (native == null || native->MaxHealth == 0)
            throw new MpProtocolException(MpError.NativeFailure);
        if (dead && !Dead) OnKilled();
        else if (!dead && Dead)
        {
            Dead = false;
            RemoveStatus(StunStatusId);
            ResetActionTimeline();
            PlayActionTimeline(77);
        }
        // Only the simulation HP fraction crosses the wire, not gear-derived
        // real MaxHealth. A simulated local KO retains the established 1-HP bar.
        native->Health = dead ? 1u : Math.Clamp((uint)(native->MaxHealth * hpFraction), 1u, native->MaxHealth);
        SyncInputLock();
    }

    public void OnKilled()
    {
        Dead = true;
        StopMoving();
        // 上游 PlayerMotion 在 !Alive 時釋放強制追隨；原生控制器不放就會拖著倒地的玩家走。
        ServerNative.ForcedFollow(null);
        ServerNative.ReleaseControlAffliction(); // 先停追隨、再放混亂旗標，與倒地同一幀
        DropHpBar(); // real-death bar drop (bots do the same in their own OnKilled); godmode skips this path
        AddStatus(StunStatusId);
        this.PlayKoActionTimeline();
        SyncInputLock(); // engage the lock now, not one frame later
    }

    public override void Despawn()
    {
        // 先停原生強制追隨、再放本插件舉的混亂旗標（D788），都在可能失敗的清理之前。
        // 上游 PlayerMotion.Clear() 同：場景結束不留原生強制追隨（沒開過時不送封包）。
        ServerNative.ForcedFollow(null);
        ServerNative.ReleaseControlAffliction();
        SimCast.CloseLocalCastBar(this);
        base.Despawn();
        StopMoving();
        // Undo any KO bar drop (no-op if already full). Unconditional so it also covers a godmode
        // preview drop, where Dead is never set and a pending heal on Game.Events may be cleared by reset.
        RestoreHpBar();
        if (Dead)
        {
            ResetActionTimeline();
            PlayActionTimeline(77); // revive
            Dead = false;
        }
        // No SimPlayer ticks between a reset and the next scenario, so the lock
        // must be cleared here — otherwise a die-then-reset leaves the player
        // input-locked in the inn.
        SyncInputLock();
    }

    internal void SyncInputLock()
    {
        var hooks = Plugin.PlayerInputHooks;
        hooks.ZeroMovement = Dead || Movement.IsMoving || LimitBreakRecovery;
        hooks.DisableAllActions = Dead || LimitBreakRecovery;
    }
}
