using System.Linq;
using System.Numerics;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;

namespace AnoMech.Scenarios.Top.P4BlueScreen;

// Upstream source labels this pair `tuuufless` / `B站莫古力`; the 維護者-approved
// UI labels intentionally include the shared `打法` prefix and omit `B站`.
public sealed class TopP4BlueScreenAi(bool moogle = false) : IScenarioAi<TopP4BlueScreenState>
{
    public string Name => moogle ? "打法 莫古力" : "打法 tuufless";
    public string? Group => moogle ? "陸服" : "日服";
    private TopP4BlueScreenState state = null!;

    public void Run(TopP4BlueScreenState s, SimWorld world)
    {
        state = s;
        var ai = new AiManager(world);
        ai.Move(0.1f, Spread, jitter: 0.05f);
        ai.Move(15.0f, () => Stack(0, 14.5f), jitter: 0.05f);
        ai.Move(20.0f, ReturnAroundFirstRing, jitter: 0.05f);
        ai.Move(22.35f, Spread, jitter: 0.05f);
        ai.Move(25.05f, () => Stack(1, 9f), jitter: 0.05f);
        ai.Move(26.45f, () => Stack(1, 14.5f), jitter: 0.05f);
        ai.Move(29.85f, Spread, jitter: 0.05f);
        ai.Move(35.2f, () => Stack(2, 14.5f), jitter: 0.05f);
        ai.Move(41.6f, () => Stack(2, 9f), jitter: 0.05f);
        ai.Move(45.7f, () => AiMove.Create(Enumerable.Repeat<Vector2?>(new(0, 9), 8).ToArray()).NaturalOrder(), jitter: 0.3f);
    }

    private IAiMove Spread() => AiMove.Create(Enumerable.Range(0, 8)
        .Select(i => (Vector2?)TopP4BlueScreenRules.SpreadPosition((PartyRole)i)).ToArray()).NaturalOrder();

    private IAiMove Stack(int round, float radius) => AiMove.Create(state.Sides(round)
        .Select(west => (Vector2?)TopP4BlueScreenRules.StackPosition(west, radius)).ToArray()).NaturalOrder();

    private IAiMove ReturnAroundFirstRing()
    {
        var west = state.Sides(0);
        return AiMove.Create(Enumerable.Range(0, 8).Select(i => (Vector2?)(Vector2.Normalize(
            TopP4BlueScreenRules.StackPosition(west[i], 14.5f) + TopP4BlueScreenRules.SpreadPosition((PartyRole)i)) * 8f)).ToArray()).NaturalOrder();
    }
}
