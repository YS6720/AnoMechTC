using System;
using System.Numerics;
using System.Runtime.InteropServices;
using AnoMech.Core.SimObjects;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.Input;

namespace AnoMech.Core.Native;

// Hooks the native action and movement input paths so the simulator can stun
// the local player when a mechanic kills them. Status-row writes don't enforce
// anything (the server overwrites StatusManager._status[] on every packet); the
// real lockout is two booleans this class exposes — the detours read them every
// frame and short-circuit the original calls. Owned by Plugin (session-lifetime);
// SimPlayer is the sole writer of the two flags, reconciling them each tick from
// its own Dead / Movement.IsMoving state.
//
// Signatures and detour shapes lifted from FFXIV-RaidsRewritten's
// PlayerMovementOverride.cs / ActionManagerEx.cs (which themselves credit
// awgil's vnavmesh + bossmod). If a future patch breaks a sig, both projects
// will need to rev them together.
public sealed unsafe class LocalPlayerInputHooks : IDisposable
{

    public bool DisableAllActions { get; set; }
    public bool ZeroMovement { get; set; }

    // --- Player activity signals (read by SimPlayer to drive Party.Player.IsMoving/IsActing) ---
    // Movement is the engine's own per-frame movement sample taken in RMIWalkDetour — the same
    // signal bossmod's MovementOverride.IsMoving() reads — captured as the player's true input
    // intent *before* the stun-zeroing. This is intent/input based (matches cast-cancel semantics),
    // not a position delta. Holds its last value on frames where RMIWalk doesn't fire.
    public bool MovementInputActive { get; private set; }

    // True while the player's weapon auto-attack is swinging.
    public bool IsAutoAttacking => UIState.Instance()->WeaponState.AutoAttackState.IsAutoAttacking;

    // True while the player is in a jump arc (CONDITION_JUMP). State poll like
    // IsAutoAttacking — a jump is always self-initiated, so the state flag is
    // equivalent to input intent here (nothing can force the player airborne).
    public bool IsJumping => Plugin.Condition[ConditionFlag.Jumping];

    // Latched whenever the player actually fires a real action; drained once per frame by SimPlayer
    // (PollActionUsed) so a same-frame action press is still observable to a snapshot mechanic.
    private bool actionUsedSincePoll;
    public bool PollActionUsed()
    {
        var used = actionUsedSincePoll;
        actionUsedSincePoll = false;
        return used;
    }

    private delegate void RMIWalkDelegate(void* self, float* sumLeft, float* sumForward, float* sumTurnLeft, byte* haveBackwardOrStrafe, byte* a6, byte bAdditiveUnk);
    [Signature("E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D")]
    private Hook<RMIWalkDelegate> rmiWalkHook = null!;

    private enum KeybindType
    {
        StrafeLeft = 325,
        StrafeRight = 326,
    }

    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool CheckStrafeKeybindDelegate(IntPtr ptr, KeybindType keybind);
    [Signature("E8 ?? ?? ?? ?? 84 C0 74 04 41 C6 06 01 BA 44 01 00 00")]
    private Hook<CheckStrafeKeybindDelegate> checkStrafeKeybindHook = null!;

    private readonly Hook<InputData.Delegates.IsInputIdPressed> isInputIdPressedHook;
    private readonly Hook<ActionManager.Delegates.Update> updateHook;
    // TC signatures match NoClippy.Game: byte return, including UseActionLocation's final byte.
    private delegate byte UseActionDelegate(ActionManager* self, ActionType actionType, uint actionId, ulong targetId, uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId, bool* outOptAreaTargeted);
    private delegate byte UseActionLocationDelegate(ActionManager* self, ActionType actionType, uint actionId, ulong targetId, Vector3* location, uint extraParam, byte a7);
    private readonly Hook<UseActionDelegate> useActionHook;
    private readonly Hook<UseActionLocationDelegate> useActionLocationHook;
    // BossMod ActionManagerEx.GetActionStatus uses these seven arguments.
    // Keep native booleans byte-sized; the uint return is a LogMessage row, not bool.
    // TC callsite verified unique, target 0x89FCA0 (2026-09-20).
    private delegate uint GetActionStatusDelegate(ActionManager* self, ActionType actionType, uint actionId,
        ulong targetId, byte checkRecastActive, byte checkCastingActive, uint* outOptExtraInfo);
    private readonly Hook<GetActionStatusDelegate> getActionStatusHook;
    // Same Hotbar.CancelCast entrypoint as ClickLib; installed TC metadata:
    // void(Hotbar*), signature 48 83 EC 38 33 D2 C7 44 24 ?? ?? ?? ?? ?? 45 33 C9.
    private readonly Hook<Hotbar.Delegates.CancelCast> cancelCastHook;

    public LocalPlayerInputHooks(IGameInteropProvider hook)
    {
        hook.InitializeFromAttributes(this);

        isInputIdPressedHook = hook.HookFromAddress<InputData.Delegates.IsInputIdPressed>(
            InputData.Addresses.IsInputIdPressed.Value, IsInputIdPressedDetour);
        updateHook = hook.HookFromAddress<ActionManager.Delegates.Update>(
            ActionManager.Addresses.Update.Value, UpdateDetour);
        useActionHook = hook.HookFromAddress<UseActionDelegate>(
            ActionManager.Addresses.UseAction.Value, UseActionDetour);
        useActionLocationHook = hook.HookFromAddress<UseActionLocationDelegate>(
            ActionManager.Addresses.UseActionLocation.Value, UseActionLocationDetour);
        getActionStatusHook = hook.HookFromAddress<GetActionStatusDelegate>(
            ActionManager.Addresses.GetActionStatus.Value, GetActionStatusDetour);
        cancelCastHook = hook.HookFromAddress<Hotbar.Delegates.CancelCast>(
            Hotbar.Addresses.CancelCast.Value, CancelCastDetour);

        rmiWalkHook.Enable();
        checkStrafeKeybindHook.Enable();
        isInputIdPressedHook.Enable();
        updateHook.Enable();
        useActionHook.Enable();
        useActionLocationHook.Enable();
        getActionStatusHook.Enable();
        cancelCastHook.Enable();
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "_CastBar", SimCast.OnLocalCastBarUpdate);
    }

    public void Dispose()
    {
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreRequestedUpdate, "_CastBar", SimCast.OnLocalCastBarUpdate);
        rmiWalkHook?.Dispose();
        checkStrafeKeybindHook?.Dispose();
        isInputIdPressedHook?.Dispose();
        updateHook?.Dispose();
        useActionHook?.Dispose();
        useActionLocationHook?.Dispose();
        getActionStatusHook?.Dispose();
        cancelCastHook?.Dispose();
    }

    private void CancelCastDetour(Hotbar* self)
    {
        cancelCastHook.Original(self);
        var game = Plugin.GameInstance;
        if (game is not { HasActivePractice: true } || !game.World.Map.IsInInstance ||
            game.World.LimitBreaks is not { } runtime) return;
        var role = game.World.Party.PlayerRole;
        if (!game.IsNetworkPeer)
        {
            runtime.Cancel(role);
            return;
        }
        var request = runtime.ActiveRequest(role);
        var player = game.World.Party.Get(role);
        if (request == 0 || player == null) return;
        var native = player.BattleCharaPtr;
        if (native != null)
            game.Multiplayer?.SubmitAbilityUse(runtime.ActionFor(role), native->ClassJob, native->Level,
                null, limitBreakRequestId: request, cancelLimitBreak: true);
    }

    private void RMIWalkDetour(void* self, float* sumLeft, float* sumForward, float* sumTurnLeft, byte* haveBackwardOrStrafe, byte* a6, byte bAdditiveUnk)
    {
        rmiWalkHook.Original(self, sumLeft, sumForward, sumTurnLeft, haveBackwardOrStrafe, a6, bAdditiveUnk);
        // Capture the engine's movement sample as the player's true movement intent, before any
        // stun-zeroing below. (self is a MoveControllerSubMemberForMine*; the sums are its move vector.)
        MovementInputActive = *sumLeft != 0 || *sumForward != 0;
        if (!ZeroMovement) return;
        *sumLeft = 0;
        *sumForward = 0;
        *haveBackwardOrStrafe = 0;
    }

    private bool CheckStrafeKeybindDetour(IntPtr ptr, KeybindType keybind)
    {
        if (ZeroMovement && (keybind == KeybindType.StrafeLeft || keybind == KeybindType.StrafeRight))
            return false;
        return checkStrafeKeybindHook.Original(ptr, keybind);
    }

    private bool IsInputIdPressedDetour(InputData* inputData, InputId inputId)
    {
        if (ZeroMovement && (inputId == InputId.JUMP || inputId == InputId.PAD_JUMPANDCANCELCAST))
            return false;
        return isInputIdPressedHook.Original(inputData, inputId);
    }

    // Drains queued auto-attacks while DisableAllActions is set so the player
    // doesn't keep swinging mid-stun; mirrors raid-rewritten's UpdateDetour.
    private void UpdateDetour(ActionManager* self)
    {
        updateHook.Original(self);
        SimCast.UpdateLocalCastBar();
        if (!DisableAllActions) return;
        var autosOn = UIState.Instance()->WeaponState.AutoAttackState.IsAutoAttacking;
        if (autosOn) self->UseAction(ActionType.GeneralAction, 1);
    }

    private uint GetActionStatusDetour(ActionManager* self, ActionType actionType, uint actionId,
        ulong targetId, byte checkRecastActive, byte checkCastingActive, uint* outOptExtraInfo)
    {
        var nativeStatus = getActionStatusHook.Original(self, actionType, actionId, targetId,
            checkRecastActive, checkCastingActive, outOptExtraInfo);
        // Full practice gauge still reports 574 in the native solo territory.
        // Project only this eligibility boundary; retain range/target/recast errors,
        // all unrelated actions, and every live-party/PvP/outside-practice query.
        if (nativeStatus is not (0 or 574) ||
            !TryGetPracticeLimitBreak(actionType, actionId, out var runtime, out var action) ||
            action == 0 || actionType == ActionType.Action && actionId != action)
            return nativeStatus;

        var game = Plugin.GameInstance;
        var role = game.World.Party.PlayerRole;
        var player = game.World.Party.Get(role);
        if (game.Paused || DisableAllActions || !runtime.IsAvailable || runtime.IsBusy(role) ||
            player is not { IsActive: true } || !player.IsAlive())
            return nativeStatus == 0 ? 574u : nativeStatus;

        if (outOptExtraInfo != null) *outOptExtraInfo = 0;
        return 0;
    }

    private byte UseActionDetour(ActionManager* self, ActionType actionType, uint actionId, ulong targetId, uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId, bool* outOptAreaTargeted)
    {
        if (SimulationPaused && actionType == ActionType.Action) return 0;
        if (DisableAllActions && !IsStopAutosAction(actionType, actionId)) return 0;
        try
        {
            if (TryGetPracticeLimitBreak(actionType, actionId, out var runtime, out var lbAction))
            {
                var game = Plugin.GameInstance;
                CrashTrace.Log($"[LB] 按鍵 type={actionType} id={actionId} action={lbAction} ready={runtime.IsAvailable} paused={game.Paused}");
                if (game.Paused || !runtime.IsAvailable || lbAction == 0) return 0;
                var row = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRow(lbAction);
                if (!row.TargetArea)
                {
                    if (outOptAreaTargeted != null) *outOptAreaTargeted = false;
                    var accepted = StartPracticeLimitBreak(runtime, lbAction, null,
                        ResolveLimitBreakTarget(targetId));
                    CrashTrace.Log($"[LB] 確認施放 action={lbAction} accepted={accepted} ground=False");
                    if (accepted) actionUsedSincePoll = true;
                    return accepted ? (byte)1 : (byte)0;
                }
                // Preserve the native placement circle. UseActionLocation below
                // commits its chosen point; opening/cancelling the circle spends no LB.
                var placed = useActionHook.Original(self, ActionType.Action, lbAction, targetId,
                    extraParam, mode, comboRouteId, outOptAreaTargeted);
                if (placed == 0)
                    CrashTrace.Log($"[LB] 地面選圈被拒 action={lbAction} status={self->GetActionStatus(ActionType.Action, lbAction, targetId)}");
                return placed;
            }
        }
        catch (Exception ex)
        {
            CrashTrace.Log($"[LB] 按鍵處理失敗：{ex.Message}");
            return 0;
        }
        var result = useActionHook.Original(self, actionType, actionId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);
        if (result != 0 && !IsStopAutosAction(actionType, actionId))
            actionUsedSincePoll = true;
        return result;
    }

    private byte UseActionLocationDetour(ActionManager* self, ActionType actionType, uint actionId, ulong targetId, Vector3* location, uint extraParam, byte a7)
    {
        if (SimulationPaused && actionType == ActionType.Action) return 0;
        if (DisableAllActions && !IsStopAutosAction(actionType, actionId)) return 0;
        try
        {
            if (TryGetPracticeLimitBreak(actionType, actionId, out var runtime, out var lbAction))
            {
                var game = Plugin.GameInstance;
                if (game.Paused || !runtime.IsAvailable || lbAction == 0) return 0;
                var row = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRow(lbAction);
                if (row.TargetArea && location == null) return 0;
                var localPoint = row.TargetArea ? game.World.Coordinates.ToLocal(*location) : (Vector3?)null;
                var accepted = StartPracticeLimitBreak(runtime, lbAction, localPoint,
                    ResolveLimitBreakTarget(targetId));
                CrashTrace.Log($"[LB] 確認施放 action={lbAction} accepted={accepted} ground={row.TargetArea}");
                if (accepted) actionUsedSincePoll = true;
                return accepted ? (byte)1 : (byte)0;
            }
        }
        catch (Exception ex)
        {
            CrashTrace.Log($"[LB] 地面施放處理失敗：{ex.Message}");
            return 0;
        }
        var result = useActionLocationHook.Original(self, actionType, actionId, targetId, location, extraParam, a7);
        if (result != 0) actionUsedSincePoll = true;
        return result;
    }

    private static bool TryGetPracticeLimitBreak(ActionType type, uint id,
        out Combat.PracticeLimitBreakRuntime runtime, out uint action)
    {
        runtime = null!;
        action = 0;
        var game = Plugin.GameInstance;
        if (game is not { HasActivePractice: true } ||
            !game.World.Map.IsInInstance ||
            game.World.LimitBreaks is not { } active)
            return false;
        var requested = type == ActionType.GeneralAction && id == Combat.TankLimitBreak.GeneralActionId ||
            type == ActionType.Action &&
            Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(id)?.ActionCategory.RowId == 9;
        if (!requested) return false;
        runtime = active;
        action = active.ActionFor(game.World.Party.PlayerRole);
        return true;
    }

    private static bool StartPracticeLimitBreak(Combat.PracticeLimitBreakRuntime runtime,
        uint action, Vector3? location, SimCharacter? target)
    {
        var game = Plugin.GameInstance;
        if (game.Multiplayer is { HasSession: true } multiplayer)
        {
            if (game.World.Party.Get(game.World.Party.PlayerRole) is not { } player) return false;
            var native = player.BattleCharaPtr;
            if (native == null) return false;
            var request = runtime.BeginRequest(game.World.Party.PlayerRole);
            if (request == 0) return false;
            var accepted = multiplayer.SubmitAbilityUse(action, native->ClassJob, native->Level,
                target, location, request);
            if (!accepted) runtime.RejectRequest(request);
            return accepted;
        }
        return !game.IsNetworkPeer &&
            runtime.TryStart(game.World.Party.PlayerRole, action, location, target);
    }

    private static SimObjects.SimCharacter? ResolveLimitBreakTarget(ulong targetId)
    {
        if (targetId is 0 or 0xE0000000)
            targetId = Plugin.ObjectTable.LocalPlayer?.TargetObject?.EntityId ?? 0;
        return Plugin.GameInstance.ResolveAbilityActor(targetId);
    }

    private static bool SimulationPaused
        => Plugin.GameInstance?.World.Map.IsInInstance == true && Plugin.GameInstance.Paused;

    // Lets the auto-cancel UseAction from UpdateDetour through; everything else
    // bounces while autos are still firing.
    private static bool IsStopAutosAction(ActionType actionType, uint actionId)
    {
        if (!UIState.Instance()->WeaponState.AutoAttackState.IsAutoAttacking) return false;
        return actionType == ActionType.GeneralAction && actionId == 1;
    }
}
