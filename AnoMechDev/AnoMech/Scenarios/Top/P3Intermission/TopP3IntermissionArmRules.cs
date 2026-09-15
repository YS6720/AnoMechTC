using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AnoMech.Scenarios.Top.P3Intermission;

public readonly record struct TopP3ArmDefinition(
    int Set,
    int Index,
    Vector3 Center,
    float Rotation,
    uint BnpcBaseId,
    uint NameId,
    ushort TimelineId)
{
    public int Slot => (Set - 1) * 3 + Index;
}

/// <summary>
/// Native arm actor mapping from the transition recording.  The two arm sides
/// and their appearance timelines are per actor, not per three-arm set.
/// </summary>
public static class TopP3IntermissionArmRules
{
    public const uint RightArmBaseId = 15719;
    public const uint LeftArmBaseId = 15718;
    public const uint RightArmNameId = 7638;
    public const uint LeftArmNameId = 7637;
    public const ushort RightArmTimelineId = 0x1E43;
    public const ushort LeftArmTimelineId = 0x1E44;
    public const float NativeScale = 1.4f;
    public const float NativeHitboxRadius = 1.68f;

    public static readonly IReadOnlyList<TopP3ArmDefinition> Definitions =
    [
        new(1, 0, new(-12.124f, 0f, -7f), MathF.PI / 3f,
            RightArmBaseId, RightArmNameId, RightArmTimelineId),
        new(1, 1, new(12.124f, 0f, -7f), -MathF.PI / 3f,
            LeftArmBaseId, LeftArmNameId, LeftArmTimelineId),
        new(1, 2, new(0f, 0f, 14f), MathF.PI,
            LeftArmBaseId, LeftArmNameId, LeftArmTimelineId),
        new(2, 0, new(12.124f, 0f, 7f), -2f * MathF.PI / 3f,
            RightArmBaseId, RightArmNameId, RightArmTimelineId),
        new(2, 1, new(0f, 0f, -14f), 0f,
            RightArmBaseId, RightArmNameId, RightArmTimelineId),
        new(2, 2, new(-12.124f, 0f, 7f), 2f * MathF.PI / 3f,
            LeftArmBaseId, LeftArmNameId, LeftArmTimelineId),
    ];

    public static IReadOnlyList<TopP3ArmDefinition> ForSet(int set)
    {
        if (set is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(set));
        return Definitions.Where(arm => arm.Set == set).ToArray();
    }

    public static TopP3ArmDefinition For(int set, int index)
    {
        if (index is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(index));
        return Definitions.Single(arm => arm.Set == set && arm.Index == index);
    }
}
