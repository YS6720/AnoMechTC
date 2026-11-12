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

    // How far a character at `position` pushed away from `from` (both scenario-local) travels
    // before leaving the circle of `radius` around the origin, at most `distance`. Outside and
    // pushed further out = 0; co-located with `from` (no direction) = the full distance.
    public static float PushDistanceWithin(Vector3 position, Vector3 from, float distance, float radius)
    {
        var start = new Vector2(position.X, position.Z);
        var away = start - new Vector2(from.X, from.Z);
        if (away == Vector2.Zero) return distance;
        var direction = Vector2.Normalize(away);
        var along = Vector2.Dot(start, direction);
        var discriminant = along * along - (start.LengthSquared() - radius * radius);
        var toCircle = discriminant <= 0f ? 0f : MathF.Max(0f, -along + MathF.Sqrt(discriminant));
        return MathF.Min(distance, toCircle);
    }
}
