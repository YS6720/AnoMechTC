using System.Collections.Generic;
using System.Numerics;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;

namespace AnoMech.Scenarios.Top.P3HelloWorld;

/// <summary>
/// Native adapter only. Scenario Tick supplies its actual elapsed time after
/// contact resolution; MoveTo takes effect on the following World.Tick. No
/// scheduler callbacks, historical catch-up moves, teleports or player writes.
/// </summary>
internal sealed class TopP3HelloWorldMovementDriver
{
    private readonly TopP3HelloWorldMovementSession session = new();
    private SimWorld world = null!;
    private TopP3HelloWorldState state = null!;

    public void Run(TopP3HelloWorldState state, SimWorld world, bool enabled)
    {
        this.state = state;
        this.world = world;
        session.Start(state, enabled);
    }

    public void Tick(float now)
    {
        var positions = new Dictionary<PartyRole, Vector3>();
        foreach (var role in TopP3HelloWorldRules.RecordingOrder)
            if (world.Party.Get(role) is { } member && member.IsAlive())
                positions[role] = member.Position;
        foreach (var command in session.Advance(now, positions))
            if (command.Role != state.PlayerRole
                && world.Party.Get(command.Role) is SimNpc npc && npc.IsAlive())
                npc.MoveTo(command.Target, command.Speed);
    }
}
