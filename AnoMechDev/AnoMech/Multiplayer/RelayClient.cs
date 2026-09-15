using System.Diagnostics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AnoMech.Multiplayer;

/// <summary>
/// BCL-only TC relay client.  Framework/game code only sees the bounded RelayEvent queue;
/// network callbacks never invoke game code.  A connection is single-use: after a socket is
/// closed this instance cannot reconnect or inherit another socket's identity.
/// </summary>
public sealed class RelayClient : IRelayTransport
{
    private sealed record OutboundFrame(byte[] Bytes, bool LatestState, StateKey? Key);
    private readonly record struct StateKey(Guid SenderId, Guid RunId, Type MessageType);

    private readonly object stateLock = new();
    private readonly object sendLock = new();
    private readonly object receiveLock = new();
    private readonly LinkedList<OutboundFrame> sendQueue = new();
    private readonly Dictionary<StateKey, LinkedListNode<OutboundFrame>> sendStates = new();
    private readonly SemaphoreSlim sendSignal = new(0);
    private readonly LinkedList<RelayEvent> receiveQueue = new();
    private readonly Dictionary<StateKey, LinkedListNode<RelayEvent>> receiveStates = new();
    private readonly Dictionary<Guid, long> lastIncomingSequences = new();
    private readonly HashSet<Guid> retiredRuns = new();

    private ClientWebSocket? socket;
    private CancellationTokenSource? lifetime;
    private Task? sendTask;
    private Task? receiveTask;
    private Task? heartbeatTask;
    private RelayStatus status = RelayStatus.Disconnected;
    private RelayIdentity? identity;
    // Host-only, handed out by the relay in the handshake so the host can mint a room
    // invitation.  Kept apart from RelayIdentity: identity stays secret-free and purely
    // diagnostic, while this value is cleared on every terminal transition.
    private string? hostJoinToken;
    private bool disposed;
    private long nextSequence;
    private long lastControlSequence;
    private long membershipGeneration;
    private Guid currentRunId;
    private long lastInboundTimestamp;
    private readonly Queue<long> outboundMessageTimes = new();

    public RelayStatus Status
    {
        get { lock (stateLock) return status; }
    }
    public RelayIdentity? Identity
    {
        get { lock (stateLock) return identity; }
    }
    /// <summary>
    /// The room join secret for a host connection, or null for a peer connection and after the
    /// connection has failed or closed.  Never logged and never sent to peers.
    /// </summary>
    public string? HostJoinToken
    {
        get { lock (stateLock) return hostJoinToken; }
    }

    // Incremented by the receive loop before a PeerJoined/PeerLeft notice is made visible to
    // the framework pump.  This is intentionally transport-owned and not derived from payload.
    public long MembershipGeneration => Interlocked.Read(ref membershipGeneration);

    public async Task<MpError> ConnectAsync(RelayConnectionOptions options, CancellationToken cancellationToken)
    {
        if (options is null)
            return MpError.InvalidEndpoint;
        lock (stateLock)
        {
            if (disposed)
                return MpError.Disposed;
            if (status != RelayStatus.Disconnected)
                return MpError.Busy;
            status = RelayStatus.Connecting;
        }

        if (!TryBuildEndpoint(options, out var endpoint, out var endpointError))
        {
            FailBeforeConnected(endpointError);
            return endpointError;
        }
        var client = new ClientWebSocket();
        try
        {
            client.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
            if (!string.IsNullOrEmpty(options.Token))
            {
                if (ContainsHeaderUnsafeCharacters(options.Token))
                {
                    FailBeforeConnected(MpError.Authentication);
                    client.Dispose();
                    return MpError.Authentication;
                }
                client.Options.SetRequestHeader("Authorization", "Bearer " + options.Token);
            }

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(TimeSpan.FromSeconds(MpLimits.IoTimeoutSeconds));
            lock (stateLock)
            {
                if (disposed)
                {
                    client.Dispose();
                    status = RelayStatus.Closed;
                    return MpError.Disposed;
                }
                socket = client;
            }
            await client.ConnectAsync(endpoint, connectCts.Token).ConfigureAwait(false);
            var nonce = WireProtocol.CreateClientNonce();
            await SendDirectAsync(client, WireProtocol.EncodeHandshakeRequest(nonce), connectCts.Token).ConfigureAwait(false);
            var handshake = await ReceiveHandshakeAsync(client, connectCts.Token).ConfigureAwait(false);
            if (!WireProtocol.TryDecodeHandshakeResponse(handshake.Bytes, out var response) ||
                response.ClientNonce != nonce || !WireProtocol.HasExactCapabilities(response.Capabilities) ||
                response.RoomId == Guid.Empty || response.PeerId == Guid.Empty || response.HostId == Guid.Empty ||
                (options.Host ? response.PeerId != response.HostId : response.PeerId == response.HostId) ||
                (!options.Host && response.RoomCode != options.RoomCode) ||
                // The join secret belongs to the host of this room and to nobody else: a peer
                // that is handed one, or a host that is not, is not a relay we accept.
                (options.Host ? !IsHostJoinToken(response.JoinToken) : response.JoinToken is not null))
            {
                FailBeforeConnected(MpError.ProtocolMismatch);
                return MpError.ProtocolMismatch;
            }

            var disposedDuringHandshake = false;
            lock (stateLock)
            {
                disposedDuringHandshake = disposed;
                if (!disposedDuringHandshake)
                {
                    identity = new RelayIdentity(response.RoomId, response.PeerId, response.HostId, response.RoomCode);
                    hostJoinToken = options.Host ? response.JoinToken : null;
                    currentRunId = Guid.Empty;
                    nextSequence = 0;
                    lastControlSequence = 0;
                    lastIncomingSequences.Clear();
                    lastInboundTimestamp = Stopwatch.GetTimestamp();
                    lifetime = new CancellationTokenSource();
                    status = RelayStatus.Connected;
                }
            }
            if (disposedDuringHandshake)
            {
                FailBeforeConnected(MpError.Disposed);
                return MpError.Disposed;
            }

            var token = lifetime!.Token;
            sendTask = RunLoopAsync(SendLoopAsync, token);
            receiveTask = RunLoopAsync(ReceiveLoopAsync, token);
            heartbeatTask = RunLoopAsync(HeartbeatLoopAsync, token);
            return MpError.None;
        }
        catch (OperationCanceledException)
        {
            var error = cancellationToken.IsCancellationRequested ? MpError.Cancelled : MpError.Timeout;
            FailBeforeConnected(error);
            return error;
        }
        catch (WebSocketException)
        {
            FailBeforeConnected(MpError.TransportFailure);
            return MpError.TransportFailure;
        }
        catch (IOException)
        {
            FailBeforeConnected(MpError.TransportFailure);
            return MpError.TransportFailure;
        }
        catch (SecurityException)
        {
            FailBeforeConnected(MpError.Authentication);
            return MpError.Authentication;
        }
        catch (InvalidOperationException)
        {
            FailBeforeConnected(MpError.TransportFailure);
            return MpError.TransportFailure;
        }
        catch
        {
            FailBeforeConnected(MpError.TransportFailure);
            return MpError.TransportFailure;
        }
    }

    public bool TrySend(Guid runId, MpMessage message)
    {
        if (message is null || !WireProtocol.IsValidPacketShape(new ClientPacket(
                MpLimits.ProtocolVersion, 1, runId, message)))
            return false;

        try
        {
            if (!MpValidation.Validate(message))
                return false;
        }
        catch
        {
            return false;
        }

        RelayIdentity? local;
        lock (stateLock)
        {
            if (disposed || status != RelayStatus.Connected || identity is null || lifetime is null)
                return false;
            local = identity;
            if (local.IsHost != (message is IHostMessage))
                return false;
            if (message is not IHostMessage && message is not IPeerMessage)
                return false;
            if (!ValidateRunTransitionForSend(runId, message))
                return false;
        }

        try
        {
            MpError? failure = null;
            var signal = false;
            lock (sendLock)
            {
                if (!AcceptOutboundRateLocked())
                {
                    failure = MpError.RateLimit;
                }
                else
                {
                    var sequence = ++nextSequence;
                    var packet = new ClientPacket(MpLimits.ProtocolVersion, sequence, runId, message);
                    var bytes = WireProtocol.EncodeClientPacket(packet);
                    var latest = message is ILatestState;
                    var key = latest ? new StateKey(local!.PeerId, runId, message.GetType()) : (StateKey?)null;
                    if (!EnqueueSendLocked(new OutboundFrame(bytes, latest, key), out signal))
                        failure = MpError.QueueOverflow;
                }
            }
            if (failure is { } error)
            {
                Complete(error);
                return false;
            }
            if (message is EndRunMessage)
            {
                lock (stateLock)
                {
                    retiredRuns.Add(runId);
                    currentRunId = Guid.Empty;
                }
            }
            if (signal)
                sendSignal.Release();
            return true;
        }
        catch (InvalidDataException)
        {
            Complete(MpError.InvalidMessage);
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            Complete(MpError.InvalidMessage);
            return false;
        }
        catch (InvalidOperationException)
        {
            Complete(MpError.InvalidMessage);
            return false;
        }
        catch (NotSupportedException)
        {
            Complete(MpError.InvalidMessage);
            return false;
        }
    }

    public bool TryReceive(out RelayEvent? item)
    {
        lock (receiveLock)
        {
            if (receiveQueue.First is not { } node)
            {
                item = null;
                return false;
            }
            receiveQueue.RemoveFirst();
            if (node.Value is RelayReceived received && received.Packet.Message is ILatestState)
            {
                var key = new StateKey(received.Packet.SenderId, received.Packet.RunId,
                    received.Packet.Message.GetType());
                if (receiveStates.TryGetValue(key, out var stateNode) && ReferenceEquals(stateNode, node))
                    receiveStates.Remove(key);
            }
            item = node.Value;
            return true;
        }
    }

    public void Dispose()
    {
        Task[] tasks;
        lock (stateLock)
        {
            if (disposed)
                return;
            disposed = true;
        }

        Complete(MpError.Disposed, notify: false);
        lock (stateLock)
        {
            tasks = FilterTasks(new Task?[] { sendTask, receiveTask, heartbeatTask });
        }

        // Abort is deliberately before waiting: a ReceiveAsync blocked in the native socket
        // must be released before any caller tears down framework/native state.
        try
        { socket?.Abort(); }
        catch { }
        foreach (var task in tasks)
        {
            if (task.IsCompleted || task.Id == Task.CurrentId)
                continue;
            try
            { task.Wait(TimeSpan.FromSeconds(1)); }
            catch { }
        }
        socket?.Dispose();
        sendSignal.Dispose();
        lifetime?.Dispose();
    }

    private static Task[] FilterTasks(Task?[] tasks)
    {
        var result = new List<Task>(tasks.Length);
        foreach (var task in tasks)
            if (task is not null)
                result.Add(task);
        return result.ToArray();
    }

    private async Task RunLoopAsync(Func<CancellationToken, Task> loop, CancellationToken token)
    {
        try
        {
            await loop(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            Complete(MpError.Timeout);
        }
        catch (InvalidDataException)
        {
            Complete(MpError.InvalidMessage);
        }
        catch (WebSocketException)
        {
            Complete(GetDisconnectError());
        }
        catch (IOException)
        {
            Complete(GetDisconnectError());
        }
        catch (ObjectDisposedException)
        {
            if (!disposed)
                Complete(MpError.Disposed);
        }
        catch
        {
            // Never leak payload or token-bearing exception text to the framework.  The public
            // queue carries only a stable MpError code.
            Complete(MpError.TransportFailure);
        }
    }

    private async Task SendLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await sendSignal.WaitAsync(token).ConfigureAwait(false);
            OutboundFrame? frame;
            lock (sendLock)
            {
                if (sendQueue.First is not { } node)
                {
                    frame = null;
                }
                else
                {
                    frame = node.Value;
                    sendQueue.RemoveFirst();
                    if (frame.Key is { } key && sendStates.TryGetValue(key, out var mapped) &&
                        ReferenceEquals(mapped, node))
                        sendStates.Remove(key);
                }
            }
            if (frame is null)
                continue;
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            sendCts.CancelAfter(TimeSpan.FromSeconds(MpLimits.IoTimeoutSeconds));
            await socket!.SendAsync(frame.Bytes, WebSocketMessageType.Text, true, sendCts.Token)
                .ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var received = await ReceiveMessageAsync(socket!, token).ConfigureAwait(false);
            lock (stateLock)
                lastInboundTimestamp = Stopwatch.GetTimestamp();
            if (received.MessageType == WebSocketMessageType.Close)
            {
                Complete(Identity?.IsHost == true ? MpError.TransportFailure : MpError.HostDisconnected);
                return;
            }
            if (received.MessageType != WebSocketMessageType.Text ||
                !WireProtocol.TryGetFrameType(received.Bytes, out var frameType))
            {
                Complete(MpError.ProtocolMismatch);
                return;
            }

            if (frameType == WireProtocol.ControlKind)
            {
                if (!WireProtocol.TryDecodeControl(received.Bytes, out var control) ||
                    !AcceptControl(control))
                {
                    Complete(MpError.InvalidMessage);
                    return;
                }
                if (control.Kind == RelayControlKind.HeartbeatAck)
                    continue;
                if (control.Kind == RelayControlKind.PeerJoined)
                {
                    Interlocked.Increment(ref membershipGeneration);
                    if (!EnqueueReceive(new RelayPeerJoined(control.PeerId), latestState: false, key: null))
                    {
                        Complete(MpError.QueueOverflow);
                        return;
                    }
                }
                else if (control.Kind == RelayControlKind.PeerLeft)
                {
                    Interlocked.Increment(ref membershipGeneration);
                    if (!EnqueueReceive(new RelayPeerLeft(control.PeerId), latestState: false, key: null))
                    {
                        Complete(MpError.QueueOverflow);
                        return;
                    }
                }
                else if (control.Kind == RelayControlKind.RoomClosed)
                {
                    Complete(Identity?.IsHost == true ? MpError.TransportFailure : MpError.HostDisconnected);
                    return;
                }
                continue;
            }

            if (frameType != WireProtocol.PacketKind ||
                !WireProtocol.TryDecodeServerPacket(received.Bytes, out var packet) ||
                !AcceptPacket(packet, out var dispatch))
            {
                Complete(MpError.InvalidMessage);
                return;
            }
            if (!dispatch)
                continue;

            var isEnd = packet.Message is EndRunMessage;
            var state = packet.Message is ILatestState;
            var stateKey = state ? new StateKey(packet.SenderId, packet.RunId, packet.Message.GetType()) : (StateKey?)null;
            if (!EnqueueReceive(new RelayReceived(packet), state, stateKey))
            {
                Complete(MpError.QueueOverflow);
                return;
            }
            if (isEnd)
            {
                lock (stateLock)
                {
                    retiredRuns.Add(packet.RunId);
                    currentRunId = Guid.Empty;
                }
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken token)
    {
        var delay = TimeSpan.FromSeconds(MpLimits.HeartbeatSeconds);
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
            long last;
            lock (stateLock)
                last = lastInboundTimestamp;
            if (Stopwatch.GetElapsedTime(last).TotalSeconds > MpLimits.LivenessSeconds)
            {
                Complete(MpError.Timeout);
                return;
            }

            var queued = false;
            lock (sendLock)
            {
                if (disposed || Status != RelayStatus.Connected)
                    return;
                var sequence = ++nextSequence;
                var bytes = WireProtocol.EncodeControl(Guid.Empty, RelayControlKind.Heartbeat, Guid.Empty, sequence);
                var key = new StateKey(Guid.Empty, Guid.Empty, typeof(RelayControlKind));
                queued = EnqueueSendLocked(new OutboundFrame(bytes, true, key), out var shouldSignal);
                if (shouldSignal)
                    sendSignal.Release();
            }
            if (!queued)
            {
                Complete(MpError.QueueOverflow);
                return;
            }
        }
    }

    private bool AcceptControl(RelayControlFrame control)
    {
        var local = Identity;
        if (local is null || control.Sequence <= lastControlSequence)
            return false;
        if (control.Kind == RelayControlKind.Heartbeat)
            return false;
        if (control.RoomId != local.RoomId)
            return false;
        if (control.Kind is RelayControlKind.HeartbeatAck or RelayControlKind.RoomClosed)
        {
            if (control.PeerId != Guid.Empty)
                return false;
        }
        else if (control.Kind is (RelayControlKind.PeerJoined or RelayControlKind.PeerLeft) &&
                 (control.PeerId == Guid.Empty || control.PeerId == local.PeerId ||
                  control.PeerId == local.HostId))
            return false;
        lock (stateLock)
            lastControlSequence = control.Sequence;
        return true;
    }

    private bool AcceptPacket(ReceivedPacket packet, out bool dispatch)
    {
        dispatch = true;
        RelayIdentity? local;
        lock (stateLock)
            local = identity;
        if (local is null || packet.RoomId != local.RoomId || packet.SenderId == Guid.Empty ||
            packet.SenderId == local.PeerId || packet.Sequence <= 0 ||
            !WireProtocol.IsValidPacketShape(new ClientPacket(MpLimits.ProtocolVersion, packet.Sequence,
                packet.RunId, packet.Message)))
            return false;

        if (local.IsHost)
        {
            if (packet.IsHost || packet.Message is not IPeerMessage)
                return false;
        }
        else if (!packet.IsHost || packet.SenderId != local.HostId || packet.Message is not IHostMessage)
        {
            return false;
        }

        lock (stateLock)
        {
            if (lastIncomingSequences.TryGetValue(packet.SenderId, out var previous) && packet.Sequence <= previous)
                return false;
            lastIncomingSequences[packet.SenderId] = packet.Sequence;
        }

        try
        {
            if (!MpValidation.Validate(packet.Message))
                return false;
        }
        catch
        {
            return false;
        }

        lock (stateLock)
        {
            if (packet.Message is IRunMessage)
            {
                if (packet.Message is CheckRunMessage)
                {
                    if (currentRunId != Guid.Empty || packet.RunId == Guid.Empty ||
                        retiredRuns.Count >= MpLimits.RunsPerRoom || retiredRuns.Contains(packet.RunId))
                        return false;
                    currentRunId = packet.RunId;
                }
                else if (currentRunId == Guid.Empty || packet.RunId != currentRunId)
                {
                    // An authenticated in-flight sample can outlive EndRun on another socket.
                    // Consume its sequence, but never give it a new game/native lifetime.
                    if (retiredRuns.Contains(packet.RunId))
                    {
                        dispatch = false;
                        return true;
                    }
                    return false;
                }
            }
            else if (packet.RunId != Guid.Empty)
            {
                return false;
            }
        }
        return true;
    }

    private bool ValidateRunTransitionForSend(Guid runId, MpMessage message)
    {
        if (message is IRunMessage)
        {
            if (message is CheckRunMessage)
            {
                if (runId == Guid.Empty || currentRunId != Guid.Empty ||
                    retiredRuns.Count >= MpLimits.RunsPerRoom || retiredRuns.Contains(runId))
                    return false;
                currentRunId = runId;
                return true;
            }
            return runId != Guid.Empty && runId == currentRunId;
        }
        return runId == Guid.Empty;
    }
    private bool AcceptOutboundRateLocked()
    {
        var now = Stopwatch.GetTimestamp();
        outboundMessageTimes.Enqueue(now);
        while (outboundMessageTimes.Count > 0 &&
               Stopwatch.GetElapsedTime(outboundMessageTimes.Peek()).TotalSeconds > 1)
            outboundMessageTimes.Dequeue();
        return outboundMessageTimes.Count <= MpLimits.MessagesPerSenderSecond;
    }
    private bool EnqueueSendLocked(OutboundFrame frame, out bool shouldSignal)
    {
        shouldSignal = false;
        if (frame.LatestState && frame.Key is { } key && sendStates.TryGetValue(key, out var existing))
        {
            existing.Value = frame;
            sendQueue.Remove(existing);
            sendQueue.AddLast(existing);
            return true;
        }
        if (!frame.LatestState)
            sendStates.Clear();

        if (sendQueue.Count >= MpLimits.SendQueue)
            return false;

        var node = sendQueue.AddLast(frame);
        if (frame.LatestState && frame.Key is { } stateKey)
            sendStates[stateKey] = node;
        shouldSignal = true;
        return true;
    }

    private bool EnqueueReceive(RelayEvent item, bool latestState, StateKey? key)
    {
        lock (receiveLock)
        {
            if (latestState && key is { } stateKey && receiveStates.TryGetValue(stateKey, out var existing))
            {
                existing.Value = item;
                receiveQueue.Remove(existing);
                receiveQueue.AddLast(existing);
                return true;
            }
            if (!latestState)
                receiveStates.Clear();
            if (receiveQueue.Count >= MpLimits.ReceiveQueue)
                return false;
            var node = receiveQueue.AddLast(item);
            if (latestState && key is { } newKey)
                receiveStates[newKey] = node;
            return true;
        }
    }

    private void Complete(MpError error, bool notify = true)
    {
        // Keep lock ordering send -> state.  TrySend and HeartbeatLoop use the same ordering
        // while consulting Status, so shutdown cannot deadlock while draining the queue.
        CancellationTokenSource? cts;
        lock (sendLock)
        {
            sendQueue.Clear();
            sendStates.Clear();
        }
        lock (stateLock)
        {
            // Clear before the idempotency check: the secret must not outlive the socket on
            // any path, including a repeated Complete.
            hostJoinToken = null;
            if (status == RelayStatus.Closed || (status == RelayStatus.Disconnected && disposed))
                return;
            status = RelayStatus.Closed;
            cts = lifetime;
            lifetime = null;
        }
        cts?.Cancel();
        try
        { socket?.Abort(); }
        catch { }
        try
        { sendSignal.Release(); }
        catch (ObjectDisposedException) { }
        lock (receiveLock)
        {
            receiveQueue.Clear();
            receiveStates.Clear();
            if (notify)
                receiveQueue.AddLast(new RelayClosed(error));
        }
    }
    private MpError GetDisconnectError()
    {
        lock (stateLock)
            return identity?.IsHost == true ? MpError.TransportFailure : MpError.HostDisconnected;
    }

    private void FailBeforeConnected(MpError error)
    {
        lock (stateLock)
        {
            hostJoinToken = null;
            status = disposed ? RelayStatus.Closed : RelayStatus.Disconnected;
            lifetime?.Cancel();
            lifetime = null;
        }
        try
        { socket?.Abort(); }
        catch { }
        try
        { socket?.Dispose(); }
        catch { }
        socket = null;
    }

    private static async Task SendDirectAsync(ClientWebSocket client, byte[] bytes, CancellationToken token)
        => await client.SendAsync(bytes, WebSocketMessageType.Text, true, token).ConfigureAwait(false);

    private static async Task<(WebSocketMessageType MessageType, byte[] Bytes)> ReceiveHandshakeAsync(
        ClientWebSocket client, CancellationToken token)
        => await ReceiveMessageAsync(client, token).ConfigureAwait(false);

    private static async Task<(WebSocketMessageType MessageType, byte[] Bytes)> ReceiveMessageAsync(
        WebSocket webSocket, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var assemblyCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        assemblyCts.CancelAfter(WireProtocol.AssemblyTimeout);
        using var stream = new MemoryStream();
        var fragments = 0;
        while (true)
        {
            var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), assemblyCts.Token)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return (result.MessageType, Array.Empty<byte>());
            if (++fragments > WireProtocol.MaxFragments)
                throw new InvalidDataException("Too many WebSocket fragments.");
            if (stream.Length + result.Count > WireProtocol.MaxJsonBytes)
                throw new InvalidDataException("WebSocket message exceeds the protocol size limit.");
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return (result.MessageType, stream.ToArray());
        }
    }

    private static bool TryBuildEndpoint(RelayConnectionOptions options, out Uri endpoint, out MpError error)
    {
        endpoint = null!;
        error = MpError.InvalidEndpoint;
        var source = options.Endpoint;
        if (source is null || !source.IsAbsoluteUri || source.UserInfo.Length != 0 ||
            source.Query.Length != 0 || source.Fragment.Length != 0)
            return false;
        if (source.Scheme is not ("ws" or "wss"))
            return false;
        var isLiteralLoopback = IPAddress.TryParse(source.Host, out var address) && IPAddress.IsLoopback(address);
        if (source.Scheme == "ws" && !isLiteralLoopback)
            return false;
        var roomCode = options.RoomCode ?? string.Empty;
        if (!options.Host && !WireProtocol.IsRoomCode(roomCode))
            return false;
        if (options.Host && roomCode.Length != 0)
            return false;

        var expectedPath = options.Host ? "/host" : "/session/" + roomCode;
        var path = source.AbsolutePath;
        if (path is not ("" or "/") && path != expectedPath)
            return false;
        try
        {
            var builder = new UriBuilder(source) { Path = expectedPath, Query = string.Empty, Fragment = string.Empty };
            endpoint = builder.Uri;
            return true;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    // The relay issues a 256-bit room join secret as 64 upper-case hex characters.  Anything
    // else — null, wrong length, lower case, non-hex — is not a join token.
    private static bool IsHostJoinToken(string? value)
    {
        if (value is null || value.Length != 64)
            return false;
        foreach (var c in value)
            if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F')))
                return false;
        return true;
    }

    private static bool ContainsHeaderUnsafeCharacters(string value)
    {
        foreach (var c in value)
            if (c is '\r' or '\n' || char.IsControl(c))
                return true;
        return false;
    }
}
