using System.Numerics;
using AnoMech.Core.Game;

namespace AnoMech.Core.SimObjects;

// Position is scenario-space (relative to SimWorld.ScenarioOrigin). Use
// SimWorld.Coordinates.ToGlobal(...) if you need world coords for native interop.
// Rotation is absolute radians (independent of scenario origin).
public interface IPositioned
{
    Vector3 Position { get; }
    float Rotation { get; }
    /// <summary>
    /// Where this actor was when the game would have snapshotted an AoE: real FFXIV takes the
    /// position slightly before the omen disappears, so stepping in during the last moments is
    /// still safe. Damage geometry uses this; movement/targeting keeps the live position.
    /// Defaults to the live position for anything without history (ground marks, enemies).
    /// </summary>
    Vector3 SnapshotPosition => Position;
    static IPositioned From(Vector3 target) => new At(target);

    private readonly record struct At(Vector3 Position, float Rotation = 0f) : IPositioned;
}

public static class PositionedExtensions
{
    public static Placement Placement(this IPositioned p) => new(p.Position, p.Rotation);
}
