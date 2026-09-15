using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AnoMech.Relay;

namespace AnoMech.Multiplayer;

public sealed record HostedRelayOptions
{
    public string PublicEndpoint { get; init; } = "";
    public string CloudflaredExecutable { get; init; } = "";
    public string TunnelConfigPath { get; init; } = "";
    // 27890：避開本機動態埠範圍 1024-15000（Hyper-V／WinNAT 會在其中成塊預留，
    // 7890 就落在 7851-7950 裡）。profile 明確指定的埠優先，這只是未指定時的值。
    public int Port { get; init; } = 27890;
    public bool LocalOnly { get; init; } = false;
}

public enum HostingError
{
    MissingConfiguration,
    InvalidConfiguration,
    PortInUse,
    PermissionDenied,
    TunnelStartFailed,
    PublicEndpointUnavailable,
    UnsupportedPlatform,
}

public sealed class HostingException : Exception
{
    public HostingError Error { get; }

    public HostingException(HostingError error)
        : base(GetSafeMessage(error))
    {
        Error = error;
    }

    private static string GetSafeMessage(HostingError error)
        => error switch
        {
            HostingError.MissingConfiguration => "Hosting configuration is missing.",
            HostingError.InvalidConfiguration => "Hosting configuration is invalid.",
            HostingError.PortInUse => "The hosting port is already in use.",
            HostingError.PermissionDenied => "The hosting listener permission was denied.",
            HostingError.TunnelStartFailed => "The hosted tunnel could not be started.",
            HostingError.PublicEndpointUnavailable => "The public hosting endpoint is unavailable.",
            HostingError.UnsupportedPlatform => "Hosted tunnels are unsupported on this platform.",
            _ => "Hosting failed.",
        };
}

/// <summary>
/// Owns one loopback relay and, for public hosting, one cloudflared process. Startup does not
/// return until the configured public endpoint reaches this relay through both readiness HTTP and
/// a real WebSocket upgrade.
/// </summary>
public sealed class HostedRelay : IAsyncDisposable
{
    private const int StartupSeconds = 45;
    private const int ProbeBodyLimit = 256;
    private const string OwnedGrantLabel = "owned-host";

    private readonly object sync = new();
    private RelayServer? relay;
    private HostedTunnelProcess? tunnel;
    private readonly bool localOnly;
    private readonly Uri publicEndpoint;
    private readonly RelayConnectionOptions hostConnection;
    private Task? disposeTask;
    private bool disposed;

    private HostedRelay(
        RelayServer relay,
        HostedTunnelProcess? tunnel,
        bool localOnly,
        Uri publicEndpoint,
        string hostAuthorization)
    {
        this.relay = relay;
        this.tunnel = tunnel;
        this.localOnly = localOnly;
        this.publicEndpoint = publicEndpoint;
        hostConnection = new RelayConnectionOptions(
            new Uri($"ws://127.0.0.1:{relay.Port}/"), true, string.Empty, hostAuthorization);
    }

    public RelayConnectionOptions HostConnection => hostConnection;
    public Uri PublicEndpoint => publicEndpoint;
    // There is no relay-wide join token any more: each room's join secret is issued by the relay
    // and reaches the host only through its handshake response.
    public bool LocalOnly => localOnly;

    public bool IsRunning
    {
        get
        {
            RelayServer? currentRelay;
            HostedTunnelProcess? currentTunnel;
            lock (sync)
            {
                if (disposed) return false;
                currentRelay = relay;
                currentTunnel = tunnel;
            }
            // tunnel 為 null 有兩種情況：LocalOnly（本來就沒有），以及 tunnel 由外部管理
            // （同機常駐的 cloudflared 服務）。兩者都只能以 relay 自己的狀態為準——
            // 拿「我們沒有子行程」當成服務停止，會讓外部管理模式一開房就被判定掛掉
            // 並關房（2026-09-15 實機：顯示「外網連線服務已停止」）。
            // 真的由我們拉起來的 tunnel 死掉，仍然要判為停止。
            return currentRelay?.IsRunning == true && currentTunnel is not { IsRunning: false };
        }
    }

    public static async Task<HostedRelay> StartAsync(
        HostedRelayOptions options, CancellationToken cancellationToken = default)
    {
        ValidateOptions(options, out var publicEndpoint);
        cancellationToken.ThrowIfCancellationRequested();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(StartupSeconds));
        // One ephemeral grant, held only in memory, validated by exactly the same admission
        // pipeline as a persisted friend grant. No global host/join token survives.
        var grantId = HostGrantFormat.NewGrantId();
        var hostSecret = HostGrantFormat.NewSecret();
        var hostAuthorization = grantId + "." + hostSecret;
        var grants = InMemoryHostGrantSource.ForSecret(grantId, hostSecret, OwnedGrantLabel, maxRooms: 1);

        RelayServer? relay = null;
        HostedTunnelProcess? tunnel = null;
        try
        {
            relay = new RelayServer(new RelayServerOptions
            {
                BindAddress = IPAddress.Loopback,
                Port = options.Port,
                MaxRooms = 1,
                HostGrantSource = grants,
            });
            // Start binds the listener and completes its first grant read before returning; the
            // deadline token owns only the asynchronous tunnel/probe phase, so a successful host
            // does not inherit a short-lived startup cancellation registration.
            await relay.StartAsync(CancellationToken.None).ConfigureAwait(false);

            if (!options.LocalOnly)
            {
                if (!OperatingSystem.IsWindows())
                    throw new HostingException(HostingError.UnsupportedPlatform);
                // profile 省略 cloudflared 路徑＝tunnel 由外部管理（同機的常駐服務）。
                // 這種情況只驗證「公開位址真的接到這一個 relay」，不自己拉——同一條
                // tunnel 兩個 connector 會讓子行程起不來，開房卡在等待（2026-09-15 實機）。
                var externallyManaged = string.IsNullOrWhiteSpace(options.CloudflaredExecutable);
                if (externallyManaged)
                {
                    await ProbePublicEndpointAsync(
                        publicEndpoint!, hostAuthorization, relay.InstanceId, relay, null, deadline.Token)
                        .ConfigureAwait(false);
                }
                else
                {
                    try
                    {
                        tunnel = await HostedTunnelProcess.StartAsync(
                            options.CloudflaredExecutable!, options.TunnelConfigPath!, deadline.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        throw new HostingException(HostingError.TunnelStartFailed);
                    }
                    if (!tunnel.IsRunning)
                        throw new HostingException(HostingError.TunnelStartFailed);
                    await ProbePublicEndpointAsync(
                        publicEndpoint!, hostAuthorization, relay.InstanceId, relay, tunnel, deadline.Token)
                        .ConfigureAwait(false);
                }
            }

            deadline.Token.ThrowIfCancellationRequested();
            var result = new HostedRelay(
                relay,
                tunnel,
                options.LocalOnly,
                publicEndpoint!,
                hostAuthorization);
            relay = null;
            tunnel = null;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CleanupFailedStartAsync(relay, tunnel).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await CleanupFailedStartAsync(relay, tunnel).ConfigureAwait(false);
            throw new HostingException(HostingError.PublicEndpointUnavailable);
        }
        catch (HostingException)
        {
            await CleanupFailedStartAsync(relay, tunnel).ConfigureAwait(false);
            throw;
        }
        catch (HttpListenerException exception)
        {
            await CleanupFailedStartAsync(relay, tunnel).ConfigureAwait(false);
            throw MapListenerFailure(exception);
        }
        catch (ArgumentException)
        {
            await CleanupFailedStartAsync(relay, tunnel).ConfigureAwait(false);
            throw new HostingException(HostingError.InvalidConfiguration);
        }
        catch
        {
            await CleanupFailedStartAsync(relay, tunnel).ConfigureAwait(false);
            throw new HostingException(HostingError.TunnelStartFailed);
        }
    }

    public ValueTask DisposeAsync()
    {
        Task cleanup;
        lock (sync)
        {
            if (disposeTask is null)
            {
                disposed = true;
                var currentRelay = relay;
                var currentTunnel = tunnel;
                relay = null;
                tunnel = null;
                disposeTask = DisposeOwnedAsync(currentRelay, currentTunnel);
            }
            cleanup = disposeTask;
        }
        return new ValueTask(cleanup);
    }

    private static async Task DisposeOwnedAsync(RelayServer? relay, HostedTunnelProcess? tunnel)
    {
        if (tunnel is not null)
        {
            try { await tunnel.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        if (relay is not null)
        {
            try { await relay.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    private static async Task CleanupFailedStartAsync(RelayServer? relay, HostedTunnelProcess? tunnel)
    {
        if (tunnel is not null)
        {
            try { await tunnel.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        if (relay is not null)
        {
            try { await relay.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    private static void ValidateOptions(HostedRelayOptions? options, out Uri? publicEndpoint)
    {
        publicEndpoint = null;
        if (options is null) throw new HostingException(HostingError.MissingConfiguration);
        if (options.Port is < 1 or > 65535)
            throw new HostingException(HostingError.InvalidConfiguration);

        if (options.LocalOnly)
        {
            publicEndpoint = new Uri($"ws://127.0.0.1:{options.Port}/", UriKind.Absolute);
            return;
        }

        // 兩個欄位都省略＝tunnel 由外部管理（例如同機已有常駐的 cloudflared 服務）：
        // 插件不自己拉，只驗證公開位址真的接到自己這個 relay。同一條 tunnel 不能有兩個
        // connector，硬拉第二個會讓開房卡在等待（2026-09-15 實機）。只填一個＝設定不完整。
        if (string.IsNullOrWhiteSpace(options.PublicEndpoint))
            throw new HostingException(HostingError.MissingConfiguration);
        var hasExecutable = !string.IsNullOrWhiteSpace(options.CloudflaredExecutable);
        var hasConfig = !string.IsNullOrWhiteSpace(options.TunnelConfigPath);
        if (hasExecutable != hasConfig)
            throw new HostingException(HostingError.MissingConfiguration);
        try
        {
            if (!RoomInvitation.TryEndpoint(options.PublicEndpoint, localOnly: false, out publicEndpoint) ||
                publicEndpoint is null ||
                !string.Equals(publicEndpoint.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
                throw new HostingException(HostingError.InvalidConfiguration);
            if (hasExecutable)
            {
                RequireExistingPath(options.CloudflaredExecutable!);
                RequireExistingPath(options.TunnelConfigPath!);
            }
        }
        catch (HostingException)
        {
            throw;
        }
        catch
        {
            throw new HostingException(HostingError.InvalidConfiguration);
        }
    }

    private static void RequireExistingPath(string path)
    {
        if (ContainsControl(path)) throw new ArgumentException();
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new ArgumentException();
    }

    private static bool ContainsControl(string value)
    {
        foreach (var character in value)
            if (char.IsControl(character)) return true;
        return false;
    }

    private static HostingException MapListenerFailure(HttpListenerException exception)
        => exception.ErrorCode switch
        {
            5 => new HostingException(HostingError.PermissionDenied),
            32 or 183 or 98 or 10048 => new HostingException(HostingError.PortInUse),
            _ => new HostingException(HostingError.TunnelStartFailed),
        };

    private static async Task ProbePublicEndpointAsync(
        Uri endpoint,
        string hostAuthorization,
        string instanceId,
        RelayServer relay,
        HostedTunnelProcess? tunnel,
        CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            MaxResponseHeadersLength = 64,
        };
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = ProbeBodyLimit + 1,
        };
        var started = Stopwatch.GetTimestamp();
        var timeout = TimeSpan.FromSeconds(StartupSeconds);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // tunnel 為 null＝外部管理，只要 relay 活著就繼續探。
            if (!relay.IsRunning || tunnel is { IsRunning: false })
                throw new HostingException(HostingError.TunnelStartFailed);

            var ready = await ProbeReadyAsync(client, endpoint, hostAuthorization, instanceId, cancellationToken)
                .ConfigureAwait(false);
            if (ready && await ProbeWebSocketAsync(endpoint, hostAuthorization, cancellationToken).ConfigureAwait(false))
                return;

            var elapsed = Stopwatch.GetElapsedTime(started);
            var remaining = timeout - elapsed;
            if (remaining <= TimeSpan.Zero) break;
            var delay = remaining < TimeSpan.FromMilliseconds(250)
                ? remaining : TimeSpan.FromMilliseconds(250);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        throw new HostingException(HostingError.PublicEndpointUnavailable);
    }

    private static async Task<bool> ProbeReadyAsync(
        HttpClient client,
        Uri endpoint,
        string hostAuthorization,
        string instanceId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildHttpEndpoint(endpoint, "/ready"));
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + hostAuthorization);
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK ||
                response.Content.Headers.ContentLength is > ProbeBodyLimit)
                return false;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var bytes = new byte[ProbeBodyLimit + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            if (count > ProbeBodyLimit) return false;
            var actual = Encoding.UTF8.GetString(bytes, 0, count);
            return string.Equals(actual, instanceId, StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> ProbeWebSocketAsync(
        Uri endpoint, string hostAuthorization, CancellationToken cancellationToken)
    {
        try
        {
            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
            socket.Options.SetRequestHeader("Authorization", "Bearer " + hostAuthorization);
            // The existing one-click ingress allows /ready. Its WebSocket form authorizes
            // the same host grant, returns a real 101, then closes without creating a room.
            await socket.ConnectAsync(BuildWebSocketEndpoint(endpoint, "/ready"), cancellationToken)
                .ConfigureAwait(false);
            // ConnectAsync completing is the 101 proof; a later close/reset is not used as proof.
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static Uri BuildHttpEndpoint(Uri endpoint, string path)
    {
        var builder = new UriBuilder(endpoint)
        {
            Scheme = "https",
            Path = path,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    private static Uri BuildWebSocketEndpoint(Uri endpoint, string path)
    {
        var builder = new UriBuilder(endpoint)
        {
            Path = path,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }
}
