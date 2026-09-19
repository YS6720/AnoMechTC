using System;
using System.Numerics;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using static AnoMech.Scenarios.Top.P6AlphaOmega.TopP6AlphaOmegaScenario;

namespace AnoMech.Scenarios.Top.P6AlphaOmega;

public sealed class TopP6AlphaOmegaAi : IScenarioAi<bool>
{
    public string Name => "兩點集合／雙坦分離";

    public void Run(bool inFirst, SimWorld world)
    {
        var ai = new AiManager(world);
        // Original P6 safe-square timing, shifted by 12.5s; everyone uses NE.
        ai.Move(14.5f, () => Northeast(inFirst ? 6f : 9f), jitter: 0f);
        ai.Move(23f, () => Northeast(inFirst ? 4f : 11f), jitter: 0f);
        if (inFirst)
        {
            ai.Move(27f, () => Northeast(11f), jitter: 0f);
            ai.Move(33f, () => AiMove.Create(null, null,
                new(9f, -9f), new(9f, -9f), new(9f, -9f), new(9f, -9f), new(9f, -9f), new(9f, -9f)).NaturalOrder(), jitter: 0f);
        }
        else
        {
            ai.Move(27f, () => Northeast(9f), jitter: 0f);
            ai.Move(29f, () => Northeast(11f), jitter: 0f);
            ai.Move(31f, () => Northeast(9f), jitter: 0f);
        }

        // Leave after the 32.41s strips: tanks cross inward before the next pulse,
        // are the two nearest at 35.189s, and bait separate 8y circles at 37.592s.
        ai.Move(32.7f, () => AiMove.Create(new(-6f, -6f), new(6f, 6f)).NaturalOrder(), jitter: 0f);
        // Restore first-enmity / farthest tank separation for the two later autos.
        ai.Move(37.8f, () => AiMove.Create(new(0f, -8f), new(0f, 16f),
            new(0f, 6f), new(0f, 6f), new(0f, 6f), new(0f, 6f), new(0f, 6f), new(0f, 6f)).NaturalOrder(), jitter: 0f);
        // Last auto hits at 48.253s. Gather during Unlimited's 48.562–53.555s cast.
        ai.Move(48.3f, () => AiMove.All(Vector2.Zero), jitter: 0f);
    }

    internal static void RunUnlimited(bool clockwise, SimWorld world)
    {
        var ai = new AiManager(world);
        // First source is NE. Start 1.5 waymark intervals against the rotation:
        // CW between A/4, CCW between B/2. Each straight dodge clears the 6y bait.
        var direction = clockwise ? 1f : -1f;
        var heading = MathF.PI / 4f - direction * 3f * MathF.PI / 8f;
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
            ai.Move(FirstPuddleAt + (step - 1) * PuddleInterval + 0.05f,
                () => AiMove.All(destination), jitter: 0f);
        }
        // Sixth snapshot: immediately head inward toward each clock direction.
        // The short turns leave bait five nearby, so use a deep inner approach
        // rather than cutting through that still-pending circle on the way back.
        ai.Move(LastPuddleAt + 0.05f, () => ClockSpots(2f), jitter: 0f);
        ai.Move(LastPuddleAt + PuddleDelay + 0.05f, () => ClockSpots(13.63f), jitter: 0f);
        // Two proteans are finished. B is east; both tanks stand boss-side.
        ai.Move(SecondProteanAt + 0.05f, () => AiMove.Create(
            new(11.63f, 0f), new(11.63f, 0f), new(13.63f, 0f), new(13.63f, 0f),
            new(13.63f, 0f), new(13.63f, 0f), new(13.63f, 0f), new(13.63f, 0f)).NaturalOrder(), jitter: 0f);
    }

    internal static void RunSecondArrow(bool inFirst, SimWorld world)
    {
        var ai = new AiManager(world);
        // Wild Charge has resolved. Separate MT / farthest ST for both autos.
        ai.Move(0.05f, () => AiMove.Create(new(0f, -8f), new(0f, 16f),
            new(0f, 6f), new(0f, 6f), new(0f, 6f), new(0f, 6f), new(0f, 6f), new(0f, 6f)).NaturalOrder(), jitter: 0f);
        // Last auto hits at +8.2s; reuse the opening's NE safe-square route
        // without its Cosmo Dive tank split. This segment ends after the arrows.
        ai.Move(SecondArrowDelay + 2f, () => Northeast(inFirst ? 6f : 9f), jitter: 0f);
        ai.Move(SecondArrowDelay + 10.5f, () => Northeast(inFirst ? 4f : 11f), jitter: 0f);
        if (inFirst)
        {
            ai.Move(SecondArrowDelay + 14.5f, () => Northeast(11f), jitter: 0f);
            ai.Move(SecondArrowDelay + 20.5f, () => Northeast(9f), jitter: 0f);
        }
        else
        {
            ai.Move(SecondArrowDelay + 14.5f, () => Northeast(9f), jitter: 0f);
            ai.Move(SecondArrowDelay + 16.5f, () => Northeast(11f), jitter: 0f);
            ai.Move(SecondArrowDelay + 18.5f, () => Northeast(9f), jitter: 0f);
        }
    }

    private static IAiMove ClockSpots(float radius)
    {
        var diagonal = radius / MathF.Sqrt(2f);
        // MT=4, ST=C, H1=D, H2=B, D1=3, D2=2, D3=A, D4=1.
        return AiMove.Create(new(-diagonal, -diagonal), new(0f, radius),
            new(-radius, 0f), new(radius, 0f), new(-diagonal, diagonal),
            new(diagonal, diagonal), new(0f, -radius), new(diagonal, -diagonal)).NaturalOrder();
    }

    private static IAiMove Northeast(float distance) => AiMove.All(new(distance, -distance));
}
