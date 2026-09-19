using System;
using System.Collections.Generic;
using AnoMech.Core.Game;
using AnoMech.Core.Native;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

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
    private long schedulerGeneration;
    private long lastUpdateMilliseconds;

    // ActionCombo 反查：有哪些 action 以 X 為前置（＝用了 X 之後連段燈該亮）。
    private readonly HashSet<uint> startsCombo = new();
    // 戰技（3）與魔法（2）：客戶端可在 GCD 轉動中先受理、GCD 到點才施放。能力技沒有這回事。
    private readonly HashSet<uint> gcdActions = new();
    // X 自己的前置（0＝無）。
    private readonly Dictionary<uint, uint> prereqOf = new();

    public RotationSim()
    {
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
        try
        {
            var practice = Plugin.GameInstance;
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
            // Original is intentionally called exactly once. A failed native call
            // is rejected rather than retried, which could submit a duplicate cast.
            ret = hook!.Original(am, actionType, actionId, targetId, extraParam, mode, comboRouteId, outOpt);
        }
        catch (Exception ex)
        {
            CrashTrace.Log($"[循環] UseAction 原函式失敗（拒絕本次）：{ex.Message}");
            return 0;
        }
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
            // 客戶端拒絕的按鍵（ret=0）全部記下原因碼：GetActionStatus 回 LogMessage id
            // （0＝可用；例如 572 量譜不足、1122 前置條件不符）。以前只記騎士幾個 id，
            // 2026-09-16 武士的明鏡止水被拒時什麼都看不到。
            if (ret == 0 && actionType == ActionType.Action && inSim)
            {
                var adjusted = am->GetAdjustedActionId(actionId);
                var status = am->GetActionStatus(ActionType.Action, adjusted);
                var text = status == 0 ? "" : Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.LogMessage>()?.GetRowOrDefault(status)?.Text.ExtractText() ?? "";
                CrashTrace.Log($"[循環] 按鍵被拒 a={actionId} adjusted={adjusted} status={status} {text}");
            }
            if (ret != 0 && actionType == ActionType.Action && canProcess && !areaTargeted)
                OnActionUsed(am, actionId, targetId);
            else if (ret != 0 && generalTankLimitBreak && canProcess && !areaTargeted)
                OnActionUsed(am, statusActionId, 0);
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
    private uint lastFired;
    private float lastFiredAt;
    private ushort lastFiredSeq = ushort.MaxValue;

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

    private void OnActionUsed(ActionManager* am, uint actionId, ulong targetId)
    {
        // 一律轉成升級後 id 再處理（2026-08-22：按王權劍時 client 連發兩次 UseAction——
        // 基底 21＋升級 3539——先處理到 21 會把連段判走位、再多清一次；統一到升級 id
        // 之後重複那發被下面的去重自然吃掉）。
        actionId = am->GetAdjustedActionId(actionId);
        var now = Environment.TickCount64 / 1000f;
        var sequence = am->LastUsedActionSequence;
        if (sequence != lastFiredSeq && Mode > 0)
        {
            lastFiredSeq = sequence;
            var confirmId = Mode == 1 ? 0u : actionId;
            localEvents.Add(0.1f, () => FirePlayerActionEffect(confirmId, sequence));
        }
        // 去重窗 2.3s（< GCD 2.5）：排隊動作會走兩次 UseAction（受理＋GCD 到點執行），
        // 兩次可相距 >1s，舊窗 1s 會重複記帳。
        if (actionId == lastFired && now - lastFiredAt < 2.3f) return;
        lastFired = actionId;
        lastFiredAt = now;

        // 排隊受理 ≠ 實際施放——戰技／魔法的記帳延到 GCD 轉完才提交。
        // 只看 GCD 類：能力技被客戶端受理（ret=1）就是立刻施放；有充能的能力技（明鏡止水兩層）
        // 只要一層在冷卻 recast group 就 IsActive、Total 是整段（110 秒），照舊算會把開火排到
        // 幾十秒後、場次換代就被清掉——2026-09-16 維護者實測明鏡止水「沒生效」就是這條。
        var fireDelay = 0.25f;
        if (gcdActions.Contains(actionId))
        {
            var grp = am->GetRecastGroup((int)ActionType.Action, actionId);
            var rd = am->GetRecastGroupDetail(grp);
            if (rd != null && rd->IsActive && rd->Elapsed > 0.2f)
                fireDelay = MathF.Max(0.25f, rd->Total - rd->Elapsed + 0.1f);
        }

        var fireId = actionId;
        var capturedGame = Plugin.GameInstance;
        var capturedGeneration = capturedGame?.ScenarioDispatchGeneration ?? -1L;
        var capturedTargetId = targetId;
        var hasNativeTarget = targetId != 0UL && targetId != 0xE0000000UL;
        var capturedTarget = hasNativeTarget ? capturedGame?.ResolveAbilityActor(targetId) : null;
        var capturedClassJob = (byte)Plugin.PlayerState.ClassJob.RowId;
        var capturedLevel = (byte)Plugin.PlayerState.EffectiveLevel;
        localEvents.Add(fireDelay, () =>
        {
            if (capturedGame is null ||
                !ReferenceEquals(Plugin.GameInstance, capturedGame) ||
                capturedGame.ScenarioDispatchGeneration != capturedGeneration)
                return;
            if (hasNativeTarget &&
                (capturedTarget is not { IsActive: true } ||
                 !ReferenceEquals(capturedGame.ResolveAbilityActor(capturedTargetId), capturedTarget)))
                return;

            var pre = prereqOf.GetValueOrDefault(fireId);
            var comboOk = pre == 0 || (shadowCombo == pre && comboAge < 30f);
            if (comboOk && startsCombo.Contains(fireId))
            {
                shadowCombo = fireId;
                comboAge = 0f;
            }
            else if (prereqOf.ContainsKey(fireId) || startsCombo.Contains(fireId))
            {
                shadowCombo = 0;
                comboAge = 30f;
            }
            CurrentCombo = shadowCombo;
            CrashTrace.Log($"[循環] a={fireId} comboOk={comboOk} shadow={shadowCombo} delay={fireDelay:F1}");
            // 職業量譜只寫自己的客戶端記憶體（房主或成員都一樣），不進網路。
            Jobs.JobRules.OnLocalFire(capturedClassJob, fireId, comboOk);
            // Local bookkeeping is complete before the host-authoritative submit.
            capturedGame.SubmitAbility(fireId, capturedClassJob, capturedLevel, capturedTargetId);
        });

        // 招式自身冷卻（瀝血劍 60s 這類）真環境由伺服器確認後啟動——模擬區沒確認，
        // 0.2s 後查該招 recast 沒轉就補踢。
        // 充能技（明鏡止水 2 層）：客戶端自己的預測會把整組從零起算（elapsed=0 → 0 層），
        // 真實只用掉一層。記住按下當時這組是不是「滿的」，0.2s 後把 Elapsed 推到「剩最後一層在轉」。
        var chargeGroup = am->GetRecastGroup((int)ActionType.Action, fireId);
        var chargeDetail = am->GetRecastGroupDetail(chargeGroup);
        var wasIdleBeforePress = chargeDetail == null || !chargeDetail->IsActive;
        localEvents.Add(0.2f, () =>
        {
            var am2 = ActionManager.Instance();
            if (am2 == null) return;
            var group = am2->GetRecastGroup((int)ActionType.Action, fireId);
            var detail = am2->GetRecastGroupDetail(group);
            if (detail == null) return;
            var charges = ActionManager.GetMaxCharges(fireId, 0);
            if (!detail->IsActive)
            {
                am2->StartCooldown(ActionType.Action, fireId);
                CrashTrace.Log($"[循環] 補踢冷卻 a={fireId} grp={group}");
            }
            if (charges > 1 && wasIdleBeforePress && detail->IsActive && detail->Total > 0f)
            {
                var oneChargeLeft = detail->Total - detail->Total / charges;
                if (detail->Elapsed < oneChargeLeft)
                {
                    detail->Elapsed = oneChargeLeft;
                    CrashTrace.Log($"[循環] 充能修正 a={fireId} charges={charges} elapsed→{detail->Elapsed:F1}/{detail->Total:F1}");
                }
            }
        });
        localEvents.Add(0.6f, () =>
        {
            var am3 = ActionManager.Instance();
            if (am3 != null)
                CrashTrace.Log($"[循環] adjust(16460)={am3->GetAdjustedActionId(16460)} adjust(16459)={am3->GetAdjustedActionId(16459)}");
        });
        localEvents.Add(0.5f, () =>
        {
            var p = Player();
            if (p != null)
                CrashTrace.Log($"[循環] 讀回 2673={Statuses.GetRemaining(p, 2673):F0}s"
                             + $" 1902={Statuses.GetRemaining(p, 1902):F0}s"
                             + $" 3827={Statuses.GetRemaining(p, 3827):F0}s"
                             + $" 3828={Statuses.GetRemaining(p, 3828):F0}s"
                             + $" 3019={Statuses.GetRemaining(p, 3019):F0}s"
                             + $" 1368x{Statuses.GetParam(p, 1368)}");
        });
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

    private static void FirePlayerActionEffect(uint actionId, ushort sourceSequence = 0)
    {
        var lp = Plugin.ObjectTable.LocalPlayer;
        if (lp == null) return;
        var chara = (Character*)lp.Address;
        const uint NullObjectId = 0xE0000000;
        var oid = NullObjectId;
        if (lp.TargetObject is { } t)
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
            CrashTrace.Log($"[循環] 回包後 used={am->LastUsedActionSequence} handled={am->LastHandledActionSequence}"
                         + $" combo={am->Combo.Action}/{am->Combo.Timer:F1}");
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
    private bool wasInSim;

    private void SnapshotRecasts(ActionManager* am)
    {
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
        if (!recastSnapshotTaken) return;
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
                if (!d->IsActive) continue;
                d->IsActive = false; d->Elapsed = 0f;
                restored++;
                continue;
            }
            var elapsed = s.Elapsed + passed;
            if (elapsed >= s.Total) { d->IsActive = false; d->Elapsed = 0f; }
            else { d->IsActive = true; d->ActionId = s.ActionId; d->Elapsed = elapsed; d->Total = s.Total; }
            restored++;
        }
        CrashTrace.Log($"[循環] 離模擬區：冷卻還原 {restored} 組（經過 {passed:F0}s）");
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
                TankLimitBreak.IsTank((byte)Plugin.PlayerState.ClassJob.RowId), game.ScenarioDispatchGeneration);
            if (inSim != wasInSim)
            {
                var amx = ActionManager.Instance();
                if (amx != null)
                {
                    if (inSim) SnapshotRecasts(amx);
                    else RestoreRecasts(amx);
                }
                wasInSim = inSim;
            }
            var generation = game.ScenarioDispatchGeneration;
            if (generation != schedulerGeneration || !inSim || !game.HasActivePractice)
            {
                localEvents.Clear();
                ResetLocalState();
                // 量譜只在模擬區內、新場次開始那一刻歸零。模擬區外這個分支每幀都會走，
                // 在那裡歸零等於把真伺服器的量譜每幀清掉。
                if (inSim && game.HasActivePractice && generation != schedulerGeneration)
                    Jobs.JobRules.ResetLocalGauge();
                schedulerGeneration = generation;
                if (!inSim || !game.HasActivePractice) return;
            }
            if (game.Paused) return;

            Jobs.JobRules.TickLocalGauge((byte)Plugin.PlayerState.ClassJob.RowId, delta);
            comboAge = MathF.Min(30f, comboAge + delta);
            if (comboAge >= 30f) shadowCombo = CurrentCombo = 0;
            localEvents.Tick(delta);

            var am = ActionManager.Instance();
            if (am == null) return;
            // 序號對齊**不放每幀**：技能佇列靠 used≠handled 判斷「上一發還沒確認」，
            // 每幀抹平會把排隊中的下一發丟掉。對齊只在合成回包後做一次。
            var want = CurrentComboAge < 30f ? CurrentCombo : 0u;
            if (am->Combo.Action != want)
            {
                am->Combo.Action = want;
                am->Combo.Timer = want == 0 ? 0f : 30f - CurrentComboAge;
            }
            // MP 維持滿格（模擬區沒有伺服器的 MP 校正與自然回復）。
            var p = Player();
            if (p != null && p->Mana < p->MaxMana)
                p->Mana = p->MaxMana;
        }
        catch { /* 每幀路徑，靜默防護 */ }
    }
    private void ResetLocalState()
    {
        shadowCombo = 0;
        CurrentCombo = 0;
        comboAge = 0f;
        lastFired = 0;
        lastFiredAt = 0f;
        lastFiredSeq = ushort.MaxValue;
    }
    public void Dispose()
    {
        Plugin.Framework.Update -= ReassertCombo;
        localEvents.Clear();
        limitBreakGauge.Restore();
        hook?.Disable();
        hook?.Dispose();
    }
}
