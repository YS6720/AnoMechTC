using System;
using System.Collections.Generic;
using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.Native;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AnoMech.Core.Combat.Jobs;
using AnoMech.Core.SimObjects;

namespace AnoMech.Core.Combat;

// 模擬區內的「職業循環判定」：連段燈與職業資源本來由**伺服器回應**驅動
// （client 送請求 → server 回 ActionEffect → combo/gauge 更新），模擬區防火牆
// 隔離後永遠等不到回應——技能按得動（GCD 是本地預測）但循環死掉。
// 這裡在本地偽造伺服器那一半：
//   1. 連段＝通用（Lumina Action.ActionCombo 反查：用了 X 且前置成立 → 設 Combo 狀態）
//   2. 職業資源／proc＝由 host/single-player RecordedAbilityRuntime 統一擁有。
//
// 🔒 安全鎖（fail-closed）：**只在模擬場景執行中**才處理——真實世界/真副本裡
// 本 hook 只轉發原函式、一個 byte 都不改。否則 client 顯示會與伺服器脫節。
//
// UseAction hook 簽名＝**抄 NoClippy fork 台服驗證版**（AGENTS §10：升版事故後
// 禁照 CS 宣告手寫；回傳 byte、8 參數＋this，位址走 MemberFunctionPointers）。
public sealed unsafe class RotationSim : IDisposable
{
    private delegate byte UseActionDelegate(
        ActionManager* thisPtr, ActionType actionType, uint actionId, ulong targetId,
        uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId, bool* outOptAreaTargeted);

    private readonly Hook<UseActionDelegate>? hook;
    private readonly EventScheduler localEvents = new();
    private readonly PracticeLimitBreakGauge limitBreakGauge = new();
    private int diagLeft = 12;
    // 同一技能、同一原因的被拒按鍵：第一次記一行，之後同一 key 每 5 秒最多一行並帶中間次數
    // （2026-09-23 Owner）。按住或連按被拒的鍵以前每按一次就記，單場最多 2434 行、占 trace 近半。
    private readonly RepeatedLogLimiter<(uint Action, uint Status)> rejectedPresses = new(5000);
    private long schedulerGeneration;
    private long lastUpdateMilliseconds;
    private long limitBreakProbeGeneration = -1;
    private long limitBreakProbeAt;
    private int limitBreakProbeRemaining;
    internal static RotationSim? Instance { get; private set; }
    private PendingAction? castingAction;
    private PendingAction? awaitingAction;
    private long nextActionRequest;
    private uint cancelSerial;
    private ushort lastExecutionSequence;
    private bool hasExecutionSequence;
    private int useActionDepth;
    private readonly record struct PendingAction(uint ActionId, ushort Sequence, int ManaCost,
        ulong TargetId, SimCharacter? Target, long Generation, byte ClassJob, byte Level,
        long StartedAt, float CastSeconds, uint CancelSerial, bool WasCooldownIdle, long RequestId,
        Vector3? Location = null);

    internal void CancelOrdinaryCast()
    {
        cancelSerial++;
        castingAction = null;
        LocalJobResources.Flush();
    }

    // ActionCombo 反查：有哪些 action 以 X 為前置（＝用了 X 之後連段燈該亮）。
    private readonly HashSet<uint> startsCombo = new();
    // 戰技（3）與魔法（2）：客戶端可在 GCD 轉動中先受理、GCD 到點才施放。能力技沒有這回事。
    private readonly HashSet<uint> gcdActions = new();
    // X 自己的前置（0＝無）。
    private readonly Dictionary<uint, uint> prereqOf = new();

    public RotationSim()
    {
        Instance = this;
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
            nextOf.Clear();
            foreach (var row in sheet)
            {
                if (row.ActionCategory.RowId is 2 or 3) gcdActions.Add(row.RowId);
                var pre = row.ActionCombo.RowId;
                if (pre == 0) continue;
                startsCombo.Add(pre);
                prereqOf[row.RowId] = pre;
                if (!nextOf.TryGetValue(pre, out var next))
                    nextOf[pre] = next = new List<uint>();
                next.Add(row.RowId);
            }
            Plugin.GameInstance?.Abilities.ConfigureCombos(prereqOf, startsCombo);
            hook = Plugin.GameInterop.HookFromAddress<UseActionDelegate>(
                (nint)ActionManager.MemberFunctionPointers.UseAction, Detour);
            hook.Enable();
            schedulerGeneration = Plugin.GameInstance?.ScenarioDispatchGeneration ?? 0;
            lastUpdateMilliseconds = Environment.TickCount64;
            // 每幀重申連段狀態：實測 client 在「請求未確認」下會反覆清 Combo
            //（第三下不亮＝寫入被清）。影子狀態是唯一事實源，凡不一致就寫回。
            Plugin.Framework.Update += ReassertCombo;
            CrashTrace.Log($"[循環] RotationSim 就緒（連段表 {startsCombo.Count} 起點）");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[RotationSim] hook 掛載失敗，模擬區內無連段判定：{ex.Message}");
        }
    }

    private byte Detour(ActionManager* am, ActionType actionType, uint actionId, ulong targetId,
                        uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId, bool* outOpt)
    {
        byte ret;
        var statusActionId = actionId;
        var generalTankLimitBreak = false;
        var generalSprint = false;
        var ordinary = false;
        var adjustedAction = actionId;
        var manaCost = 0;
        var cooldownWasIdle = false;
        var sequenceBefore = am->LastUsedActionSequence;
        var outermost = useActionDepth++ == 0;
        try
        {
            var practice = Plugin.GameInstance;
            // P6 owns all LB jobs and ground-target commits in LocalPlayerInputHooks.
            // Do not also submit through ordinary rotation/status bookkeeping.
            if (practice?.World.LimitBreaks != null &&
                (actionType == ActionType.GeneralAction && actionId == TankLimitBreak.GeneralActionId ||
                 actionType == ActionType.Action &&
                 Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(actionId)?.ActionCategory.RowId == 9))
                return hook!.Original(am, actionType, actionId, targetId, extraParam, mode, comboRouteId, outOpt);
            var classJob = (byte)Plugin.PlayerState.ClassJob.RowId;
            // The Sprint hotbar entry is a GeneralAction; its nested Action call
            // is deliberately not submitted again by the outermost-only guard.
            generalSprint = practice is { HasActivePractice: true } && practice.World.Map.IsInInstance &&
                actionType == ActionType.GeneralAction &&
                Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.GeneralAction>()
                    .GetRowOrDefault(actionId)?.Action.RowId == 3;
            if (generalSprint) statusActionId = 3;
            var limitBreakPressed = actionType == ActionType.GeneralAction && actionId == TankLimitBreak.GeneralActionId ||
                actionType == ActionType.Action && TankLimitBreak.TryGetStatus(actionId, classJob, out _, out _);
            if (practice is { HasActivePractice: true, IsNetworkPeer: false } &&
                practice.World.Map.IsInInstance && practice.Multiplayer?.HasSession != true &&
                TankLimitBreak.IsTank(classJob) && limitBreakPressed)
            {
                // A simulated party is not a native party. Submit locally instead
                // of requiring the real server/client's solo LB eligibility.
                if (practice.Paused || Plugin.PlayerInputHooks.DisableAllActions) return 0;
                limitBreakGauge.Update(true, practice.ScenarioDispatchGeneration);
                var localGauge = FFXIVClientStructs.FFXIV.Client.Game.UI.LimitBreakController.StaticAddressPointers.pInstance;
                var localPlayer = Player();
                if (!limitBreakGauge.IsAvailable || localGauge == null || localPlayer == null) return 0;
                var localAction = localGauge->GetActionId(localPlayer, 2);
                if (!TankLimitBreak.TryGetStatus(localAction, classJob, out _, out _) ||
                    !practice.Abilities.TryUse(practice.World.Party.PlayerRole, localAction, classJob,
                        (byte)Plugin.PlayerState.EffectiveLevel, null, out _))
                    return 0;
                limitBreakGauge.Consume();
                if (outOpt != null) *outOpt = false;
                FirePlayerActionEffect(localAction, selfTarget: true);
                CrashTrace.Log($"[LB] 單人練習施放 a={localAction}；本輪量表已消耗，只顯示狀態。");
                return 1;
            }
            if (practice is { HasActivePractice: true, Paused: false } &&
                practice.World.Map.IsInInstance &&
                TankLimitBreak.IsTank((byte)Plugin.PlayerState.ClassJob.RowId) &&
                actionType == ActionType.GeneralAction && actionId == TankLimitBreak.GeneralActionId)
            {
                limitBreakGauge.Update(true, practice.ScenarioDispatchGeneration);
                var gauge = FFXIVClientStructs.FFXIV.Client.Game.UI.LimitBreakController.StaticAddressPointers.pInstance;
                var player = Player();
                if (gauge != null && player != null && gauge->BarUnits > 0 && gauge->CurrentUnits >= gauge->BarUnits)
                {
                    var grade = (byte)Math.Clamp(gauge->CurrentUnits / gauge->BarUnits - 1, 0, 2);
                    statusActionId = gauge->GetActionId(player, grade);
                    generalTankLimitBreak = TankLimitBreak.TryGetStatus(statusActionId,
                        (byte)Plugin.PlayerState.ClassJob.RowId, out _, out _);
                }
            }
            ordinary = outermost && (actionType == ActionType.Action || generalSprint) &&
                practice is { HasActivePractice: true } && practice.World.Map.IsInInstance &&
                JobRules.Supports(classJob);
            if (ordinary)
            {
                if (practice!.Paused || !LocalJobResources.OwnsPlayer || awaitingAction != null) return 0;
                UpdateOrdinaryCast(am, Environment.TickCount64);
                if (awaitingAction != null) return 0;
                LocalJobResources.Flush();
                adjustedAction = am->GetAdjustedActionId(statusActionId);
                var row = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(adjustedAction);
                if (row is not { } action || !JobRules.IsAvailableAction(action, classJob,
                        (byte)Plugin.PlayerState.EffectiveLevel)) return 0;
                // Only MP cost kinds use GetActionCost as mana; status/gauge costs do not.
                manaCost = action.PrimaryCostType is 3 or 4 or 76
                    ? Math.Max(0, ActionManager.GetActionCost(ActionType.Action, adjustedAction, 0, 0, 0, 0)) : 0;
                var detail = am->GetRecastGroupDetail(am->GetRecastGroup((int)ActionType.Action, adjustedAction));
                cooldownWasIdle = detail == null || !detail->IsActive;
                LocalJobResources.CapturePressStatuses();
            }
            // Original is intentionally called exactly once. A failed native call
            // is rejected rather than retried, which could submit a duplicate cast.
            ret = hook!.Original(am, actionType, actionId, targetId, extraParam, mode, comboRouteId, outOpt);
        }
        catch (Exception ex)
        {
            CrashTrace.Log($"[循環] UseAction 原函式失敗（拒絕本次）：{ex.Message}");
            return 0;
        }
        finally { useActionDepth--; }
        try
        {
            // 🔒 只在模擬場景執行中才介入（IsInInstance＝Start 後 true、Leave/Reset 落回 false）
            var game = Plugin.GameInstance;
            var inSim = game?.World.Map.IsInInstance == true;
            var canProcess = inSim && game != null && !game.Paused &&
                             game.HasActivePractice;
            if (diagLeft > 0 && actionType == ActionType.Action)
            {
                diagLeft--;
                CrashTrace.Log($"[循環] UseAction a={actionId} ret={ret} inSim={inSim}");
            }
            var areaTargeted = outOpt != null && *outOpt;
            // 客戶端拒絕的按鍵（ret=0）記下原因碼：GetActionStatus 回 LogMessage id
            // （0＝可用；例如 572 量譜不足、1122 前置條件不符）。以前只記騎士幾個 id，
            // 2026-09-16 武士的明鏡止水被拒時什麼都看不到。重複的由 rejectedPresses 合併。
            if (ret == 0 && actionType == ActionType.Action && inSim)
            {
                var adjusted = am->GetAdjustedActionId(actionId);
                var status = am->GetActionStatus(ActionType.Action, adjusted);
                if (rejectedPresses.ShouldWrite((adjusted, status), Environment.TickCount64, out var repeats))
                {
                    var text = status == 0 ? "" : Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.LogMessage>()?.GetRowOrDefault(status)?.Text.ExtractText() ?? "";
                    CrashTrace.Log($"[循環] 按鍵被拒 a={actionId} adjusted={adjusted} status={status} {text}" +
                        (repeats > 0 ? $"（上一筆後同原因再 {repeats} 次）" : ""));
                }
            }
            if (ret != 0 && canProcess && !areaTargeted && outermost &&
                (actionType == ActionType.Action || generalTankLimitBreak || generalSprint))
                OnActionUsed(am, ordinary ? adjustedAction : statusActionId,
                    statusActionId == 3 ? 0 : targetId,
                    sequenceBefore, manaCost, cooldownWasIdle);
            if (ordinary) LocalJobResources.Flush();
            if (generalTankLimitBreak)
                CrashTrace.Log($"[LB] 原生施放 a={statusActionId} ret={ret} active={canProcess}");
        }
        catch (Exception ex)
        {
            CrashTrace.Log($"[循環] 處理出錯（已忽略）：{ex.Message}");
        }
        return ret;
    }

    // 影子連段狀態（2026-08-22）：**不再寫 am->Combo**——實測讀回正確但畫面不亮
    // ＝TC CS 位移可疑，繼續寫等於朝不明位置戳 8 bytes。連段判定自己記，
    // 顯示交給自畫的循環面板（RotationHudWindow）——client 的連段燈驗證鏈
    // 四輪對欄位（target／SourceSequence／GlobalSequence／Combo 直寫）皆不受控，放棄該路。
    private uint shadowCombo;

    /// <summary>A/B 實驗模式（/anomech rot N）：0=不發合成回包 1=只發序號確認（ActionId=0
    /// 空包，不帶演出） 2=完整回包（現行預設）。動畫消失／GCD 不顯示的嫌疑都指向
    /// 合成回包與 client 本地預測互踩——讓 維護者 現場切模式對照，不用每輪重 build。</summary>
    public static int Mode = 1;   // A/B 定案（2026-08-22）：1＝空包確認。0 全滅、2 吃動畫。

    /// <summary>循環面板讀的公開狀態：目前連段起點（0＝無）與「接得上的下一招」清單。</summary>
    public static uint CurrentCombo { get; private set; }
    public static float CurrentComboAge => CurrentCombo == 0 ? 0f : comboAge;
    private static float comboAge;
    private static readonly Dictionary<uint, List<uint>> nextOf = new();
    public static IReadOnlyList<uint> NextActions =>
        CurrentCombo != 0 && nextOf.TryGetValue(CurrentCombo, out var list) ? list : [];

    internal bool PrepareGroundAction(ActionManager* am, uint actionId, out int manaCost, out bool idle)
    {
        manaCost = 0;
        idle = false;
        if (Plugin.GameInstance is not { HasActivePractice: true, Paused: false } game ||
            !game.World.Map.IsInInstance || !LocalJobResources.OwnsPlayer || awaitingAction != null)
            return false;
        LocalJobResources.Flush();
        var row = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRow(actionId);
        if (!row.TargetArea || !JobRules.IsAvailableAction(row, LocalJobResources.ClassJob, LocalJobResources.Level))
            return false;
        manaCost = row.PrimaryCostType is 3 or 4 or 76
            ? Math.Max(0, ActionManager.GetActionCost(ActionType.Action, actionId, 0, 0, 0, 0)) : 0;
        var detail = am->GetRecastGroupDetail(am->GetRecastGroup((int)ActionType.Action, actionId));
        idle = detail == null || !detail->IsActive;
        LocalJobResources.CapturePressStatuses();
        return true;
    }

    internal void CompleteGroundAction(ActionManager* am, uint actionId, Vector3 point,
        ushort sequenceBefore, int manaCost, bool idle, byte result)
    {
        if (result != 0) OnActionUsed(am, actionId, 0, sequenceBefore, manaCost, idle, point);
        LocalJobResources.Flush();
    }

    private void OnActionUsed(ActionManager* am, uint actionId, ulong targetId,
        ushort sequenceBefore, int manaCost, bool cooldownWasIdle, Vector3? location = null)
    {
        var sequence = am->LastUsedActionSequence;
        // Native queue acceptance does not advance the execution sequence.
        if (sequence == sequenceBefore || hasExecutionSequence && sequence == lastExecutionSequence) return;
        hasExecutionSequence = true;
        lastExecutionSequence = sequence;
        var game = Plugin.GameInstance;
        if (game == null || !LocalJobResources.OwnsPlayer) return;
        var hasTarget = (uint)targetId is not (0 or 0xE0000000);
        var target = hasTarget ? game.ResolveAbilityActor(targetId) : null;
        if (hasTarget && target == null) return;
        LocalJobResources.AcceptPressStatuses();
        var cast = am->CastActionType == ActionType.Action && am->CastActionId == actionId &&
            am->CastTimeTotal > 0f;
        var pending = new PendingAction(actionId, sequence, manaCost, targetId, target,
            game.ScenarioDispatchGeneration, LocalJobResources.ClassJob, LocalJobResources.Level,
            Environment.TickCount64, cast ? am->CastTimeTotal - am->CastTimeElapsed : 0f,
            cancelSerial, cooldownWasIdle, ++nextActionRequest, location);
        if (cast) castingAction = pending;
        else SubmitCompleted(pending);
    }

    private bool IsCurrent(in PendingAction action)
    {
        var game = Plugin.GameInstance;
        return game is { HasActivePractice: true } && game.World.Map.IsInInstance &&
            LocalJobResources.OwnsPlayer && action.Generation == game.ScenarioDispatchGeneration &&
            action.ClassJob == LocalJobResources.ClassJob && action.Level == LocalJobResources.Level &&
            game.World.Party.Player.IsAlive() &&
            (action.Target == null || action.Target.IsActive &&
                ReferenceEquals(game.ResolveAbilityActor(action.TargetId), action.Target));
    }

    private void UpdateOrdinaryCast(ActionManager* am, long now)
    {
        if (castingAction is not { } action) return;
        if (!IsCurrent(action) || action.CancelSerial != cancelSerial)
        { castingAction = null; return; }
        var elapsed = (now - action.StartedAt) / 1000f;
        var nativeCasting = am->CastActionType == ActionType.Action && am->CastActionId == action.ActionId &&
            am->CastTimeTotal > 0f && am->CastTimeElapsed < am->CastTimeTotal;
        // A disappeared cast before its deadline is cancellation, not completion.
        if (!nativeCasting && elapsed + 0.02f < action.CastSeconds)
        { castingAction = null; LocalJobResources.Flush(); return; }
        if (nativeCasting || elapsed < action.CastSeconds) return;
        castingAction = null;
        SubmitCompleted(action);
    }

    private void SubmitCompleted(in PendingAction action)
    {
        if (!IsCurrent(action) || Plugin.GameInstance.Paused) return;
        awaitingAction = action;
        if (!Plugin.GameInstance.SubmitAbility(action.ActionId, action.ClassJob, action.Level,
                action.TargetId, action.RequestId, action.Location))
            CompleteOrdinaryAction(action.RequestId, accepted: false, comboOk: false);
    }

    internal void CompleteOrdinaryAction(long requestId, bool accepted, bool comboOk)
    {
        if (awaitingAction is not { } action || action.RequestId != requestId) return;
        awaitingAction = null;
        if (!IsCurrent(action)) return;
        if (accepted)
        {
            var comboAction = Machinist.ComboActionId(action.ActionId);
            if (comboOk && startsCombo.Contains(comboAction))
            { shadowCombo = comboAction; comboAge = 0f; }
            else if (prereqOf.ContainsKey(action.ActionId) || startsCombo.Contains(comboAction))
            { shadowCombo = 0; comboAge = 30f; }
            CurrentCombo = shadowCombo;
            LocalJobResources.CommitAction(action.ActionId, comboOk, action.ManaCost);
            var am = ActionManager.Instance();
            if (am != null)
            {
                var detail = am->GetRecastGroupDetail(am->GetRecastGroup((int)ActionType.Action, action.ActionId));
                if (detail != null && !detail->IsActive) am->StartCooldown(ActionType.Action, action.ActionId);
                var charges = ActionManager.GetMaxCharges(action.ActionId, 0);
                if (detail != null && detail->IsActive && charges > 1 && action.WasCooldownIdle)
                    detail->Elapsed = MathF.Max(detail->Elapsed, detail->Total - detail->Total / charges);
            }
        }
        LocalJobResources.Flush();
        // A cast's synthetic native acknowledgment must never precede completion.
        if (Mode > 0) FirePlayerActionEffect(Mode == 1 ? 0u : action.ActionId, action.Sequence);
    }

    private static Character* Player()
    {
        var p = Plugin.ObjectTable.LocalPlayer;
        return p == null ? null : (Character*)p.Address;
    }

    /// <summary>
    /// 幫玩家發一份合成的 ActionEffect 回包（SimCast.NativeActionEffect 同構、目標＝
    /// 玩家當前目標）。client 處理自己動作的回包時會點連段燈／清 queue——這正是
    /// 防火牆擋掉、導致「技能按得動但循環死掉」的那半邊。零傷害 effect 區塊＝純狀態演出。
    /// </summary>
    private static uint globalSeq = 0x40000000;

    private static void FirePlayerActionEffect(uint actionId, ushort sourceSequence = 0, bool selfTarget = false)
    {
        var lp = Plugin.ObjectTable.LocalPlayer;
        if (lp == null) return;
        var chara = (Character*)lp.Address;
        const uint NullObjectId = 0xE0000000;
        var oid = selfTarget ? chara->EntityId : NullObjectId;
        if (!selfTarget && lp.TargetObject is { } t)
        {
            var cm = CharacterManager.Instance();
            if (cm != null && cm->LookupBattleCharaByEntityId(t.EntityId) != null)
                oid = t.EntityId;
        }
        var target = new FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectId { ObjectId = oid };
        var header = new ActionEffectHandler.Header
        {
            AnimationTargetId = target,
            ActionId = actionId,
            GlobalSequence = ++globalSeq,
            AnimationLock = 0.6f,
            BallistaEntityId = NullObjectId,
            SourceSequence = sourceSequence,
            RotationInt = MathUtil.QuantizeRotation(chara->Rotation),
            SpellId = (ushort)actionId,
            AnimationVariation = 0,
            ActionType = ActionType.Action,
            Flags = 0,
            NumTargets = (byte)(target.ObjectId == NullObjectId ? 0 : 1),
        };
        var effects = new ActionEffectHandler.TargetEffects();
        var pos = (System.Numerics.Vector3)chara->Position;
        ActionEffectHandler.Receive(chara->EntityId, chara, &pos, &header, &effects, &target);

        var am = ActionManager.Instance();
        if (am != null)
        {
            if (am->LastHandledActionSequence != am->LastUsedActionSequence)
                am->LastHandledActionSequence = am->LastUsedActionSequence;
            am->Combo.Action = CurrentCombo;
            am->Combo.Timer = CurrentCombo == 0 ? 0f : MathF.Max(0f, 30f - CurrentComboAge);
        }
    }

    // 模擬區是隔離的：裡面的施放伺服器從沒看到，離開後真實冷卻應該等於「進場時的狀態減去
    // 經過的時間」。進場快照全部 80 個 recast group，離場還原並補上經過時間。少了這步，
    // 充能技（明鏡止水）離場後會停在模擬區裡被壓下去的冷卻上（維護者 2026-09-16）。
    private const int RecastGroupCount = 80;
    private readonly (bool IsActive, uint ActionId, float Elapsed, float Total)[] recastSnapshot = new (bool, uint, float, float)[RecastGroupCount];
    private bool recastSnapshotTaken;
    private long recastSnapshotAt;
    private nint recastSnapshotPlayer;
    private ulong recastSnapshotObjectId;
    private byte recastSnapshotClassJob;
    private byte recastSnapshotLevel;
    private bool wasInSim;

    internal void CaptureRecastsBeforePracticeReset()
    {
        var am = ActionManager.Instance();
        if (am == null) return;
        if (!recastSnapshotTaken)
        {
            SnapshotRecasts(am);
            if (!recastSnapshotTaken) return;
        }
        wasInSim = true;
    }

    private void SnapshotRecasts(ActionManager* am)
    {
        var local = Plugin.ObjectTable.LocalPlayer;
        if (local == null) return;
        recastSnapshotPlayer = local.Address;
        recastSnapshotObjectId = (ulong)local.GameObjectId;
        recastSnapshotClassJob = (byte)Plugin.PlayerState.ClassJob.RowId;
        recastSnapshotLevel = (byte)Plugin.PlayerState.EffectiveLevel;
        for (var i = 0; i < RecastGroupCount; i++)
        {
            var d = am->GetRecastGroupDetail(i);
            recastSnapshot[i] = d == null ? default : (d->IsActive, d->ActionId, d->Elapsed, d->Total);
        }
        recastSnapshotTaken = true;
        recastSnapshotAt = Environment.TickCount64;
        CrashTrace.Log("[循環] 進模擬區：冷卻快照");
    }

    private void RestoreRecasts(ActionManager* am)
    {
        if (!recastSnapshotTaken || am == null) return;
        var local = Plugin.ObjectTable.LocalPlayer;
        if (local == null ||
            local.Address != recastSnapshotPlayer ||
            (ulong)local.GameObjectId != recastSnapshotObjectId ||
            (byte)Plugin.PlayerState.ClassJob.RowId != recastSnapshotClassJob ||
            (byte)Plugin.PlayerState.EffectiveLevel != recastSnapshotLevel)
        {
            recastSnapshotTaken = false;
            CrashTrace.Log("[循環] 離模擬區：冷卻快照擁有者已變更，略過還原");
            return;
        }
        recastSnapshotTaken = false;
        var passed = (Environment.TickCount64 - recastSnapshotAt) / 1000f;
        var restored = 0;
        for (var i = 0; i < RecastGroupCount; i++)
        {
            var d = am->GetRecastGroupDetail(i);
            if (d == null) continue;
            var s = recastSnapshot[i];
            if (!s.IsActive)
            {
                if (!d->IsActive && d->ActionId == 0 && d->Elapsed == 0f && d->Total == 0f) continue;
                d->IsActive = false;
                d->ActionId = 0;
                d->Elapsed = 0f;
                d->Total = 0f;
                restored++;
                continue;
            }
            var elapsed = s.Elapsed + passed;
            if (elapsed >= s.Total)
            {
                d->IsActive = false;
                d->ActionId = 0;
                d->Elapsed = 0f;
                d->Total = 0f;
            }
            else
            {
                d->IsActive = true;
                d->ActionId = s.ActionId;
                d->Elapsed = elapsed;
                d->Total = s.Total;
            }
            restored++;
        }
        CrashTrace.Log($"[循環] 離模擬區：冷卻還原 {restored} 組（經過 {passed:F0}s）");
    }

    // Temporary solo-LB boundary evidence: at most three snapshots per run,
    // no native writes/input. Remove after HUD/eligibility has been verified.
    private void ObserveSoloLimitBreak(Game.Game game, long now)
    {
        if (!game.HasActivePractice || !game.World.Map.IsInInstance ||
            game.IsNetworkPeer || game.Multiplayer?.HasSession == true ||
            game.World.LimitBreaks is not { } runtime)
            return;
        if (limitBreakProbeGeneration != game.ScenarioDispatchGeneration)
        {
            limitBreakProbeGeneration = game.ScenarioDispatchGeneration;
            limitBreakProbeRemaining = 3;
            limitBreakProbeAt = now + 1000;
        }
        if (limitBreakProbeRemaining == 0 || now < limitBreakProbeAt) return;
        limitBreakProbeRemaining--;
        limitBreakProbeAt = now + 2000;
        var gauge = LimitBreakController.StaticAddressPointers.pInstance;
        var group = GroupManager.Instance();
        var manager = ActionManager.Instance();
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName("_LimitBreak").Address;
        var stage = AtkStage.Instance();
        var holder = stage == null ? null : stage->AtkArrayDataHolder;
        var numbers = holder == null ? null : holder->GetNumberArrayData((int)NumberArrayType.LimitBreak);
        var action = runtime.ActionFor(game.World.Party.PlayerRole);
        var generalStatus = manager == null ? uint.MaxValue :
            manager->GetActionStatus(ActionType.GeneralAction, TankLimitBreak.GeneralActionId);
        var actionStatus = manager == null || action == 0 ? uint.MaxValue :
            manager->GetActionStatus(ActionType.Action, action);
        CrashTrace.Log($"[LB診斷] solo party={(group == null ? -1 : group->MainGroup.MemberCount)}"
            + $" gauge={(gauge == null ? "missing" : $"{gauge->BarCount}/{gauge->CurrentUnits}/{gauge->BarUnits}")}"
            + $" clientPvP={Plugin.ClientState.IsPvP} controllerPvP={(gauge == null ? "missing" : gauge->IsPvP.ToString())}"
            + $" addon={addon != null} visible={addon != null && addon->IsVisible}"
            + $" arraySize={(numbers == null ? -1 : numbers->Size)}"
            + $" subscribers={(numbers == null ? -1 : numbers->SubscribedAddonsCount)}"
            + $" update={(numbers == null ? -1 : numbers->UpdateState)}"
            + $" generalStatus={generalStatus} action={action} actionStatus={actionStatus} ready={runtime.IsAvailable}");
    }

    private void ReassertCombo(Dalamud.Plugin.Services.IFramework _)
    {
        try
        {
            var game = Plugin.GameInstance;
            if (game == null) return;
            var now = Environment.TickCount64;
            var delta = MathF.Max(0f, (now - lastUpdateMilliseconds) / 1000f);
            lastUpdateMilliseconds = now;
            var inSim = game.World.Map.IsInInstance;
            limitBreakGauge.Update(inSim && game.HasActivePractice &&
                (game.World.LimitBreaks != null || TankLimitBreak.IsTank((byte)Plugin.PlayerState.ClassJob.RowId)),
                game.ScenarioDispatchGeneration, game.World.LimitBreaks?.HasGaugeCharge);
            ObserveSoloLimitBreak(game, now);
            var amx = ActionManager.Instance();
            if (inSim != wasInSim)
            {
                if (inSim)
                {
                    if (!recastSnapshotTaken && amx != null)
                        SnapshotRecasts(amx);
                }
                else
                {
                    if (amx != null) RestoreRecasts(amx);
                    FlushRejectedPresses();
                }
                wasInSim = inSim;
            }
            else if (!inSim && recastSnapshotTaken && amx != null)
            {
                // Zone unload can briefly leave ActionManager unavailable; retry
                // the one-shot real-world restore without taking a new snapshot.
                RestoreRecasts(amx);
            }
            var generation = game.ScenarioDispatchGeneration;
            if (generation != schedulerGeneration || !inSim || !game.HasActivePractice)
            {
                localEvents.Clear();
                ResetLocalState();
                castingAction = awaitingAction = null;
                if (LocalJobResources.Active) LocalJobResources.End();
                if (inSim && game.HasActivePractice)
                    LocalJobResources.Begin((byte)Plugin.PlayerState.ClassJob.RowId,
                        (byte)Plugin.PlayerState.EffectiveLevel);
                schedulerGeneration = generation;
                if (!inSim || !game.HasActivePractice) return;
            }
            if (!LocalJobResources.Active)
                LocalJobResources.Begin((byte)Plugin.PlayerState.ClassJob.RowId,
                    (byte)Plugin.PlayerState.EffectiveLevel);
            if (!LocalJobResources.OwnsPlayer) return;
            LocalJobResources.Flush();
            if (game.Paused)
            {
                if (castingAction is { } pausedCast)
                    castingAction = pausedCast with { StartedAt = pausedCast.StartedAt + (long)(delta * 1000f) };
                return;
            }

            Jobs.JobRules.TickLocalGauge(LocalJobResources.ClassJob, delta, LocalJobResources.Level);
            LocalJobResources.CaptureGauge();
            LocalJobResources.Tick(delta, JobRules.AllowsNaturalManaRecovery(LocalJobResources.ClassJob));
            comboAge = MathF.Min(30f, comboAge + delta);
            if (comboAge >= 30f) shadowCombo = CurrentCombo = 0;
            localEvents.Tick(delta);

            var am = ActionManager.Instance();
            if (am == null) return;
            UpdateOrdinaryCast(am, now);
            // 序號對齊**不放每幀**：技能佇列靠 used≠handled 判斷「上一發還沒確認」，
            // 每幀抹平會把排隊中的下一發丟掉。對齊只在合成回包後做一次。
            var want = CurrentComboAge < 30f ? CurrentCombo : 0u;
            if (am->Combo.Action != want)
            {
                am->Combo.Action = want;
                am->Combo.Timer = want == 0 ? 0f : 30f - CurrentComboAge;
            }
            LocalJobResources.Flush();
        }
        catch { /* 每幀路徑，靜默防護 */ }
    }
    private void FlushRejectedPresses()
    {
        foreach (var ((action, status), repeats) in rejectedPresses.Drain())
            CrashTrace.Log($"[循環] 按鍵被拒 adjusted={action} status={status}：上一筆後同原因再 {repeats} 次（未逐筆記）");
    }
    private void ResetLocalState()
    {
        shadowCombo = 0;
        CurrentCombo = 0;
        comboAge = 0f;
        hasExecutionSequence = false;
    }
    public void Dispose()
    {
        Plugin.Framework.Update -= ReassertCombo;
        FlushRejectedPresses();
        castingAction = awaitingAction = null;
        LocalJobResources.End();
        if (ReferenceEquals(Instance, this)) Instance = null;
        localEvents.Clear();
        limitBreakGauge.Restore();
        hook?.Disable();
        hook?.Dispose();
    }
}
