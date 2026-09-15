using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game.Party;

namespace AnoMech.Scenarios.Top.P4BlueScreen;

public static class TopP4BlueScreenRules
{
    // tuuufless source priority: north-to-south tanks, ranged, healers, melee on each side.
    private static readonly PartyRole[] Priority =
    [PartyRole.MainTank, PartyRole.OffTank, PartyRole.PhysRangedDps, PartyRole.CasterDps,
     PartyRole.RegenHealer, PartyRole.ShieldHealer, PartyRole.MeleeDpsA, PartyRole.MeleeDpsB];

    public static bool[] StackSides(PartyRole first, PartyRole second)
    {
        var west = Enumerable.Range(0, 8).Select(i => i % 2 == 0).ToArray();
        if (west[(int)first] == west[(int)second])
        {
            var lower = Array.IndexOf(Priority, first) > Array.IndexOf(Priority, second) ? first : second;
            var oppositeMelee = west[(int)first] ? PartyRole.MeleeDpsB : PartyRole.MeleeDpsA;
            west[(int)lower] = !west[(int)lower];
            west[(int)oppositeMelee] = !west[(int)oppositeMelee];
        }
        return west;
    }

    public static Vector2 SpreadPosition(PartyRole role)
    {
        var rank = Array.IndexOf(Priority, role) / 2;
        var angle = (112.5f - 22.5f * rank) * ((int)role % 2 == 0 ? -1 : 1);
        return Polar(angle, 15f);
    }

    public static Vector2 StackPosition(bool west, float radius) => Polar(west ? -22.5f : 22.5f, radius);

    public static bool LineContains(Vector2 direction, Vector2 point)
    {
        if (direction.LengthSquared() < 0.0001f) return point.LengthSquared() <= 9f;
        var unit = Vector2.Normalize(direction);
        var forward = Vector2.Dot(unit, point);
        return forward >= 0 && forward <= 100f && MathF.Abs(unit.X * point.Y - unit.Y * point.X) <= 3f;
    }

    public static bool[] FailedSpreads(IReadOnlyList<Vector2?> positions, IReadOnlyList<Vector2> directions)
    {
        var failed = new bool[8];
        foreach (var direction in directions)
        {
            var victims = Enumerable.Range(0, 8).Where(i => positions[i] is { } p && LineContains(direction, p)).ToArray();
            if (victims.Length > 1)
                foreach (var victim in victims) failed[victim] = true;
        }
        return failed;
    }

    public static bool[] FailedStacks(IReadOnlyList<Vector2?> positions, IReadOnlyList<int> targets)
    {
        var hits = new int[8];
        var failed = new bool[8];
        foreach (var target in targets)
        {
            if (positions[target] is not { } direction)
            {
                Array.Fill(failed, true);
                continue;
            }
            var victims = Enumerable.Range(0, 8).Where(i => positions[i] is { } p && LineContains(direction, p)).ToArray();
            foreach (var victim in victims)
            {
                hits[victim]++;
                if (victims.Length != 4) failed[victim] = true;
            }
        }
        for (var i = 0; i < 8; i++) failed[i] |= positions[i] != null && hits[i] != 1;
        return failed;
    }

    public static bool RingContains(Vector2 position, int ring) =>
        position.Length() >= ring * 6f && position.Length() <= (ring + 1) * 6f;

    private static Vector2 Polar(float degrees, float radius)
    {
        var angle = degrees * MathF.PI / 180f;
        return new Vector2(MathF.Sin(angle), MathF.Cos(angle)) * radius;
    }
}
