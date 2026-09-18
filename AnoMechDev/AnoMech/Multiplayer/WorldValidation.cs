using System;
using System.Collections.Generic;
using AnoMech.Core;
using AnoMech.Core.Game.Party;
namespace AnoMech.Multiplayer;

// Pure protocol validation for world-state messages. This class deliberately has
// no plugin/native dependency so the relay can reject malformed batches before
// they reach the game adapter.
public static class WorldValidation
{
    private const float QuaternionLimit = 2f;
    private const float MaxDuration = 3600f;

    public static bool Validate(MpMessage message)
        => message switch
        {
            WorldSnapshotMessage world => ValidateWorld(world),
            RolesSnapshotMessage roles => ValidateRoles(roles),
            PoseSnapshotMessage poses => ValidatePoses(poses),
            WorldEventMessage worldEvent => ValidateEventMessage(worldEvent),
            _ => true,
        };

    public static bool ValidateWorld(WorldSnapshotMessage message)
    {
        if (message is null || message.Enemies is null || message.EventObjects is null ||
            message.Tethers is null || message.StaticVisuals is null || message.PartyMarkers is null ||
            message.Enemies.Length > MpLimits.Enemies ||
            message.EventObjects.Length > MpLimits.EventObjects ||
            message.Tethers.Length > MpLimits.Tethers ||
            message.StaticVisuals.Length > MpLimits.StaticVisuals ||
            message.PartyMarkers.Length > MpLimits.Markers)
            return false;

        var enemyIds = new HashSet<int>();
        foreach (var enemy in message.Enemies)
        {
            if (!ValidateEnemy(enemy) || !enemyIds.Add(enemy.NetId)) return false;
        }

        var eventObjectIds = new HashSet<int>();
        foreach (var eventObject in message.EventObjects)
        {
            if (!ValidateEventObject(eventObject) || !eventObjectIds.Add(eventObject.NetId)) return false;
        }

        var tetherIds = new HashSet<int>();
        foreach (var tether in message.Tethers)
        {
            if (!ValidateTether(tether) || !tetherIds.Add(tether.NetId) ||
                (tether.Source.Kind == MpEntityKind.Enemy && !enemyIds.Contains(tether.Source.Id)) ||
                (tether.Target.Kind == MpEntityKind.Enemy && !enemyIds.Contains(tether.Target.Id)))
                return false;
        }

        var visualIds = new HashSet<int>();
        foreach (var visual in message.StaticVisuals)
        {
            if (!ValidateStaticVisual(visual) || !visualIds.Add(visual.NetId)) return false;
        }
        var markerRoles = new HashSet<PartyRole>();
        var markerSigns = new HashSet<Sign>();
        if (message.PartyMarkerAcks is { } acks)
        {
            if (acks.Length > MpLimits.Members) return false;
            var ackPeers = new HashSet<Guid>();
            foreach (var ack in acks)
                if (ack is null || ack.PeerId == Guid.Empty || ack.RequestId < 0 || !ackPeers.Add(ack.PeerId))
                    return false;
        }
        foreach (var marker in message.PartyMarkers)
            if (!ValidatePartyMarker(marker) || !markerRoles.Add(marker.Role) || !markerSigns.Add(marker.Sign))
                return false;

        return true;
    }

    public static bool ValidateRoles(RolesSnapshotMessage message)
    {
        if (message is null || message.Roles is null || message.Roles.Length > MpLimits.Members)
            return false;

        var roles = new HashSet<PartyRole>();
        foreach (var role in message.Roles)
        {
            if (!ValidateRole(role) || !roles.Add(role.Role)) return false;
        }
        return true;
    }

    public static bool ValidateEventMessage(WorldEventMessage message)
        => message is not null && message.Event is not null && ValidateEvent(message.Event);

    public static bool ValidateEnemy(EnemyState enemy)
    {
        return enemy is not null &&
            enemy.NetId > 0 &&
            enemy.BnpcBaseId > 0 &&
            FiniteRange(enemy.Scale, 0.001f, 100f) &&
            FiniteRange(enemy.HitboxRadius, 0f, MpLimits.CoordinateLimit) &&
            enemy.EnemyListMode <= 3 &&
            ValidatePose(enemy.Pose) &&
            ValidateStatuses(enemy.Statuses) &&
            enemy.CurrentHp <= enemy.MaxHp;
    }

    public static bool ValidatePoses(PoseSnapshotMessage message)
    {
        if (message is null || message.Poses is null || message.Poses.Length is 0 or > MpLimits.Members)
            return false;
        var seen = 0;
        foreach (var pose in message.Poses)
        {
            if (!ValidateRolePose(pose)) return false;
            var bit = 1 << (int)pose.Role;
            if ((seen & bit) != 0) return false;   // 同一個角色出現兩次＝壞封包
            seen |= bit;
        }
        return true;
    }

    // 動畫 id 不在這裡設上限：真表最大 row id 是 25000，任何寫死的數字都會誤殺真技能
    //（2026-09-19 事故）。守門的是套用前的 ActionTimeline 查表。
    public static bool ValidateRolePose(RolePoseState pose)
        => pose is not null && Enum.IsDefined(pose.Role) && ValidatePose(pose.Pose);

    public static bool ValidateRole(RoleState role)
    {
        return role is not null && Enum.IsDefined(role.Role) &&
            ValidatePose(role.Pose) &&
            ValidateStatuses(role.Statuses) &&
            FiniteRange(role.HpFraction, 0f, 1f);
    }
    public static bool ValidatePartyMarker(PartyMarkerState marker)
        => marker is not null && Enum.IsDefined(marker.Role) && Enum.IsDefined(marker.Sign);

    public static bool ValidateEventObject(EventObjectState eventObject)
    {
        return eventObject is not null &&
            eventObject.NetId > 0 &&
            eventObject.EobjId > 0 &&
            ValidatePose(eventObject.Pose);
    }

    public static bool ValidateTether(TetherState tether)
    {
        return tether is not null && tether.NetId > 0 && tether.TetherId > 0 &&
            ValidateEntity(tether.Source) && ValidateEntity(tether.Target) &&
            tether.Source != tether.Target;
    }

    public static bool ValidateStaticVisual(StaticVisualState visual)
    {
        return visual is not null && visual.NetId > 0 &&
            IsResourceKey(visual.ResourceKey, "static:") &&
            ValidateVector(visual.Position) && ValidateQuaternion(visual.Rotation) &&
            ValidateVector(visual.Scale) &&
            visual.Scale.X >= 0f && visual.Scale.Y >= 0f && visual.Scale.Z >= 0f &&
            visual.Scale.X <= 100f && visual.Scale.Y <= 100f && visual.Scale.Z <= 100f;
    }

    public static bool ValidateEvent(WorldEvent value)
    {
        if (value is null) return false;
        return value switch
        {
            FailureNoticeEvent e => (e.Role is not { } role || Enum.IsDefined(role)) &&
                (!e.Death || e.Role.HasValue) && MpValidation.SingleLineText(e.Reason, 1024),
            ActionTimelineEvent e => ValidateEntity(e.Actor) && e.TimelineId > 0 && e.LoopId <= 0x7FFF,
            ResetTimelineEvent e => ValidateEntity(e.Actor),
            NativeCastEvent e => ValidateCast(e),
            NativeActionEffectEvent e => ValidateActionEffect(e),
            ActorVfxEvent e => ValidateEntity(e.Actor) && IsResourceKey(e.ResourceKey, "actor:") &&
                FiniteRange(e.Duration, 0f, MaxDuration),
            RemoveActorVfxEvent e => ValidateEntity(e.Actor) && IsResourceKey(e.ResourceKey, "actor:"),
            MapEffectEvent e => true,
            DirectorEvent e => true,
            WeatherEvent e => e.Transition >= 0f && float.IsFinite(e.Transition) && e.Transition <= MaxDuration,
            KnockbackEvent e => Enum.IsDefined(e.Role) && ValidateVector(e.Source) &&
                FiniteRange(e.Distance, 0f, MpLimits.CoordinateLimit) &&
                FiniteRange(e.Speed, 0f, MpLimits.CoordinateLimit),
            TeleportEvent e => Enum.IsDefined(e.Role) && ValidateVector(e.Position),
            FaceEvent e => Enum.IsDefined(e.Role) && float.IsFinite(e.Rotation),
            _ => false,
        };
    }

    private static bool ValidateCast(NativeCastEvent value)
    {
        return ValidateEntity(value.Actor) && value.ActionId > 0 &&
            FiniteRange(value.CastSeconds, 0f, MaxDuration) &&
            FiniteRange(value.OmenDelay, 0f, MaxDuration) &&
            ValidateOptionalRotation(value.Rotation) && ValidateOptionalVector(value.Position) &&
            (!value.Target.HasValue || ValidateEntity(value.Target.Value));
    }

    private static bool ValidateActionEffect(NativeActionEffectEvent value)
    {
        if (!ValidateEntity(value.Actor) || value.ActionId == 0 ||
            !FiniteRange(value.AnimationLock, 0f, MaxDuration) ||
            value.Targets is null || value.Targets.Length > MpLimits.Members ||
            !ValidateOptionalRotation(value.Rotation) || !ValidateOptionalVector(value.Position) ||
            (value.AnimationTarget.HasValue && !ValidateEntity(value.AnimationTarget.Value)))
            return false;

        foreach (var target in value.Targets)
            if (!ValidateEntity(target)) return false;
        return true;
    }

    private static bool ValidateStatuses(StatusState[] statuses)
    {
        if (statuses is null || statuses.Length > MpLimits.Statuses) return false;
        var keys = new HashSet<StatusKey>();
        foreach (var status in statuses)
        {
            if (status is null || status.Id == 0 || !FiniteRange(status.Remaining, 0f, MaxDuration) ||
                (status.SourceRole is { } source && !MpValidation.Role(source)) ||
                (status.NativeRemainingOverride is { } nativeRemaining &&
                    (!float.IsFinite(nativeRemaining) || nativeRemaining >= 0f || nativeRemaining < -MaxDuration)) ||
                !keys.Add(status.Key))
                return false;
        }
        return true;
    }

    internal static bool ValidateEntity(MpEntity entity)
        => entity.Kind switch
        {
            MpEntityKind.Party => entity.Id >= 0 && entity.Id < MpLimits.Members,
            MpEntityKind.Enemy => entity.Id > 0,
            _ => false,
        };

    private static bool ValidatePose(MpPose pose)
        => ValidateVector(pose.Position) && float.IsFinite(pose.Rotation);

    private static bool ValidateVector(MpVector value)
        => FiniteRange(value.X, -MpLimits.CoordinateLimit, MpLimits.CoordinateLimit) &&
           FiniteRange(value.Y, -MpLimits.CoordinateLimit, MpLimits.CoordinateLimit) &&
           FiniteRange(value.Z, -MpLimits.CoordinateLimit, MpLimits.CoordinateLimit);

    private static bool ValidateQuaternion(MpQuaternion value)
        => FiniteRange(value.X, -QuaternionLimit, QuaternionLimit) &&
           FiniteRange(value.Y, -QuaternionLimit, QuaternionLimit) &&
           FiniteRange(value.Z, -QuaternionLimit, QuaternionLimit) &&
           FiniteRange(value.W, -QuaternionLimit, QuaternionLimit) &&
           (value.X * value.X + value.Y * value.Y + value.Z * value.Z + value.W * value.W) > 0.5f;

    private static bool ValidateOptionalVector(MpVector? value)
        => !value.HasValue || ValidateVector(value.Value);

    private static bool ValidateOptionalRotation(float? value)
        => !value.HasValue || float.IsFinite(value.Value);

    private static bool FiniteRange(float value, float min, float max)
        => float.IsFinite(value) && value >= min && value <= max;

    private static bool IsResourceKey(string? value, string prefix)
    {
        if (value is null || value.Length <= prefix.Length || value.Length > 256 ||
            !value.StartsWith(prefix, StringComparison.Ordinal)) return false;

        for (var i = prefix.Length; i < value.Length; i++)
        {
            var ch = value[i];
            if (!(ch is >= 'a' and <= 'z' || ch is >= 'A' and <= 'Z' ||
                  ch is >= '0' and <= '9' || ch is '.' or '-' or '_' or ':' or '/'))
                return false;
        }
        return true;
    }

}
