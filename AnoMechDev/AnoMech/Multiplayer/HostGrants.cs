using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AnoMech.Multiplayer;

/// <summary>
/// Fixed bounds for the host authorization allowlist. Every consumer (relay admission, the
/// standalone management store and the embedded owned-host grant) shares these limits so a
/// document that one component accepts can never be larger than another component tolerates.
/// </summary>
public static class HostGrantLimits
{
    /// <summary>Grant ids are 128-bit, rendered as lowercase hex.</summary>
    public const int GrantIdLength = 32;

    /// <summary>Host and join secrets are 256-bit, rendered as uppercase hex.</summary>
    public const int SecretLength = 64;

    /// <summary>SHA-256 of the secret, rendered as lowercase hex.</summary>
    public const int SecretHashLength = 64;

    public const int MaxLabelLength = 64;
    public const int MaxGrants = 128;
    public const int MaxDocumentBytes = 64 * 1024;
    public const int MaxRoomsPerGrant = MpLimits.Rooms;

    /// <summary>Relay refresh cadence; every source is read on this period.</summary>
    public const double RefreshIntervalSeconds = 1;

    /// <summary>Upper bound for one <see cref="IHostGrantSource.ReadAsync"/> call.</summary>
    public const int ReadTimeoutMilliseconds = 500;

    /// <summary>
    /// A published snapshot may authorize for this long after the read that produced it
    /// succeeded. Measured with a monotonic clock, never with wall time or object identity.
    /// </summary>
    public const double SnapshotFreshnessSeconds = 2;

    public const string SchemaVersion = "anomech-host-grants/1";
}

/// <summary>Format rules for grant ids, secrets and bearer values. No lenient parsing.</summary>
public static class HostGrantFormat
{
    private const string LowerHex = "0123456789abcdef";
    private const string UpperHex = "0123456789ABCDEF";

    public static bool IsGrantId(string? value) => IsHex(value, HostGrantLimits.GrantIdLength, upper: false);

    public static bool IsSecret(string? value) => IsHex(value, HostGrantLimits.SecretLength, upper: true);

    public static bool IsSecretHash(string? value) => IsHex(value, HostGrantLimits.SecretHashLength, upper: false);

    public static bool IsLabel(string? value)
    {
        if (value is null || value.Length == 0 || value.Length > HostGrantLimits.MaxLabelLength) return false;
        foreach (var character in value)
        {
            if (char.IsControl(character) || char.IsSurrogate(character)) return false;
        }
        return value.Trim().Length == value.Length;
    }

    public static bool IsMaxRooms(int value) => value >= 1 && value <= HostGrantLimits.MaxRoomsPerGrant;

    public static string NewGrantId() => Render(RandomNumberGenerator.GetBytes(HostGrantLimits.GrantIdLength / 2), LowerHex);

    public static string NewSecret() => Render(RandomNumberGenerator.GetBytes(HostGrantLimits.SecretLength / 2), UpperHex);

    /// <summary>Lowercase hex SHA-256 of the UTF-8 secret. Raw secrets are never persisted.</summary>
    public static string HashSecret(string secret)
    {
        if (!IsSecret(secret)) throw new ArgumentException("Host secrets are 64 uppercase hex characters.", nameof(secret));
        return Render(SHA256.HashData(Encoding.UTF8.GetBytes(secret)), LowerHex);
    }

    /// <summary>
    /// Splits a host bearer value. Exactly one separator dot is accepted; both halves must match
    /// their fixed shape, so an attacker cannot smuggle a second dot or a differently sized field.
    /// </summary>
    public static bool TryParseAuthorization(string? token, out string grantId, out string secret)
    {
        grantId = string.Empty;
        secret = string.Empty;
        if (token is null ||
            token.Length != HostGrantLimits.GrantIdLength + 1 + HostGrantLimits.SecretLength)
            return false;
        var separator = token.IndexOf('.');
        if (separator != HostGrantLimits.GrantIdLength ||
            token.LastIndexOf('.') != separator)
            return false;
        var id = token[..separator];
        var value = token[(separator + 1)..];
        if (!IsGrantId(id) || !IsSecret(value)) return false;
        grantId = id;
        secret = value;
        return true;
    }

    private static bool IsHex(string? value, int length, bool upper)
    {
        if (value is null || value.Length != length) return false;
        var alphabet = upper ? UpperHex : LowerHex;
        foreach (var character in value)
        {
            if (alphabet.IndexOf(character) < 0) return false;
        }
        return true;
    }

    private static string Render(byte[] bytes, string alphabet)
    {
        var result = new char[bytes.Length * 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            result[i * 2] = alphabet[bytes[i] >> 4];
            result[i * 2 + 1] = alphabet[bytes[i] & 0x0F];
        }
        return new string(result);
    }
}

/// <summary>
/// One authorized host. The raw secret is never held: the grant stores only its SHA-256 and
/// compares in fixed time after the caller has located the grant by id.
/// </summary>
public sealed class HostGrant
{
    private readonly byte[] secretHash;

    public HostGrant(string grantId, string secretSha256, string label, bool enabled, int maxRooms)
    {
        if (!HostGrantFormat.IsGrantId(grantId))
            throw new ArgumentException("Grant ids are 32 lowercase hex characters.", nameof(grantId));
        if (!HostGrantFormat.IsSecretHash(secretSha256))
            throw new ArgumentException("Grant secret hashes are 64 lowercase hex characters.", nameof(secretSha256));
        if (!HostGrantFormat.IsLabel(label))
            throw new ArgumentException("Grant labels are 1-64 printable characters.", nameof(label));
        if (!HostGrantFormat.IsMaxRooms(maxRooms))
            throw new ArgumentOutOfRangeException(nameof(maxRooms));
        GrantId = grantId;
        SecretSha256 = secretSha256;
        Label = label;
        Enabled = enabled;
        MaxRooms = maxRooms;
        secretHash = Convert.FromHexString(secretSha256);
    }

    public string GrantId { get; }
    public string SecretSha256 { get; }
    public string Label { get; }
    public bool Enabled { get; }
    public int MaxRooms { get; }

    public bool MatchesSecret(string? secret)
    {
        if (!HostGrantFormat.IsSecret(secret)) return false;
        Span<byte> supplied = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(secret!), supplied);
        return CryptographicOperations.FixedTimeEquals(supplied, secretHash);
    }

    public override string ToString()
        => $"HostGrant {{ GrantId = {GrantId}, Label = {Label}, Enabled = {Enabled}, MaxRooms = {MaxRooms} }}";
}

/// <summary>
/// An immutable, bounded allowlist. Construction rejects duplicates and oversized documents so a
/// malformed source cannot publish an ambiguous authorization state.
/// </summary>
public sealed class HostGrantSnapshot
{
    private readonly Dictionary<string, HostGrant> byId;

    public static HostGrantSnapshot Empty { get; } = new(Array.Empty<HostGrant>());

    public HostGrantSnapshot(IReadOnlyList<HostGrant> grants)
    {
        if (grants is null) throw new ArgumentNullException(nameof(grants));
        if (grants.Count > HostGrantLimits.MaxGrants)
            throw new ArgumentException("Too many host grants.", nameof(grants));
        byId = new Dictionary<string, HostGrant>(grants.Count, StringComparer.Ordinal);
        var ordered = new HostGrant[grants.Count];
        for (var i = 0; i < grants.Count; i++)
        {
            var grant = grants[i] ?? throw new ArgumentException("Host grants cannot be null.", nameof(grants));
            if (!byId.TryAdd(grant.GrantId, grant))
                throw new ArgumentException("Duplicate host grant id.", nameof(grants));
            ordered[i] = grant;
        }
        Grants = new ReadOnlyCollection<HostGrant>(ordered);
    }

    public IReadOnlyList<HostGrant> Grants { get; }

    public int Count => byId.Count;

    /// <summary>Locates a grant by exact id. Secrets are never scanned across entries.</summary>
    public HostGrant? Find(string? grantId)
        => grantId is not null && byId.TryGetValue(grantId, out var grant) ? grant : null;

    public override string ToString() => $"HostGrantSnapshot {{ Count = {Count} }}";
}

/// <summary>
/// The single authorization seam. The relay polls every source on a fixed period and treats the
/// completion time of each successful read as the freshness origin; a source never pushes.
/// </summary>
public interface IHostGrantSource
{
    ValueTask<HostGrantSnapshot> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Fixed in-process allowlist. Used by the embedded owned host for its single ephemeral grant;
/// it reads no files and holds no management authority.
/// </summary>
public sealed class InMemoryHostGrantSource : IHostGrantSource
{
    private readonly HostGrantSnapshot snapshot;

    public InMemoryHostGrantSource(HostGrantSnapshot snapshot)
        => this.snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

    /// <summary>Builds a one-grant source from a raw secret; only the hash is retained.</summary>
    public static InMemoryHostGrantSource ForSecret(string grantId, string secret, string label, int maxRooms = 1)
        => new(new HostGrantSnapshot(new[]
        {
            new HostGrant(grantId, HostGrantFormat.HashSecret(secret), label, enabled: true, maxRooms),
        }));

    public ValueTask<HostGrantSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<HostGrantSnapshot>(snapshot);
    }
}
