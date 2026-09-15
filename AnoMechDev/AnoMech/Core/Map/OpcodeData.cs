using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AnoMech.Core.Map;

internal static class OpcodeData
{
    internal const string HyperboreaCommit = "2538a9053b7ca04ad33880d8cf50d2b3d67c5cff";
    internal const int MaxGameVersionLength = 64;
    internal const int MaxBodyBytes = 64 * 1024;
    internal const int MaxZoneDownOpcodes = 2048;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool TryNormalizeGameVersion(string? candidate, out string version)
    {
        version = string.Empty;
        if (string.IsNullOrEmpty(candidate) || candidate.Length > MaxGameVersionLength)
            return false;

        var previousWasDot = true;
        for (var i = 0; i < candidate.Length; i++)
        {
            var character = candidate[i];
            if (character == '.')
            {
                if (previousWasDot) return false;
                previousWasDot = true;
                continue;
            }

            if (character is < '0' or > '9') return false;
            previousWasDot = false;
        }

        if (previousWasDot) return false;
        version = candidate;
        return true;
    }

    internal static string CacheKey(string gameVersion)
        => gameVersion + "@" + HyperboreaCommit;

    internal static Uri DownloadUri(string gameVersion)
        => new($"https://raw.githubusercontent.com/kawaii/Hyperborea/{HyperboreaCommit}/opcodes/{gameVersion}.txt");

    internal static bool TryParse(ReadOnlySpan<byte> bytes, out ushort[] opcodes)
    {
        opcodes = Array.Empty<ushort>();
        if (bytes.Length > MaxBodyBytes)
            return false;
        try
        {
            var text = StrictUtf8.GetString(bytes);
            return TryParseText(text, out opcodes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    internal static bool TryParseText(string text, out ushort[] opcodes)
    {
        opcodes = Array.Empty<ushort>();
        if (text is null) return false;

        var result = new List<ushort>(Math.Min(32, MaxZoneDownOpcodes));
        var seen = new HashSet<ushort>();
        var foundZoneDown = false;
        var lines = text.Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.AsSpan();
            if (line.Length > 0 && line[^1] == '\r')
                line = line[..^1];
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (line.StartsWith("ZoneUp=".AsSpan(), StringComparison.Ordinal))
            {
                // The file may contain the opposite direction. It is valid input,
                // but none of its values can enter the receive allowlist.
                continue;
            }

            if (!line.StartsWith("ZoneDown=".AsSpan(), StringComparison.Ordinal))
                return false;
            if (foundZoneDown)
                return false;
            foundZoneDown = true;

            var values = line["ZoneDown=".Length..];
            if (values.Length == 0)
                return false;

            var start = 0;
            while (start <= values.Length)
            {
                var comma = values[start..].IndexOf(',');
                var end = comma < 0 ? values.Length : start + comma;
                var token = values[start..end];
                while (token.Length > 0 && (token[0] == ' ' || token[0] == '\t'))
                    token = token[1..];
                while (token.Length > 0 &&
                       (token[^1] == ' ' || token[^1] == '\t'))
                    token = token[..^1];
                if (!TryParseNonZeroUShort(token, out var opcode) || !seen.Add(opcode))
                    return false;
                if (result.Count == MaxZoneDownOpcodes)
                    return false;
                result.Add(opcode);
                if (comma < 0) break;
                start = end + 1;
            }
        }

        if (!foundZoneDown || result.Count == 0)
            return false;
        opcodes = result.ToArray();
        return true;
    }

    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = new byte[MaxBodyBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }

        if (count > MaxBodyBytes)
            throw new InvalidDataException($"Opcode response exceeds {MaxBodyBytes} bytes.");
        var result = new byte[count];
        Buffer.BlockCopy(bytes, 0, result, 0, count);
        return result;
    }

    internal static bool TryReadCache(
        string? storedKey, uint[]? storedOpcodes, string gameVersion, out ushort[] opcodes)
    {
        opcodes = Array.Empty<ushort>();
        if (!string.Equals(storedKey, CacheKey(gameVersion), StringComparison.Ordinal) ||
            storedOpcodes is null || storedOpcodes.Length == 0 ||
            storedOpcodes.Length > MaxZoneDownOpcodes)
            return false;

        var result = new ushort[storedOpcodes.Length];
        var seen = new HashSet<ushort>();
        for (var i = 0; i < storedOpcodes.Length; i++)
        {
            var value = storedOpcodes[i];
            if (value == 0 || value > ushort.MaxValue)
                return false;
            var opcode = (ushort)value;
            if (!seen.Add(opcode))
                return false;
            result[i] = opcode;
        }

        opcodes = result;
        return true;
    }

    internal static bool TryParseNonZeroUShort(ReadOnlySpan<char> token, out ushort value)
    {
        value = 0;
        if (token.Length == 0) return false;

        var parsed = 0;
        foreach (var character in token)
        {
            if (character is < '0' or > '9') return false;
            var digit = character - '0';
            if (parsed > (ushort.MaxValue - digit) / 10)
                return false;
            parsed = parsed * 10 + digit;
        }

        if (parsed == 0) return false;
        value = (ushort)parsed;
        return true;
    }
}

// Owns the cancellation token and the callback admission decision for one
// captured game-version session. Dispose never waits for the async download.
internal sealed class OpcodeUpdateOwner : IDisposable
{
    private readonly object commitGate = new();
    private readonly CancellationTokenSource cancellation = new();
    private int disposed;

    internal OpcodeUpdateOwner(string gameVersion)
    {
        GameVersion = gameVersion;
        Token = cancellation.Token;
    }

    internal string GameVersion { get; }
    internal CancellationToken Token { get; }
    internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

    // Dispose and framework commits share this gate. An admitted commit finishes
    // before Dispose returns; callbacks admitted afterwards cannot write config.
    internal bool TryCommit(string capturedGameVersion, Action commit)
    {
        lock (commitGate)
        {
            if (IsDisposed || !string.Equals(capturedGameVersion, GameVersion, StringComparison.Ordinal))
                return false;
            commit();
            return true;
        }
    }

    public void Dispose()
    {
        lock (commitGate)
        {
            if (IsDisposed) return;
            Volatile.Write(ref disposed, 1);
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }
}

internal static class OpcodeCacheCommit
{
    internal static bool TryPersistAndPublish(
        OpcodeAllowlist runtime,
        string cacheKey,
        ReadOnlySpan<ushort> opcodes,
        Func<string> readKey,
        Func<uint[]> readOpcodes,
        Action<string, uint[]> write,
        Action save,
        out Exception? saveError)
    {
        saveError = null;
        var oldKey = readKey();
        var oldOpcodes = readOpcodes();
        var persisted = new uint[opcodes.Length];
        for (var i = 0; i < opcodes.Length; i++)
            persisted[i] = opcodes[i];

        write(cacheKey, persisted);
        try
        {
            save();
        }
        catch (Exception error)
        {
            // SavePluginConfig's disk atomicity is an SDK concern; this restores
            // only the in-memory fields and leaves the runtime snapshot untouched.
            write(oldKey, oldOpcodes);
            saveError = error;
            return false;
        }

        runtime.Publish(opcodes);
        return true;
    }
}
