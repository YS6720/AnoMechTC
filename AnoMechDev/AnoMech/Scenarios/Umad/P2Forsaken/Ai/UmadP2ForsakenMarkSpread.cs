using System;
using System.Collections.Generic;
using System.Linq;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;

namespace AnoMech.Scenarios.Umad.P2Forsaken.Ai;

// 我方（Owner 11-06）：過去／未來終結引導時 8 人排在同一點，同標記的兩人疊在一起，看不出同標記的人是誰，
// 而下一組塔的站位正是靠同標記的次序決定。這裡把「下一組塔的四人」裡剛好兩人同標記的那一對沿站位軸
// （場中心→站位點）前後拉開 Gap；其他人（單一標記、不在這組的人）位置不動。上游沒有這個行為。
//
// 軸向以「遠離場中心」為外側：站位點在場中心外 9.7y（過去）／11y（未來），拉開量都在判定線（過場中心、垂直於軸）
// 的同一側，不會讓人站到另一半。
public static class UmadP2ForsakenMarkSpread
{
    // 同標記兩人之間的距離（y）。
    public const float Gap = 3f;

    // group＝下一組塔的四人（順序同 ActiveRole 的順序，先出現者是第 0 位）。
    // 回傳每個要移動的人沿軸向外的位移（負＝朝場中心）：
    //   兩人都是電腦：第 0 位朝內 Gap/2、第 1 位朝外 Gap/2；
    //   其中一人是玩家（真人）：玩家不動，另一個（電腦）固定朝外 Gap。
    public static IReadOnlyDictionary<PartyRole, float> Offsets(
        UmadP2ForsakenState state, IReadOnlyList<PartyRole> group, PartyRole player)
    {
        var offsets = new Dictionary<PartyRole, float>();
        var pairs = group
            .Where(role => state.Lockons.GetValueOrDefault(role) != 0)
            .GroupBy(role => state.Lockons[role])
            .Where(marked => marked.Count() == 2);
        foreach (var pair in pairs)
        {
            var first = pair.First();
            var second = pair.Last();
            if (first == player) offsets[second] = Gap;
            else if (second == player) offsets[first] = Gap;
            else
            {
                offsets[first] = -Gap / 2f;
                offsets[second] = Gap / 2f;
            }
        }
        return offsets;
    }

    // outwardSign＝站位點本地 Y 的正負（過去為負、未來為正），外側就是這個方向。
    public static Action<IAiPositions> Apply(
        UmadP2ForsakenState state, IReadOnlyList<PartyRole> group, PartyRole player, float outwardSign)
        => positions =>
        {
            foreach (var (role, offset) in Offsets(state, group, player))
                positions.AddY(role, offset * outwardSign);
        };
}
