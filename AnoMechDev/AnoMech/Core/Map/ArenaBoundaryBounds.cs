using System;
using System.Numerics;

namespace AnoMech.Core.Map;

// Pure XZ-plane arena bounds shared by the native fence and its callers.
internal readonly struct ArenaBoundaryBounds
{
    private const float CircleGraceYalms = 1.0f;

    private readonly float limit;
    private readonly bool square;

    private ArenaBoundaryBounds(float limit, bool square)
    {
        this.limit = limit;
        this.square = square;
    }

    public static ArenaBoundaryBounds Circle(float radius)
    {
        var killRadius = radius + CircleGraceYalms;
        return new(killRadius * killRadius, square: false);
    }

    public static ArenaBoundaryBounds Square(float halfWidth)
        => new(halfWidth, square: true);

    public bool IsOutside(Vector3 local)
        => square
            ? MathF.Abs(local.X) > limit || MathF.Abs(local.Z) > limit
            : local.X * local.X + local.Z * local.Z > limit;
}
