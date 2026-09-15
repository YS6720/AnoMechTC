using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AnoMech.Multiplayer;

namespace AnoMech.Relay;

internal static class Program
{
    private const int ReadTimeoutMilliseconds = HostGrantLimits.ReadTimeoutMilliseconds;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }
        if (HasFlag(args, "--help") || HasFlag(args, "-h"))
        {
            PrintUsage();
            return 0;
        }
        if (HasFlag(args, "--token") || HasFlag(args, "--host-token"))
        {
            PrintMigration();
            return 2;
        }

        return args[0] switch
        {
            "serve" => await ServeAsync(args).ConfigureAwait(false),
            "grants" => RunGrants(args),
            _ => RejectUsage("Unknown command."),
        };
    }

    private static async Task<int> ServeAsync(string[] args)
    {
        var bind = IPAddress.Loopback;
        var port = 27890;
        var maxRooms = 4;
        string? grantsFile = null;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--bind":
                    if (!Take(args, ref i, out var bindText) || !IPAddress.TryParse(bindText, out var parsedBind))
                        return RejectUsage("--bind requires a literal IP address.");
                    bind = parsedBind;
                    break;
                case "--port":
                case "-p":
                    if (!Take(args, ref i, out var portText) || !int.TryParse(portText, out port))
                        return RejectUsage("--port requires an integer.");
                    break;
                case "--max-rooms":
                    if (!Take(args, ref i, out var roomsText) || !int.TryParse(roomsText, out maxRooms))
                        return RejectUsage("--max-rooms requires an integer.");
                    break;
                case "--grants-file":
                    if (!Take(args, ref i, out grantsFile))
                        return RejectUsage("--grants-file requires a path.");
                    break;
                default:
                    return RejectUsage("Unknown relay option.");
            }
        }

        if (string.IsNullOrEmpty(grantsFile))
            return RejectUsage("--grants-file is required; hosting is authorized per person.");

        FileHostGrantSource source;
        try
        {
            source = new FileHostGrantSource(grantsFile);
            using var probe = new CancellationTokenSource(ReadTimeoutMilliseconds);
            var initial = await source.ReadAsync(probe.Token).ConfigureAwait(false);
            Console.WriteLine($"Loaded {initial.Count} host grant(s) from {source.Path}.");
        }
        catch (HostGrantStoreException failure)
        {
            Console.Error.WriteLine(failure.Message);
            return 2;
        }
        catch (IOException)
        {
            Console.Error.WriteLine("The host allowlist could not be read.");
            return 2;
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine("The host allowlist could not be read.");
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Reading the host allowlist timed out.");
            return 2;
        }

        var options = new RelayServerOptions
        {
            BindAddress = bind,
            Port = port,
            MaxRooms = maxRooms,
            HostGrantSource = source,
            // 只有獨立主機印；插件內嵌 relay 不傳這個委派，維持完全沉默。
            // 時間戳在這裡加，RelayServer 只負責內容。
            ActivityLog = line => Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {line}"),
        };

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.Cancel();
        };

        try
        {
            // 不在這裡報 listening：此時還沒 bind。成功訊息由 RelayServer 綁定後透過
            // ActivityLog 發出，失敗則落到下面的 catch。
            await using var relay = new RelayServer(options);
            await relay.RunAsync(stop.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return 0;
        }
        catch (HttpListenerException)
        {
            Console.Error.WriteLine("Relay could not bind the configured listener.");
            return 1;
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine("Relay configuration is invalid.");
            return 2;
        }
        catch
        {
            Console.Error.WriteLine("Relay stopped because of an unexpected transport failure.");
            return 1;
        }
    }

    private static int RunGrants(string[] args)
    {
        if (args.Length < 2) return RejectUsage("A grants subcommand is required.");
        try
        {
            return args[1] switch
            {
                "init" => GrantsInit(args),
                "issue" => GrantsIssue(args),
                "list" => GrantsList(args),
                "revoke" => GrantsRevoke(args),
                _ => RejectUsage("Unknown grants subcommand."),
            };
        }
        catch (HostGrantStoreException failure)
        {
            Console.Error.WriteLine(failure.Message);
            return 1;
        }
        catch (InvalidOperationException)
        {
            Console.Error.WriteLine("The credential could not be generated for that endpoint; nothing was changed.");
            return 1;
        }
        catch (IOException)
        {
            Console.Error.WriteLine("The allowlist could not be updated; the previous file is unchanged.");
            return 1;
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine("The allowlist could not be updated; the previous file is unchanged.");
            return 1;
        }
    }

    private static int GrantsInit(string[] args)
    {
        if (!TryOptions(args, new[] { "--file" }, Array.Empty<string>(), out var values)) return 2;
        HostGrantStore.Initialize(values["--file"]);
        Console.WriteLine($"Initialized an empty host allowlist at {HostGrantStore.NormalizePath(values["--file"])}.");
        return 0;
    }

    private static int GrantsIssue(string[] args)
    {
        if (!TryOptions(args, new[] { "--file", "--endpoint", "--label", "--out" }, new[] { "--max-rooms" }, out var values))
            return 2;
        if (!TryEndpoint(values["--endpoint"], out var endpoint))
            return RejectUsage("--endpoint must be a wss:// URI, or ws:// on literal loopback.");
        var maxRooms = 1;
        if (values.TryGetValue("--max-rooms", out var roomsText) &&
            (!int.TryParse(roomsText, out maxRooms) || !HostGrantFormat.IsMaxRooms(maxRooms)))
            return RejectUsage($"--max-rooms must be between 1 and {HostGrantLimits.MaxRoomsPerGrant}.");

        var outPath = HostGrantStore.NormalizePath(values["--out"]);
        string? written = null;
        HostGrant grant;
        try
        {
            // The credential file is created before the allowlist is committed. A refused
            // destination therefore leaves the existing allowlist untouched.
            grant = HostGrantStore.Issue(values["--file"], values["--label"], maxRooms, (issued, secret) =>
            {
                var invitation = new HostInvitation(endpoint!, issued.GrantId, secret);
                var encoded = invitation.Encode();
                if (!HostInvitation.TryParse(encoded, out var parsed) || parsed is null)
                    throw new HostGrantStoreException("The generated credential failed its own validation.");
                using var stream = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                written = outPath;
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(encoded);
            });
        }
        catch
        {
            // Only a failure before the commit rolls back. Deleting the credential after the
            // grant is in the allowlist would destroy the only copy of a live secret.
            if (written is not null)
            {
                try { File.Delete(written); } catch { }
            }
            throw;
        }

        // 已提交：授權在允許清單裡，憑證檔是它唯一的副本。此後的輸出失敗只是報告失敗，
        // 不得刪檔，也不得讓 RunGrants 的 IOException 分支把它謊報成「previous file is unchanged」。
        try
        {
            Console.WriteLine($"Issued grant {grant.GrantId} for \"{grant.Label}\"; up to {grant.MaxRooms} concurrent room(s).");
            Console.WriteLine($"Credential written to {outPath}. It contains the only copy of the secret; deliver it privately.");
        }
        catch (IOException)
        {
            try
            {
                Console.Error.WriteLine(
                    $"Issued grant {grant.GrantId}; the credential is at {outPath}. Reporting to standard output failed.");
            }
            catch (IOException)
            {
                // 兩條輸出都壞掉時無處可報。授權仍然成立，退出碼必須維持成功。
            }
        }
        return 0;
    }

    private static int GrantsList(string[] args)
    {
        if (!TryOptions(args, new[] { "--file" }, Array.Empty<string>(), out var values)) return 2;
        var grants = HostGrantStore.List(values["--file"]);
        Console.WriteLine($"{grants.Count} host grant(s) in {HostGrantStore.NormalizePath(values["--file"])}:");
        foreach (var grant in grants)
            Console.WriteLine($"  {grant.GrantId}  {(grant.Enabled ? "enabled " : "disabled")}  rooms={grant.MaxRooms}  {grant.Label}");
        return 0;
    }

    private static int GrantsRevoke(string[] args)
    {
        if (!TryOptions(args, new[] { "--file", "--id", "--confirm-id" }, Array.Empty<string>(), out var values))
            return 2;
        if (!string.Equals(values["--id"], values["--confirm-id"], StringComparison.Ordinal))
            return RejectUsage("--confirm-id must repeat --id exactly.");
        if (!HostGrantStore.Revoke(values["--file"], values["--id"]))
        {
            Console.Error.WriteLine("No grant with that id; nothing was changed.");
            return 1;
        }
        Console.WriteLine($"Revoked grant {values["--id"]}.");
        Console.WriteLine("That host can no longer create rooms, and its live rooms close within 2 seconds. Other grants are unaffected.");
        return 0;
    }

    private static bool TryOptions(string[] args, string[] required, string[] optional,
        out Dictionary<string, string> values)
    {
        values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 2; i < args.Length; i++)
        {
            var name = args[i];
            if (Array.IndexOf(required, name) < 0 && Array.IndexOf(optional, name) < 0)
            {
                RejectUsage("Unknown option for this command.");
                return false;
            }
            if (values.ContainsKey(name))
            {
                RejectUsage("An option was supplied twice.");
                return false;
            }
            if (!Take(args, ref i, out var value))
            {
                RejectUsage($"{name} requires a value.");
                return false;
            }
            values.Add(name, value);
        }
        foreach (var name in required)
        {
            if (!values.ContainsKey(name))
            {
                RejectUsage($"{name} is required.");
                return false;
            }
        }
        return true;
    }

    private static bool TryEndpoint(string text, out Uri? endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)) return false;
        // ws is accepted only for literal loopback; RoomInvitation.TryEndpoint owns that rule and
        // the SSRF/TLS boundary, and this CLI does not widen it.
        var localOnly = string.Equals(parsed.Scheme, "ws", StringComparison.OrdinalIgnoreCase);
        return RoomInvitation.TryEndpoint(text, localOnly, out endpoint) && endpoint is not null;
    }

    private static bool Take(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            return false;
        }
        value = args[++index];
        return value.Length != 0;
    }

    private static bool HasFlag(string[] args, string flag)
    {
        foreach (var arg in args)
            if (arg == flag) return true;
        return false;
    }

    private static string FormatAddress(IPAddress address)
        => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{address}]" : address.ToString();

    private static int RejectUsage(string message)
    {
        Console.Error.WriteLine(message);
        PrintUsage();
        return 2;
    }

    private static void PrintMigration()
    {
        Console.Error.WriteLine("--token and --host-token no longer exist: there is no relay-wide host or join token.");
        Console.Error.WriteLine("Authorize each host instead:");
        Console.Error.WriteLine("  AnoMech.TcRelay grants init --file <allowlist.json>");
        Console.Error.WriteLine("  AnoMech.TcRelay grants issue --file <allowlist.json> --endpoint wss://<host>/ --label <name> --out <credential.txt>");
        Console.Error.WriteLine("  AnoMech.TcRelay serve --grants-file <allowlist.json>");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("AnoMech.TcRelay serve --grants-file <path> [--bind <literal-ip>] [--port <port>] [--max-rooms <count>]");
        Console.WriteLine("AnoMech.TcRelay grants init --file <path>");
        Console.WriteLine("AnoMech.TcRelay grants issue --file <path> --endpoint <uri> --label <label> --out <path> [--max-rooms <count>]");
        Console.WriteLine("AnoMech.TcRelay grants list --file <path>");
        Console.WriteLine("AnoMech.TcRelay grants revoke --file <path> --id <grant-id> --confirm-id <grant-id>");
        Console.WriteLine("Defaults: --bind 127.0.0.1 --port 27890 --max-rooms 4; issue defaults to 1 concurrent room.");
        Console.WriteLine("Secrets are written only to the --out credential file; they never appear in arguments or output.");
    }
}
