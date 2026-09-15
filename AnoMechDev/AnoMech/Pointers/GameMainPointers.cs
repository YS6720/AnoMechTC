using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AnoMech.Pointers;

internal unsafe class GameMainPointers
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
    [Signature("40 55 41 54 41 55 41 56 41 57 48 83 EC 60 4C 8B F1 45 0F B6", UseFlags = SignatureUseFlags.Pointer, ScanType = ScanType.Text)]
    public static LoadZoneDelegate LoadZone = null!;

    public delegate void LoadZoneDelegate(GameMain* thisPtr, uint territoryTypeId, uint transitionTerritoryFilterKey, byte a4, byte a5);

    public static void Initialize()
    {
        Plugin.GameInterop.InitializeFromAttributes(new GameMainPointers());
        // 把解析到的位址留在追蹤檔：LoadZone 是手刻 sig（CS 無權威可對），
        // 萬一 native 端崩潰（try/catch 攔不到）時，這是唯一能事後回推「掃到哪」的線索。
        try
        {
            var addr = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(LoadZone);
            var baseAddr = System.Diagnostics.Process.GetCurrentProcess().MainModule!.BaseAddress;
            AnoMech.Core.CrashTrace.Log($"[sig] LoadZone = {addr:X}（module+0x{(long)addr - (long)baseAddr:X}）");
        }
        catch (System.Exception ex)
        {
            AnoMech.Core.CrashTrace.Log($"[sig] LoadZone 位址取得失敗：{ex.Message}");
        }
    }
}
