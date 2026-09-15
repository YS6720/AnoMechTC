using System.Collections.Generic;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.SimObjects;

namespace AnoMech.Scenarios.Top.P3Monitors;

public sealed class TopP3MonitorsAi : IScenarioAi<TopP3MonitorsState>
{
    private const float MoveSpeed = TopP3MonitorRules.MoveSpeed;

    public string Name => "標準AI";

    public void Run(TopP3MonitorsState state, SimWorld world)
    {
        var localRole = world.Party.PlayerRole;
        var queueMoves = TopP3MonitorRules.PlanQueueMoves(localRole);
        world.Events.Add(TopP3MonitorRules.QueueAt, () => ApplyQueueMoves(queueMoves, world));

        var moves = TopP3MonitorRules.PlanMoves(
            state.Assignment,
            state.BossSide,
            state.MonitorStatuses,
            localRole);
        // Movement starts 0.25s after the recorded buff/VFX. MoveTo retains each
        // monitor's finalRotation until arrival instead of a timer-based SetRotation.
        world.Events.Add(TopP3MonitorRules.MoveStartAt, () => ApplyMoves(moves, world));
    }

    private static void ApplyQueueMoves(
        IReadOnlyList<TopP3MonitorMove> moves,
        SimWorld world)
    {
        foreach (var move in moves)
        {
            // The local player is intentionally absent from queueMoves. Keep the
            // NPC type guard here so this path can never reposition the real player.
            if (world.Party.Get(move.Role) is not SimNpc character || !character.IsAlive()) continue;
            character.SetPosition(move.Target);
        }
    }

    private static void ApplyMoves(
        IReadOnlyList<TopP3MonitorMove> moves,
        SimWorld world)
    {
        foreach (var move in moves)
        {
            // Only spawned NPCs move automatically; the local player walks to the slot.
            if (world.Party.Get(move.Role) is not SimNpc character || !character.IsAlive()) continue;
            character.MoveTo(move.Target, MoveSpeed, move.FinalRotation);
        }
    }
}
