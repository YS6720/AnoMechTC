using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AnoMech.Multiplayer;

namespace AnoMech.Relay;

/// <summary>A management or parsing failure that is safe to report to the operator.</summary>
public sealed class HostGrantStoreException : Exception
{
    public HostGrantStoreException(string message) : base(message) { }
}

/// <summary>
/// Strict serializer for the persisted allowlist. Unknown schema, unknown or duplicated
/// properties, duplicate grant ids and oversized documents are rejected outright: a document the
/// relay cannot fully understand must never be interpreted as a partial permission set.
/// </summary>
public static class HostGrantDocument
{
    private static readonly string[] RootProperties = { "schema", "grants" };
    private static readonly string[] GrantProperties = { "id", "secretSha256", "label", "enabled", "maxRooms" };
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static bool TryParse(ReadOnlySpan<byte> utf8, out HostGrantSnapshot snapshot)
    {
        snapshot = HostGrantSnapshot.Empty;
        if (utf8.Length == 0 || utf8.Length > HostGrantLimits.MaxDocumentBytes) return false;
        try
        {
            _ = StrictUtf8.GetCharCount(utf8);
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
            {
                MaxDepth = 8,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
            using var document = JsonDocument.ParseValue(ref reader);
            if (reader.Read()) return false;

            var root = document.RootElement;
            if (!IsExactObject(root, RootProperties)) return false;
            if (root.GetProperty("schema") is not { ValueKind: JsonValueKind.String } schema ||
                !string.Equals(schema.GetString(), HostGrantLimits.SchemaVersion, StringComparison.Ordinal))
                return false;
            if (root.GetProperty("grants") is not { ValueKind: JsonValueKind.Array } array ||
                array.GetArrayLength() > HostGrantLimits.MaxGrants)
                return false;

            var grants = new List<HostGrant>(array.GetArrayLength());
            foreach (var item in array.EnumerateArray())
            {
                if (!IsExactObject(item, GrantProperties)) return false;
                if (item.GetProperty("id") is not { ValueKind: JsonValueKind.String } id ||
                    item.GetProperty("secretSha256") is not { ValueKind: JsonValueKind.String } hash ||
                    item.GetProperty("label") is not { ValueKind: JsonValueKind.String } label ||
                    item.GetProperty("maxRooms") is not { ValueKind: JsonValueKind.Number } maxRooms ||
                    !maxRooms.TryGetInt32(out var rooms))
                    return false;
                var enabled = item.GetProperty("enabled");
                if (enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                grants.Add(new HostGrant(id.GetString()!, hash.GetString()!, label.GetString()!,
                    enabled.GetBoolean(), rooms));
            }
            snapshot = new HostGrantSnapshot(grants);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException or
                                              KeyNotFoundException or InvalidOperationException)
        {
            snapshot = HostGrantSnapshot.Empty;
            return false;
        }
    }

    private static bool IsExactObject(JsonElement element, string[] properties)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var seen = 0;
        foreach (var property in element.EnumerateObject())
        {
            var index = 0;
            while (index < properties.Length && !property.NameEquals(properties[index])) index++;
            if (index == properties.Length || (seen & (1 << index)) != 0) return false;
            seen |= 1 << index;
        }
        return seen == (1 << properties.Length) - 1;
    }

    public static byte[] Serialize(IReadOnlyList<HostGrant> grants)
    {
        if (grants.Count > HostGrantLimits.MaxGrants)
            throw new HostGrantStoreException("The allowlist would exceed the supported grant count.");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", HostGrantLimits.SchemaVersion);
            writer.WriteStartArray("grants");
            foreach (var grant in grants)
            {
                writer.WriteStartObject();
                writer.WriteString("id", grant.GrantId);
                writer.WriteString("secretSha256", grant.SecretSha256);
                writer.WriteString("label", grant.Label);
                writer.WriteBoolean("enabled", grant.Enabled);
                writer.WriteNumber("maxRooms", grant.MaxRooms);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        var bytes = stream.ToArray();
        if (bytes.Length > HostGrantLimits.MaxDocumentBytes)
            throw new HostGrantStoreException("The allowlist would exceed the supported document size.");
        return bytes;
    }
}

/// <summary>
/// Reads the persisted allowlist for the relay refresh loop. A missing, oversized or malformed
/// document raises: the relay then publishes a deny-all snapshot instead of reusing older
/// permissions. This type is compiled only into the standalone relay.
/// </summary>
public sealed class FileHostGrantSource : IHostGrantSource
{
    public FileHostGrantSource(string path) => Path = HostGrantStore.NormalizePath(path);

    public string Path { get; }

    public async ValueTask<HostGrantSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var bytes = await HostGrantStore.ReadBoundedAsync(Path, cancellationToken).ConfigureAwait(false);
        if (!HostGrantDocument.TryParse(bytes, out var snapshot))
            throw new HostGrantStoreException("The host allowlist is malformed or uses an unknown schema.");
        return snapshot;
    }
}

/// <summary>
/// Local management of the persisted allowlist. Every read-modify-write runs under one
/// cross-process exclusive lock keyed on the normalized full path, re-reads the document after
/// taking the lock and commits with an atomic replace, so a revocation can never be resurrected
/// by a copy that was read before the lock.
/// </summary>
public static class HostGrantStore
{
    private const int LockWaitMilliseconds = 5000;
    private const int LockRetryMilliseconds = 25;

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new HostGrantStoreException("A file path is required.");
        foreach (var character in path)
            if (char.IsControl(character))
                throw new HostGrantStoreException("The file path contains control characters.");
        try
        {
            return System.IO.Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new HostGrantStoreException("The file path is not usable.");
        }
    }

    internal static async Task<byte[]> ReadBoundedAsync(string fullPath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        if (stream.Length > HostGrantLimits.MaxDocumentBytes)
            throw new HostGrantStoreException("The host allowlist exceeds the supported document size.");
        var bytes = new byte[stream.Length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            offset += read;
        }
        if (offset != bytes.Length ||
            await stream.ReadAsync(bytes.AsMemory(0, Math.Min(1, bytes.Length)), cancellationToken).ConfigureAwait(false) != 0 ||
            stream.Length != bytes.Length)
            throw new HostGrantStoreException("The host allowlist changed or could not be read completely.");
        return bytes;
    }

    /// <summary>Creates an empty allowlist. An existing file is never overwritten.</summary>
    public static void Initialize(string path)
    {
        var fullPath = NormalizePath(path);
        RequireDirectory(fullPath);
        using var guard = AcquireLock(fullPath);
        if (File.Exists(fullPath))
            throw new HostGrantStoreException("That allowlist already exists; refusing to overwrite it.");
        WriteAtomic(fullPath, HostGrantDocument.Serialize(Array.Empty<HostGrant>()));
    }

    /// <summary>
    /// Issues one grant. <paramref name="writeCredential"/> runs inside the lock and before the
    /// allowlist is committed: if it fails, the previous allowlist stays exactly as it was.
    /// </summary>
    public static HostGrant Issue(string path, string label, int maxRooms, Action<HostGrant, string> writeCredential)
    {
        if (!HostGrantFormat.IsLabel(label))
            throw new HostGrantStoreException("Labels are 1-64 printable characters without leading or trailing space.");
        if (!HostGrantFormat.IsMaxRooms(maxRooms))
            throw new HostGrantStoreException($"Concurrent rooms must be between 1 and {HostGrantLimits.MaxRoomsPerGrant}.");
        var fullPath = NormalizePath(path);
        using var guard = AcquireLock(fullPath);
        var current = ReadForUpdate(fullPath);
        if (current.Count >= HostGrantLimits.MaxGrants)
            throw new HostGrantStoreException("The allowlist already holds the maximum number of grants.");

        string grantId;
        do { grantId = HostGrantFormat.NewGrantId(); }
        while (current.Find(grantId) is not null);
        var secret = HostGrantFormat.NewSecret();
        var grant = new HostGrant(grantId, HostGrantFormat.HashSecret(secret), label, enabled: true, maxRooms);

        writeCredential(grant, secret);
        var updated = new List<HostGrant>(current.Grants) { grant };
        WriteAtomic(fullPath, HostGrantDocument.Serialize(updated));
        return grant;
    }

    public static IReadOnlyList<HostGrant> List(string path)
    {
        var fullPath = NormalizePath(path);
        using var guard = AcquireLock(fullPath);
        return ReadForUpdate(fullPath).Grants;
    }

    /// <summary>Removes one grant. Returns false when the id is absent; nothing is written then.</summary>
    public static bool Revoke(string path, string grantId)
    {
        if (!HostGrantFormat.IsGrantId(grantId))
            throw new HostGrantStoreException("Grant ids are 32 lowercase hex characters.");
        var fullPath = NormalizePath(path);
        using var guard = AcquireLock(fullPath);
        var current = ReadForUpdate(fullPath);
        if (current.Find(grantId) is null) return false;
        var remaining = new List<HostGrant>(current.Count - 1);
        foreach (var grant in current.Grants)
            if (!string.Equals(grant.GrantId, grantId, StringComparison.Ordinal))
                remaining.Add(grant);
        WriteAtomic(fullPath, HostGrantDocument.Serialize(remaining));
        return true;
    }

    private static HostGrantSnapshot ReadForUpdate(string fullPath)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(fullPath);
        }
        catch (FileNotFoundException)
        {
            throw new HostGrantStoreException("That allowlist does not exist; run `grants init` first.");
        }
        catch (DirectoryNotFoundException)
        {
            throw new HostGrantStoreException("That allowlist does not exist; run `grants init` first.");
        }
        if (bytes.Length > HostGrantLimits.MaxDocumentBytes)
            throw new HostGrantStoreException("The allowlist exceeds the supported document size.");
        if (!HostGrantDocument.TryParse(bytes, out var snapshot))
            throw new HostGrantStoreException("The allowlist is malformed or uses an unknown schema; it was not modified.");
        return snapshot;
    }

    private static void RequireDirectory(string fullPath)
    {
        var directory = System.IO.Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            throw new HostGrantStoreException("The target directory does not exist.");
    }

    /// <summary>
    /// Bounded cross-process exclusive lock. The lock file is keyed on the same normalized path
    /// every writer uses; the OS releases it if a process exits while holding it.
    /// </summary>
    private static FileStream AcquireLock(string fullPath)
    {
        RequireDirectory(fullPath);
        var lockPath = fullPath + ".lock";
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.None);
            }
            catch (IOException)
            {
                if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= LockWaitMilliseconds)
                    throw new HostGrantStoreException("Another management command is holding the allowlist lock.");
                Thread.Sleep(LockRetryMilliseconds);
            }
            catch (UnauthorizedAccessException)
            {
                throw new HostGrantStoreException("The allowlist lock file cannot be opened.");
            }
        }
    }

    private static void WriteAtomic(string fullPath, byte[] bytes)
    {
        var directory = System.IO.Path.GetDirectoryName(fullPath)!;
        var temporary = System.IO.Path.Combine(directory,
            "." + System.IO.Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null, ignoreMetadataErrors: true);
            else File.Move(temporary, fullPath);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
        }
    }
}
