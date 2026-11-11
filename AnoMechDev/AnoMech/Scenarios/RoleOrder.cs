using System.Collections.Generic;
using System.Linq;
using AnoMech.Core.Game.Party;

namespace AnoMech.Scenarios;

// RoleList.RandomRoleStable(Rng, SimParty) 的排序（上游 1646a70 同式）：前 4 席坦補洗牌、後 4 席 DPS 洗牌；
// 用 run 的 seeded Rng，同種子同結果。
internal static class RoleOrder
{
    public static List<PartyRole> RandomRoleStable(Rng rng)
    {
        var supports = rng.Shuffle(Enumerable.Range(0, 4).Select(i => (PartyRole)i));
        var dps = rng.Shuffle(Enumerable.Range(4, 4).Select(i => (PartyRole)i));
        return supports.Concat(dps).ToList();
    }
}
