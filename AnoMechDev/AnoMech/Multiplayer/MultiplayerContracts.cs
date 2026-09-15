using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AnoMech.Core.Game.Party;

namespace AnoMech.Multiplayer;

// Token carries a host credential ("<grant-id>.<secret>") on the host route and the room's join
// secret on the join route. ToString is masked so a credential never reaches a log or exception.
public sealed record RelayConnectionOptions(Uri Endpoint, bool Host, string RoomCode, string Token)
{
    public override string ToString()
        => $"RelayConnectionOptions {{ Endpoint = {Endpoint}, Host = {Host}, RoomCode = {RoomCode}, Token = <redacted> }}";
}
public sealed record RelayIdentity(Guid RoomId, Guid PeerId, Guid HostId, string RoomCode)
{
    public bool IsHost => PeerId == HostId;
}

public enum RelayStatus { Disconnected, Connecting, Connected, Closed }
public abstract record RelayEvent;
public sealed record RelayPeerJoined(Guid PeerId) : RelayEvent;
public sealed record RelayPeerLeft(Guid PeerId) : RelayEvent;
public sealed record RelayReceived(ReceivedPacket Packet) : RelayEvent;
public sealed record RelayClosed(MpError Error) : RelayEvent;

// Connect is explicitly initiated by the caller. Transport owns heartbeat,
// bounded parsing/queues, authenticated framing and socket lifetime; it never
// calls game/native code. TrySend assigns sequence in the serialized send order.
public interface IRelayTransport : IDisposable
{
    RelayStatus Status { get; }
    RelayIdentity? Identity { get; }
    long MembershipGeneration { get; }
    Task<MpError> ConnectAsync(RelayConnectionOptions options, CancellationToken cancellationToken);
    bool TrySend(Guid runId, MpMessage message);
    bool TryReceive(out RelayEvent? item);
}

// Called only by the framework thread. BeginPrepare does not run a scenario;
// readiness includes actual allocation/draw preparation. Returning success from
// BeginPrepare is not equivalent to IsPrepared. No method queues uncancellable
// work behind the caller's scope guard.
public interface IMultiplayerGame
{
    string BuildFingerprint { get; }
    bool Paused { get; set; }
    bool IsPrepared { get; }
    bool IsRestoring { get; }
    MpError CheckRun(RunDescriptor descriptor);
    MpError BeginPrepare(RunScope scope, RunDescriptor descriptor,
        IReadOnlyList<LobbyMember> roster, Guid localPeerId, bool host, Func<bool> isCurrent);
    MpError Commit(RunScope scope, bool host);
    void EndRun(bool returnToInn);
    SelfPoseMessage? CaptureSelfPose();
    void ApplySelfPose(PartyRole role, SelfPoseMessage pose);
    bool ApplyAbilityUse(PartyRole role, AbilityUseMessage ability);
    void ResetAbilityState();
    void GiveInvulnerability(PartyRole role);
    IReadOnlyList<PartyMarkerRequestMessage> CaptureLocalPartyMarkers();
    void ApplyPartyMarker(PartyMarkerRequestMessage marker);
    WorldSnapshotMessage CaptureWorld();
    RolesSnapshotMessage CaptureRoles();
    IReadOnlyList<WorldEvent> DrainEvents();
    RunStatusMessage CaptureRunStatus();
    WorldSnapshotMessage? TakeWorldAfterEvents();
    void ApplyWorld(WorldSnapshotMessage snapshot);
    void ApplyRoles(RolesSnapshotMessage snapshot);
    void ApplyEvent(WorldEvent item);
    void ApplyRunStatus(RunStatusMessage status);
}
