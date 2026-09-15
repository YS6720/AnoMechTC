using System.Collections.Generic;
using System.Linq;

namespace AnoMech.Core.Map;

// 進入模擬區之前的 fail-closed 硬閘（AGENTS.md 紅線 1、BACKLOG B-001）。
//
// 這是全專案唯一「靜默失效且後果不可逆」的地方。封包防火牆若沒真的生效，
// client 會在假副本狀態下向伺服器回報「我在 territory XXX 且正在戰鬥」——
// 那是伺服器端立即可見、且無法用「我只是在練習」解釋的異常狀態，代價是帳號。
// 而它**零回饋訊號**：畫面全正常、build 全綠、log 乾淨。
//
// 檢查採「證明有效才放行」而非「找到問題才擋」。但要分清兩種項目：
//
//   安全項（Safety，硬擋）——沒證明成立就拒絕啟動，因為關係到**送出洩漏**：
//     送出 hook 已安裝／心跳 opcode 已解析／送出 detour 真的被呼叫／心跳被辨識／
//     Blocking 期間零外洩。會賠帳號的是「client 送封包給 server 洩漏假副本狀態」，
//     這條路 100% 由送出側守著，所以送出側全是安全項。
//
//   功能項（Functional，警告不擋）——不成立只影響「模擬跑不跑得起來」，不洩漏：
//     ZoneDown allowlist 非空。實測（ZoneSession.ReceivePacketDetour）：Release 下
//     safeMode 恆真，空 allowlist ⇒ 接收封包一律不放行 = **接收全擋 = fail-safe**。
//     台服抓不到 opcode 表（上游只有國際服版本檔），清單必然為空；但空清單是安全的，
//     只是假副本可能收不到某些 client 端狀態。故降為警告——把它當硬閘等於為零安全
//     收益封死整個台服使用。真要補 opcode 是功能問題（B-006），不是安全問題。
internal enum FirewallSeverity { Safety, Functional }

internal sealed record FirewallCheckItem(
    string Name, bool Passed, string Detail, FirewallSeverity Severity);

internal sealed record FirewallCheckResult(IReadOnlyList<FirewallCheckItem> Items)
{
    // 只有安全項未過才算「未通過、拒絕啟動」；功能項未過只警告。
    public bool Passed => Items.Where(i => i.Severity == FirewallSeverity.Safety).All(i => i.Passed);

    public IEnumerable<FirewallCheckItem> BlockingFailures =>
        Items.Where(i => i.Severity == FirewallSeverity.Safety && !i.Passed);

    public IEnumerable<FirewallCheckItem> Warnings =>
        Items.Where(i => i.Severity == FirewallSeverity.Functional && !i.Passed);

    /// <summary>給 UI／log 用的單段摘要。</summary>
    public string Summary => Passed
        ? (Warnings.Any()
            ? "防火牆自檢通過（含功能警告：" + string.Join("；", Warnings.Select(w => w.Name)) + "）"
            : "防火牆自檢通過")
        : "防火牆自檢未通過（安全項）：" + string.Join("；", BlockingFailures.Select(f => $"{f.Name}（{f.Detail}）"));
}

internal static class FirewallSelfCheck
{
    // 心跳需觀察到的最少次數。1 次就足以證明 hook 活著且 opcode 判定正確；
    // 要求更多只會讓玩家在剛載入插件時多等，換不到額外的保證。
    private const long MinHeartbeatObserved = 1;

    // 送出側 detour 的最少呼叫次數。心跳本身就會計入，所以這項在心跳項通過時
    // 必然通過；它的價值在於**心跳項失敗時**能區分兩種病因：
    //   detour 有被呼叫但沒認出心跳 → opcode 掃錯
    //   detour 完全沒被呼叫         → hook 掛錯函式或沒啟用
    private const long MinSendObserved = 1;

    public static FirewallCheckResult Run(ZoneSession session)
    {
        var items = new List<FirewallCheckItem>();

        items.Add(new FirewallCheckItem(
            "送出 hook 已安裝並啟用",
            session.SendHookLive && session.SendHookAddress != 0,
            $"address=0x{session.SendHookAddress:X}, live={session.SendHookLive}",
            FirewallSeverity.Safety));

        items.Add(new FirewallCheckItem(
            "接收 hook 已安裝並啟用",
            session.RecvHookLive && session.RecvHookAddress != 0,
            $"address=0x{session.RecvHookAddress:X}, live={session.RecvHookLive}",
            FirewallSeverity.Safety));

        // opcode 為 0 代表 ScanText 讀到的立即數不是 opcode（掃錯位置）。
        // 不設上界：opcode 是 ushort，整個值域都合法，硬編一個上限只會製造假紅。
        items.Add(new FirewallCheckItem(
            "心跳 opcode 已解析",
            session.HeartbeatOpcode != 0,
            $"opcode={session.HeartbeatOpcode}",
            FirewallSeverity.Safety));

        var sendSeen = session.SendSeenTotal;
        items.Add(new FirewallCheckItem(
            "送出 detour 確實被呼叫",
            sendSeen >= MinSendObserved,
            $"已攔截 {sendSeen} 筆（Passive 模式累計）",
            FirewallSeverity.Safety));

        var hbSeen = session.SendSeenHeartbeat;
        items.Add(new FirewallCheckItem(
            "心跳封包已被辨識",
            hbSeen >= MinHeartbeatObserved,
            hbSeen >= MinHeartbeatObserved
                ? $"已辨識 {hbSeen} 筆心跳"
                : sendSeen >= MinSendObserved
                    ? $"detour 有被呼叫（{sendSeen} 筆）但一筆心跳都沒認出 ⇒ 心跳 opcode 可能掃錯"
                    : "detour 完全沒被呼叫 ⇒ hook 可能掛在錯的函式上"
                      + "（若剛載入插件不久，可能只是還沒累積到觀測樣本，稍候再試）",
            FirewallSeverity.Safety));

        var leak = session.BlockedLeakCount;
        items.Add(new FirewallCheckItem(
            "Blocking 期間零外洩",
            leak == 0,
            leak == 0 ? "0" : $"偵測到 {leak} 筆非心跳封包走到 Original——detour 邏輯已被破壞",
            FirewallSeverity.Safety));

        // ── 功能項（不擋，只警告）──────────────────────────────────
        var opcodeCount = session.IncomingOpcodes.Count;
        items.Add(new FirewallCheckItem(
            "ZoneDown allowlist 非空",
            opcodeCount > 0,
            opcodeCount > 0
                ? $"{opcodeCount} 筆"
                : "清單為空——接收全擋是 fail-safe（不洩漏），但假副本可能收不到某些 client 狀態。",
            FirewallSeverity.Functional));

        return new FirewallCheckResult(items);
    }

    /// <summary>把結果逐項寫進 log，方便使用者回報與排查。</summary>
    public static void LogResult(FirewallCheckResult result)
    {
        if (result.Passed)
        {
            Plugin.Log.Information("[FirewallSelfCheck] 安全項全部通過：");
            foreach (var i in result.Items)
            {
                var tag = i.Passed ? "OK" : "警告";
                Plugin.Log.Information($"  [{tag}] {i.Name} — {i.Detail}");
            }
            return;
        }

        Plugin.Log.Error("[FirewallSelfCheck] 安全項未通過，拒絕啟動 scenario：");
        foreach (var i in result.Items)
        {
            var tag = i.Passed ? "OK" : (i.Severity == FirewallSeverity.Safety ? "!!" : "警告");
            Plugin.Log.Error($"  [{tag}] {i.Name} — {i.Detail}");
        }
    }
}
