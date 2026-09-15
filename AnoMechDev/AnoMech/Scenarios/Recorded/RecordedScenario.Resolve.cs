using System.Linq;
using System.Numerics;
using AnoMech.Core;
using AnoMech.Core.Game;
using AnoMech.Core.SimObjects;

namespace AnoMech.Scenarios.Recorded;

/// <summary>實戰資料重播——傷害結算層（拆自主檔；職責＝把落下的招變成死亡或提示）。</summary>
public partial class RecordedScenario
{
    /// <summary>
    /// 子類別自己的機制結算（DSR 的迴旋／塔／武神槍／跳）。回 true＝基底不再做 generic 判定。
    /// </summary>
    /// <param name="hitTime">場景時間的落下時刻——子類別要用它去對「誰是這一跳的被點者」。</param>
    protected virtual bool TryResolveSpecial(uint action, SimEnemy? caster, float hitTime) => false;

    /// <summary>
    /// 場地半徑：全域 AOE 的判準（EffectRange ≥ 半徑＝整個場地都在範圍內）。
    /// <see cref="RecordedZone"/> 的場景用資料裡的值；手寫 zone 沿用 20——
    /// DSR／FRU／M8S 三者原本就共用同一個場地半徑 20。
    /// </summary>
    protected float ArenaRadius => Phase.Zone is RecordedZone rz ? rz.Radius : 20f;

    /// <summary>
    /// generic 結算（**只有 v2 資料走這裡**）。順序：子類別特化 → 全域 AOE 不判 →
    /// 自身型（坦克點名只提示）→ 其餘幾何招走既有三型語意。擊退最後獨立套用。
    /// </summary>
    private void ResolveRecordedCast(RecordedTimeline.Event e, SimEnemy? caster, float hitTime, bool knockback)
    {
        // fail-closed 守門：generic 幾何結算**只對 v2 資料開**。v1 檔（dsr-*.json）是
        // 反覆調校過的，多判一招沒有任何測試會紅，只有打過那個副本的人看得出來（AC6）。
        // 呼叫端本來就只在 v2 排這個回呼，這裡是第二道——守衛不該只有一層。
        if (!IsV2) return;
        if (TryResolveSpecial(e.Action, caster, hitTime)) return;

        if (IsRaidwide(e.Action))
        {
            // 判死一個躲不掉的招沒有訓練意義；實戰它靠減傷吃，而本模擬沒有減傷模型。
            if (knockback) ApplyKnockback(e, caster);
            return;
        }

        if (IsSelfCast(e.Action))
        {
            // CastType 1 對玩家＝坦克點名。v1 無仇恨／減傷模型，判死只會誤殺對的人，
            // 所以只標出來：「這一刀點的是誰」本身就是要練的資訊。
            if (TargetRole(e) is { } role)
                ChatOutput.Coach($"[AnoMech] {ActionLookup.Name(e.Action)} 坦克點名 —— {role}");
            if (knockback) ApplyKnockback(e, caster);
            return;
        }

        if (!IsNoResolve(e.Action))
            ResolvePlayerLethal(caster, e.Action);
        if (knockback) ApplyKnockback(e, caster);
    }

    /// <summary>
    /// 擊退（effect type 32 的 Knockback row）。**只推玩家**——AI 的軌跡本來就含
    /// 擊退後的位置，兩邊都推會讓全隊瞬間散開而玩家原地不動（維護者 2026-08-21 實測
    /// 「動得很亂」，DSR P2 信仰即此）。距離／速度一律查 Knockback 表，不寫死。
    /// </summary>
    private void ApplyKnockback(RecordedTimeline.Event e, SimEnemy? caster)
    {
        if (e.Kb == 0) return;
        if (!KnockbackLookup.TryGet(e.Kb, out var distance, out var speed)) return;
        var role = party.PlayerRole;
        // 錄影錄到打中誰就照錄影；沒錄到（舊錄影缺 eff）才退回幾何。
        var hit = e.Hits.Count > 0
            ? e.Hits.Contains((int)role)
            : caster != null
              && party.Find.InsideActionAoe(e.Action, caster.Placement())
                       .Any(m => m is ISimPartyMember pm && pm.Role == role);
        if (!hit) return;
        var source = caster?.Position ?? FirstPosition(e) ?? Vector3.Zero;
        (party.Get(role) as ISimPartyMember)?.Knockback(source, distance, speed);
        ChatOutput.Coach($"[AnoMech] {ActionLookup.Name(e.Action)} 擊退 {distance:F0}m");
    }

    /// <summary>
    /// 全域 AOE（整個場地都在範圍內、沒地方可躲）＝不做致死判定。
    /// 判死一個躲不掉的招沒有訓練意義；實戰它靠減傷吃，而本模擬沒有減傷模型（v1 範圍外）。
    /// </summary>
    protected bool IsRaidwide(uint actionId)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        return sheet.TryGetRow(actionId, out var a)
            && a.CastType == 2
            && a.EffectRange >= ArenaRadius;
    }

    /// <summary>自身型施法（Lumina CastType 1，如邪龍爪牙／幻象衝母體）＝沒有幾何可判。</summary>
    protected static bool IsSelfCast(uint action)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        return sheet.TryGetRow(action, out var a) && a.CastType == 1;
    }

    /// <summary>有出招演出、但**不做致死判定**的招。基底沒有這種招，子類別按副本列舉。</summary>
    protected virtual bool IsNoResolve(uint action) => false;

    /// <summary>
    /// 引導承傷類結算。
    /// 2026-08-20 實測＋離線審計定調：通關隊的打法本來就有「整條線穿過人群、全隊吃減傷」
    /// 的線（audit：二輪武神槍掃 7 人、零死亡）——那種線判死玩家是懲罰站對的人。
    /// 語意分兩型：**線上 ≥3 人＝全隊承傷**（提示吃減傷、不判死）；
    /// **線上 1~2 人＝該離開沒離開**（玩家判死、AI 提示）。
    /// </summary>
    protected void ResolvePlayerLethal(SimEnemy? source, uint action)
    {
        if (source == null) return;
        var hits = damage.Resolve(source, action, [DamageType.Lethal], [], killTargets: false);
        if (hits.Count >= 3)
        {
            ChatOutput.Coach($"[AnoMech] {ActionLookup.Name(action)} 全隊承傷 ×{hits.Count}（真本吃減傷）");
            return;
        }
        var aiHit = 0;
        foreach (var h in hits)
        {
            if (h is ISimPartyMember m && m.Role == party.PlayerRole)
                h.Die($"受到{ActionLookup.Name(action)}致命傷害");
            else
                aiHit++;
        }
        if (aiHit > 0)
            ChatOutput.Coach($"[AnoMech] {ActionLookup.Name(action)} 掃到隊友 ×{aiHit}");
    }
}
