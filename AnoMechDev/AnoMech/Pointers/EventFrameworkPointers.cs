using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace AnoMech.Pointers;

internal unsafe class EventFrameworkPointers
{
    [Signature("E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? E8 ?? ?? ?? ?? 8B 54 24 70 48 8B C8 E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? E8 ?? ?? ?? ?? 0F", UseFlags = SignatureUseFlags.Pointer, ScanType = ScanType.Text)]
    public static InitDirectorDelegate InitDirector { get; private set; } = null!;

    [Signature("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 70 48 8D B1", UseFlags = SignatureUseFlags.Pointer, ScanType = ScanType.Text)]
    public static TerminateDirectorDelegate TerminateDirector { get; private set; } = null!;

    [Signature("89 54 24 10 48 89 4C 24 ?? 53 56 57 41 55 41 57 48 83 EC 30 48 8B 99", UseFlags = SignatureUseFlags.Pointer, ScanType = ScanType.Text)]
    public static SetDirectorDataDelegate SetDirectorData { get; private set; } = null!;

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
    [Signature("48 89 5C 24 08 57 48 83 EC 30 41 8B D9 41 8B F8 E8 ?? ?? ?? ?? 48 85 C0 74 ?? 8B 4C 24 70 44 8B C3 4C 8B 10", UseFlags = SignatureUseFlags.Pointer, ScanType = ScanType.Text)]
    public static ProcessDirectorUpdateDelegate ProcessDirectorUpdate { get; private set; } = null!;

    // 台服為 7 參數（見上方 sig 註解）——不要照國際服 / 上游補回 arg5/arg6。
    public delegate void ProcessDirectorUpdateDelegate(
        EventFramework* thisPtr, EventId eventId, uint category,
        uint arg1, uint arg2, uint arg3, uint arg4);
    public delegate void InitDirectorDelegate(EventFramework* thisPtr, EventId eventId, uint contentId, uint flags);
    public delegate void TerminateDirectorDelegate(EventFramework* thisPtr, EventId eventId);
    public delegate void SetDirectorDataDelegate(EventFramework* thisPtr, EventId eventId, byte sequence, byte unknown, byte* unionDataBuffer, ulong length);

    public static void Initialize()
    {
        Plugin.GameInterop.InitializeFromAttributes(new EventFrameworkPointers());
        // 把解析到的位址留在追蹤檔，實機即可核對是否＝module+0xB69260（離線驗證的位址）。
        // 手刻 sig 沒有權威來源可交叉驗證，這是唯一能事後確認「掃對了沒」的線索。
        try
        {
            var addr = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(ProcessDirectorUpdate);
            var baseAddr = System.Diagnostics.Process.GetCurrentProcess().MainModule!.BaseAddress;
            AnoMech.Core.CrashTrace.Log($"[sig] ProcessDirectorUpdate = module+0x{(long)addr - (long)baseAddr:X}（預期 0xB69260）");
        }
        catch (System.Exception ex)
        {
            AnoMech.Core.CrashTrace.Log($"[sig] ProcessDirectorUpdate 位址取得失敗：{ex.Message}");
        }
    }
}
