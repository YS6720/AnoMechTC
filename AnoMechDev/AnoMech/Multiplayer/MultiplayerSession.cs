using System;
using System.Collections.Generic;
using System.Linq;
using AnoMech.Core.Game.Party;

namespace AnoMech.Multiplayer;

public enum MultiplayerPhase { Connecting, Lobby, Checking, Preparing, Running, Restoring, Closed }

// Single writer: the framework thread. UI commands are marshalled by the
// plugin manager, never by an uncancellable Framework.Run inside this class.
public sealed class MultiplayerSession : IDisposable
{
    private readonly IRelayTransport transport;
    private readonly IMultiplayerGame game;
    private readonly string alias;
    private readonly Dictionary<Guid, LobbyMember> members = new();
    private readonly HashSet<Guid> connected = new();
    private readonly Dictionary<Guid, long> sequences = new();
    private readonly HashSet<Guid> seenRuns = new();
    private readonly HashSet<Guid> checkedPeers = new();
    private readonly HashSet<Guid> preparedPeers = new();
    private LobbyMember[] roster = [];
    private RunDescriptor? descriptor;
    private RunScope scope;
    private long generation;
    private long runMembershipGeneration;
    private double deadline;
    private double nextSnapshot;
    private double nextPoseSnapshot;
    private bool ownsNativeRun;
    private bool prepareSent;
    private bool localPrepared;
    private bool lastPaused;
    private RunStatusMessage? lastRunStatus;
    private bool disposed;

    public MultiplayerSession(IRelayTransport transport, IMultiplayerGame game, string alias)
    {
        if (!MpValidation.Alias(alias) || !MpValidation.Fingerprint(game.BuildFingerprint))
            throw new ArgumentException("Invalid multiplayer identity.");
        this.transport = transport;
        this.game = game;
        this.alias = alias;
    }

    public RelayIdentity? Identity { get; private set; }
    public MultiplayerPhase Phase { get; private set; } = MultiplayerPhase.Connecting;
    public MpError LastError { get; private set; }
    public IReadOnlyList<LobbyMember> Members => roster;
    public bool IsHost => Identity?.IsHost == true;
    public bool Paused => game.Paused;
    public event Action<Exception>? Faulted;

    public void BeforeGameTick(double now)
    {
        if (disposed)
            return;
        try
        {
            if (Identity == null && transport.Status == RelayStatus.Connected && transport.Identity is { } identity)
                Initialize(identity);
            CaptureLocalPartyMarkers();
            for (var n = 0; n < MpLimits.DrainPerTick && transport.TryReceive(out var item); n++)
            {
                if (item != null)
                    Receive(item, now);
                if (disposed)
                    return;
            }
            if (Identity != null && transport.Status is RelayStatus.Closed or RelayStatus.Disconnected)
            {
                Close(MpError.TransportFailure);
                return;
            }
            if (Phase is MultiplayerPhase.Checking or MultiplayerPhase.Preparing or MultiplayerPhase.Running &&
                transport.MembershipGeneration != runMembershipGeneration)
                EndForMembershipChange();
            if (Phase is MultiplayerPhase.Checking or MultiplayerPhase.Preparing && now >= deadline)
            {
                if (IsHost)
                    EndAsHost(true, MpError.Timeout);
                else
                    Close(MpError.Timeout);
            }
            if (Phase == MultiplayerPhase.Restoring && !game.IsRestoring)
                Phase = MultiplayerPhase.Lobby;
        }
        catch (Exception ex) { Fail(ex); }
    }

    public void AfterGameTick(double now)
    {
        if (disposed || Identity == null)
            return;
        try
        {
            if (Phase == MultiplayerPhase.Preparing && !IsCurrent(scope))
            {
                EndForMembershipChange();
                return;
            }
            if (Phase == MultiplayerPhase.Preparing && !localPrepared && game.IsPrepared)
            {
                localPrepared = true;
                preparedPeers.Add(Identity.PeerId);
                if (IsHost)
                    TryCommit();
                else if (!Send(scope.RunId, new PreparedRunMessage(MpError.None)))
                    return;
            }
            if (Phase != MultiplayerPhase.Running || !IsCurrent(scope))
                return;
            if (IsHost && game.Paused != lastPaused)
            {
                lastPaused = game.Paused;
                if (!Send(scope.RunId, new PauseMessage(lastPaused)))
                    return;
            }
            var worldDue = IsHost && now >= nextSnapshot;
            var poseDue = now >= nextPoseSnapshot;
            if (!worldDue && !poseDue)
                return;
            if (worldDue)
            {
                var interval = 1d / MpLimits.SnapshotHz;
                nextSnapshot = nextSnapshot == 0
                    ? now + interval
                    : nextSnapshot + (Math.Floor((now - nextSnapshot) / interval) + 1) * interval;
                // Creation state must still precede reliable cues and later retirement.
                if (!Send(scope.RunId, game.CaptureWorld()))
                    return;
            }
            if (poseDue)
            {
                var interval = 1d / MpLimits.PoseHz;
                // Preserve cadence across frame jitter, skipping missed ticks without bursts.
                nextPoseSnapshot = nextPoseSnapshot == 0
                    ? now + interval
                    : nextPoseSnapshot + (Math.Floor((now - nextPoseSnapshot) / interval) + 1) * interval;
                if (IsHost)
                {
                    if (!Send(scope.RunId, game.CaptureRoles()))
                        return;
                }
                else if (game.CaptureSelfPose() is { } pose && !Send(scope.RunId, pose))
                    return;
            }
            if (!worldDue)
                return;
            foreach (var item in game.DrainEvents())
                if (!Send(scope.RunId, new WorldEventMessage(item)))
                    return;
            if (game.TakeWorldAfterEvents() is { } retired && !Send(scope.RunId, retired))
                return;
            // Result presentation keeps the world cadence; only character poses run faster.
            if (game.CaptureRunStatus() is { } status && status != lastRunStatus)
            {
                lastRunStatus = status;
                Send(scope.RunId, status);
            }
        }
        catch (Exception ex) { Fail(ex); }
    }

    public MpError ClaimRole(PartyRole role)
    {
        if (Phase != MultiplayerPhase.Lobby || Identity == null || !MpValidation.Role(role))
            return MpError.Busy;
        LastError = MpError.None;
        if (!IsHost)
            return Send(Guid.Empty, new ClaimRoleMessage(role)) ? MpError.None : LastError;
        return AssignRole(Identity.PeerId, role);
    }

    public bool SubmitAbilityUse(AbilityUseMessage ability)
    {
        if (disposed || Identity is null || Phase != MultiplayerPhase.Running ||
            !IsCurrent(scope) || game.Paused || !MpValidation.Validate(ability) ||
            !members.TryGetValue(Identity.PeerId, out var member) || member.Role is not { } role)
            return false;
        try
        {
            return IsHost ? game.ApplyAbilityUse(role, ability) : Send(scope.RunId, ability);
        }
        catch (Exception ex) { Fail(ex); return false; }
    }

    public MpError Start(RunDescriptor run, double now)
    {
        if (!IsHost || Identity == null || Phase != MultiplayerPhase.Lobby || game.IsRestoring)
            return MpError.Busy;
        if (!MpValidation.Descriptor(run))
            return MpError.InvalidMessage;
        if (connected.Count != members.Count || members.Values.Any(member => member.Role == null))
            return MpError.RoleRequired;
        if (seenRuns.Count >= MpLimits.RunsPerRoom)
        { Close(MpError.Capacity); return LastError; }
        try
        {
            var error = game.CheckRun(run);
            if (error != MpError.None)
                return LastError = error;
            Guid runId;
            do
            { runId = Guid.NewGuid(); } while (!seenRuns.Add(runId));
            BeginScope(runId, run, now);
            checkedPeers.Add(Identity.PeerId);
            if (!Send(runId, new CheckRunMessage(run)))
                return LastError;
            if (checkedPeers.Count == members.Count)
                PrepareLocal(now);
            return LastError;
        }
        catch (Exception ex) { Fail(ex); return LastError; }
    }

    public MpError SetPaused(bool paused)
    {
        if (!IsHost || Phase != MultiplayerPhase.Running || !IsCurrent(scope))
            return MpError.Busy;
        game.Paused = lastPaused = paused;
        return Send(scope.RunId, new PauseMessage(paused)) ? MpError.None : LastError;
    }

    public MpError RequestControl(MpControl control)
    {
        if (Phase != MultiplayerPhase.Running || Identity == null)
            return MpError.Busy;
        if (!IsHost)
            return Send(scope.RunId, new ControlRequestMessage(control)) ? MpError.None : LastError;
        HandleControl(Identity.PeerId, control);
        return LastError;
    }

    // Local exit never waits for the Host to approve releasing native state.
    public void Dispose() => Close(MpError.Disposed);
    public void StopForError(Exception error) => Fail(error);

    private void Initialize(RelayIdentity identity)
    {
        Identity = identity;
        connected.Add(identity.PeerId);
        connected.Add(identity.HostId);
        Phase = MultiplayerPhase.Lobby;
        if (identity.IsHost)
        {
            members.Add(identity.PeerId, new LobbyMember(identity.PeerId, alias, null, game.BuildFingerprint));
            PublishLobby();
        }
        else
            Send(Guid.Empty, new HelloMessage(alias, game.BuildFingerprint));
    }

    private void Receive(RelayEvent item, double now)
    {
        if (Identity == null)
            return;
        switch (item)
        {
            case RelayClosed closed:
                Close(closed.Error);
                return;
            case RelayPeerJoined joined:
                connected.Add(joined.PeerId);
                if (Phase is MultiplayerPhase.Checking or MultiplayerPhase.Preparing or MultiplayerPhase.Running)
                    EndForMembershipChange();
                return;
            case RelayPeerLeft left:
                connected.Remove(left.PeerId);
                members.Remove(left.PeerId);
                sequences.Remove(left.PeerId);
                if (left.PeerId == Identity.HostId)
                { Close(MpError.HostDisconnected); return; }
                if (Phase is MultiplayerPhase.Checking or MultiplayerPhase.Preparing or MultiplayerPhase.Running)
                    EndForMembershipChange();
                if (IsHost)
                    PublishLobby();
                return;
            case RelayReceived received:
                ReceivePacket(received.Packet, now);
                return;
        }
    }

    private void ReceivePacket(ReceivedPacket packet, double now)
    {
        var identity = Identity!;
        if (packet.RoomId != identity.RoomId || packet.SenderId == identity.PeerId ||
            packet.IsHost != (packet.SenderId == identity.HostId) ||
            packet.Sequence <= 0 || (sequences.TryGetValue(packet.SenderId, out var previous) && packet.Sequence <= previous))
            throw new MpProtocolException(MpError.InvalidMessage);
        sequences[packet.SenderId] = packet.Sequence;
        if (packet.Message is IHostMessage && (!packet.IsHost || IsHost) ||
            packet.Message is IPeerMessage && (packet.IsHost || !IsHost))
            throw new MpProtocolException(MpError.InvalidMessage);

        switch (packet.Message)
        {
            case HelloMessage hello when IsHost && Phase is MultiplayerPhase.Lobby or MultiplayerPhase.Restoring:
                if (!connected.Contains(packet.SenderId) || members.ContainsKey(packet.SenderId))
                    throw new MpProtocolException(MpError.InvalidMessage);
                if (hello.BuildFingerprint != game.BuildFingerprint)
                {
                    Reject(packet.SenderId, MpError.BuildMismatch);
                    return;
                }
                members.Add(packet.SenderId, new LobbyMember(packet.SenderId, hello.Alias, null, hello.BuildFingerprint));
                PublishLobby();
                return;
            case LobbyMessage lobby when !IsHost:
                if (!lobby.Members.Any(m => m.PeerId == identity.PeerId) ||
                    !lobby.Members.Any(m => m.PeerId == identity.HostId) ||
                    lobby.Members.Any(m => m.BuildFingerprint != game.BuildFingerprint))
                    throw new MpProtocolException(MpError.BuildMismatch);
                if (Phase is MultiplayerPhase.Checking or MultiplayerPhase.Preparing or MultiplayerPhase.Running)
                    throw new MpProtocolException(MpError.InvalidMessage);
                members.Clear();
                foreach (var member in lobby.Members)
                    members.Add(member.PeerId, member);
                roster = lobby.Members;
                return;
            case ClaimRoleMessage claim when IsHost && Phase == MultiplayerPhase.Lobby:
                Reject(packet.SenderId, AssignRole(packet.SenderId, claim.Role));
                return;
            case RejectedMessage rejected when rejected.RecipientId == identity.PeerId:
                LastError = rejected.Error;
                if (rejected.Error == MpError.BuildMismatch)
                    Close(rejected.Error);
                return;
            case RejectedMessage:
                return;
            case CheckRunMessage check when !IsHost:
                if (Phase is not (MultiplayerPhase.Lobby or MultiplayerPhase.Restoring) || packet.RunId == Guid.Empty ||
                    seenRuns.Count >= MpLimits.RunsPerRoom || !seenRuns.Add(packet.RunId))
                    throw new MpProtocolException(MpError.InvalidMessage);
                var restoring = Phase == MultiplayerPhase.Restoring || game.IsRestoring;
                BeginScope(packet.RunId, check.Descriptor, now);
                var checkError = restoring ? MpError.Busy :
                    members.Values.Any(m => m.Role == null) ? MpError.RoleRequired : game.CheckRun(check.Descriptor);
                Send(packet.RunId, new CheckedRunMessage(checkError));
                if (checkError != MpError.None)
                    FinishLocal(false, checkError);
                return;
        }

        // Old-run messages cannot acquire a new native lifetime. Sequence gaps
        // are allowed (state coalescing); duplicate/backward frames are not.
        if (packet.Message is not IRunMessage || packet.RunId != scope.RunId || scope.RunId == Guid.Empty)
            return;
        if (!IsCurrent(scope))
        { EndForMembershipChange(); return; }
        switch (packet.Message)
        {
            case CheckedRunMessage checkedRun when IsHost && Phase == MultiplayerPhase.Checking:
                RequireMember(packet.SenderId);
                if (checkedRun.Error != MpError.None)
                { EndAsHost(false, checkedRun.Error); return; }
                checkedPeers.Add(packet.SenderId);
                if (checkedPeers.Count == members.Count)
                    PrepareLocal(now);
                break;
            case PrepareRunMessage when !IsHost && Phase == MultiplayerPhase.Checking:
                PrepareLocal(now);
                break;
            case PreparedRunMessage prepared when IsHost && Phase == MultiplayerPhase.Preparing && prepareSent:
                RequireMember(packet.SenderId);
                if (prepared.Error != MpError.None)
                { EndAsHost(true, prepared.Error); return; }
                preparedPeers.Add(packet.SenderId);
                TryCommit();
                break;
            case CommitRunMessage when !IsHost && Phase == MultiplayerPhase.Preparing && localPrepared:
                CommitLocal(false);
                break;
            case EndRunMessage end when !IsHost:
                FinishLocal(end.ReturnToInn, end.Reason);
                break;
            case PauseMessage pause when !IsHost && Phase == MultiplayerPhase.Running:
                game.Paused = pause.Paused;
                break;
            case SelfPoseMessage pose when IsHost && Phase == MultiplayerPhase.Running:
                game.ApplySelfPose(RequireMember(packet.SenderId).Role!.Value, pose);
                break;
            case AbilityUseMessage ability when IsHost:
                // A press may race pause/prepare/retirement. Refuse that input;
                // do not fall through to the fatal unknown-message branch.
                if (Phase == MultiplayerPhase.Running && !game.Paused &&
                    members.TryGetValue(packet.SenderId, out var actor) && actor.Role is { } actorRole)
                    game.ApplyAbilityUse(actorRole, ability);
                break;
            case ControlRequestMessage request when IsHost && Phase == MultiplayerPhase.Running:
                HandleControl(packet.SenderId, request.Control);
                break;
            case PartyMarkerRequestMessage marker when IsHost && Phase == MultiplayerPhase.Running:
                RequireMember(packet.SenderId);
                game.ApplyPartyMarker(marker);
                break;
            case WorldSnapshotMessage world when !IsHost && Phase == MultiplayerPhase.Running:
                game.ApplyWorld(world);
                break;
            case RolesSnapshotMessage roles when !IsHost && Phase == MultiplayerPhase.Running:
                game.ApplyRoles(roles);
                break;
            case WorldEventMessage cue when !IsHost && Phase == MultiplayerPhase.Running:
                game.ApplyEvent(cue.Event);
                break;
            case RunStatusMessage status when !IsHost && Phase == MultiplayerPhase.Running:
                game.ApplyRunStatus(status);
                break;
            default:
                throw new MpProtocolException(MpError.InvalidMessage);
        }
    }

    private void BeginScope(Guid runId, RunDescriptor run, double now)
    {
        scope = new RunScope(Identity!.RoomId, runId, ++generation);
        runMembershipGeneration = transport.MembershipGeneration;
        descriptor = run;
        checkedPeers.Clear();
        preparedPeers.Clear();
        game.ResetAbilityState();
        localPrepared = prepareSent = false;
        lastRunStatus = null;
        LastError = MpError.None;
        Phase = MultiplayerPhase.Checking;
        deadline = now + MpLimits.PrepareSeconds;
    }

    private void PrepareLocal(double now)
    {
        Phase = MultiplayerPhase.Preparing;
        deadline = now + MpLimits.PrepareSeconds;
        var current = scope;
        // Prepare every client concurrently; otherwise the host's deadline
        // includes the host's loading time before any peer can begin loading.
        if (IsHost)
        {
            prepareSent = true;
            if (!Send(current.RunId, new PrepareRunMessage()))
                return;
        }
        ownsNativeRun = true;
        var error = game.BeginPrepare(current, descriptor!, roster, Identity!.PeerId, IsHost, () => IsCurrent(current));
        if (error == MpError.None && IsCurrent(current))
            return;
        if (error == MpError.None)
            error = MpError.Cancelled;
        if (IsHost)
            EndAsHost(true, error);
        else
        {
            Send(current.RunId, new PreparedRunMessage(error));
            FinishLocal(true, error);
        }
    }

    private void TryCommit()
    {
        if (Phase != MultiplayerPhase.Preparing || !localPrepared || preparedPeers.Count != members.Count)
            return;
        if (!IsCurrent(scope))
        { EndForMembershipChange(); return; }
        CommitLocal(true);
        if (Phase == MultiplayerPhase.Running)
            Send(scope.RunId, new CommitRunMessage());
    }

    private void CommitLocal(bool host)
    {
        var error = game.Commit(scope, host);
        if (error != MpError.None)
        {
            if (host)
                EndAsHost(true, error);
            else
                Close(error);
            return;
        }
        if (!IsCurrent(scope))
        { EndForMembershipChange(); return; }
        Phase = MultiplayerPhase.Running;
        lastPaused = game.Paused;
        nextSnapshot = 0;
        nextPoseSnapshot = 0;
        if (!host)
            CaptureLocalPartyMarkers();
    }

    private void CaptureLocalPartyMarkers()
    {
        if (IsHost || Phase != MultiplayerPhase.Running || !IsCurrent(scope))
            return;
        // Capture before incoming snapshots replace native signs. A failed send
        // retires this run, including the adapter's advanced input baseline.
        foreach (var marker in game.CaptureLocalPartyMarkers())
            if (!Send(scope.RunId, marker))
                return;
    }

    private bool IsCurrent(RunScope candidate) => !disposed && candidate == scope &&
        candidate.Generation == generation && transport.Status == RelayStatus.Connected &&
        transport.MembershipGeneration == runMembershipGeneration &&
        Phase is MultiplayerPhase.Checking or MultiplayerPhase.Preparing or MultiplayerPhase.Running;

    private LobbyMember RequireMember(Guid peerId)
    {
        if (!members.TryGetValue(peerId, out var member) || member.Role == null)
            throw new MpProtocolException(MpError.InvalidMessage);
        return member;
    }

    private MpError AssignRole(Guid peerId, PartyRole role)
    {
        if (!members.TryGetValue(peerId, out var member))
            return MpError.RoleRequired;
        if (members.Values.Any(m => m.PeerId != peerId && m.Role == role))
            return LastError = MpError.RoleOccupied;
        members[peerId] = member with { Role = role };
        LastError = MpError.None;
        PublishLobby();
        return MpError.None;
    }

    private void PublishLobby()
    {
        roster = members.Values.OrderBy(m => m.Role).ThenBy(m => m.PeerId).ToArray();
        if (roster.Length != 0)
            Send(Guid.Empty, new LobbyMessage(roster));
    }

    private void Reject(Guid peerId, MpError error)
    {
        if (error == MpError.None)
            return;
        if (peerId == Identity!.PeerId)
            LastError = error;
        else
            Send(Guid.Empty, new RejectedMessage(peerId, error));
    }

    private void HandleControl(Guid sender, MpControl control)
    {
        var member = RequireMember(sender);
        switch (control)
        {
            case MpControl.GiveInvulnerability:
                game.GiveInvulnerability(member.Role!.Value);
                break;
            // Ending a live run and re-opening it belongs to the host alone: a member
            // must never be able to wipe everyone else's round. Refused, not fatal —
            // a stale or unauthorised request is not a reason to close the room.
            case MpControl.Reset when sender != Identity!.PeerId:
                Reject(sender, MpError.InvalidMessage);
                break;
            case MpControl.Reset:
                EndAsHost(false, MpError.None);
                break;
            case MpControl.Leave:
                EndAsHost(true, MpError.None);
                break;
            default:
                throw new MpProtocolException(MpError.InvalidMessage);
        }
    }

    private void EndForMembershipChange()
    {
        if (IsHost)
            EndAsHost(true, MpError.PeerDisconnected);
        else
            FinishLocal(true, MpError.PeerDisconnected);
    }

    private void EndAsHost(bool returnToInn, MpError reason)
    {
        var runId = scope.RunId;
        if (runId != Guid.Empty && !Send(runId, new EndRunMessage(returnToInn, reason)))
            return;
        FinishLocal(returnToInn, reason);
    }

    private void FinishLocal(bool returnToInn, MpError reason)
    {
        ++generation;
        scope = default;
        descriptor = null;
        checkedPeers.Clear();
        preparedPeers.Clear();
        game.ResetAbilityState();
        LastError = reason;
        if (ownsNativeRun)
        {
            ownsNativeRun = false;
            game.EndRun(returnToInn);
        }
        Phase = game.IsRestoring ? MultiplayerPhase.Restoring : MultiplayerPhase.Lobby;
    }

    private bool Send(Guid runId, MpMessage message)
    {
        if (!disposed && transport.TrySend(runId, message))
            return true;
        Close(MpError.QueueOverflow);
        return false;
    }

    private void Fail(Exception error)
    {
        LastError = error is MpProtocolException protocol ? protocol.Error : MpError.NativeFailure;
        Close(LastError);
        Faulted?.Invoke(error);
    }

    private void Close(MpError reason)
    {
        if (disposed)
            return;
        disposed = true;
        ++generation;
        scope = default;
        game.ResetAbilityState();
        // Abort network first; native cleanup cannot race a live queued dispatch.
        transport.Dispose();
        LastError = reason;
        Phase = MultiplayerPhase.Closed;
        if (!ownsNativeRun)
            return;
        ownsNativeRun = false;
        try
        { game.EndRun(true); }
        catch (Exception ex) { LastError = MpError.NativeFailure; Faulted?.Invoke(ex); }
    }
}
