using System;
using System.Collections.Generic;
using System.Linq;
using AnoMech.Core;
using AnoMech.Core.Game.Party;

namespace AnoMech.Scenarios.Umad.P3BlackHole;

// YAN(211) head markers: buff I wears Attack 1-3, buff II Bind 1-3, buff III Ignore 1-2. Each sign
// stands in for one tether slot of the D>S>A timeline (UmadP3BlackHoleAi), which already pulls in
// exactly the order YAN calls by sign; only the 2nd, 9th and 10th pulls differ (see the Ai).
// Pure logic so Tests can link it: no native Markings here.
public static class UmadP3BlackHoleYanMarks
{
    // Sign → D>S>A slot it takes over. Same-line signs are listed in sign order.
    public static readonly IReadOnlyList<(Sign Sign, int Slot)> Plan =
    [
        (Sign.Attack1, 4), (Sign.Attack2, 0), (Sign.Attack3, 3),
        (Sign.Bind1, 5), (Sign.Bind2, 1), (Sign.Bind3, 7),
        (Sign.Ignore1, 6), (Sign.Ignore2, 2),
    ];

    // Automatic marking: each buff line's holders shuffled across that line's signs.
    // slotRoles = state.Roles (slot → role; the buff line follows the slot).
    public static Dictionary<PartyRole, Sign> Assign(IReadOnlyList<PartyRole> slotRoles, Rng rng)
    {
        var marks = new Dictionary<PartyRole, Sign>();
        foreach (var group in LineGroups())
        {
            var holders = rng.Shuffle(group.Select(p => slotRoles[p.Slot]));
            for (var i = 0; i < group.Length; i++)
                marks[holders[i]] = group[i].Sign;
        }
        return marks;
    }

    // Who pulls each slot, read from the signs on the party. A sign counts only when it sits on
    // someone carrying that sign's buff line; a missing sign, one on a wrong-line player, or a
    // second sign on the same player is ignored and reported. Every slot is still filled from the
    // line's members nobody's sign claimed: first each unclaimed sign keeps its D>S>A seat when
    // that member is free, then the rest go out in sign order. Pullers[slot] = role.
    public static (PartyRole[] Pullers, List<string> Problems) Resolve(IReadOnlyList<PartyRole> slotRoles, Func<Sign, PartyRole?> wearer)
    {
        var pullers = new PartyRole[8];
        var problems = new List<string>();
        foreach (var group in LineGroups())
        {
            var line = UmadP3BlackHoleState.SlotLineOf(group[0].Slot);
            var members = group.Select(p => slotRoles[p.Slot]).ToList();
            var taken = new PartyRole?[group.Length];
            for (var i = 0; i < group.Length; i++)
            {
                var sign = group[i].Sign;
                if (wearer(sign) is not { } role)
                {
                    problems.Add($"{sign}: not on any party member.");
                    continue;
                }
                if (!members.Contains(role))
                {
                    problems.Add($"{sign}: on {role}, who is not line {line} -- ignored.");
                    continue;
                }
                if (taken.Contains(role))
                {
                    problems.Add($"{sign}: {role} already wears another line {line} sign -- ignored.");
                    continue;
                }
                taken[i] = role;
            }
            var spare = members.Where(m => !taken.Contains(m)).ToList();
            for (var i = 0; i < group.Length; i++)
                if (taken[i] is null && spare.Remove(members[i]))
                    taken[i] = members[i];
            for (var i = 0; i < group.Length; i++)
            {
                var role = taken[i] ?? spare[0];
                spare.Remove(role);
                pullers[group[i].Slot] = role;
            }
        }
        return (pullers, problems);
    }

    private static IEnumerable<(Sign Sign, int Slot)[]> LineGroups() =>
        Plan.GroupBy(p => UmadP3BlackHoleState.SlotLineOf(p.Slot)).Select(g => g.ToArray());
}
