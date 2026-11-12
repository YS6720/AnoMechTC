using System.Collections.Generic;
using System.Runtime.InteropServices;
using AnoMech.Core.Native.Interfaces;
using AnoMech.Pointers;

namespace AnoMech.Compat.Server;

/// <summary>
/// TC 伺服器層：台服 binary 專屬的原生簽章與位移（國際版見 Compat/Server/Global）。
/// 兩服共用的 sig 留在 Pointers/*，只有兩服不同的才放這裡。
/// </summary>
internal static class ServerNative
{
    // 台服 LoadZone（.text+0x5D18F0）。
    //
    // ⚠️ GameMain.LoadZone **不存在於 FFXIVClientStructs**（CS 的 GameMain 只有
    // Instance/ExecuteCommand 等），是上游手刻的 sig ⇒ 沒有權威來源可交叉驗證。
    //
    // 2026-08-19 實機教訓：先前「按序言反推」找到 .text+0x1121EE0，唯一命中但**是錯的
    // 函式**——解碼其開頭發現 `49 8D 48 20`（lea rcx,[r8+0x20]）把第 3 參數當**指標**
    // 解參考，而我方傳 0 ⇒ null deref ⇒ client 當場崩潰。教訓：手刻 sig 的尾段錨在
    // `48 8B 05 …48 33 C4` 是 stack canary 的通用樣式，「唯一命中」只代表那組
    // register-push 唯一，**不代表找對函式**。
    //
    // 正解＝比對「行為特徵」而非序言：本機有國際服 client，上游 sig 在其中唯一命中
    // (.text+0x6025C0)，dump 該函式得參數搬運模式
    //     mov r?,rcx / movzx r?d,r9b / mov ecx,edx / mov r?d,r8d / mov ebp,edx
    // ——即「第3參數當 DWORD、第4參數當 byte」，與 LoadZoneDelegate 相符。
    // 拿這段（暫存器放寬）掃台服 ⇒ 唯一命中 .text+0x5D18F0，其後控制流
    // (call / test rax,rax / jz / mov ecx,ebp / test ebp,ebp) 與國際服逐項對齊。
    // 台服僅暫存器分配與 push 組合不同（少 56、多 41 55），故按序言掃必然掃不到。
    //
    // sig 必須包含參數搬運段：只用序言在台服有 3 處命中。
    internal const string LoadZoneSignature = "40 55 41 54 41 55 41 56 41 57 48 83 EC 60 4C 8B F1 45 0F B6";

    // 台服 ProcessDirectorUpdate（.text+0xB69260）。**參數個數與國際服不同：7 個，不是 9 個。**
    //
    // 定位手法＝xref 反查（前兩次靠「序言相似」與「唯一命中」都掃到錯的函式，已廢棄）：
    //   1. 國際服真函式（.text+0xB7BDE0）＝拿 CS 的 ProcessDirectorUpdate sig 唯一命中；
    //      解其第一個 call → 0xB84020，與 CS 的 GetEventHandlerById call-site sig 解出的
    //      目標完全相同 ⇒ 函式本體＝ handler = GetEventHandlerById(this, eventId)，
    //      再呼叫 handler->vtable[+0x1E8]。
    //   2. 兩服各自列出「所有呼叫 GetEventHandlerById 的函式」，兩份清單逐項對齊
    //      （前鄰居 0xB7A1C0↔0xB67690 大小 0x390／vtable+0x168 完全相同；
    //        0xB7B8C0↔0xB68D40 大小 0x223）⇒ 目標對到台服 0xB69260。
    //   3. 反組譯確認：+0x10 的 call 目標＝0xB71020＝台服 GetEventHandlerById，
    //      結尾 `41 FF 92 E0 01 00 00`＝call [r10+0x1E0]（國際服為 +0x1E8）。
    //
    // 參數差異（實測）：國際服讀 [rsp+0x90/98/A0/A8/B0]＝5 個堆疊參數 ⇒ 共 9 參數；
    // 台服只讀 [rsp+0x60/68/70]＝3 個 ⇒ 共 **7 參數**（this, eventId, category, arg1..arg4）。
    // 上游的 9 參數 delegate 在台服本來就是錯的。
    internal const string ProcessDirectorUpdateSignature = "48 89 5C 24 08 57 48 83 EC 30 41 8B D9 41 8B F8 E8 ?? ?? ?? ?? 48 85 C0 74 ?? 8B 4C 24 70 44 8B C3 4C 8B 10";

    /// <summary>掃不到即拋（與原本 [Signature] 屬性路徑同樣在載入時失敗）。</summary>
    internal static EventFrameworkPointers.ProcessDirectorUpdateDelegate ResolveProcessDirectorUpdate()
    {
        var address = Plugin.SigScanner.ScanText(ProcessDirectorUpdateSignature);
        // 把解析到的位址留在追蹤檔，實機即可核對是否＝module+0xB69260（離線驗證的位址）。
        // 手刻 sig 沒有權威來源可交叉驗證，這是唯一能事後確認「掃對了沒」的線索。
        try
        {
            var baseAddr = System.Diagnostics.Process.GetCurrentProcess().MainModule!.BaseAddress;
            AnoMech.Core.CrashTrace.Log($"[sig] ProcessDirectorUpdate = module+0x{(long)address - (long)baseAddr:X}（預期 0xB69260）");
        }
        catch (System.Exception ex)
        {
            AnoMech.Core.CrashTrace.Log($"[sig] ProcessDirectorUpdate 位址取得失敗：{ex.Message}");
        }
        return Marshal.GetDelegateForFunctionPointer<EventFrameworkPointers.ProcessDirectorUpdateDelegate>(address);
    }

    // ── 國際服原生能力的台服對應（上游 1646a70 經國際服 sig 做，B8 只填國際服）──────────
    // 台服 binary 沒有這些 handler，也不新增台服 sig：同簽章空實作／回傳 false。
    // 呼叫端在 UMAD（台服 Compile Remove，不可達）；共用路徑的解除呼叫（ForcedFollow(null)）是 no-op。
    // 已警告過的項目：每個只警告一次，免得逐幀洗版。
    private static bool forcedFollowWarned;
    private static bool mapEffectSnapshotWarned;

    // EffectResult：讓 client 套用 ActionEffect 中該目標的傷害條目（含飛字）。本批不送，傷害條目不顯示飛字。
    internal static void ReceiveEffectResult(AnoMech.Core.SimObjects.SimCharacter target, uint globalSequence, byte targetIndex)
    {
    }

    // 本機玩家的原生 forced follow（混亂類）；本批不支援，玩家不會被拖動。null＝解除。
    internal static void ForcedFollow(uint? targetEntityId)
    {
        if (targetEntityId is null || forcedFollowWarned) return;
        forcedFollowWarned = true;
        AnoMech.Core.DiagnosticLog.Warn("[ServerNative] ForcedFollow 本版尚未支援，玩家不會被強制追隨。");
    }

    // 副本 MapEffect 全槽快照（DutyInitData.MapEffectSnapshot）；本批不送，回 true 表示不需重試。
    internal static bool ApplyMapEffectSnapshot(ushort[] states, bool zoneIn)
    {
        if (!mapEffectSnapshotWarned)
        {
            mapEffectSnapshotWarned = true;
            AnoMech.Core.DiagnosticLog.Warn("[ServerNative] ApplyMapEffectSnapshot 本版尚未支援，場地 MapEffect 快照不送出。");
        }
        return true;
    }

    // 台服 PacketDispatcher 不額外解析任何 handler。
    internal static void InitializePacketHandlers()
    {
    }

    internal static void StopForcedMove()
    {
    }

    internal static void ReceiveStatusList(AnoMech.Core.SimObjects.SimCharacter target, IReadOnlyList<StatusSlot> statuses, uint health, uint mana, byte shield)
    {
    }

    internal static void PreloadActionTimelines(IEnumerable<(ushort Id, string Key)> rows, string tag)
    {
    }

    internal static bool ControlAffliction => false;

    // D788（國際服專屬）：台服 Compile Remove 掉 UMAD，不會有本機玩家帶混亂狀態；共用 SimPlayer.Tick
    // 每幀以 false 呼叫，所以是空實作而不是拒絕。不新增台服 sig。
    internal static void SyncControlAffliction(bool wanted)
    {
    }

    internal static void ReleaseControlAffliction()
    {
    }

    // 墜落接住只在國際服做：台服沒有 PlayerStateFlags sig，墜落用的 ActorControl 0xED／0x107 也沒在
    // 台服核對過，不送未驗證的封包。MapController 共用碼引用 EndClientLoad／PlaceLocalPlayer，故留同簽章空實作。
    internal static bool HandlesArenaFalls => false;

    internal static void EndClientLoad()
    {
    }

    internal static void PlaceLocalPlayer(System.Numerics.Vector3 position)
    {
    }

}
