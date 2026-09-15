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
    private int diagLeft = 12;
    private long schedulerGeneration;
    private long lastUpdateMilliseconds;

    // ActionCombo 反查：有哪些 action 以 X 為前置（＝用了 X 之後連段燈該亮）。
    private readonly HashSet<uint> startsCombo = new();
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
        try
        {
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
            if (ret == 0 && actionId is 16460 or 36918 or 36919 or 16459 or 25748 or 25749 or 25750)
                CrashTrace.Log($"[循環] 按鍵被拒 a={actionId}（adjusted={am->GetAdjustedActionId(actionId)}）");
            if (ret != 0 && actionType == ActionType.Action && canProcess && !areaTargeted)
                OnActionUsed(am, actionId, targetId);
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

        // 排隊受理 ≠ 實際施放——所有記帳延到 GCD 轉完才提交。
        var fireDelay = 0.25f;
        var grp = am->GetRecastGroup((int)ActionType.Action, actionId);
        var rd = am->GetRecastGroupDetail(grp);
        if (rd != null && rd->IsActive && rd->Elapsed > 0.2f)
            fireDelay = MathF.Max(0.25f, rd->Total - rd->Elapsed + 0.1f);

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
            // Local bookkeeping is complete before the host-authoritative submit.
            capturedGame.SubmitAbility(fireId, capturedClassJob, capturedLevel, capturedTargetId);
        });

        // 招式自身冷卻（瀝血劍 60s 這類）真環境由伺服器確認後啟動——模擬區沒確認，
        // 0.2s 後查該招 recast 沒轉就補踢。
        localEvents.Add(0.2f, () =>
        {
            var am2 = ActionManager.Instance();
            if (am2 == null) return;
            var group = am2->GetRecastGroup((int)ActionType.Action, fireId);
            var detail = am2->GetRecastGroupDetail(group);
            if (detail != null && !detail->IsActive)
            {
                am2->StartCooldown(ActionType.Action, fireId);
                CrashTrace.Log($"[循環] 補踢冷卻 a={fireId} grp={group}");
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
            var generation = game.ScenarioDispatchGeneration;
            if (generation != schedulerGeneration || !inSim || !game.HasActivePractice)
            {
                localEvents.Clear();
                ResetLocalState();
                schedulerGeneration = generation;
                if (!inSim || !game.HasActivePractice) return;
            }
            if (game.Paused) return;

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
        hook?.Disable();
        hook?.Dispose();
    }
}
