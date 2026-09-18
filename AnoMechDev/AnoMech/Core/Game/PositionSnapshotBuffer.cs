using System.Numerics;

namespace AnoMech.Core.Game;

/// <summary>
/// Short history of an actor's position so AoE damage can be judged on where the game would
/// have snapshotted it — real FFXIV takes the position shortly before the omen disappears, so
/// stepping in during the last moments is still safe (維護者 2026-09-18 實機觀察 ~0.3 s).
/// Pure logic: no native access, so the damage rule is unit-testable.
/// </summary>
public struct PositionSnapshotBuffer
{
    public const float LeadSeconds = 0.3f;
    private const int Samples = 64;

    private (float Time, Vector3 Position)[]? history;
    private int count;
    private int next;
    private float clock;

    public void Record(float deltaSeconds, Vector3 position)
    {
        history ??= new (float, Vector3)[Samples];
        clock += deltaSeconds;
        history[next] = (clock, position);
        next = (next + 1) % Samples;
        if (count < Samples) count++;
    }

    /// <summary>Position as of <see cref="LeadSeconds"/> ago, or <paramref name="live"/> when
    /// there is no history yet (just spawned). Never extrapolates.</summary>
    public readonly Vector3 Resolve(Vector3 live)
    {
        if (history is null || count == 0) return live;
        var wanted = clock - LeadSeconds;
        for (var step = 1; step <= count; step++)
        {
            var index = (next - step + Samples) % Samples;
            if (history[index].Time <= wanted) return history[index].Position;
        }
        // History does not reach that far back yet: the oldest sample is the best answer.
        return history[(next - count + Samples) % Samples].Position;
    }

    public void Reset()
    {
        count = 0;
        next = 0;
        clock = 0f;
    }
}
