using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using ThreadingTask = System.Threading.Tasks.Task;

namespace AnoMech.Core.Map;

// Capture native version once on the framework thread. Async work never reads
// native pointers; the receive detour consumes only an immutable snapshot.
internal sealed class OpcodeUpdater : IDisposable
{
    private const int DownloadTimeoutSeconds = 10;

    private readonly OpcodeAllowlist runtime;
    private readonly Configuration config;
    private readonly OpcodeUpdateOwner? owner;

    internal OpcodeUpdater(OpcodeAllowlist runtime)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        config = Plugin.Config;

        string? gameVersion = null;
        try
        {
            gameVersion = CaptureGameVersion();
        }
        catch (Exception error)
        {
            Plugin.Log.Warning($"[OpcodeUpdater] Could not capture the native game version: {error.Message}");
        }

        if (!OpcodeData.TryNormalizeGameVersion(gameVersion, out var capturedVersion))
        {
            Plugin.Log.Warning(
                "[OpcodeUpdater] Native Framework was unavailable or returned an invalid game version; "
                + "opcode download skipped.");
            owner = null;
            return;
        }

        var sessionOwner = new OpcodeUpdateOwner(capturedVersion);
        owner = sessionOwner;
        var cacheKey = OpcodeData.CacheKey(capturedVersion);
        if (OpcodeData.TryReadCache(
                config.ZoneFirewallGameVersion,
                config.ZoneDownOpcodes,
                capturedVersion,
                out var cachedOpcodes))
        {
            runtime.Publish(cachedOpcodes);
            Plugin.Log.Information($"[OpcodeUpdater] Using cached ZoneDown opcodes ({runtime.Count} entries).");
            return;
        }

        Plugin.Log.Information(
            $"[OpcodeUpdater] No valid cache for game version {capturedVersion}; "
            + $"fetching pinned table ({OpcodeData.HyperboreaCommit}).");
        _ = DownloadAndQueueAsync(sessionOwner, capturedVersion, cacheKey);
    }

    private static unsafe string? CaptureGameVersion()
    {
        var framework = Framework.Instance();
        if (framework == null) return null;
        return new string(framework->GameVersionString);
    }

    private async ThreadingTask DownloadAndQueueAsync(
        OpcodeUpdateOwner sessionOwner, string capturedVersion, string cacheKey)
    {
        try
        {
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(DownloadTimeoutSeconds));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                sessionOwner.Token, timeout.Token);
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                MaxResponseHeadersLength = 64,
            };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(DownloadTimeoutSeconds),
            };
            using var request = new HttpRequestMessage(
                HttpMethod.Get, OpcodeData.DownloadUri(capturedVersion));
            using var response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidDataException($"Opcode endpoint returned HTTP {(int)response.StatusCode}.");

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellation.Token).ConfigureAwait(false);
            var body = await OpcodeData.ReadBoundedAsync(stream, cancellation.Token)
                .ConfigureAwait(false);
            if (!OpcodeData.TryParse(body, out var opcodes))
                throw new InvalidDataException("Opcode document failed validation.");

            if (sessionOwner.IsDisposed) return;
            await Plugin.Framework.Run(() =>
            {
                // This check runs on the framework callback itself. It prevents a
                // queued callback from an old updater writing a later session's cache.
                if (!sessionOwner.TryCommit(capturedVersion, () =>
                    {
                        if (!OpcodeCacheCommit.TryPersistAndPublish(
                                runtime,
                                cacheKey,
                                opcodes,
                                () => config.ZoneFirewallGameVersion,
                                () => config.ZoneDownOpcodes,
                                (key, values) =>
                                {
                                    config.ZoneFirewallGameVersion = key;
                                    config.ZoneDownOpcodes = values;
                                },
                                config.Save,
                                out var saveError))
                        {
                            Plugin.Log.Warning(
                                $"[OpcodeUpdater] Cache save failed; keeping previous snapshot: "
                                + $"{saveError?.Message}");
                            return;
                        }

                        Plugin.Log.Information(
                            $"[OpcodeUpdater] ZoneDown opcode snapshot updated ({runtime.Count} entries).");
                    }))
                {
                    Plugin.Log.Debug("[OpcodeUpdater] Ignored a stale or disposed update callback.");
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (sessionOwner.IsDisposed)
        {
            Plugin.Log.Debug("[OpcodeUpdater] Download cancelled during disposal.");
        }
        catch (OperationCanceledException)
        {
            Plugin.Log.Warning(
                $"[OpcodeUpdater] Download timed out after {DownloadTimeoutSeconds} seconds.");
        }
        catch (Exception error)
        {
            if (!sessionOwner.IsDisposed)
                Plugin.Log.Warning($"[OpcodeUpdater] Failed to fetch opcodes: {error.Message}");
        }
    }

    public void Dispose() => owner?.Dispose();
}
