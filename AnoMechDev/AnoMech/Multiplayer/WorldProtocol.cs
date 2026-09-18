using System;
using System.Text.Json.Serialization;
using AnoMech.Core;
using AnoMech.Core.Game.Party;

namespace AnoMech.Multiplayer;

public enum MpEntityKind { Party, Enemy }
public readonly record struct MpEntity(MpEntityKind Kind, int Id)
{
    public static MpEntity Party(PartyRole role) => new(MpEntityKind.Party, (int)role);
    public static MpEntity Enemy(int netId) => new(MpEntityKind.Enemy, netId);
}

public readonly record struct StatusKey(ushort Id, PartyRole? SourceRole = null);
public sealed record StatusState(
    ushort Id, ushort Param, float Remaining,
    PartyRole? SourceRole = null, float? NativeRemainingOverride = null)
{
    internal StatusKey Key => new(Id, SourceRole);
}
public sealed record EnemyState(
    int NetId, uint BnpcBaseId, uint ModelCharaId, ulong? MainHandModel, ulong? OffHandModel,
    uint NameId, byte Level,
    float Scale, float HitboxRadius, byte EnemyListMode, bool InEnemyList, bool Visible, bool Targetable,
    byte ModelState, byte ModeAttributeFlags, MpPose Pose, StatusState[] Statuses,
    uint CurrentHp, uint MaxHp);
public sealed record PartyMarkerState(PartyRole Role, Sign Sign);
// 60 Hz 只送**會動的東西**：位置、朝向、生死、動畫。狀態列與 HP 跟著 20 Hz 的完整
// RolesSnapshot 走——先前兩者綁在一起，量到的封包是 3455 B（每人 3 個狀態）對 1375 B，
// 六成的位元組是每秒 60 次重送同一份狀態列（維護者 2026-09-18 要求先做不影響穩定度的優化）。
public sealed record RolePoseState(PartyRole Role, bool Dead, MpPose Pose, ushort Timeline = 0);

// Timeline＝該成員當下的 base ActionTimeline（slot 0），讓替身播出來源端真正在播的動畫。
public sealed record RoleState(PartyRole Role, bool Dead, MpPose Pose, StatusState[] Statuses, float HpFraction,
    ushort Timeline = 0);
public sealed record EventObjectState(int NetId, uint EobjId, uint LayoutId, ushort TimelineState, ushort CurrentState, MpPose Pose);
public sealed record TetherState(int NetId, ushort TetherId, MpEntity Source, MpEntity Target);
public sealed record StaticVisualState(int NetId, string ResourceKey, MpVector Position, MpQuaternion Rotation, MpVector Scale);

public sealed record WorldSnapshotMessage(
    EnemyState[] Enemies, EventObjectState[] EventObjects, TetherState[] Tethers,
    StaticVisualState[] StaticVisuals, PartyMarkerState[] PartyMarkers,
    PartyMarkerAck[]? PartyMarkerAcks = null) : MpMessage, IHostMessage, IRunMessage, ILatestState;
// 只帶變動的角色（常態 1～3 人在動，先前每次都送滿 8 格）。掉一則沒有後果：
// 20 Hz 的完整 RolesSnapshot 會在 50 ms 內把所有人校正回來。
public sealed record PoseSnapshotMessage(RolePoseState[] Poses) : MpMessage, IHostMessage, IRunMessage, ILatestState;

public sealed record RolesSnapshotMessage(RoleState[] Roles) : MpMessage, IHostMessage, IRunMessage, ILatestState;
public sealed record WorldEventMessage(WorldEvent Event) : MpMessage, IHostMessage, IRunMessage;

/// <summary>Host-authoritative run outcome. The peer only renders it: it never
/// judges completion/failure, never counts a streak and never starts a retry.</summary>
public enum MpRunOutcome { Running, Completed, Failed }

public sealed record RunStatusMessage(
    MpRunOutcome Outcome, int ConsecutiveWins, bool RetryPending, float RetrySeconds)
    : MpMessage, IHostMessage, IRunMessage, ILatestState;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "event")]
[JsonDerivedType(typeof(ActionTimelineEvent), "timeline")]
[JsonDerivedType(typeof(ResetTimelineEvent), "reset-timeline")]
[JsonDerivedType(typeof(NativeCastEvent), "cast")]
[JsonDerivedType(typeof(NativeActionEffectEvent), "effect")]
[JsonDerivedType(typeof(ActorVfxEvent), "actor-vfx")]
[JsonDerivedType(typeof(RemoveActorVfxEvent), "remove-vfx")]
[JsonDerivedType(typeof(MapEffectEvent), "map")]
[JsonDerivedType(typeof(DirectorEvent), "director")]
[JsonDerivedType(typeof(WeatherEvent), "weather")]
[JsonDerivedType(typeof(KnockbackEvent), "knockback")]
[JsonDerivedType(typeof(TeleportEvent), "teleport")]
[JsonDerivedType(typeof(FaceEvent), "face")]
[JsonDerivedType(typeof(FailureNoticeEvent), "failure-notice")]
public abstract record WorldEvent;

public sealed record ActionTimelineEvent(MpEntity Actor, ushort TimelineId, ushort LoopId, ushort BaseOverride) : WorldEvent;
public sealed record ResetTimelineEvent(MpEntity Actor) : WorldEvent;
public sealed record NativeCastEvent(
    MpEntity Actor, uint ActionId, byte ActionType, float CastSeconds, float OmenDelay,
    bool Interruptible, float? Rotation, MpVector? Position, MpEntity? Target) : WorldEvent;
public sealed record NativeActionEffectEvent(
    MpEntity Actor, uint ActionId, float AnimationLock, ushort SpellId, byte AnimationVariation,
    byte ActionType, byte Flags, MpEntity[] Targets, float? Rotation, MpVector? Position,
    MpEntity? AnimationTarget) : WorldEvent;
public sealed record ActorVfxEvent(MpEntity Actor, string ResourceKey, float Duration, bool Persistent) : WorldEvent;
public sealed record RemoveActorVfxEvent(MpEntity Actor, string ResourceKey) : WorldEvent;
public sealed record MapEffectEvent(uint Flags, byte Index) : WorldEvent;
public sealed record DirectorEvent(uint Category, uint Arg1, uint Arg2, uint Arg3, uint Arg4) : WorldEvent;
public sealed record WeatherEvent(byte WeatherId, float Transition) : WorldEvent;
public sealed record KnockbackEvent(PartyRole Role, MpVector Source, float Distance, float Speed) : WorldEvent;
public sealed record TeleportEvent(PartyRole Role, MpVector Position) : WorldEvent;
public sealed record FaceEvent(PartyRole Role, float Rotation) : WorldEvent;
public sealed record FailureNoticeEvent(PartyRole? Role, string Reason, bool Death) : WorldEvent;
