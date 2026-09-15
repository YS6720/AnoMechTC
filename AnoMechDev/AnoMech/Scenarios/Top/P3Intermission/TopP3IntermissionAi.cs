using AnoMech.Core.Game.Ai;
using AnoMech.Core.SimObjects;

namespace AnoMech.Scenarios.Top.P3Intermission;

/// <summary>Recorded-position AI for the seven non-player rehearsal actors.</summary>
public sealed class TopP3IntermissionAi : IScenarioAi<TopP3IntermissionState>
{
    public string Name => "P3 轉場固定位置";

    public void Run(TopP3IntermissionState state, SimWorld world)
    {
        foreach (var role in TopP3IntermissionRules.RecordingOrder)
        {
            if (role == state.PlayerRole) continue;
            foreach (var point in TopP3IntermissionRules.RuntimeRouteFor(role))
            {
                var move = point;
                world.Events.Add(move.At, () => ApplyMove(move, role, state.PlayerRole, world));
            }
        }
    }

    private static void ApplyMove(
        TopP3RoutePoint move,
        AnoMech.Core.Game.Party.PartyRole role,
        AnoMech.Core.Game.Party.PartyRole playerRole,
        SimWorld world)
    {
        // Only a spawned SimNpc is ever moved.  The explicit role check protects
        // the local player even if PartyRole/slot wiring changes later; MoveTo on
        // SimPlayer is a no-op too, but this path must never call it or rotate it.
        if (role == playerRole) return;
        if (world.Party.Get(role) is not SimNpc npc || !npc.IsAlive()) return;
        npc.MoveTo(move.Target, TopP3IntermissionRules.MoveSpeed);
    }
}
