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
    private readonly record struct StateKey(Guid SenderId, Guid RunId, Type MessageType);

    private readonly object stateLock = new();
    private readonly object sendLock = new();
    private readonly object receiveLock = new();
    // Both queues shed (drop cosmetic cues and superseded samples) exactly where the old bounded
    // queues refused and the connection closed itself; see DeliveryQueue. Only a Required backlog
    // reaching QueueHardLimit still ends the connection.
    private readonly DeliveryQueue<byte[], StateKey> sendQueue = new(MpLimits.SendQueue, MpLimits.QueueHardLimit);
    private readonly SemaphoreSlim sendSignal = new(0);
    private readonly DeliveryQueue<RelayEvent, StateKey> receiveQueue = new(MpLimits.ReceiveQueue, MpLimits.QueueHardLimit);
    private readonly Dictionary<Guid, long> lastIncomingSequences = new();
    private readonly HashSet<Guid> retiredRuns = new();
    // Receive scratch, reused for every frame: the receive side (handshake, then the single
    // receive loop) is strictly sequential, and every returned payload is a fresh copy. A new
    // 16 KiB buffer per message was the largest allocation source on a busy host (~420 msg/s
    // from seven peers, plus the embedded relay in the same game process).
    private readonly byte[] receiveBuffer = new byte[16 * 1024];

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
    // Send loop only: when the frames written in the last second (heartbeats included) reach the
    // relay's per-sender budget, the loop waits for the window instead of failing the message.
    private readonly Queue<long> sentFrameTimes = new();
    // Health readout only (see RelayTransportStats).  Heartbeat RTT is measured from the
    // moment the heartbeat is queued, so a stalled send loop inflates it on purpose.
    private long heartbeatSentTimestamp;
    private double rttMilliseconds = -1;
    // Latest-state / cosmetic items dropped at the hard limit (health readout, with queue sheds).
    private long droppedAtLimit;
    // Diagnostic only (IRelayTransport.LastSendFailure): why the last TrySend refused, and the
    // first terminal error, so a send on an already-closed socket names what closed it.
    // lastSendFailure is written and read by the TrySend caller; closeError under stateLock.
    private MpError lastSendFailure;
    private MpError? closeError;

    public MpError LastSendFailure => lastSendFailure;

    public RelayTransportStats Stats
    {
        get
        {
            int send, receive;
            long shed;
            lock (sendLock) { send = sendQueue.Count; shed = sendQueue.Shed; }
            lock (receiveLock) { receive = receiveQueue.Count; shed += receiveQueue.Shed; }
            double rtt;
            lock (stateLock) rtt = rttMilliseconds;
            return new RelayTransportStats(rtt, send, receive, shed + Interlocked.Read(ref droppedAtLimit));
        }
    }

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
            var handshake = await ReceiveMessageAsync(client, receiveBuffer, timeIdle: true, connectCts.Token).ConfigureAwait(false);
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
            return Refuse(MpError.InvalidMessage);

        try
        {
            if (!MpValidation.Validate(message))
                return Refuse(MpError.InvalidMessage);
        }
        catch
        {
            return Refuse(MpError.InvalidMessage);
        }

        RelayIdentity? local;
        lock (stateLock)
        {
            if (disposed || status != RelayStatus.Connected || identity is null || lifetime is null)
                return Refuse(closeError ?? MpError.TransportFailure);
            local = identity;
            if (local.IsHost != (message is IHostMessage))
                return Refuse(MpError.InvalidMessage);
            if (message is not IHostMessage && message is not IPeerMessage)
                return Refuse(MpError.InvalidMessage);
            if (!ValidateRunTransitionForSend(runId, message))
                return Refuse(MpError.InvalidMessage);
        }

        try
        {
            MpError? failure = null;
            var signal = false;
            // The send loop paces the rate and the queue sheds under pressure (DeliveryQueue), so a
            // burst no longer closes the room. Only a Required backlog at the hard limit still fails:
            // dropping a lifecycle or stateful message would desync the run.
            var deliveryClass = MpDelivery.Classify(message);
            lock (sendLock)
            {
                var sequence = ++nextSequence;
                var packet = new ClientPacket(MpLimits.ProtocolVersion, sequence, runId, message);
                var bytes = WireProtocol.EncodeClientPacket(packet);
                var key = deliveryClass == DeliveryClass.LatestState
                    ? new StateKey(local!.PeerId, runId, message.GetType()) : (StateKey?)null;
                switch (sendQueue.Enqueue(bytes, deliveryClass, key, out _))
                {
                    case DeliveryResult.Accepted:
                        signal = true;
                        break;
                    case DeliveryResult.Overflow when deliveryClass != DeliveryClass.Required:
                        Interlocked.Increment(ref droppedAtLimit);
                        return true;
                    case DeliveryResult.Overflow:
                        failure = MpError.QueueOverflow;
                        break;
                }
            }
            if (failure is { } error)
            {
                Complete(error);
                return Refuse(error);
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
            return Refuse(MpError.InvalidMessage);
        }
        catch (ObjectDisposedException)
        {
            return Refuse(MpError.Disposed);
        }
        catch (ArgumentException)
        {
            Complete(MpError.InvalidMessage);
            return Refuse(MpError.InvalidMessage);
        }
        catch (InvalidOperationException)
        {
            Complete(MpError.InvalidMessage);
            return Refuse(MpError.InvalidMessage);
        }
        catch (NotSupportedException)
        {
            Complete(MpError.InvalidMessage);
            return Refuse(MpError.InvalidMessage);
        }
    }

    private bool Refuse(MpError reason)
    {
        lastSendFailure = reason;
        return false;
    }

    public bool TryReceive(out RelayEvent? item)
    {
        lock (receiveLock)
        {
            if (receiveQueue.TryDequeue(out var next))
            {
                item = next;
                return true;
            }
            item = null;
            return false;
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
            // Wait for rate room before taking the frame, so a newer sample can still replace it.
            await PaceAsync(token).ConfigureAwait(false);
            byte[]? frame;
            lock (sendLock)
                frame = sendQueue.TryDequeue(out var next) ? next : null;
            if (frame is null)
                continue;
            sentFrameTimes.Enqueue(Stopwatch.GetTimestamp());
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            // Same tolerance as liveness: a write that cannot progress for LivenessSeconds is a dead link.
            sendCts.CancelAfter(TimeSpan.FromSeconds(MpLimits.LivenessSeconds));
            await socket!.SendAsync(frame, WebSocketMessageType.Text, true, sendCts.Token)
                .ConfigureAwait(false);
        }
    }

    // At most MessagesPerSenderSecond frames (heartbeats included) in any one-second window, the
    // relay's per-sender budget. Before 2026-09-23 a reliable message over this limit closed the
    // room itself (RateLimit); now the burst waits here. Normal traffic (~100-140/s) never waits.
    private async Task PaceAsync(CancellationToken token)
    {
        while (true)
        {
            while (sentFrameTimes.Count > 0 && Stopwatch.GetElapsedTime(sentFrameTimes.Peek()).TotalSeconds >= 1)
                sentFrameTimes.Dequeue();
            if (sentFrameTimes.Count < MpLimits.MessagesPerSenderSecond)
                return;
            var wait = TimeSpan.FromSeconds(1) - Stopwatch.GetElapsedTime(sentFrameTimes.Peek());
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, token).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var received = await ReceiveMessageAsync(socket!, receiveBuffer, timeIdle: false, token).ConfigureAwait(false);
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
                {
                    lock (stateLock)
                    {
                        if (heartbeatSentTimestamp != 0)
                            rttMilliseconds = Stopwatch.GetElapsedTime(heartbeatSentTimestamp).TotalMilliseconds;
                    }
                    continue;
                }
                if (control.Kind == RelayControlKind.PeerJoined)
                {
                    Interlocked.Increment(ref membershipGeneration);
                    if (!EnqueueReceive(new RelayPeerJoined(control.PeerId), DeliveryClass.Required, key: null))
                    {
                        Complete(MpError.QueueOverflow);
                        return;
                    }
                }
                else if (control.Kind == RelayControlKind.PeerLeft)
                {
                    Interlocked.Increment(ref membershipGeneration);
                    if (!EnqueueReceive(new RelayPeerLeft(control.PeerId), DeliveryClass.Required, key: null))
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
            var deliveryClass = MpDelivery.Classify(packet.Message);
            var stateKey = deliveryClass == DeliveryClass.LatestState
                ? new StateKey(packet.SenderId, packet.RunId, packet.Message.GetType()) : (StateKey?)null;
            if (!EnqueueReceive(new RelayReceived(packet), deliveryClass, stateKey))
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

            lock (sendLock)
            {
                if (disposed || Status != RelayStatus.Connected)
                    return;
                var sequence = ++nextSequence;
                var bytes = WireProtocol.EncodeControl(Guid.Empty, RelayControlKind.Heartbeat, Guid.Empty, sequence);
                var key = new StateKey(Guid.Empty, Guid.Empty, typeof(RelayControlKind));
                lock (stateLock) heartbeatSentTimestamp = Stopwatch.GetTimestamp();
                // A heartbeat that finds the queue at its hard limit is skipped, not fatal: liveness
                // (ours and the relay's) decides whether this connection is still alive.
                if (sendQueue.Enqueue(bytes, DeliveryClass.LatestState, key, out _) == DeliveryResult.Accepted)
                    sendSignal.Release();
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
                    // An authenticated in-flight sample can outlive EndRun on another socket,
                    // and a peer that joined mid-run sees the host's run traffic for a run it
                    // was never checked into. Consume the sequence, never give it a lifetime.
                    if (currentRunId == Guid.Empty || retiredRuns.Contains(packet.RunId))
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
    // False only when Required items alone reach the hard limit; a sample or a cue that finds
    // the queue there is dropped instead, and the connection stays up.
    private bool EnqueueReceive(RelayEvent item, DeliveryClass deliveryClass, StateKey? key)
    {
        lock (receiveLock)
        {
            if (receiveQueue.Enqueue(item, deliveryClass, key, out _) != DeliveryResult.Overflow)
                return true;
            if (deliveryClass == DeliveryClass.Required)
                return false;
            Interlocked.Increment(ref droppedAtLimit);
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
        }
        lock (stateLock)
        {
            // Clear before the idempotency check: the secret must not outlive the socket on
            // any path, including a repeated Complete.
            hostJoinToken = null;
            if (status == RelayStatus.Closed || (status == RelayStatus.Disconnected && disposed))
                return;
            status = RelayStatus.Closed;
            closeError = error;
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
            if (notify)
                receiveQueue.Enqueue(new RelayClosed(error), DeliveryClass.Required, null, out _);
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

    // timeIdle: the handshake is bounded from its first byte (no liveness monitor exists yet). An
    // established connection waits for its next message without a timer — liveness
    // (LivenessSeconds, heartbeat-driven) decides when silence means dead — and only a message
    // that has started must finish within AssemblyTimeout. Before 2026-09-23 the timer also ran
    // while idle, so 10 s of silence ended the connection ahead of the documented 12 s.
    private static async Task<(WebSocketMessageType MessageType, byte[] Bytes)> ReceiveMessageAsync(
        WebSocket webSocket, byte[] buffer, bool timeIdle, CancellationToken token)
    {
        using var assemblyCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (timeIdle)
            assemblyCts.CancelAfter(WireProtocol.AssemblyTimeout);
        // Only a fragmented message needs an accumulator; the common single-frame message is
        // copied straight out of the scratch buffer. Limits and their order are unchanged.
        MemoryStream? stream = null;
        try
        {
            var fragments = 0;
            while (true)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), assemblyCts.Token)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return (result.MessageType, Array.Empty<byte>());
                if (++fragments > WireProtocol.MaxFragments)
                    throw new InvalidDataException("Too many WebSocket fragments.");
                if ((stream?.Length ?? 0) + result.Count > WireProtocol.MaxJsonBytes)
                    throw new InvalidDataException("WebSocket message exceeds the protocol size limit.");
                if (result.EndOfMessage && stream is null)
                    return (result.MessageType, buffer.AsSpan(0, result.Count).ToArray());
                if (stream is null && !timeIdle)
                    assemblyCts.CancelAfter(WireProtocol.AssemblyTimeout);
                stream ??= new MemoryStream();
                stream.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                    return (result.MessageType, stream.ToArray());
            }
        }
        finally
        {
            stream?.Dispose();
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
