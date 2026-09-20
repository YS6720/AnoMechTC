using System.Diagnostics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AnoMech.Multiplayer;

namespace AnoMech.Relay;

public sealed record RelayServerOptions
{
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;
    // 同 HostedRelayOptions：避開動態埠保留區。部署一律明確指定 --port。
    public int Port { get; init; } = 27890;
    public int MaxRooms { get; init; } = MpLimits.Rooms;

    /// <summary>
    /// The only way to authorize a host. There is no global host or join token: every room is
    /// created by a grant from this source and gets its own join secret.
    /// </summary>
    public IHostGrantSource? HostGrantSource { get; init; }

    /// <summary>
    /// Optional room-lifecycle sink for the standalone host. Connection counts cannot answer
    /// "who is using this relay" — every socket arrives from the same loopback tunnel.
    /// The plugin's embedded relay leaves this null, so in-process hosting stays silent;
    /// this type must never write to Console itself. Secrets are never passed in.
    /// </summary>
    public Action<string>? ActivityLog { get; init; }
}

/// <summary>
/// A deliberately small, self-hosted TC relay.  It is a star router, not a game server: the
/// host is the only socket allowed to publish host messages and the relay stamps all identities
/// on forwarded envelopes.  The default listener is literal loopback and no proxy headers are
/// interpreted.
///
/// Host admission, capacity reservation, room publication and revocation all share
/// <see cref="roomsSync"/>. HTTP-time checks are provisional: a credential verified before the
/// WebSocket upgrade is re-verified inside that lock before a room becomes reachable.
/// </summary>
public sealed class RelayServer : IAsyncDisposable, IDisposable
{
    private enum RunPhase
    {
        Lobby,
        Checked,
        Preparing,
        Running,
    }

    private enum RelayRoute
    {
        None,
        Ready,
        Probe,
        Host,
        Join,
    }

    private sealed class Room
    {
        public readonly object Sync = new();
        public readonly Guid RoomId = Guid.NewGuid();
        public readonly string Code;

        /// <summary>The grant that created this room; revoking that grant closes it.</summary>
        public readonly string GrantId;

        /// <summary>Room-scoped 256-bit join secret. It grants no hosting or management rights.</summary>
        public readonly string JoinSecret;
        public readonly Dictionary<Guid, PeerConnection> Members = new();
        public readonly HashSet<Guid> SeenRuns = new();
        public PeerConnection? Host;
        public Guid CurrentRunId;
        public RunPhase Phase;
        public bool Closed;

        public Room(string code, string grantId, string joinSecret)
        {
            Code = code;
            GrantId = grantId;
            JoinSecret = joinSecret;
        }
    }

    /// <summary>
    /// A revocable reservation taken before the WebSocket upgrade. It holds one unit of the
    /// grant's room capacity so two simultaneous upgrades cannot both promote past the limit,
    /// and its cancellation aborts a pending upgrade the instant the grant is revoked.
    /// </summary>
    private sealed class HostLease
    {
        public readonly Guid LeaseId = Guid.NewGuid();
        public readonly string GrantId;
        public readonly CancellationTokenSource Cancellation;
        public bool Revoked;

        public HostLease(string grantId, CancellationToken serverToken)
        {
            GrantId = grantId;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        }
    }

    private sealed class PeerConnection
    {
        public readonly object Sync = new();
        public readonly Room Room;
        public readonly WebSocket Socket;
        public readonly Guid PeerId = Guid.NewGuid();
        public readonly bool IsHost;
        public readonly RelayOutboundQueue Outbound = new();
        public double RateTokens = MpLimits.RelayIngressBurst;
        public long LastRateTimestamp = Stopwatch.GetTimestamp();
        public readonly CancellationTokenSource Lifetime;
        public long LastInboundTimestamp = Stopwatch.GetTimestamp();
        public long LastInboundSequence;
        public long NextControlSequence;
        public Task? SendTask;
        public bool Joined;
        public bool Closed;
        public bool ResourcesDisposed;

        public PeerConnection(Room room, WebSocket socket, bool isHost, CancellationToken serverToken,
            CancellationToken grantToken)
        {
            Room = room;
            Socket = socket;
            IsHost = isHost;
            Lifetime = CancellationTokenSource.CreateLinkedTokenSource(serverToken, grantToken);
        }
    }

    private sealed record RelayOutboundItem(byte[] Bytes, bool LatestState, StateKey? Key);
    private readonly record struct StateKey(Guid SenderId, Guid RunId, Type MessageType);

    /// <summary>Bounded, single-consumer send queue. Reliable items are never silently dropped.</summary>
    private sealed class RelayOutboundQueue : IDisposable
    {
        private readonly object sync = new();
        private readonly LinkedList<RelayOutboundItem> items = new();
        private readonly Dictionary<StateKey, LinkedListNode<RelayOutboundItem>> latest = new();
        private readonly SemaphoreSlim signal = new(0);
        private bool disposed;

        public bool Enqueue(RelayOutboundItem item)
        {
            lock (sync)
            {
                if (disposed) return false;
                if (item.LatestState && item.Key is { } key && latest.TryGetValue(key, out var existing))
                {
                    existing.Value = item;
                    items.Remove(existing);
                    items.AddLast(existing);
                    return true;
                }
                if (!item.LatestState) latest.Clear();
                if (items.Count >= MpLimits.SendQueue) return false;
                var node = items.AddLast(item);
                if (item.LatestState && item.Key is { } stateKey) latest[stateKey] = node;
            }
            try { signal.Release(); } catch (ObjectDisposedException) { return false; }
            return true;
        }

        public bool TryDequeue(out RelayOutboundItem? item)
        {
            lock (sync)
            {
                if (items.First is not { } node)
                {
                    item = null;
                    return false;
                }
                item = node.Value;
                items.RemoveFirst();
                if (item.Key is { } key && latest.TryGetValue(key, out var mapped) && ReferenceEquals(mapped, node))
                    latest.Remove(key);
                return true;
            }
        }

        public async Task WaitAsync(CancellationToken token)
            => await signal.WaitAsync(token).ConfigureAwait(false);

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                items.Clear();
                latest.Clear();
            }
            try { signal.Release(); } catch (ObjectDisposedException) { }
            signal.Dispose();
        }
    }

    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int CodeLength = 6;
    private const int MaxFragments = WireProtocol.MaxFragments;

    private readonly RelayServerOptions options;
    private readonly IHostGrantSource[] grantSources;
    private readonly Dictionary<string, Room> rooms = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, HostLease> leases = new();
    private readonly object roomsSync = new();
    private readonly CancellationTokenSource stopSource = new();
    private readonly List<Task> connectionTasks = new();
    private readonly object connectionTasksSync = new();

    /// <summary>Guarded by <see cref="roomsSync"/> for writes; the published allowlist.</summary>
    private HostGrantSnapshot grants = HostGrantSnapshot.Empty;

    /// <summary>
    /// Monotonic timestamp of the read that produced <see cref="grants"/>; 0 means "no usable
    /// snapshot". Read without the lock on the forwarding path, written under it.
    /// </summary>
    private long grantsTimestamp;
    private HttpListener? listener;
    private Task? acceptTask;
    private Task? livenessTask;
    private Task? refreshTask;
    private CancellationTokenRegistration cancellationRegistration;
    private bool hasCancellationRegistration;
    private bool started;
    private bool disposed;
    private Task? disposeTask;
    private readonly SemaphoreSlim admission;
    private readonly int admissionLimit;
    public RelayServer(RelayServerOptions? options = null)
    {
        this.options = options ?? new RelayServerOptions();
        ValidateOptions(this.options);
        grantSources = new[] { this.options.HostGrantSource! };
        admissionLimit = checked(this.options.MaxRooms * MpLimits.Members + MpLimits.Members);
        admission = new SemaphoreSlim(admissionLimit, admissionLimit);
    }

    public int Port => options.Port;
    public IPAddress BindAddress => options.BindAddress;
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
    public bool IsRunning
    {
        get
        {
            lock (roomsSync)
                return !disposed && started && listener?.IsListening == true;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!StartListener(cancellationToken)) return;
        // 綁定成功後才報，否則日誌會先說 listening 再說 bind 失敗。
        Log($"relay listening  {options.BindAddress}:{options.Port}  maxRooms={options.MaxRooms}");
        // The first allowlist read completes before Start returns, so a caller that awaits
        // startup never races an empty snapshot into a spurious "unauthorized" rejection.
        await RefreshGrantsAsync(stopSource.Token).ConfigureAwait(false);
        lock (roomsSync)
        {
            if (started) refreshTask ??= RefreshLoopAsync(stopSource.Token);
        }
    }

    private bool StartListener(CancellationToken cancellationToken)
    {
        lock (roomsSync)
        {
            if (disposed) throw new ObjectDisposedException(nameof(RelayServer));
            if (started) return false;
            cancellationRegistration = cancellationToken.Register(
                static state => ((CancellationTokenSource)state!).Cancel(), stopSource);
            hasCancellationRegistration = true;
            if (stopSource.IsCancellationRequested)
            {
                cancellationRegistration.Dispose();
                hasCancellationRegistration = false;
                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();
                throw new ObjectDisposedException(nameof(RelayServer));
            }
            listener = new HttpListener();
            try
            {
                listener.Prefixes.Add(BuildPrefix(options.BindAddress, options.Port));
                listener.Start();
            }
            catch
            {
                listener.Close();
                listener = null;
                cancellationRegistration.Dispose();
                hasCancellationRegistration = false;
                throw;
            }
            started = true;
            acceptTask = AcceptLoopAsync(stopSource.Token);
            livenessTask = LivenessLoopAsync(stopSource.Token);
            return true;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (acceptTask is not null) await acceptTask.ConfigureAwait(false);
        }
        finally
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    public async Task StopAsync()
    {
        lock (roomsSync)
        {
            try { stopSource.Cancel(); } catch (ObjectDisposedException) { }
            try { listener?.Stop(); } catch { }
        }

        Room[] currentRooms;
        lock (roomsSync) currentRooms = rooms.Values.ToArray();
        foreach (var room in currentRooms)
            await CloseRoomAsync(room).ConfigureAwait(false);

        var accept = acceptTask;
        if (accept is not null && accept.Id != Task.CurrentId)
        {
            try { await accept.ConfigureAwait(false); } catch { }
        }
        var liveness = livenessTask;
        if (liveness is not null && liveness.Id != Task.CurrentId)
        {
            try { await liveness.ConfigureAwait(false); } catch { }
        }
        var refresh = refreshTask;
        if (refresh is not null && refresh.Id != Task.CurrentId)
        {
            try { await refresh.ConfigureAwait(false); } catch { }
        }

        while (true)
        {
            Task[] handlers;
            lock (connectionTasksSync)
                handlers = connectionTasks.Where(task => task.Id != Task.CurrentId).ToArray();
            if (handlers.Length == 0) break;
            foreach (var task in handlers)
            {
                try { await task.ConfigureAwait(false); } catch { }
            }
        }

        CancellationTokenRegistration registration = default;
        var unregister = false;
        lock (roomsSync)
        {
            started = false;
            if (hasCancellationRegistration)
            {
                registration = cancellationRegistration;
                hasCancellationRegistration = false;
                unregister = true;
            }
        }
        if (unregister) registration.Dispose();
    }

    public void Dispose()
    {
        var cleanup = BeginDispose();
        try { cleanup.GetAwaiter().GetResult(); } catch { }
    }

    public ValueTask DisposeAsync()
        => new(BeginDispose());

    private Task BeginDispose()
    {
        lock (roomsSync)
        {
            if (disposeTask is null)
            {
                disposed = true;
                disposeTask = DisposeOwnedAsync();
            }
            return disposeTask;
        }
    }

    private async Task DisposeOwnedAsync()
    {
        try { await StopAsync().ConfigureAwait(false); } catch { }
        listener?.Close();
        admission.Dispose();
        stopSource.Dispose();
    }


    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener!.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (token.IsCancellationRequested || listener is null || !listener.IsListening)
            {
                break;
            }
            catch (ObjectDisposedException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                if (token.IsCancellationRequested) break;
                continue;
            }

            var task = HandleConnectionAsync(context, token);
            lock (connectionTasksSync) connectionTasks.Add(task);
            _ = ObserveConnectionTaskAsync(task);
        }
    }

    private async Task ObserveConnectionTaskAsync(Task task)
    {
        try { await task.ConfigureAwait(false); } catch { }
        lock (connectionTasksSync) connectionTasks.Remove(task);
    }

    private async Task HandleConnectionAsync(HttpListenerContext context, CancellationToken serverToken)
    {
        PeerConnection? connection = null;
        WebSocket? socket = null;
        Room? room = null;
        HostLease? lease = null;
        var route = RelayRoute.None;
        var admissionHeld = false;
        try
        {
            if (!TryRoute(context.Request, out route, out var roomCode))
            {
                RejectHttp(context.Response, HttpStatusCode.BadRequest);
                return;
            }

            var authorization = context.Request.Headers["Authorization"];
            if (route == RelayRoute.Ready)
            {
                HandleReady(context.Response, context.Request, authorization);
                return;
            }

            if (!context.Request.IsWebSocketRequest ||
                !string.IsNullOrEmpty(context.Request.Headers["Sec-WebSocket-Protocol"]) ||
                !string.IsNullOrEmpty(context.Request.Headers["Sec-WebSocket-Extensions"]))
            {
                RejectHttp(context.Response, HttpStatusCode.BadRequest);
                return;
            }

            // Pre-upgrade authorization. For a host this only reserves capacity; the binding
            // decision is remade under the lock once the socket exists.
            string? hostSecret = null;
            switch (route)
            {
                case RelayRoute.Probe:
                    if (!IsHostGrantAuthorized(authorization))
                    {
                        RejectHttp(context.Response, HttpStatusCode.BadRequest);
                        return;
                    }
                    break;
                case RelayRoute.Host:
                    if (!TryReserveHostLease(authorization, serverToken, out lease, out hostSecret))
                    {
                        RejectHttp(context.Response, HttpStatusCode.BadRequest);
                        return;
                    }
                    break;
                case RelayRoute.Join:
                    if (!TryAuthorizeJoin(authorization, roomCode!, out room))
                    {
                        RejectHttp(context.Response, HttpStatusCode.BadRequest);
                        return;
                    }
                    break;
                default:
                    RejectHttp(context.Response, HttpStatusCode.BadRequest);
                    return;
            }

            try
            {
                admissionHeld = admission.Wait(0);
            }
            catch (ObjectDisposedException)
            {
                admissionHeld = false;
            }
            if (!admissionHeld)
            {
                RejectHttp(context.Response, HttpStatusCode.ServiceUnavailable);
                return;
            }

            Task<HttpListenerWebSocketContext>? accept = null;
            try
            {
                accept = context.AcceptWebSocketAsync(subProtocol: null);
                using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(
                    serverToken, lease?.Cancellation.Token ?? default);
                acceptCts.CancelAfter(TimeSpan.FromSeconds(MpLimits.IoTimeoutSeconds));
                socket = (await accept.WaitAsync(acceptCts.Token).ConfigureAwait(false)).WebSocket;
            }
            catch (OperationCanceledException)
            {
                AbortResponse(context.Response);
                await ObserveLateAcceptedSocketAsync(accept).ConfigureAwait(false);
                return;
            }
            catch
            {
                AbortResponse(context.Response);
                await ObserveLateAcceptedSocketAsync(accept).ConfigureAwait(false);
                return;
            }

            if (route == RelayRoute.Probe)
            {
                // Readiness only: a real 101 through the public path, then an immediate close.
                // No room, no peer routing and no membership are created for a probe.
                var probeSocket = socket;
                socket = null;
                await CloseSocketAsync(probeSocket, WebSocketCloseStatus.NormalClosure).ConfigureAwait(false);
                return;
            }

            if (route == RelayRoute.Host)
            {
                if (lease!.Cancellation.IsCancellationRequested ||
                    !TryPromoteHostLease(lease, hostSecret!, socket!, serverToken, out room, out connection))
                {
                    await CloseSocketAsync(socket!, WebSocketCloseStatus.PolicyViolation).ConfigureAwait(false);
                    socket = null;
                    return;
                }
            }
            else
            {
                // The pre-upgrade room and secret are re-verified; a room closed or replaced
                // during the upgrade must not accept this socket.
                if (!TryAuthorizeJoin(authorization, roomCode!, out var current) ||
                    current is null || !ReferenceEquals(current, room))
                {
                    await CloseSocketAsync(socket!, WebSocketCloseStatus.PolicyViolation).ConfigureAwait(false);
                    socket = null;
                    return;
                }
                connection = new PeerConnection(room!, socket!, isHost: false, serverToken, default);
            }

            var handshake = await ReceiveMessageAsync(socket!, connection.Lifetime.Token).ConfigureAwait(false);
            if (connection.Lifetime.IsCancellationRequested ||
                handshake.MessageType != WebSocketMessageType.Text ||
                !WireProtocol.TryDecodeHandshakeRequest(handshake.Bytes, out var request) ||
                !WireProtocol.IsValidHandshakeRequest(request))
            {
                await CloseSocketAsync(socket!, WebSocketCloseStatus.PolicyViolation).ConfigureAwait(false);
                socket = null;
                return;
            }

            Volatile.Write(ref connection.LastInboundTimestamp, Stopwatch.GetTimestamp());
            if (route == RelayRoute.Join)
            {
                if (!TryJoinRoom(room!, connection))
                {
                    await CloseSocketAsync(socket!, WebSocketCloseStatus.PolicyViolation).ConfigureAwait(false);
                    socket = null;
                    return;
                }
            }
            else if (!IsRoomLive(room!))
            {
                // The grant was revoked while the host was completing its handshake.
                await CloseSocketAsync(socket!, WebSocketCloseStatus.PolicyViolation).ConfigureAwait(false);
                socket = null;
                return;
            }

            var response = new RelayHandshakeResponse(
                MpLimits.ProtocolName,
                MpLimits.ProtocolVersion,
                WireProtocol.Capabilities.ToArray(),
                room!.RoomId,
                connection.PeerId,
                room.Host!.PeerId,
                room.Code,
                request.ClientNonce,
                route == RelayRoute.Host ? room.JoinSecret : null);
            await SendDirectAsync(socket!, WireProtocol.EncodeHandshakeResponse(response), connection.Lifetime.Token)
                .ConfigureAwait(false);
            if (connection.Lifetime.IsCancellationRequested || !IsRoomLive(room!)) return;
            connection.Joined = true;
            connection.SendTask = ObserveConnectionTaskAsync(RunSendLoopAsync(connection));

            if (route == RelayRoute.Join) NotifyPeerJoined(room, connection);
            else NotifyExistingPeersToHost(room, connection);
            await RunReceiveLoopAsync(connection).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (serverToken.IsCancellationRequested)
        {
        }
        catch
        {
            // The relay deliberately exposes no exception or payload text to clients or logs.
        }
        finally
        {
            try
            {
                if (connection is not null)
                    await RemoveConnectionAsync(connection).ConfigureAwait(false);
                else
                {
                    if (route == RelayRoute.Host && room is not null)
                        await CloseRoomAsync(room).ConfigureAwait(false);
                    if (socket is not null)
                        await CloseSocketAsync(socket, WebSocketCloseStatus.PolicyViolation).ConfigureAwait(false);
                }
            }
            catch
            {
            }
            finally
            {
                // Released after the connection is gone: until then the lease cancellation is
                // the handle a revocation uses to abort a host that is still upgrading.
                if (lease is not null) ReleaseHostLease(lease);
                if (admissionHeld)
                {
                    try { admission.Release(); } catch (ObjectDisposedException) { }
                }
            }
        }
    }

    /// <summary>
    /// 踢掉連線並記下原因。斷線對使用者是同一個結果，但成因完全不同——限流、
    /// 序號倒退、封包格式、應用層拒絕各有各的修法。不記原因就只能猜。
    /// 只記分類字串，不記封包內容。
    /// </summary>
    private void AbortWithReason(PeerConnection connection, string reason)
    {
        Log($"peer dropped  reason={reason}  peer={Short(connection.PeerId.ToString("N"))}");
        connection.Socket.Abort();
    }

    private async Task RunReceiveLoopAsync(PeerConnection connection)
    {
        while (!connection.Lifetime.IsCancellationRequested && connection.Socket.State == WebSocketState.Open)
        {
            var received = await ReceiveMessageAsync(connection.Socket, connection.Lifetime.Token).ConfigureAwait(false);
            if (received.MessageType == WebSocketMessageType.Close) return;
            if (received.MessageType != WebSocketMessageType.Text ||
                !WireProtocol.TryGetFrameType(received.Bytes, out var frameType))
            {
                AbortWithReason(connection, "frame-type");
                return;
            }

            if (frameType == WireProtocol.ControlKind)
            {
                // 這幾個條件以前混成同一個 Abort()，斷線後查不出是哪一個——
                // 使用者只看到「斷線」。逐條分辨後記下原因（不含封包內容）。
                if (!WireProtocol.TryDecodeControl(received.Bytes, out var control))
                    { AbortWithReason(connection, "control-decode"); return; }
                if (control.Kind != RelayControlKind.Heartbeat)
                    { AbortWithReason(connection, "control-kind"); return; }
                if (control.Sequence <= connection.LastInboundSequence)
                    { AbortWithReason(connection, "control-sequence"); return; }
                if (!AcceptRate(connection))
                    { AbortWithReason(connection, "rate-limit"); return; }
                connection.LastInboundSequence = control.Sequence;
                Volatile.Write(ref connection.LastInboundTimestamp, Stopwatch.GetTimestamp());
                EnqueueControl(connection, RelayControlKind.HeartbeatAck, Guid.Empty);
                continue;
            }

            if (frameType != WireProtocol.PacketKind)
                { AbortWithReason(connection, "frame-kind"); return; }
            if (!WireProtocol.TryDecodeClientPacket(received.Bytes, out var packet))
                { AbortWithReason(connection, "packet-decode"); return; }
            if (packet.Sequence <= connection.LastInboundSequence)
                { AbortWithReason(connection, "packet-sequence"); return; }
            if (!AcceptRate(connection))
                { AbortWithReason(connection, "rate-limit"); return; }
            if (!AcceptApplicationPacket(connection, packet, out var forward))
                { AbortWithReason(connection, "packet-rejected"); return; }
            connection.LastInboundSequence = packet.Sequence;
            Volatile.Write(ref connection.LastInboundTimestamp, Stopwatch.GetTimestamp());
            if (forward) ForwardApplicationPacket(connection, packet);

        }
    }
    private bool AcceptApplicationPacket(PeerConnection connection, ClientPacket packet, out bool forward)
    {
        forward = true;
        if (!WireProtocol.IsValidPacketShape(packet)) return false;
        if (connection.IsHost != (packet.Message is IHostMessage) ||
            (packet.Message is not IHostMessage && packet.Message is not IPeerMessage))
            return false;
        try
        {
            if (!MpValidation.Validate(packet.Message)) return false;
        }
        catch
        {
            return false;
        }

        var room = connection.Room;
        lock (room.Sync)
        {
            if (room.Closed) return false;
            if (packet.Message is IRunMessage)
            {
                if (packet.Message is CheckRunMessage)
                {
                    if (!connection.IsHost || packet.RunId == Guid.Empty || room.Phase != RunPhase.Lobby ||
                        !room.SeenRuns.Add(packet.RunId))
                    {
                        if (room.SeenRuns.Count >= MpLimits.RunsPerRoom)
                            _ = ObserveConnectionTaskAsync(CloseRoomAsync(room));
                        return false;
                    }
                    if (room.SeenRuns.Count > MpLimits.RunsPerRoom)
                    {
                        _ = ObserveConnectionTaskAsync(CloseRoomAsync(room));
                        return false;
                    }
                    room.CurrentRunId = packet.RunId;
                    room.Phase = RunPhase.Checked;
                    return true;
                }

                if (room.CurrentRunId == Guid.Empty || packet.RunId != room.CurrentRunId)
                {
                    // EndRun and owner samples travel on different sockets. Known retired runs
                    // are consumed without forwarding; unknown runs remain protocol violations.
                    if (!room.SeenRuns.Contains(packet.RunId)) return false;
                    forward = false;
                    return true;
                }
                if (packet.Message is PrepareRunMessage)
                {
                    if (!connection.IsHost || room.Phase != RunPhase.Checked) return false;
                    room.Phase = RunPhase.Preparing;
                }
                else if (packet.Message is CommitRunMessage)
                {
                    if (!connection.IsHost || room.Phase != RunPhase.Preparing) return false;
                    room.Phase = RunPhase.Running;
                }
                else if (packet.Message is PauseMessage)
                {
                    if (!connection.IsHost || room.Phase != RunPhase.Running) return false;
                }
                else if (packet.Message is EndRunMessage)
                {
                    if (!connection.IsHost || room.Phase == RunPhase.Lobby) return false;
                    room.CurrentRunId = Guid.Empty;
                    room.Phase = RunPhase.Lobby;
                }
                else if (packet.Message is WorldSnapshotMessage or RolesSnapshotMessage or WorldEventMessage or AbilityResultMessage)
                {
                    if (!connection.IsHost || room.Phase != RunPhase.Running) return false;
                }
                else if (packet.Message is AbilityUseMessage)
                {
                    if (connection.IsHost) return false;
                    // Input can race prepare/commit across sockets; discard it
                    // without granting authority or disconnecting the room.
                    if (room.Phase != RunPhase.Running) forward = false;
                }
                else if (packet.Message is SelfPoseMessage or PartyMarkerRequestMessage)
                {
                    if (connection.IsHost || room.Phase != RunPhase.Running) return false;
                }
            }
            else if (packet.RunId != Guid.Empty)
            {
                return false;
            }
        }
        return true;
    }

    private void ForwardApplicationPacket(PeerConnection source, ClientPacket packet)
    {
        // A stale or deny-all allowlist cannot carry traffic either: the relay stops routing
        // until a fresh snapshot proves the room's grant is still authorized.
        if (!IsGrantSnapshotFresh()) return;
        var room = source.Room;
        PeerConnection[] targets;
        lock (room.Sync)
        {
            if (room.Closed) return;
            if (packet.Message is IPeerMessage and IRunMessage && packet.RunId != room.CurrentRunId) return;
            targets = source.IsHost
                ? room.Members.Values.Where(peer => !ReferenceEquals(peer, source) && peer.Joined
                    // A pre-join lobby snapshot may still be queued when the new
                    // socket finishes its handshake. It belongs only to the roster
                    // it describes, never to a newcomer awaiting Hello acceptance.
                    && (packet.Message is not LobbyMessage lobby ||
                        lobby.Members.Any(member => member.PeerId == peer.PeerId))).ToArray()
                : room.Host is { Joined: true } host ? new[] { host } : Array.Empty<PeerConnection>();
        }

        var stamped = new ReceivedPacket(room.RoomId, source.PeerId, source.IsHost,
            packet.Sequence, packet.RunId, packet.Message);
        byte[] bytes;
        try { bytes = WireProtocol.EncodeServerPacket(stamped); }
        catch { source.Socket.Abort(); return; }
        var latest = packet.Message is ILatestState;
        var key = latest ? new StateKey(source.PeerId, packet.RunId, packet.Message.GetType()) : (StateKey?)null;
        foreach (var target in targets)
        {
            // 送不出去＝這位成員讀得比房主送得慢，佇列在 liveness 內就滿了。以前只有
            // Abort()，房主端看到的只是 PeerDisconnected；記下原因才分得出是網路還是主執行緒卡住。
            if (!target.Outbound.Enqueue(new RelayOutboundItem(bytes, latest, key)))
                AbortWithReason(target, "outbound-overflow:packet");
        }

    }

    private async Task RunSendLoopAsync(PeerConnection connection)
    {
        try
        {
            while (!connection.Lifetime.IsCancellationRequested)
            {
                await connection.Outbound.WaitAsync(connection.Lifetime.Token).ConfigureAwait(false);
                if (!connection.Outbound.TryDequeue(out var item) || item is null) continue;
                using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(connection.Lifetime.Token);
                sendCts.CancelAfter(TimeSpan.FromSeconds(MpLimits.IoTimeoutSeconds));
                await connection.Socket.SendAsync(item.Bytes, WebSocketMessageType.Text, true, sendCts.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AbortWithReason(connection, $"send-failed:{ex.GetType().Name}");
        }
        catch
        {
            connection.Socket.Abort();
        }
    }

    private async Task LivenessLoopAsync(CancellationToken token)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(1, MpLimits.HeartbeatSeconds));
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(delay, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            Room[] currentRooms;
            lock (roomsSync) currentRooms = rooms.Values.ToArray();
            foreach (var room in currentRooms)
            {
                (PeerConnection Peer, double SilentSeconds)[] stale;
                lock (room.Sync)
                    stale = room.Members.Values
                        .Select(peer => (Peer: peer,
                            SilentSeconds: Stopwatch.GetElapsedTime(Volatile.Read(ref peer.LastInboundTimestamp)).TotalSeconds))
                        .Where(entry => entry.SilentSeconds > MpLimits.LivenessSeconds)
                        .ToArray();
                // 這條是「成員突然被踢」最常見的路，以前完全不記——2026-09-16 本機開房
                // 連續數次都只看得到 PeerDisconnected 就是因為它靜默。
                foreach (var (peer, silent) in stale)
                    AbortWithReason(peer, $"liveness silent={silent:F1}s");
            }
        }
    }

    /// <summary>
    /// Authorization refresh. It is deliberately independent of the accept loop and of the 20 Hz
    /// data path: source I/O never runs while a room lock is held.
    /// </summary>
    private async Task RefreshLoopAsync(CancellationToken token)
    {
        var period = TimeSpan.FromSeconds(HostGrantLimits.RefreshIntervalSeconds);
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(period, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            await RefreshGrantsAsync(token).ConfigureAwait(false);
        }
    }

    private async Task RefreshGrantsAsync(CancellationToken token)
    {
        var merged = new List<HostGrant>();
        var succeeded = true;
        foreach (var source in grantSources)
        {
            HostGrantSnapshot read;
            try
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                readCts.CancelAfter(TimeSpan.FromMilliseconds(HostGrantLimits.ReadTimeoutMilliseconds));
                read = await source.ReadAsync(readCts.Token).ConfigureAwait(false);
            }
            catch
            {
                succeeded = false;
                break;
            }
            if (read is null || read.Count > HostGrantLimits.MaxGrants ||
                merged.Count + read.Count > HostGrantLimits.MaxGrants)
            {
                succeeded = false;
                break;
            }
            merged.AddRange(read.Grants);
        }

        var published = HostGrantSnapshot.Empty;
        if (succeeded)
        {
            try { published = new HostGrantSnapshot(merged); }
            catch (ArgumentException) { succeeded = false; }
        }
        await PublishGrantsAsync(published, succeeded).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes a snapshot and retires everything it no longer authorizes. Invalidation of every
    /// affected lease and room happens inside one lock; only the socket drain is awaited
    /// afterwards, so one slow socket cannot delay another grant's revocation.
    /// </summary>
    private async Task PublishGrantsAsync(HostGrantSnapshot published, bool succeeded)
    {
        Room[] revokedRooms;
        HostLease[] revokedLeases;
        lock (roomsSync)
        {
            grants = published;
            // A failed or malformed read denies everything and stops being fresh; it never
            // falls back to the previously granted permissions.
            Volatile.Write(ref grantsTimestamp, succeeded ? Stopwatch.GetTimestamp() : 0);

            revokedLeases = leases.Values.Where(lease => !IsGrantUsableLocked(lease.GrantId)).ToArray();
            foreach (var lease in revokedLeases)
            {
                lease.Revoked = true;
                leases.Remove(lease.LeaseId);
            }
            revokedRooms = rooms.Values.Where(room => !IsGrantUsableLocked(room.GrantId)).ToArray();
            foreach (var room in revokedRooms) rooms.Remove(room.Code);
        }

        if (revokedLeases.Length == 0 && revokedRooms.Length == 0) return;
        foreach (var lease in revokedLeases)
        {
            try { lease.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
        var pending = new List<Task>();
        foreach (var room in revokedRooms) pending.AddRange(CloseRoomCore(room));
        foreach (var task in pending)
        {
            try { await task.ConfigureAwait(false); } catch { }
        }
    }

    private bool IsGrantUsableLocked(string grantId)
        => grants.Find(grantId) is { Enabled: true };

    private bool IsGrantSnapshotFresh()
    {
        var stamp = Volatile.Read(ref grantsTimestamp);
        return stamp != 0 &&
            Stopwatch.GetElapsedTime(stamp).TotalSeconds <= HostGrantLimits.SnapshotFreshnessSeconds;
    }

    private void NotifyExistingPeersToHost(Room room, PeerConnection host)
    {
        PeerConnection[] existing;
        lock (room.Sync)
        {
            existing = room.Members.Values.Where(peer => !ReferenceEquals(peer, host) && peer.Joined).ToArray();
        }
        foreach (var peer in existing) EnqueueControl(host, RelayControlKind.PeerJoined, peer.PeerId);
    }

    private void NotifyPeerJoined(Room room, PeerConnection joined)
    {
        PeerConnection[] existing;
        lock (room.Sync)
        {
            existing = room.Members.Values.Where(peer => !ReferenceEquals(peer, joined) && peer.Joined).ToArray();
        }
        foreach (var target in existing)
            EnqueueControl(target, RelayControlKind.PeerJoined, joined.PeerId);
        foreach (var peer in existing.Where(peer => !peer.IsHost))
            EnqueueControl(joined, RelayControlKind.PeerJoined, peer.PeerId);
    }
    private void NotifyPeerLeft(Room room, Guid peerId, IReadOnlyList<PeerConnection> targets)
    {
        foreach (var target in targets)
            EnqueueControl(target, RelayControlKind.PeerLeft, peerId);
    }

    private void EnqueueControl(PeerConnection target, RelayControlKind kind, Guid peerId)
    {
        lock (target.Sync)
        {
            if (target.Closed || !target.Joined) return;
            var sequence = ++target.NextControlSequence;
            byte[] bytes;
            try { bytes = WireProtocol.EncodeControl(target.Room.RoomId, kind, peerId, sequence); }
            catch { target.Socket.Abort(); return; }
            var key = kind == RelayControlKind.HeartbeatAck
                ? new StateKey(Guid.Empty, Guid.Empty, typeof(RelayControlKind))
                : (StateKey?)null;
            if (!target.Outbound.Enqueue(new RelayOutboundItem(bytes, kind == RelayControlKind.HeartbeatAck, key)))
                AbortWithReason(target, "outbound-overflow:control");
        }
    }

    private async Task RemoveConnectionAsync(PeerConnection connection)
    {
        var ownsCleanup = false;
        lock (connection.Sync)
        {
            if (!connection.Closed)
            {
                connection.Closed = true;
                connection.Lifetime.Cancel();
                connection.Outbound.Dispose();
                try { connection.Socket.Abort(); } catch { }
                ownsCleanup = true;
            }
        }

        if (ownsCleanup)
        {
            var room = connection.Room;
            if (connection.IsHost)
            {
                await CloseRoomAsync(room).ConfigureAwait(false);
            }
            else
            {
                PeerConnection[] targets;
                var removed = false;
                lock (room.Sync)
                {
                    if (room.Members.Remove(connection.PeerId, out _))
                    {
                        removed = true;
                        targets = room.Members.Values.Where(peer => peer.Joined && !peer.Closed).ToArray();
                    }
                    else targets = Array.Empty<PeerConnection>();
                }
                if (removed)
                {
                    NotifyPeerLeft(room, connection.PeerId, targets);
                    Log($"room left     code={room.Code}  members={targets.Length}/{MpLimits.Members}");
                }
            }
        }

        var sendTask = connection.SendTask;
        if (sendTask is not null && sendTask.Id != Task.CurrentId)
        {
            try { await sendTask.ConfigureAwait(false); } catch { }
        }
        try { await connection.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false); }
        catch { }
        var disposeResources = false;
        lock (connection.Sync)
        {
            if (!connection.ResourcesDisposed)
            {
                connection.ResourcesDisposed = true;
                disposeResources = true;
            }
        }
        if (disposeResources)
        {
            connection.Socket.Dispose();
            connection.Lifetime.Dispose();
        }
    }

    private async Task CloseRoomAsync(Room room)
    {
        foreach (var sendTask in CloseRoomCore(room))
        {
            try { await sendTask.ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>
    /// Synchronously retires a room: every member is cancelled and aborted before this returns,
    /// and the returned tasks are only the send loops left to drain.
    /// </summary>
    private List<Task> CloseRoomCore(Room room)
    {
        PeerConnection[] members;
        lock (room.Sync)
        {
            if (room.Closed) return new List<Task>();
            room.Closed = true;
            members = room.Members.Values.ToArray();
            room.Members.Clear();
            room.Host = null;
        }
        Log($"room closed   code={room.Code}  grant={Short(room.GrantId)}  members={members.Length}");
        lock (roomsSync)
        {
            if (rooms.TryGetValue(room.Code, out var current) && ReferenceEquals(current, room))
                rooms.Remove(room.Code);
        }
        var sendTasks = new List<Task>(members.Length);
        foreach (var member in members)
        {
            lock (member.Sync)
            {
                member.Closed = true;
                member.Lifetime.Cancel();
                member.Outbound.Dispose();
                try { member.Socket.Abort(); } catch { }
            }
            if (member.SendTask is { } sendTask && sendTask.Id != Task.CurrentId)
                sendTasks.Add(sendTask);
        }
        return sendTasks;
    }

    private static bool IsRoomLive(Room room)
    {
        lock (room.Sync) return !room.Closed;
    }

    /// <summary>
    /// Reserves one unit of the grant's room capacity before the upgrade. The reservation is
    /// revocable and is not a final authorization.
    /// </summary>
    private bool TryReserveHostLease(string? authorization, CancellationToken serverToken,
        out HostLease? lease, out string? secret)
    {
        lease = null;
        secret = null;
        if (!TryBearer(authorization, out var token) ||
            !HostGrantFormat.TryParseAuthorization(token, out var grantId, out var supplied))
            return false;

        lock (roomsSync)
        {
            if (!TryUseGrantLocked(grantId, supplied, out var grant)) return false;
            if (rooms.Count + leases.Count >= options.MaxRooms) return false;
            if (CountGrantUsageLocked(grantId, Guid.Empty) >= grant!.MaxRooms) return false;
            var reserved = new HostLease(grantId, serverToken);
            leases.Add(reserved.LeaseId, reserved);
            lease = reserved;
            secret = supplied;
            return true;
        }
    }

    /// <summary>
    /// Re-verifies the grant and capacity now that a socket exists, then publishes the room with
    /// its host already attached: no other socket can observe a host-less room.
    /// </summary>
    private bool TryPromoteHostLease(HostLease lease, string secret, WebSocket socket,
        CancellationToken serverToken, out Room? room, out PeerConnection? connection)
    {
        room = null;
        connection = null;
        int roomCount;
        string label;
        lock (roomsSync)
        {
            if (lease.Revoked || !leases.ContainsKey(lease.LeaseId)) return false;
            if (!TryUseGrantLocked(lease.GrantId, secret, out var grant)) return false;
            if (rooms.Count >= options.MaxRooms) return false;
            if (CountGrantUsageLocked(lease.GrantId, lease.LeaseId) >= grant!.MaxRooms) return false;
            if (!TryReserveRoomCodeLocked(out var code)) return false;

            var created = new Room(code!, lease.GrantId, HostGrantFormat.NewSecret());
            var host = new PeerConnection(created, socket, isHost: true, serverToken, lease.Cancellation.Token);
            created.Host = host;
            created.Members.Add(host.PeerId, host);
            rooms.Add(created.Code, created);
            leases.Remove(lease.LeaseId);
            room = created;
            connection = host;
            roomCount = rooms.Count;
            label = grant.Label;
        }
        Log($"room created  code={room.Code}  host={label} ({Short(room.GrantId)})  rooms={roomCount}/{options.MaxRooms}");
        return true;
    }

    /// <summary>One room-lifecycle line. Never contains a secret; a failing sink never
    /// affects forwarding.</summary>
    private void Log(string message)
    {
        if (options.ActivityLog is not { } sink) return;
        try { sink(message); }
        catch { }
    }

    private static string Short(string id) => id.Length <= 8 ? id : id[..8];

    private void ReleaseHostLease(HostLease lease)
    {
        lock (roomsSync) leases.Remove(lease.LeaseId);
        lease.Cancellation.Dispose();
    }

    private int CountGrantUsageLocked(string grantId, Guid excludedLease)
    {
        var used = 0;
        foreach (var room in rooms.Values)
            if (string.Equals(room.GrantId, grantId, StringComparison.Ordinal)) used++;
        foreach (var lease in leases.Values)
            if (lease.LeaseId != excludedLease && string.Equals(lease.GrantId, grantId, StringComparison.Ordinal))
                used++;
        return used;
    }

    private bool TryUseGrantLocked(string grantId, string secret, out HostGrant? grant)
    {
        grant = null;
        if (!IsGrantSnapshotFresh()) return false;
        var candidate = grants.Find(grantId);
        if (candidate is null || !candidate.Enabled || !candidate.MatchesSecret(secret)) return false;
        grant = candidate;
        return true;
    }

    private bool IsHostGrantAuthorized(string? authorization)
    {
        if (!TryBearer(authorization, out var token) ||
            !HostGrantFormat.TryParseAuthorization(token, out var grantId, out var secret))
            return false;
        lock (roomsSync) return TryUseGrantLocked(grantId, secret, out _);
    }

    /// <summary>
    /// Join admission. The bearer must be the join secret of exactly this room, so a secret from
    /// another room, a host credential or a stale invitation cannot enter.
    /// </summary>
    private bool TryAuthorizeJoin(string? authorization, string roomCode, out Room? room)
    {
        room = null;
        if (!TryBearer(authorization, out var token) || !HostGrantFormat.IsSecret(token)) return false;
        lock (roomsSync)
        {
            if (!IsGrantSnapshotFresh()) return false;
            if (!rooms.TryGetValue(roomCode, out var found)) return false;
            if (!IsGrantUsableLocked(found.GrantId)) return false;
            if (!FixedTimeEquals(token, found.JoinSecret)) return false;
            room = found;
            return true;
        }
    }

    private bool TryReserveRoomCodeLocked(out string? code)
    {
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var candidate = GenerateRoomCode();
            if (rooms.ContainsKey(candidate)) continue;
            code = candidate;
            return true;
        }
        code = null;
        return false;
    }

    private bool TryJoinRoom(Room room, PeerConnection connection)
    {
        int members;
        lock (room.Sync)
        {
            if (room.Closed || room.Phase != RunPhase.Lobby || room.Members.Count >= MpLimits.Members)
                return false;
            room.Members.Add(connection.PeerId, connection);
            connection.Joined = true;
            members = room.Members.Count;
        }
        Log($"room joined   code={room.Code}  members={members}/{MpLimits.Members}");
        return true;
    }

    private bool AcceptRate(PeerConnection connection)
    {
        lock (connection.Sync)
        {
            var now = Stopwatch.GetTimestamp();
            connection.RateTokens = Math.Min(MpLimits.RelayIngressBurst,
                connection.RateTokens + Stopwatch.GetElapsedTime(connection.LastRateTimestamp, now).TotalSeconds *
                MpLimits.MessagesPerSenderSecond);
            connection.LastRateTimestamp = now;
            if (connection.RateTokens < 1) return false;
            connection.RateTokens--;
            return true;
        }
    }

    private static bool TryBearer(string? header, out string token)
    {
        token = string.Empty;
        if (header is null || !header.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        token = header[7..];
        return token.Length != 0;
    }

    private static bool FixedTimeEquals(string supplied, string expected)
    {
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash);
    }

    private void HandleReady(HttpListenerResponse response, HttpListenerRequest request, string? authorization)
    {
        if (!string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) ||
            request.IsWebSocketRequest ||
            !IsHostGrantAuthorized(authorization))
        {
            RejectHttp(response, HttpStatusCode.Unauthorized);
            return;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(InstanceId);
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "text/plain; charset=utf-8";
            response.Headers[HttpResponseHeader.CacheControl] = "no-store";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.Close();
        }
        catch
        {
            try { response.Abort(); } catch { }
        }
    }
    private static async Task ObserveLateAcceptedSocketAsync(Task<HttpListenerWebSocketContext>? accept)
    {
        if (accept is null) return;
        try
        {
            var accepted = await accept.ConfigureAwait(false);
            if (accepted.WebSocket is { } socket)
                await CloseSocketAsync(socket, WebSocketCloseStatus.PolicyViolation).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static void AbortResponse(HttpListenerResponse response)
    {
        try { response.Abort(); } catch { }
    }

    private static bool TryRoute(HttpListenerRequest request, out RelayRoute route, out string? roomCode)
    {
        route = RelayRoute.None;
        roomCode = null;
        if (request.Url is null || !string.IsNullOrEmpty(request.Url.Query) ||
            !string.IsNullOrEmpty(request.Url.Fragment))
            return false;
        var parts = request.Url.AbsolutePath.Split('/', StringSplitOptions.None);
        if (parts.Length == 2 && parts[0].Length == 0)
        {
            switch (parts[1])
            {
                case "host":
                    route = RelayRoute.Host;
                    return true;
                case "ready":
                    route = request.IsWebSocketRequest ? RelayRoute.Probe : RelayRoute.Ready;
                    return true;
                default:
                    return false;
            }
        }
        if (parts.Length == 3 && parts[0].Length == 0 && parts[1] == "session" &&
            WireProtocol.IsRoomCode(parts[2]))
        {
            route = RelayRoute.Join;
            roomCode = parts[2];
            return true;
        }
        return false;
    }

    private static async Task<(WebSocketMessageType MessageType, byte[] Bytes)> ReceiveMessageAsync(
        WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var assemblyCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        assemblyCts.CancelAfter(WireProtocol.AssemblyTimeout);
        using var stream = new MemoryStream();
        var fragments = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), assemblyCts.Token)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return (result.MessageType, Array.Empty<byte>());
            if (++fragments > MaxFragments) throw new InvalidDataException();
            if (stream.Length + result.Count > WireProtocol.MaxJsonBytes) throw new InvalidDataException();
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return (result.MessageType, stream.ToArray());
        }
    }

    private static async Task SendDirectAsync(WebSocket socket, byte[] bytes, CancellationToken token)
    {
        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        sendCts.CancelAfter(TimeSpan.FromSeconds(MpLimits.IoTimeoutSeconds));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, sendCts.Token).ConfigureAwait(false);
    }

    private static async Task CloseSocketAsync(WebSocket socket, WebSocketCloseStatus status)
    {
        try { socket.Abort(); } catch { }
        try { await socket.CloseAsync(status, null, CancellationToken.None).ConfigureAwait(false); }
        catch { }
        socket.Dispose();
    }

    private static void RejectHttp(HttpListenerResponse response, HttpStatusCode status)
    {
        try
        {
            response.StatusCode = (int)status;
            response.ContentLength64 = 0;
            response.Close();
        }
        catch { }
    }

    private static string GenerateRoomCode()
    {
        Span<byte> random = stackalloc byte[CodeLength];
        RandomNumberGenerator.Fill(random);
        Span<char> chars = stackalloc char[CodeLength];
        for (var i = 0; i < CodeLength; i++) chars[i] = CodeAlphabet[random[i] % CodeAlphabet.Length];
        return new string(chars);
    }

    private static string BuildPrefix(IPAddress address, int port)
    {
        var host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{address}]" : address.ToString();
        return $"http://{host}:{port}/";
    }

    private static void ValidateOptions(RelayServerOptions options)
    {
        if (options.BindAddress is null || options.BindAddress.Equals(IPAddress.Any) ||
            options.BindAddress.Equals(IPAddress.IPv6Any))
            throw new ArgumentException("A literal bind address is required.", nameof(options));
        if (options.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(options.Port));
        if (options.MaxRooms is < 1 or > MpLimits.Rooms) throw new ArgumentOutOfRangeException(nameof(options.MaxRooms));
        if (options.HostGrantSource is null)
            throw new ArgumentException("A host grant source is required.", nameof(options));
    }
}
