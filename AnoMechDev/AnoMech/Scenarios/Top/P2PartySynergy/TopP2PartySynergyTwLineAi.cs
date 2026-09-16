using System;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;

namespace AnoMech.Scenarios.Top.P2PartySynergy;

// TW variant of the Standard strat (hackmd @nanahara233/S1AuDTE3p).
// Isolated copy on purpose — the repo convention for strat variants is a fork,
// not a shared base, so tuning one strat can never move another.
//
// Only two things differ from TopP2PartySynergyAi; every later step (spread rows,
// glitch swap/geometry, stack adjust, knockback, stacks) is identical because the
// destinations are the same — only *who* walks to them changes.
//
//   1. Opening formation: two north->south columns beside their own boss instead of
//      one east-west line in the south. Left column (Omega-M side, west) is
//      H1 > MT > D1 > D3; right column (Omega-F side, east) is H2 > ST > D2 > D4.
//   2. Same-symbol rule: within a tethered pair standing in the same column, the
//      *southern* one crosses to the other side (e.g. H1 and MT both get O -> MT
//      goes right). Both columns send their southern member across, which makes the
//      left/right decision asymmetric — unlike the east-west line, where the single
//      rule "further west takes the left slot" covers both halves.
public class TopP2PartySynergyTwLineAi : IScenarioAi<TopP2PartySynergyState>
{
    public string Name => "打法 tuufless";

    private TopP2PartySynergyState state = null!;

    public void Run(TopP2PartySynergyState s, SimWorld world)
    {
        state = s;
        var ai = new AiManager(world);
        ai.Move(1f, ColumnLine, jitter: 0.7f);
        ai.Move(11f, AttackDodge, arrivalTime: 14.8f);
        ai.Move(16f, SpreadPositions, arrivalTime: 21.5f);
        ai.Move(23f, KnockbackPositions, arrivalTime: 28.4f);
        ai.Move(30f, StackPositions, arrivalTime: 33f);
    }

    // Two arcs hugging the outer side of each boss, upper -> lower, indexed by
    // PartyRole (NaturalOrder). Omega-M sits at (-3.4, -1.0), Omega-F at (3.2, -1.5),
    // so "left of M" is further west and "up" is -z:
    //   H1 upper-left of M / MT left of M, a touch above it / D1, D3 lower-left.
    // East side is the mirror. 維護者 2026-09-05: the first pass sat too far south
    // and too far inboard (a straight x=+-5 column from z=2 to z=11), which put the
    // queue behind the bosses instead of beside them.
    private IAiMove ColumnLine()
    {
        return AiMove.Create(
            new(-10, -2.5f),  // MainTank      (MT)  左，比 M 稍微上面一點
            new(10, -2.5f),   // OffTank       (ST)
            new(-8.5f, -6),   // RegenHealer   (H1)  左上
            new(8.5f, -6),    // ShieldHealer  (H2)
            new(-9, 2),       // MeleeDpsA     (D1)  左下
            new(9, 2),        // MeleeDpsB     (D2)
            new(7.5f, 5.5f),  // PhysRangedDps (D4)
            new(-7.5f, 5.5f)  // CasterDps     (D3)  左下（外側）
        ).NaturalOrder();
    }

    private IAiMove AttackDodge()
    {
        return AiMove.All(new(0, -1f))
                     .ApplyPositions(
                         AttackSafeCardinal,
                         AttackSafeSpot
                     );
    }


    private IAiMove SpreadPositions()
    {
        return AiMove.Create(
                         new(-11, -16),
                         new(11, -16),
                         new(-11, -5.5f),
                         new(11, -5.5f),
                         new(-11, 5.5f),
                         new(11, 5.5f),
                         new(-11, 16),
                         new(11, 16)
                     )
                     .Assignments(state.Order.List)
                     .ApplySwaps(SwapForColumnOrder, GlitchSwap)
                     .ApplyPositions(GlitchGeometry, state.NewNorthA.Apply);
    }

    private IAiMove KnockbackPositions()
    {
        return AiMove.Create(
                         new(-2, 0),
                         new(2, 0),
                         new(-2, 0),
                         new(2, 0),
                         new(-2, 0),
                         new(2, 0),
                         new(-2, 0),
                         new(2, 0)
                     )
                     .Assignments(state.Order.List)
                     .ApplySwaps(SwapForColumnOrder, GlitchSwap, AdjustForStacks)
                     .ApplyPositions(AdjustKbForFarGlitch, state.NewNorthB.Apply);
    }

    private IAiMove StackPositions()
    {
        return AiMove.Create(
                         new(-15, 0),
                         new(15, 0),
                         new(-15, 0),
                         new(15, 0),
                         new(-15, 0),
                         new(15, 0),
                         new(-15, 0),
                         new(15, 0)
                     )
                     .Assignments(state.Order.List)
                     .ApplySwaps(SwapForColumnOrder, GlitchSwap, AdjustForStacks)
                     .ApplyPositions(AdjustKbForFarGlitch, state.NewNorthB.Apply);
    }


    private void AttackSafeCardinal(IAiPositions move)
    {
        state.AttackDir.Rotate(1).Apply(move);
    }

    private void AttackSafeSpot(IAiPositions move)
    {
        float mul;
        if (state.AttackF == OmegaAttack.Legs)
            mul = state.AttackM == OmegaAttack.Sword ? 2.5f : -2.5f;
        else
            mul = state.AttackM == OmegaAttack.Sword ? -17f : -10f;
        move.Multiply(mul);
    }

    private void SwapForColumnOrder(IAiRoles s)
    {
        for (int i = 0; i < 4; i++)
        {
            if (!TakesLeftSlot(Column(s.RoleAt(2 * i)), Column(s.RoleAt(2 * i + 1))))
            {
                s.ByPosition(2 * i, 2 * i + 1);
            }
        }
    }

    private void GlitchSwap(IAiRoles s)
    {
        if (state.Glitch == GlitchType.Far)
            s.ByPosition(1, 7);
    }

    private void GlitchGeometry(IAiPositions move)
    {
        if (state.Glitch == GlitchType.Far)
        {
            var mul = 18f / 11;
            move.MultiplyX(2, mul);
            move.MultiplyX(3, mul);
            move.MultiplyX(4, mul);
            move.MultiplyX(5, mul);
        }
    }


    private void AdjustForStacks(IAiRoles s)
    {
        var pos0 = s.PositionOf(state.Stacks[0]);
        var pos1 = s.PositionOf(state.Stacks[1]);
        Plugin.Log.Info($"Stack adjustments positions {pos0} {pos1}");
        if ((pos0 + pos1) % 2 == 0)
        {
            // 同邊兩人都被點分攤時，由**較南（遠離眼球、index 較大）**的那位換到對面，
            // 較北的照常跑。原本取較小 index（較北）換邊，維護者 2026-09-16 指出與實際打法相反。
            if (pos0 < pos1)
                pos0 = pos1;
            var partner = pos0 % 2 == 0 ? pos0 + 1 : pos0 - 1;
            if (state.Glitch == GlitchType.Far && pos0 is < 2 or > 5)
                partner = pos0 < 2 ? partner + 6 : partner - 6;
            Plugin.Log.Info($"Stack adjustments partners {pos0} {partner}");
            s.ByPosition(pos0, partner);
        }
    }

    private void AdjustKbForFarGlitch(IAiPositions move)
    {
        if (state.Glitch == GlitchType.Mid)
            for (var i = 0; i < 4; i++)
                move.Rotate(i * 2 + 1, MathF.PI / 2);
        else
            move.Multiply(19f / 15);
    }

    // Opening columns, north -> south: indices 0-3 are the west (Omega-M) column,
    // 4-7 the east (Omega-F) one.
    private static readonly PartyRole[] ColumnOrder =
    [
        PartyRole.RegenHealer,   // H1
        PartyRole.MainTank,      // MT
        PartyRole.MeleeDpsA,     // D1
        PartyRole.CasterDps,     // D3
        PartyRole.ShieldHealer,  // H2
        PartyRole.OffTank,       // ST
        PartyRole.MeleeDpsB,     // D2
        PartyRole.PhysRangedDps, // D4
    ];

    private static int Column(PartyRole role) => Array.IndexOf(ColumnOrder, role);

    // Does the role at column rank `a` take the left (west of the eye) spread slot,
    // given its tether partner sits at rank `b`?
    // Different columns: everyone keeps their own side. Same column: the southern
    // one (higher rank) crosses over, so the west column keeps the northern one on
    // the left and the east column sends the southern one to the left.
    private static bool TakesLeftSlot(int a, int b)
    {
        var aWest = a < 4;
        if (aWest != b < 4) return aWest;
        return aWest ? a < b : a > b;
    }
}
