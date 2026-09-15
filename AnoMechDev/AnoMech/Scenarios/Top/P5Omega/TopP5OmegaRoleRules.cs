using System.Collections.Generic;
using System.Linq;
using AnoMech.Core.Game.Party;

namespace AnoMech.Scenarios.Top.P5Omega;

/// <summary>
/// P5 Omega 的站位分配：把「誰站哪一格」從 AI 抽出來的純邏輯，沒有 native 依賴。
///
/// 由來（2026-09-16 實機）：`solveHelloWorld2` 直接索引 `tethers[0]`／`tethers[1]`／
/// `freeAgents[0..3]`，而 tether 名單的條件是「QuickeningDynamis 剛好 3 層」——第 3 層
/// 要玩家實際接到才有。有人漏接、死亡或狀態還沒複製到位時名單就湊不滿，
/// `ArgumentOutOfRangeException` 直接從 EventScheduler 冒到 Plugin.OnFrameworkUpdate，
/// 多人場次被判 NativeFailure、整個房間一起斷。
///
/// 這裡的契約：**輸入再爛也要回得出 8 個不重複的 role，絕不拋例外**。
/// 分配不理想只是站位不準，玩家看得出來；拋例外會把整房帶走，而且看不出原因。
/// </summary>
internal static class TopP5OmegaRoleRules
{
    /// <summary>
    /// 依序填滿 8 格：先放指定的 <paramref name="fixedSlots"/>，再從 <paramref name="preferred"/>
    /// 取到指定人數，不足的從 <paramref name="fallback"/> 依序遞補，仍不足就從
    /// <paramref name="everyone"/> 補；重複者略過。
    /// </summary>
    /// <param name="preferredCount">preferred 名單要佔幾格（例如兩條線）。</param>
    internal static List<PartyRole> Assign(
        IReadOnlyList<PartyRole> fixedSlots,
        IReadOnlyList<PartyRole> preferred,
        int preferredCount,
        IReadOnlyList<PartyRole> fallback,
        IReadOnlyList<PartyRole> everyone,
        int total = 8)
    {
        var result = new List<PartyRole>(total);
        var used = new HashSet<PartyRole>();

        void Take(PartyRole role)
        {
            if (result.Count >= total || !used.Add(role)) return;
            result.Add(role);
        }

        foreach (var role in fixedSlots) Take(role);

        // preferred 只佔 preferredCount 格：湊不滿就讓 fallback 補進這幾格，
        // 湊太多則多出來的人之後會以 fallback 身分被填進剩餘格，不會被丟掉。
        var quota = result.Count + preferredCount;
        foreach (var role in preferred)
        {
            if (result.Count >= quota) break;
            Take(role);
        }
        foreach (var role in fallback)
        {
            if (result.Count >= quota) break;
            Take(role);
        }

        foreach (var role in fallback) Take(role);
        foreach (var role in preferred) Take(role);
        foreach (var role in everyone) Take(role);
        return result;
    }

    /// <summary>
    /// 監視器人選：必須接的先進，不足兩人從可接名單補。
    /// 原本是 `while (count &lt; 2) canTake[rng.Next(canTake.Count)]`——可接名單為空時
    /// `rng.Next(0)` 回 0 而索引 0 不存在，同樣會拋例外。改成補得到就補、補不到就回現有的，
    /// 由呼叫端的 8 格填補機制處理缺口。
    /// </summary>
    internal static List<PartyRole> Monitors(
        IReadOnlyList<PartyRole> mustTake,
        IReadOnlyList<PartyRole> canTake,
        int wanted = 2)
    {
        var result = mustTake.Distinct().Take(wanted).ToList();
        foreach (var role in canTake)
        {
            if (result.Count >= wanted) break;
            if (!result.Contains(role)) result.Add(role);
        }
        return result;
    }
}
