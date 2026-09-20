using System;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using static AnoMech.Scenarios.Top.P6AlphaOmega.TopP6AlphaOmegaScenario;

namespace AnoMech.Scenarios.Top.P6AlphaOmega;

public sealed class TopP6AlphaOmegaAi : IScenarioAi<bool>
{
    public string Name => "三號集合／雙坦分離／宇宙隕石";

    public void Run(bool inFirst, SimWorld world)
    {
        var ai = new AiManager(world);
        // First arrow stays on the waymark-3 (SW) side; second-arrow clockspots are separate.
        Move(ai, world, 14.5f, () => FirstArrowSpots(inFirst ? 6f : 9f));
        Move(ai, world, 23f, () => FirstArrowSpots(inFirst ? 4f : 11f));
        if (inFirst)
        {
            Move(ai, world, 27f, () => FirstArrowSpots(11f));
            Move(ai, world, 33f, () => AiMove.Create(null, null,
                new(-9f, 9f), new(-9f, 9f), new(-9f, 9f), new(-9f, 9f),
                new(-9f, 9f), new(-9f, 9f)).NaturalOrder());
        }
        else
        {
            Move(ai, world, 27f, () => FirstArrowSpots(9f));
            Move(ai, world, 29f, () => FirstArrowSpots(11f));
            Move(ai, world, 31f, () => FirstArrowSpots(9f));
        }

        // Leave after the 32.41s strips: tanks cross inward before the next pulse,
        // are the two nearest at 35.189s, and bait separate 8y circles at 37.592s.
        Move(ai, world, 32.7f, () => AiMove.Create(new(-6f, -6f), new(6f, 6f)).NaturalOrder());
        // Restore first-enmity / farthest tank separation for the two later autos.
        var gather = MeteorGatherPosition();
        Move(ai, world, 37.8f, () => AiMove.Create(new(0f, -8f), new(0f, 16f),
            gather, gather, gather, gather, gather, gather).NaturalOrder());
        // Last auto hits at 48.253s. Idle gathering is at the front of waymark 3.
        Move(ai, world, 48.3f, () => AiMove.All(MeteorGatherPosition()));
    }

    internal static void RunUnlimited(float startAngle, bool clockwise, SimWorld world, bool second = false)
    {
        var ai = new AiManager(world);
        // This mechanic's bait path starts at center; idle gathering resumes later.
        Move(ai, world, 0f, () => AiMove.All(Vector2.Zero));
        // Start 1.5 waymark intervals against the rotation from the sampled source.
        // Rotate only this bait path; the later clockspots and B stack stay fixed.
        var direction = clockwise ? 1f : -1f;
        var heading = startAngle - direction * 3f * MathF.PI / 8f;
        const float dodgeDistance = 7f; // 6y puddle + 1y clearance.
        const float turnRadius = 2f * dodgeDistance;
        // Chord length is one dodge, not a fixed 45-degree lap around the boss.
        var turnAngle = 2f * MathF.Asin(dodgeDistance / (2f * turnRadius));
        for (var step = 1; step <= 5; step++)
        {
            // Turn on the third bait instead of taking a third radial step
            // to 18.9y, which would run next to the 20y electric fence.
            var radius = Math.Min(step, 2) * dodgeDistance;
            var angle = heading + direction * Math.Max(0, step - 2) * turnAngle;
            var destination = new Vector2(radius * MathF.Sin(angle), -radius * MathF.Cos(angle));
            Move(ai, world, FirstPuddleAt + (step - 1) * PuddleInterval + 0.05f,
                () => AiMove.All(destination));
        }
        var finalHeading = heading + direction * 4f * turnAngle;
        var south = new Vector2(MathF.Sin(finalHeading), -MathF.Cos(finalHeading));
        // Dodge the sixth bait outward before returning. Cutting straight inward
        // here clips the fifth puddle when the manual player's bait is off-stack.
        Move(ai, world, LastPuddleAt + 0.05f, () => AiMove.All(south * turnRadius));
        if (!second)
        {
            // Finish the short turn and let the fifth puddle resolve first.
            Move(ai, world, LastPuddleAt + PuddleDelay - PuddleInterval + 0.3f,
                () => ClockSpots(2f));
            Move(ai, world, LastPuddleAt + PuddleDelay + 0.05f, () => ClockSpots(13.63f));
            // Two proteans are finished. B is east; both tanks stand boss-side.
            Move(ai, world, SecondProteanAt + 0.05f, StackAtB);
            return;
        }

        // Only tanks cross the center. The group is the Y's local south, not
        // geographic south or waymark 3. Half a 2y waymark = 1y inward.
        Move(ai, world, 22.55f, () => AiMove.Create(Vector2.Zero, Vector2.Zero).NaturalOrder());
        Move(ai, world, LastPuddleAt + PuddleDelay + 0.05f, () =>
        {
            var stack = south * (turnRadius - 1f);
            return AiMove.Create(null, null, stack, stack, stack, stack, stack, stack).NaturalOrder();
        });
        var left = new Vector2(-south.Y, south.X);
        Move(ai, world, 23.765f, () => AiMove.Create(
            -south * 6f + left * 6f, -south * 6f - left * 6f).NaturalOrder());
        // After Dive, retain the six-player stack; restore tank enmity/farthest
        // separation for the following autos without routing the group to 3.
        Move(ai, world, 26.18f, () => AiMove.Create(-south * 8f, south * 16f).NaturalOrder());
        RunMeteorTail(world, SecondUnlimitedMeteorAt);
    }

    internal static void RunMeteorTail(SimWorld world, float meteorOffset)
    {
        var ai = new AiManager(world);
        // Meteor puddles snapshot the party before the cast resolves. The idle
        // position is the inward side of waymark 3, not arena center.
        Move(ai, world, MathF.Max(0f, meteorOffset - 0.516f),
            () => AiMove.All(MeteorGatherPosition()));
        // The eight puddles are a mechanic bait, not idle positioning. Start them
        // at center so every clock spot is reachable before the 3.987s detonation.
        Move(ai, world, meteorOffset, () => AiMove.All(Vector2.Zero));
        Move(ai, world, meteorOffset + 5.064f, () => ClockSpots(13.63f));

        // D4 aims at the arena center from its spread position. Keep it stationary
        // until its job's real cast completes, not the log's slidecast time.
        var casterLbAt = meteorOffset + CasterLimitBreakBegin - SecondUnlimitedMeteorAt;
        var rangedLbAt = meteorOffset + RangedLimitBreakBegin - SecondUnlimitedMeteorAt;
        Move(ai, world, rangedLbAt - 1.1f,
            () => AiMove.Single((int)PartyRole.PhysRangedDps, new(0f, -19f)).NaturalOrder());
        world.Events.Add(casterLbAt,
            () => StartLimitBreak(world, PartyRole.CasterDps, Vector3.Zero));
        world.Events.Add(rangedLbAt,
            () => StartLimitBreak(world, PartyRole.PhysRangedDps, new(0f, 0f, 10f)));
    }

    internal static void RunMeteorFlares(
        SimWorld world, TopP6MeteorFlarePlan plan, float untilResolve)
    {
        // Give the visible marker a 1s human reaction window; keep the original
        // resolve deadline. A member still casting/recovering waits longer.
        world.Events.Add(1f, () =>
        {
            foreach (var (role, position) in plan.MarkedPositions)
                MoveMeteorMemberWhenReady(world, role, position);
            foreach (var role in plan.UnmarkedRoles)
                MoveMeteorMemberWhenReady(world, role, plan.StackPosition);
        });
        var ai = new AiManager(world);
        Move(ai, world, untilResolve + 0.1f, () => AiMove.All(MeteorGatherPosition()));
    }

    internal static Vector2 MeteorGatherPosition()
    {
        var mark = TopUtils.TopWaymarks.First(waymark => waymark.Slot == WaymarkSlot.Three).Offset;
        var point = new Vector2(mark.X, mark.Z);
        return point + Vector2.Normalize(-point) * 2f;
    }

    private static void MoveMeteorMemberWhenReady(SimWorld world, PartyRole role, Vector2 position)
    {
        if (role == world.Party.PlayerRole) return;
        if (world.LimitBreaks?.IsBusy(role) == true)
        {
            world.Events.Add(0.05f, () => MoveMeteorMemberWhenReady(world, role, position));
            return;
        }
        MoveNpc(world, role, position);
    }

    private static void MoveNpc(SimWorld world, PartyRole role, Vector2 position)
    {
        if (role == world.Party.PlayerRole) return;
        if (world.Party.Get(role) is SimNpc npc && npc.IsAlive())
            npc.MoveTo(new Vector3(position.X, 0f, position.Y));
    }

    private static void Move(AiManager ai, SimWorld world, float time, Func<IAiMove> positions)
        => ai.Move(time, () => NpcOnly(world, positions()), jitter: 0f);

    private static IAiMove NpcOnly(SimWorld world, IAiMove move)
        => new PlayerSafeMove(move, world.Party.PlayerRole);

    private sealed class PlayerSafeMove(IAiMove inner, PartyRole playerRole) : IAiMove
    {
        public Vector2? this[int role] => (PartyRole)role == playerRole ? null : inner[role];
    }

    internal static void StartLimitBreak(SimWorld world, PartyRole role, Vector3? groundLocation)
    {
        var runtime = world.LimitBreaks;
        if (runtime == null || role == world.Party.PlayerRole ||
            world.Party.Get(role) is SimNetworkPuppet { Orphaned: false } ||
            !runtime.IsAvailable || runtime.IsBusy(role))
            return;
        var actionId = runtime.ActionFor(role);
        if (actionId == 0) return;

        var action = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRow(actionId);
        Vector3? location = null;
        SimCharacter? target = null;
        if (action.TargetArea)
            location = groundLocation;
        else
        {
            var support = role is PartyRole.MainTank or PartyRole.OffTank or PartyRole.RegenHealer or PartyRole.ShieldHealer;
            target = support ? world.Party.Get(role) : FindMeteorTarget(world);
            if (!support) world.Party.Get(role)?.SetRotation(0f);
        }
        runtime.TryStart(role, actionId, location, target);
    }

    private static SimCharacter? FindMeteorTarget(SimWorld world)
    {
        foreach (var child in world.Children)
        {
            if (child is not SimEnemy enemy || !enemy.IsActive || !enemy.Targetable) continue;
            var p = enemy.Position;
            if (MathF.Abs(p.X) <= 2f && MathF.Abs(p.Z) is >= 8f and <= 12f)
                return enemy;
        }
        return null;
    }

    private static Vector2 ClockPosition(PartyRole role, float radius)
    {
        var diagonal = radius / MathF.Sqrt(2f);
        return role switch
        {
            PartyRole.MainTank => new(-diagonal, -diagonal),
            PartyRole.OffTank => new(0f, radius),
            PartyRole.RegenHealer => new(-radius, 0f),
            PartyRole.ShieldHealer => new(radius, 0f),
            PartyRole.MeleeDpsA => new(-diagonal, diagonal),
            PartyRole.MeleeDpsB => new(diagonal, diagonal),
            PartyRole.PhysRangedDps => new(0f, -radius),
            PartyRole.CasterDps => new(diagonal, -diagonal),
            _ => Vector2.Zero,
        };
    }

    internal static void RunSecondArrow(bool inFirst, SimWorld world)
    {
        var ai = new AiManager(world);
        // Wild Charge has resolved. Separate MT / farthest ST for both autos.
        Move(ai, world, 0.05f, () => AiMove.Create(new(0f, -8f), new(0f, 16f),
            new(0f, 6f), new(0f, 6f), new(0f, 6f), new(0f, 6f),
            new(0f, 6f), new(0f, 6f)).NaturalOrder());
        // WC2 overlaps the final arrow pulses: establish each role's quadrant
        // before the arrows, then open eight lanes without crossing live strips.
        Move(ai, world, SecondArrowDelay + 2f,
            () => ArrowSpots(inFirst ? 6f : 9f, inFirst ? 6f : 9f, inFirst ? 6f : 9f));
        Move(ai, world, SecondArrowDelay + 10.5f,
            () => ArrowSpots(inFirst ? 4f : 11f, inFirst ? 4f : 11f, inFirst ? 4f : 11f));
        if (inFirst)
        {
            // Cardinals stay outside the central strips until the +17.91 pulse.
            Move(ai, world, SecondArrowDelay + 14.5f, () => ArrowSpots(11f, 12f, 6f));
            Move(ai, world, SecondArrowDelay + 17.96f, () => ArrowSpots(11f, 12f, 0f));
            // Second protean +21.07 precedes the ±12.5y strips at +21.91.
            Move(ai, world, SecondArrowDelay + 21.12f, () => ClockSpots(9f));
            Move(ai, world, SecondArrowDelay + 21.96f, StackAtB);
        }
        else
        {
            Move(ai, world, SecondArrowDelay + 14.5f, () => ArrowSpots(9f, 9f, 9f));
            Move(ai, world, SecondArrowDelay + 16.5f, () => ArrowSpots(11f, 12f, 0f));
            // First protean +19.07 precedes the ±12.5y strips at +19.91.
            Move(ai, world, SecondArrowDelay + 19.12f, () => ClockSpots(9f));
            Move(ai, world, SecondArrowDelay + 21.12f, StackAtB);
        }
        var secondChargeAt = SecondCannonAt + 11.37f;
        var gather = MeteorGatherPosition();
        Move(ai, world, secondChargeAt + 0.05f, () => AiMove.Create(new(0f, -8f), new(0f, 16f),
            gather, gather, gather, gather, gather, gather).NaturalOrder());
        Move(ai, world, secondChargeAt + 8.1f, () => AiMove.All(MeteorGatherPosition()));
    }

    private static IAiMove ArrowSpots(float diagonal, float cardinal, float offset) =>
        AiMove.Create(new(-diagonal, -diagonal), new(-offset, cardinal),
            new(-cardinal, -offset), new(cardinal, offset), new(-diagonal, diagonal),
            new(diagonal, diagonal), new(offset, -cardinal), new(diagonal, -diagonal)).NaturalOrder();

    private static IAiMove StackAtB() => AiMove.Create(
        new(11.63f, 0f), new(11.63f, 0f), new(13.63f, 0f), new(13.63f, 0f),
        new(13.63f, 0f), new(13.63f, 0f), new(13.63f, 0f), new(13.63f, 0f)).NaturalOrder();

    private static IAiMove ClockSpots(float radius)
    {
        var diagonal = radius / MathF.Sqrt(2f);
        // MT=4, ST=C, H1=D, H2=B, D1=3, D2=2, D3=A, D4=1.
        return AiMove.Create(new(-diagonal, -diagonal), new(0f, radius),
            new(-radius, 0f), new(radius, 0f), new(-diagonal, diagonal),
            new(diagonal, diagonal), new(0f, -radius), new(diagonal, -diagonal)).NaturalOrder();
    }

    private static IAiMove FirstArrowSpots(float distance) => AiMove.All(new(-distance, distance));
}
