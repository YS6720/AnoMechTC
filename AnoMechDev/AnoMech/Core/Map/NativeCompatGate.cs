namespace AnoMech.Core.Map;

// 「這個 native 呼叫在台服驗證過了嗎」的閘門——擋的是 **crash**，不是洩漏。
//
// 與 FirewallSelfCheck 的分工：
//   FirewallSelfCheck — 擋「封包外洩」（賠帳號）
//   NativeCompatGate  — 擋「呼叫到未驗證的手刻 native 函式」（當場 crash）
//
// 由來（2026-08-19 實機）：按 Start 後 client 直接崩潰，log 停在
// 「Step 4: LoadZone (native)」。事後解碼候選函式開頭：
//     48 8B D9        mov rbx, rcx      ; this
//     49 8D 48 20     lea rcx, [r8+20]  ; 把第 3 參數當**指標**解參考
// 而 AnoMech 的 LoadZoneDelegate 第 3 參數是整數、實際傳 0
// ⇒ 解參考 0x20 ≈ null pointer ⇒ crash。結論：那不是 LoadZone，是長得像的別的函式。
//
// 為什麼只有 LoadZone 會這樣：其餘 30 個 signature 都能拿 FFXIVClientStructs 的
// [MemberFunction] 屬性交叉驗證（sig 由 CS 權威提供），唯獨 **GameMain.LoadZone
// 不存在於 CS**（CS 的 GameMain 只有 Instance/ExecuteCommand 等），是上游自己手刻的。
// 手刻 sig 的尾段錨在 `48 8B 05 …48 33 C4`＝stack canary 的通用樣式，
// 「唯一命中」只代表那組 register-push 唯一，**不代表找對函式**。
// 要確定得用反組譯器看函式本體／xref，或取得台服已驗證的 sig。
internal static class NativeCompatGate
{
    /// <summary>
    /// GameMain.LoadZone 的台服 signature 是否已驗證。
    /// **false 時 Start 會被擋下**——這是刻意的：已知會 crash 的呼叫不該讓使用者踩。
    /// 取得可信 sig（反組譯確認 or 台服已知能動的來源）後改 true 並記 CHANGELOG。
    /// </summary>
    // 2026-08-19 已用「行為特徵比對」定位台服 LoadZone (.text+0x5D18F0)：
    // 拿國際服真 LoadZone 的參數搬運模式（第3參數當 DWORD、第4參數當 byte）掃台服，
    // 唯一命中且後續控制流與國際服逐項對齊。詳見 GameMainPointers.LoadZone 註解。
    // 仍屬「位置＋參數用法已驗證、實機行為待確認」——首次實機請用測試帳號。
    public const bool LoadZoneVerified = true;

    public const string LoadZoneBlockReason =
        "GameMain.LoadZone 的台服 signature 尚未驗證——目前掃到的位址經解碼確認是別的函式"
        + "（它把第 3 參數當指標解參考，而我方傳 0 ⇒ null deref ⇒ client 崩潰）。"
        + "在取得可信的台服 sig 之前，載入模擬區的功能暫時停用。";

    /// <summary>
    /// EventFramework.ProcessDirectorUpdate 的台服 signature 是否已驗證。
    ///
    /// **2026-08-19 已改為 true**：真函式由 xref 反查定位到 .text+0xB69260（詳見
    /// EventFrameworkPointers.ProcessDirectorUpdate 註解），且 delegate 已改為台服的
    /// 7 參數。以下保留當初判定為 false 的證據——這是「唯一命中 ≠ 找對函式」的實證。
    ///
    /// 原本那條 sig 是用「丟開頭 token＋錨在
    /// `48 8B 05 …48 33 C4`（通用 stack canary）」反推的，與 LoadZone 第一次踩雷同一套錯誤手法。
    /// 離線行為比對確認掃到的是**別的函式**：
    ///   國際服真函式（.text+0xB7BDE0）讀 [rsp+0x90/0x98/0xA0/0xA8/0xB0]＝arg2–arg6，
    ///     組成 7 個 dword 的結構後呼叫 director 的 vtable+0x1E8；
    ///   台服候選（.text+0x1EFBCD0）**完全不讀任何堆疊參數**，只用 4 個暫存器參數，
    ///     且做 `cmp r9d,2` ＋ `lea rdx,[rip+…]` ＋ `mov edx,0x6F464673`（fourcc 常數）。
    /// 我方 delegate 會推 9 個參數過去 ⇒ 對面按自己的簽名解讀堆疊 ⇒ 實機當場崩潰
    /// （2026-08-19 追蹤檔停在 `Entered territory 1122.`，下一步正是 Commence()）。
    ///
    /// 取得可信 sig 前，所有 DirectorUpdate 重放一律 no-op（副本仍可載入、機制仍可跑，
    /// 只是少了伺服器端的 duty commence 事件重放）。
    /// </summary>
    public static readonly bool ProcessDirectorUpdateVerified = true;

    public const string ProcessDirectorUpdateBlockReason =
        "EventFramework.ProcessDirectorUpdate 的台服 signature 尚未驗證（掃到的是別的函式，"
        + "呼叫會當場崩潰）——DirectorUpdate 事件重放已暫時停用。";

    /// <summary>可否進入模擬區（載 zone）。</summary>
    public static bool CanEnterSimZone => LoadZoneVerified;
}
