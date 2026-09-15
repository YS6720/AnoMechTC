using AnoMech.Core.Game.Ai;
using AnoMech.Core.SimObjects;

namespace AnoMech.Scenarios.Top.P3HelloWorld;

/// <summary>Recorded route and contact chase entry point for the NPC driver.</summary>
public sealed class TopP3HelloWorldAi : IScenarioAi<TopP3HelloWorldState>
{
    private readonly TopP3HelloWorldMovementDriver driver = new();
    public string Name => "P3 Hello World 四輪固定位置";

    public void Run(TopP3HelloWorldState state, SimWorld world)
        => Run(state, world, true);

    public void Run(TopP3HelloWorldState state, SimWorld world, bool enabled)
        => driver.Run(state, world, enabled);

    public void Tick(float elapsed) => driver.Tick(elapsed);
}
