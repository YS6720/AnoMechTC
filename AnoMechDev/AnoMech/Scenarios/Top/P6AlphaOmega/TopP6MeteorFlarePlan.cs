using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using static AnoMech.Scenarios.Top.TopConstants;

namespace AnoMech.Scenarios.Top.P6AlphaOmega;

internal sealed class TopP6MeteorFlarePlan
{
    internal const float EdgeClearance = 1f;
    internal const float EdgeRadius = Geometry.ArenaRadius - EdgeClearance;

    private static readonly (char Label, Vector2 Position)[] EdgePositions =
    [
        ('A', new(0f, -EdgeRadius)),
        ('B', new(EdgeRadius, 0f)),
        ('C', new(0f, EdgeRadius)),
        ('D', new(-EdgeRadius, 0f)),
    ];

    internal IReadOnlyList<PartyRole> MarkedRoles { get; }
    internal IReadOnlyList<PartyRole> UnmarkedRoles { get; }
    internal IReadOnlyDictionary<PartyRole, Vector2> MarkedPositions { get; }
    internal PartyRole? StackTargetRole { get; }
    internal Vector2 StackPosition { get; }
    internal bool IncludesD3 { get; }

    private TopP6MeteorFlarePlan(
        IReadOnlyList<PartyRole> markedRoles,
        IReadOnlyList<PartyRole> unmarkedRoles,
        IReadOnlyDictionary<PartyRole, Vector2> markedPositions,
        PartyRole? stackTargetRole,
        Vector2 stackPosition,
        bool includesD3)
    {
        MarkedRoles = markedRoles;
        UnmarkedRoles = unmarkedRoles;
        MarkedPositions = markedPositions;
        StackTargetRole = stackTargetRole;
        StackPosition = stackPosition;
        IncludesD3 = includesD3;
    }

    internal static TopP6MeteorFlarePlan Create(
        IEnumerable<SimCharacter> alive,
        bool? includeD3Override,
        Rng rng)
    {
        var members = alive
            .Select(member =>
            {
                var role = ((ISimPartyMember)member).Role;
                return (Role: role, Position: new Vector2(member.Position.X, member.Position.Z));
            })
            .DistinctBy(member => member.Role)
            .OrderBy(member => member.Role)
            .ToArray();
        return Create(members, includeD3Override, rng);
    }

    internal static TopP6MeteorFlarePlan Create(
        IReadOnlyList<(PartyRole Role, Vector2 Position)> alive,
        bool? includeD3Override,
        Rng rng)
    {
        var available = alive
            .DistinctBy(member => member.Role)
            .OrderBy(member => member.Role)
            .ToArray();
        var includeD3 = includeD3Override ?? rng.NextBool();
        var marked = SelectMarkedRoles(available, includeD3, rng);
        var markedSet = marked.ToHashSet();
        var unmarked = available
            .Where(member => !markedSet.Contains(member.Role))
            .Select(member => member.Role)
            .ToArray();
        var positions = AssignEdgePositions(marked, available, markedSet.Contains(PartyRole.PhysRangedDps));
        var stackSlot = markedSet.Contains(PartyRole.PhysRangedDps) ? WaymarkSlot.C : WaymarkSlot.A;
        PartyRole? stackTarget = unmarked.Length == 0 ? null : unmarked[0];
        return new TopP6MeteorFlarePlan(marked, unmarked, positions, stackTarget,
            WaymarkPosition(stackSlot), markedSet.Contains(PartyRole.PhysRangedDps));
    }

    private static PartyRole[] SelectMarkedRoles(
        IReadOnlyList<(PartyRole Role, Vector2 Position)> available,
        bool includeD3,
        Rng rng)
    {
        var pool = includeD3
            ? available.ToList()
            : available.Where(member => member.Role != PartyRole.PhysRangedDps).ToList();
        pool = rng.Shuffle(pool.ToArray()).ToList();
        if (includeD3 && available.Any(member => member.Role == PartyRole.PhysRangedDps))
        {
            pool.RemoveAll(member => member.Role == PartyRole.PhysRangedDps);
            var selected = new List<PartyRole> { PartyRole.PhysRangedDps };
            selected.AddRange(pool.Take(2).Select(member => member.Role));
            return selected.ToArray();
        }

        return pool.Take(3).Select(member => member.Role).ToArray();
    }

    private static IReadOnlyDictionary<PartyRole, Vector2> AssignEdgePositions(
        IReadOnlyList<PartyRole> marked,
        IReadOnlyList<(PartyRole Role, Vector2 Position)> available,
        bool d3Marked)
    {
        var memberPositions = available.ToDictionary(member => member.Role, member => member.Position);
        var assigned = new Dictionary<PartyRole, Vector2>();
        if (d3Marked)
        {
            assigned[PartyRole.PhysRangedDps] = Edge('A');
            var others = marked.Where(role => role != PartyRole.PhysRangedDps).OrderBy(role => role).ToArray();
            if (others.Length > 0)
                assigned[others[0]] = Edge('D');
            if (others.Length > 1)
                assigned[others[1]] = Edge('B');
            if (others.Length == 2 && memberPositions.TryGetValue(others[0], out var first)
                && memberPositions.TryGetValue(others[1], out var second)
                && Distance(first, Edge('D')) + Distance(second, Edge('B'))
                    > Distance(first, Edge('B')) + Distance(second, Edge('D')))
            {
                assigned[others[0]] = Edge('B');
                assigned[others[1]] = Edge('D');
            }
            return assigned;
        }

        var roles = marked.OrderBy(role => role).ToArray();
        var edges = new[] { Edge('D'), Edge('C'), Edge('B') };
        var bestCost = float.PositiveInfinity;
        var best = new int[roles.Length];
        var permutation = new int[roles.Length];
        for (var i = 0; i < permutation.Length; i++) permutation[i] = i;
        foreach (var candidate in Permutations(permutation))
        {
            var cost = 0f;
            for (var i = 0; i < roles.Length; i++)
                if (memberPositions.TryGetValue(roles[i], out var position))
                    cost += Distance(position, edges[candidate[i]]);
            if (cost < bestCost)
            {
                bestCost = cost;
                Array.Copy(candidate, best, candidate.Length);
            }
        }
        for (var i = 0; i < roles.Length; i++)
            assigned[roles[i]] = edges[best[i]];
        return assigned;
    }

    private static IEnumerable<int[]> Permutations(int[] values)
    {
        if (values.Length == 0)
        {
            yield return [];
            yield break;
        }
        for (var i = 0; i < values.Length; i++)
        {
            var rest = values.Where((_, index) => index != i).ToArray();
            foreach (var suffix in Permutations(rest))
                yield return [values[i], .. suffix];
        }
    }

    private static float Distance(Vector2 first, Vector2 second)
        => Vector2.Distance(first, second);

    private static Vector2 Edge(char label)
        => EdgePositions.First(edge => edge.Label == label).Position;

    private static Vector2 WaymarkPosition(WaymarkSlot slot)
    {
        var waymark = TopUtils.TopWaymarks.FirstOrDefault(mark => mark.Slot == slot);
        return waymark is null ? slot switch
        {
            WaymarkSlot.A => new(0f, -13.63f),
            WaymarkSlot.C => new(0f, 13.63f),
            _ => Vector2.Zero,
        } : new(waymark.Offset.X, waymark.Offset.Z);
    }
}
