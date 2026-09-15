using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AnoMech.Multiplayer;

internal sealed partial class MultiplayerManager
{
    private CancellationTokenSource? connectionCancellation;
    private Task<MpError>? connection;
    private Task<HostedRelay>? hosting;
    // Everything the current hosting attempt needs after the handshake: the endpoint friends
    // must reach, whether that endpoint is same-machine only, the concrete transport holding
    // this room's join secret, and — only for the one-click local host — the relay/tunnel
    // process this plugin owns.  An imported shared-relay grant owns nothing: it must never be
    // health-checked or shut down by us.
    private sealed record HostingState(Uri Endpoint, bool LocalOnly, HostedRelay? Owned, RelayClient? Client = null);

    private HostingState? hostingState;
    private Task? hostingCleanup;
    private long connectionGeneration;
    private long hostingGeneration;
    private string connectingAlias = "";
    private bool connectingAsHost;
    private bool cleanupFailed;

    public bool IsConnecting => hosting != null || connection != null || hostingCleanup != null;
    public string ConnectionStatus { get; private set; } = "";
    public string Destination { get; private set; } = "";
    public string Invitation { get; private set; } = "";

    public void Host(string profilePath, string alias) => Queue(() =>
    {
        if (!CanConnect(alias)) return;
        DisconnectInternal();
        game.InvalidateQueuedScenarioWork();
        connectingAlias = alias;
        connectingAsHost = true;
        connectionCancellation = new CancellationTokenSource();
        var token = connectionCancellation.Token;
        hostingGeneration = ++connectionGeneration;
        Report(MpError.None);
        ConnectionStatus = "正在啟動連線服務並確認外網入口（最長 45 秒）…";
        hosting = Task.Run(() => StartFromProfileAsync(profilePath, token));
    });

    // Shared relay: a friend with an imported hosting grant creates a room on someone else's
    // always-on relay.  No local relay or tunnel is started, so nothing here is ours to stop.
    public void HostShared(HostInvitation? invitation, string alias) => Queue(() =>
    {
        if (invitation is null)
        {
            ConnectionStatus = "尚未匯入有效的開房授權；請先匯入房主授權檔內容。";
            Report(MpError.InvalidEndpoint);
            return;
        }
        if (!CanConnect(alias)) return;
        DisconnectInternal();
        game.InvalidateQueuedScenarioWork();
        connectingAlias = alias;
        connectingAsHost = true;
        connectionCancellation = new CancellationTokenSource();
        hostingGeneration = ++connectionGeneration;
        Report(MpError.None);
        Destination = invitation.Endpoint.GetLeftPart(UriPartial.Authority);
        hostingState = new HostingState(invitation.Endpoint, invitation.LocalOnly, null);
        try
        {
            ConnectTransport(new RelayConnectionOptions(
                invitation.Endpoint, true, string.Empty, invitation.AuthorizationToken));
        }
        catch
        {
            // The hosting state is already published so cleanup can find it; nothing is owned
            // here, but the slot must not stay occupied after a failed start.
            DisconnectInternal();
            ConnectionStatus = "無法開始共用 Relay 連線；請稍後再試。";
            Report(MpError.TransportFailure);
        }
    });

    public void Join(string invitation, string alias) => Queue(() =>
    {
        if (!CanConnect(alias)) return;
        if (!RoomInvitation.TryParse(invitation, out var parsed) || parsed == null)
        {
            ConnectionStatus = "邀請格式不正確；請貼上房主複製的完整邀請資訊。";
            Report(MpError.InvalidEndpoint);
            return;
        }
        DisconnectInternal();
        game.InvalidateQueuedScenarioWork();
        connectingAsHost = false;
        connectingAlias = alias;
        connectionCancellation = new CancellationTokenSource();
        Destination = parsed.Endpoint.GetLeftPart(UriPartial.Authority);
        ConnectTransport(new RelayConnectionOptions(parsed.Endpoint, false, parsed.RoomCode, parsed.Token));
    });

    private bool CanConnect(string alias)
    {
        if (cleanupFailed)
        {
            ConnectionStatus = "上次連線服務未完成清理；請關閉遊戲後再建立房間。";
            Report(MpError.TransportFailure);
            return false;
        }
        if (HasSession || IsConnecting || hostingState != null || game.World.Map.IsInInstance || game.NetworkIsRestoring)
        {
            Report(MpError.Busy);
            return false;
        }
        if (!MpValidation.Alias(alias))
        {
            ConnectionStatus = "請填寫有效的顯示別名。";
            Report(MpError.InvalidMessage);
            return false;
        }
        return true;
    }

    private static async Task<HostedRelay> StartFromProfileAsync(string profilePath, CancellationToken token)
    {
        HostedRelayOptions? options;
        try
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(profilePath))
                throw new HostingException(HostingError.MissingConfiguration);
            await using var stream = new FileStream(profilePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous);
            if (stream.Length is <= 0 or > 4096)
                throw new HostingException(HostingError.InvalidConfiguration);
            options = await JsonSerializer.DeserializeAsync<HostedRelayOptions>(stream,
                new JsonSerializerOptions { MaxDepth = 4, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow },
                token).ConfigureAwait(false);
            if (options == null) throw new HostingException(HostingError.InvalidConfiguration);
        }
        catch (FileNotFoundException) { throw new HostingException(HostingError.MissingConfiguration); }
        catch (DirectoryNotFoundException) { throw new HostingException(HostingError.MissingConfiguration); }
        catch (UnauthorizedAccessException) { throw new HostingException(HostingError.InvalidConfiguration); }
        catch (IOException) { throw new HostingException(HostingError.InvalidConfiguration); }
        catch (ArgumentException) { throw new HostingException(HostingError.InvalidConfiguration); }
        catch (JsonException) { throw new HostingException(HostingError.InvalidConfiguration); }
        return await HostedRelay.StartAsync(options, token,
            line => Core.CrashTrace.Log($"[relay] {line}")).ConfigureAwait(false);
    }

    private void ConnectTransport(RelayConnectionOptions options)
    {
        var transport = new RelayClient();
        try
        {
            // Attach before anything can throw: cleanup must be able to find both the owned
            // process and the transport that holds this room's join secret.
            if (hostingState is { } state)
                hostingState = state with { Client = transport };
            Session = new MultiplayerSession(transport, this, connectingAlias);
            Session.Faulted += OnFault;
            closedCleaned = false;
            Report(MpError.None);
            ConnectionStatus = connectingAsHost ? "正在建立房間…" : "正在加入房間…";
            connection = transport.ConnectAsync(options, connectionCancellation!.Token);
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }

    // Only the framework consumes successful startup results. Disconnected tasks
    // move to cleanup instead; they can never publish a session in a later room.
    private bool AdvanceConnection()
    {
        if (hostingCleanup is { IsCompleted: true } cleanup)
        {
            hostingCleanup = null;
            try { cleanup.GetAwaiter().GetResult(); }
            catch
            {
                cleanupFailed = true;
                ConnectionStatus = "連線服務清理失敗；請關閉遊戲後再開房。";
                Report(MpError.TransportFailure);
                return false;
            }
        }
        if (hosting is { IsCompleted: true } started)
        {
            hosting = null;
            try
            {
                var owned = started.GetAwaiter().GetResult();
                hostingState = new HostingState(owned.PublicEndpoint, owned.LocalOnly, owned);
                if (hostingGeneration != connectionGeneration || disposed ||
                    game.World.Map.IsInInstance || game.NetworkIsRestoring)
                {
                    DisconnectInternal();
                    Report(MpError.Busy);
                    return false;
                }
                Destination = owned.PublicEndpoint.GetLeftPart(UriPartial.Authority);
                game.InvalidateQueuedScenarioWork();
                ConnectTransport(owned.HostConnection);
            }
            catch (OperationCanceledException)
            {
                DisconnectInternal();
                ConnectionStatus = "已取消開房。";
                return false;
            }
            catch (HostingException error)
            {
                DisconnectInternal();
                ConnectionStatus = HostingFailureText(error.Error);
                Report(MpError.TransportFailure);
                return false;
            }
            catch
            {
                DisconnectInternal();
                ConnectionStatus = "無法啟動連線服務；請檢查房主設定。";
                Report(MpError.TransportFailure);
                return false;
            }
        }
        // Only a relay/tunnel we started ourselves can be health-checked or torn down here.
        if (hostingState is { Owned: { } activeOwned } && !activeOwned.IsRunning)
        {
            DisconnectInternal();
            ConnectionStatus = "外網連線服務已停止，房間已關閉；請重新建立房間。";
            Report(MpError.TransportFailure);
            return false;
        }
        if (connection is { IsCompleted: true } completed)
        {
            connection = null;
            var error = completed.GetAwaiter().GetResult();
            if (error != MpError.None)
            {
                var wasHost = connectingAsHost;
                var wasOwnedHost = hostingState?.Owned != null;
                DisconnectInternal();
                ConnectionStatus = wasHost
                    ? wasOwnedHost
                        ? "建立房間失敗；連線服務已停止，請檢查房主設定後重試。"
                        : "共用 Relay 開房失敗；請確認授權未被撤銷、未超過開房上限，並向 Relay 管理者確認後重試。"
                    : "加入失敗；請確認房主仍在線、房間未滿且尚未開始，並重新取得邀請。";
                Report(error);
                return false;
            }
            ConnectionStatus = connectingAsHost ? "房間已建立，可複製邀請給朋友。" : "已加入房間。";
        }
        return true;
    }

    private void UpdateInvitation()
    {
        if (Invitation.Length != 0 || hostingState is null) return;
        var state = hostingState;
        if (state.Client is not { } client || (state.Owned is { } owned && !owned.IsRunning) ||
            client.HostJoinToken is not { } joinToken ||
            Session is not { IsHost: true, Phase: MultiplayerPhase.Lobby, Identity: { } identity }) return;
        try
        {
            Invitation = new RoomInvitation(state.Endpoint, identity.RoomCode, joinToken, state.LocalOnly).Encode();
        }
        catch (InvalidOperationException)
        {
            // A room we cannot describe to a friend is not a usable room; do not keep it open
            // in a state where the UI silently offers nothing to share.
            DisconnectInternal();
            ConnectionStatus = "無法產生邀請資訊；房間已關閉，請確認開房入口網址後重試。";
            Report(MpError.InvalidEndpoint);
        }
    }

    private void DisconnectInternal()
    {
        ++connectionGeneration;
        Invitation = "";
        Destination = "";
        ConnectionStatus = "";
        var cancellation = connectionCancellation;
        connectionCancellation = null;
        cancellation?.Cancel();
        var old = Session;
        Session = null;
        if (old != null)
        {
            old.Faulted -= OnFault;
            old.Dispose();
        }
        var pendingHost = hosting;
        hosting = null;
        // Only the owned relay/tunnel is disposed here.  A shared relay belongs to somebody
        // else: leaving the room must never stop their service.
        var activeHost = hostingState?.Owned;
        hostingState = null;
        var pendingConnection = connection;
        connection = null;
        if (pendingHost != null || activeHost != null || pendingConnection != null || cancellation != null)
        {
            hostingCleanup = Task.Run(() => CleanupConnectionAsync(pendingHost, activeHost, pendingConnection, cancellation));
            // Dispose can be the final framework invocation. Observe failures even
            // then; ordinary ticks additionally surface the failure and block reuse.
            _ = hostingCleanup.ContinueWith(static task => { _ = task.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        closedCleaned = true;
        pendingRetry = null;
        preserveStreakOnEnd = false;
        game.CancelPendingRetry();
        game.ClearStreak();
        EndRun(true);
        resources = null;
        resourceSceneFingerprint = null;
        approvedScene = null;
        connectingAlias = "";
    }

    private static async Task CleanupConnectionAsync(Task<HostedRelay>? pendingHost, HostedRelay? activeHost,
        Task<MpError>? pendingConnection, CancellationTokenSource? cancellation)
    {
        try
        {
            if (pendingHost != null)
            {
                try { activeHost = await pendingHost.ConfigureAwait(false); }
                catch (Exception) { /* StartAsync owns cleanup before its failure returns. */ }
            }
            if (activeHost != null) await activeHost.DisposeAsync().ConfigureAwait(false);
            if (pendingConnection != null)
            {
                try { await pendingConnection.ConfigureAwait(false); }
                catch (Exception) { /* The old session has already disposed its transport. */ }
            }
        }
        finally { cancellation?.Dispose(); }
    }

    private static string HostingFailureText(HostingError error) => error switch
    {
        HostingError.MissingConfiguration => "此電腦尚未完成一次性房主設定；加入朋友的房間只需貼邀請。",
        HostingError.InvalidConfiguration => "房主設定不完整或無法讀取；請檢查設定檔、外網網址與 cloudflared 路徑。",
        HostingError.PortInUse => "本機連線埠已被其他服務使用；未接管或停止其他服務。",
        HostingError.PermissionDenied => "本機監聽權限不足；需完成這個連線埠的一次性房主設定。",
        HostingError.TunnelStartFailed => "無法啟動外網入口；請確認 cloudflared 與專用 Tunnel 設定。",
        HostingError.PublicEndpointUnavailable => "外網入口尚未連通，或 WebSocket 檢查未通過；未建立可分享的邀請。",
        HostingError.UnsupportedPlatform => "此平台不支援自動外網開房；仍可貼邀請加入。",
        _ => "無法建立房間。",
    };
}
