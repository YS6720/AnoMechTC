using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;

namespace AnoMech.Scenarios.Top.P5Sigma;

public class TopP5SigmaTuuuflessAi : TopP5SigmaAi
{
    public override string Name => "打法 tuufless";
    public override string? Group => "日服";

    private static readonly PartyRole[] OmegaFSidePriority =
    [
        PartyRole.RegenHealer, PartyRole.MainTank, PartyRole.OffTank, PartyRole.MeleeDpsA,
        PartyRole.MeleeDpsB, PartyRole.PhysRangedDps, PartyRole.CasterDps, PartyRole.ShieldHealer,
    ];

    protected override IAiMove LineupNextToOmegaM() => LineupBetweenTheArms();

    protected override RoleList BuildMarkingsOrder(SimWorld world) => SendHighestPriorityUndebuffedToOmegaF(world);

    private IAiMove LineupBetweenTheArms()
    {
        return AiMove.Create(
                         new(-2f, 6f), new(2f, 6f),
                         new(-2f, 8f), new(2f, 8f),
                         new(-2f, 10f), new(2f, 10f),
                         new(-2f, 12f), new(2f, 12f)
                     )
                     .Assignments(state.Order.List)
                     .ApplyPositions(state.NewNorthA.Apply);
    }

    private RoleList SendHighestPriorityUndebuffedToOmegaF(SimWorld world)
    {
        var debuffed = state.HelloWorldTargets.List;
        var holdsDynamis = state.DynamisTargets.List;
        var candidates = OmegaFSidePriority.Where(role => !debuffed.Contains(role)).ToList();

        var omegaFSide = candidates.Take(3).ToList();
        while (omegaFSide.Count(holdsDynamis.Contains) < 2)
        {
            var demoted = omegaFSide.Last(role => !holdsDynamis.Contains(role));
            var promoted = candidates.First(role => holdsDynamis.Contains(role) && !omegaFSide.Contains(role));
            omegaFSide[omegaFSide.IndexOf(demoted)] = promoted;
        }

        var laserBaits = omegaFSide.Where(holdsDynamis.Contains)
                                   .OrderByDescending(role => role.IsTank())
                                   .Take(2)
                                   .ToList();
        var thirdOnOmegaFSide = omegaFSide.First(role => !laserBaits.Contains(role));
        var farSide = candidates.Where(role => !omegaFSide.Contains(role)).ToList();

        return new RoleList(world.Party, new List<PartyRole>
        {
            laserBaits[0], thirdOnOmegaFSide, laserBaits[1],
            farSide[0], farSide[1], farSide[2],
            debuffed[0], debuffed[1],
        });
    }

    protected override IAiMove KnockbackPrePosition() => BetweenTheTwoWaymarks(4f);

    protected override IAiMove KnockbackPosition() => BetweenTheTwoWaymarks(1.7f);

    protected override IAiMove TowerPositions()
    {
        var towers = state.GlitchType == GlitchType.Mid ? MidGlitchTowerForEachGap : FarGlitchTowerForEachGap;
        return AiMove.Create(towers.Select((t, i) => (Vector2?)(state.GlitchType == GlitchType.Far ? FarTowerWallPosition(i) : t)).ToArray())
                     .Assignments(WaveCannonAssignments())
                     .ApplySwaps(ToTowerRelativeClockSpot)
                     .ApplyPositions(state.AdjustedNorthA.Apply);
    }

    private IAiMove BetweenTheTwoWaymarks(float radius)
    {
        return AiMove.Create(Enumerable.Range(0, 8).Select(i => (Vector2?)Gap(i, radius)).ToArray())
                     .Assignments(WaveCannonAssignments())
                     .ApplySwaps(ToTowerRelativeClockSpot)
                     .ApplyPositions(state.AdjustedNorthA.Apply);
    }

    private static Vector2 FarTowerWallPosition(int index)
    {
        var tower = FarGlitchTowerForEachGap[index];
        var partnerTower = FarGlitchTowerForEachGap[(index + 4) % 8];
        var outward = Vector2.Normalize(tower - partnerTower);
        var projection = Vector2.Dot(tower, outward);
        var radius = TopConstants.Geometry.ArenaRadius - 0.5f;
        var distance = MathF.Sqrt(projection * projection + radius * radius - tower.LengthSquared()) - projection;
        return tower + outward * distance;
    }

    private void ToTowerRelativeClockSpot(IAiRoles s)
    {
        s.Offset(state.AdjustedNorthA.Index() - state.NewNorthA.Index());
    }

    private static Vector2 Gap(int index, float radius) => Ray(MathF.PI / 4f * index + MathF.PI / 8f, radius);

    private static Vector2 Waymark(int index, float radius) => Ray(MathF.PI / 4f * index, radius);

    private static Vector2 Ray(float clockwiseFromNorth, float radius) =>
        new(MathF.Sin(clockwiseFromNorth) * radius, -MathF.Cos(clockwiseFromNorth) * radius);

    private static readonly Vector2[] MidGlitchTowerForEachGap =
    [
        Gap(7, TowerRadius), Gap(2, TowerRadius), Gap(3, TowerRadius), Gap(2, TowerRadius),
        Gap(5, TowerRadius), Gap(4, TowerRadius), Gap(5, TowerRadius), Gap(0, TowerRadius),
    ];

    private static readonly Vector2[] FarGlitchTowerForEachGap =
    [
        Waymark(0, TowerRadius), Waymark(2, TowerRadius), Waymark(3, TowerRadius), Waymark(3, TowerRadius),
        Waymark(5, TowerRadius), Waymark(5, TowerRadius), Waymark(6, TowerRadius), Waymark(0, TowerRadius),
    ];

    private const float TowerRadius = 17f;
}
