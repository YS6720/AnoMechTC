using System;
using System.Runtime.InteropServices;
using AnoMech.Pointers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace AnoMech.Core.Native;

// Status helpers for sim-only buffs. Apply writes directly into the
// StatusManager.Status array — bypasses bc->StatusManager.AddStatus, which
// drives _flags / ExtraFlags from the Status sheet AND auto-prunes any status
// whose sheet MaxDuration is 0 (most sim-only ids). Direct-slot insertion lets
// any statusId stick at the duration we ask for, with no sheet dependency.
//
// Trade-off: _flags / ExtraFlags don't get the sheet-driven bit set. That bit
// vector controls "is this entity stunned/silenced/etc" gameplay flags; for
// purely visual debuffs (tether markers, raid buffs we just want shown) this
// doesn't matter. If a future caller needs gameplay-effective flags, route
// that one through bc->StatusManager.AddStatus instead.
internal static unsafe class Statuses
{

    public static void Apply(
        Character* chara,
        ushort statusId,
        float duration,
        ushort param = 0,
        GameObjectId? sourceObject = null)
    {
        if (chara == null || statusId == 0) return;
        var bc = (BattleChara*)chara;
        var slots = bc->StatusManager.Status;

        // A missing source is the legacy wildcard: preserve the old ID-only
        // match for unrelated callers. SimStatus always passes an explicit
        // GameObjectId (including default/source 0) for exact ownership.
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].StatusId != statusId) continue;
            if (sourceObject is {} source && slots[i].SourceObject != source) continue;
            slots[i].Param = param;
            slots[i].RemainingTime = duration == 0 ? 20 : duration;
            if (sourceObject is {} explicitSource)
                slots[i].SourceObject = explicitSource;
            return;
        }

        // Otherwise drop into the first empty slot. Keep the array packed at
        // low indices — see Remove's comment for why that matters to PartyHud.
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].StatusId != 0) continue;
            slots[i].StatusId = statusId;
            slots[i].Param = param;
            slots[i].RemainingTime = duration == 0 ? 20 : duration;
            slots[i].SourceObject = sourceObject.GetValueOrDefault();
            if (bc->StatusManager.NumValidStatuses <= i)
                bc->StatusManager.NumValidStatuses = (byte)(i + 1);
            return;
        }
    }

    /// <summary>
    /// 原生狀態槽裡還有沒有這一筆（依 id＋來源精確比對）。
    ///
    /// 用途是判斷「遊戲把它拿掉了」：武裝戌守之類的狀態由遊戲決定存活條件（移動即取消），
    /// 模擬層只是鏡像。少了這個查詢，鏡像會在下一幀把狀態寫回去——維護者 2026-09-15 實機
    /// 回報「移動取消後打一個 GCD，翅膀又出現」。
    /// </summary>
    public static bool Has(Character* chara, ushort statusId, GameObjectId sourceObject)
    {
        if (chara == null || statusId == 0) return false;
        var slots = ((BattleChara*)chara)->StatusManager.Status;
        for (int i = 0; i < slots.Length; i++)
            if (slots[i].StatusId == statusId && slots[i].SourceObject == sourceObject)
                return true;
        return false;
    }

    // A managed status is admitted only when its exact native source slot is
    // already present or an empty native slot can hold it. This is deliberately
    // a refusal, not a destructive attempt to evict another status.
    public static bool HasCapacity(Character* chara, ushort statusId, GameObjectId sourceObject)
    {
        if (chara == null || statusId == 0) return false;
        var slots = ((BattleChara*)chara)->StatusManager.Status;
        var hasEmptySlot = false;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].StatusId == statusId && slots[i].SourceObject == sourceObject)
                return true;
            if (slots[i].StatusId == 0) hasEmptySlot = true;
        }
        return hasEmptySlot;
    }

    // Applies a status through the engine's native StatusManager::AddStatus.
    // Triggers the sheet-driven side effects our direct-slot Apply skips:
    // _flags / ExtraFlags bits, StatusLoopVFX, and (most importantly for our
    // boss form swaps) the Param → CharacterData.TransformationId derivation
    // that runs inline inside the engine's apply path. Use for real game
    // statuses like Superfluid / OmegaM / OmegaF where that visual is wanted.
    //
    // Caveats:
    // - Duration comes from the sheet's MaxDuration. Sim-only ids with
    //   MaxDuration=0 will be auto-pruned by the engine immediately, so this
    //   helper is only safe for real game statuses today.
    // - All sheet-driven side effects fire (StatusLoopVFX, Flags etc.). For
    //   the transformation buffs that's exactly the canonical visual; for
    //   purely-visual sim debuffs you still want plain Apply.
    /// <param name="refreshDuration">>0＝掛上（或已存在）後把 RemainingTime 釘成此值。
    /// 「預備」類狀態 sheet MaxDuration=0（真實時長由伺服器下發）——native AddStatus
    /// 會以 0 秒入格、下一 tick 被引擎剔除（2026-08-22 贖罪鏈實證：3827 入格 dur=0.0
    /// 即消失）。變身類（Superfluid 等 0=永久）不要傳，維持 0。</param>
    public static void AddStatusInit(
        Character* chara,
        ushort statusId,
        ushort param,
        float refreshDuration = 0f,
        bool trace = false,
        GameObjectId? sourceObject = null)
    {
        if (chara == null || statusId == 0) return;
        var bc = (BattleChara*)chara;
        var slots = bc->StatusManager.Status;
        var hasSameId = false;

        // Refresh an exact source match without replaying the native gain path.
        // A missing source keeps the legacy ID-only wildcard behavior.
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].StatusId != statusId) continue;
            hasSameId = true;
            if (sourceObject is {} source && slots[i].SourceObject != source) continue;
            if (refreshDuration != 0f &&
                (sourceObject is {} ||
                 refreshDuration < 0f ||
                 MathF.Abs(slots[i].RemainingTime) < MathF.Abs(refreshDuration)))
                slots[i].RemainingTime = refreshDuration;
            if (param != 0) slots[i].Param = param;
            if (trace)
                Core.CrashTrace.Log($"[狀態] {statusId} native 取走 slot={i} dur→{slots[i].RemainingTime:F1}");
            return;
        }

        // StatusManager.AddStatus has no source-object parameter. With no
        // existing ID it is safe to use the engine gain path, then retag the
        // sole new slot to the requested source. If another source already owns
        // this ID, fall through to the established direct-slot + OnGain path so
        // AddStatus cannot refresh the wrong caster's copy.
        //
        // ⚠️ 玩家本人不走這條（2026-09-15，維護者 實機四輪定位）：對本地玩家呼叫原生
        // AddStatus 會點亮 sheet 的 StatusLoopVFX（武裝戌守翅膀），**卻登記不了狀態**
        // ——實測日誌「gain 後 id=1175 共 0 份」。特效因此從出生就是孤兒：沒有任何狀態
        // 可供移除，改幾次移除函式都沒用，每放一次技能就再閃一次。
        // 敵人／場景演員維持原路徑：Boss 變身要靠 AddStatus 推導 TransformationId。
        var isLocalPlayer = Plugin.ObjectTable.LocalPlayer is { } lp && (nint)chara == lp.Address;
        if (!hasSameId && !isLocalPlayer)
        {
            bc->StatusManager.AddStatus(statusId, param);
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i].StatusId != statusId) continue;
                if (sourceObject is {} source)
                    slots[i].SourceObject = source;
                if (refreshDuration != 0f &&
                    (sourceObject is {} ||
                     refreshDuration < 0f ||
                     MathF.Abs(slots[i].RemainingTime) < MathF.Abs(refreshDuration)))
                    slots[i].RemainingTime = refreshDuration;
                if (param != 0) slots[i].Param = param;
                if (trace)
                    Core.CrashTrace.Log($"[狀態] {statusId} native 取走 slot={i} dur→{slots[i].RemainingTime:F1}");
                return;
            }
        }

        Apply(chara, statusId, refreshDuration, param, sourceObject);
        // 玩家本人：不走 AddStatus（登記不了、只留孤兒特效），但槽位寫好後仍要 OnGainStatus——
        // 那是唯一實證會點亮 StatusLoopVFX 的路徑（9/15 翅膀就是它亮的）。9/16 兩條替代路
        // 都實測不亮：SetStatus(refreshFlags: true)、直寫 CharacterData.StatusLoopVfxId。
        // 特效現在綁在真實存在的狀態上；能否被 Remove 的 SetStatus(…, refreshFlags: true)
        // 收掉，以維護者實機為準。
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].StatusId != statusId) continue;
            if (sourceObject is {} source && slots[i].SourceObject != source) continue;
            // 直寫槽位跳過了 sheet 驅動的旗標計算；連段類判定（明鏡止水讓月光／花車／雪風亮）
            // 看的是那些旗標而不是槽位本身——資料表裡這些戰技沒有 ActionProcStatus
            // （2026-09-16 實測 proc=0），只有騎士贖罪劍那類才靠狀態發亮。
            // SetStatus(refreshFlags: true) 是遊戲自己「改這一格並重算旗標」的函式（不點特效，
            // 9/16 實測）；特效仍由下面的 OnGainStatus 點。
            if (isLocalPlayer)
                bc->StatusManager.SetStatus(i, statusId, slots[i].RemainingTime, slots[i].Param, slots[i].SourceObject, true);
            StatusManagerPointers.OnGainStatus(
                &bc->StatusManager,
                statusId,
                refreshDuration,
                param,
                0,
                0);
            if (isLocalPlayer)
                Core.CrashTrace.Log($"[狀態] 本地玩家 gain {statusId} slot={i} dur={slots[i].RemainingTime:F1}");
            else if (trace)
                Core.CrashTrace.Log($"[狀態] {statusId} fallback Apply+OnGain 即讀={slots[i].RemainingTime:F1}s");
            return;
        }
    }
    public static ushort GetParam(
        Character* chara,
        ushort statusId,
        GameObjectId? sourceObject = null)
    {
        if (chara == null) return 0;
        var bc = (BattleChara*)chara;
        var slots = bc->StatusManager.Status;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].StatusId != statusId) continue;
            if (sourceObject is {} source && slots[i].SourceObject != source) continue;
            return slots[i].Param;
        }
        return 0;
    }

    public static float GetRemaining(
        Character* chara,
        ushort statusId,
        GameObjectId? sourceObject = null)
    {
        if (chara == null) return 0f;
        var bc = (BattleChara*)chara;
        var slots = bc->StatusManager.Status;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].StatusId != statusId) continue;
            if (sourceObject is {} source && slots[i].SourceObject != source) continue;
            return slots[i].RemainingTime;
        }
        return 0f;
    }

    public static void Remove(
        Character* chara,
        ushort statusId,
        GameObjectId? sourceObject = null)
    {
        if (chara == null || statusId == 0) return;
        var bc = (BattleChara*)chara;
        var slots = bc->StatusManager.Status;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].StatusId != statusId) continue;
            if (sourceObject is {} source && slots[i].SourceObject != source) continue;
            // 移除方式試過兩種（維護者 實機為準）：
            //   ① RemoveStatus(i)      → 清得掉資料，翅膀留下
            //   ② RemoveStatus(i, 1)   → 同上，旗標 1 不是「跑失去處理」的意思
            // 這裡改走 SetStatus：那是遊戲自己變更狀態槽用的函式，最後一個參數
            // refreshFlags=true 會重跑 sheet 驅動的副作用（含 StatusLoopVFX 收尾）。
            // 把 statusId 設成 0 等於宣告這一格空了。
            bc->StatusManager.SetStatus(i, 0, 0f, 0, default, true);
            // SetStatus 沒清乾淨就補一次直接移除，寧可留孤兒特效也不能留住狀態本身。
            if (slots[i].StatusId == statusId)
                bc->StatusManager.RemoveStatus(i, 1);
            if (Plugin.ObjectTable.LocalPlayer is { } lp && (nint)chara == lp.Address)
                Core.CrashTrace.Log($"[狀態] 本地玩家 remove {statusId} slot={i} 之後={slots[i].StatusId}");
            return;
        }
    }

}
