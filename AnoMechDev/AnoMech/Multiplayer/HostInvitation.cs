using System;
using System.Text;

namespace AnoMech.Multiplayer;

/// <summary>
/// A bounded, versioned hosting credential: the shared relay endpoint plus one personal grant id
/// and its host secret. This is strictly more powerful than a <see cref="RoomInvitation"/> — it
/// creates rooms — so it uses a different magic, can never be parsed from a room invitation, and
/// must never be logged, echoed in the UI or copied to the clipboard by the plugin.
/// </summary>
public sealed record HostInvitation(Uri Endpoint, string GrantId, string Secret)
{
    public const int MaxTextLength = 512;

    private const string Magic = "ANOMECH-HOST";
    private const string Version = "1";
    private const int MaxEndpointTextLength = 256;
    private const int GrantIdLength = 32;
    private const int SecretLength = 64;
    private const string InvalidMessage = "Invalid host invitation.";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Bearer value for <c>RelayConnectionOptions.Token</c> on the host route.</summary>
    public string AuthorizationToken => GrantId + "." + Secret;

    /// <summary>
    /// True only for a well-formed <c>ws://</c> literal-loopback endpoint, i.e. same-machine
    /// synthetic use. Public endpoints are <c>wss</c>; the decision is derived from the endpoint
    /// itself so an encoded credential cannot claim a weaker transport than it actually uses.
    /// </summary>
    public bool LocalOnly => Endpoint is not null &&
        RoomInvitation.TryEndpoint(Endpoint.ToString(), true, out _);

    public string Encode()
    {
        if (!TryValidate(this, out var endpointText))
            throw new InvalidOperationException(InvalidMessage);

        var endpoint = Base64UrlEncode(StrictUtf8.GetBytes(endpointText));
        var encoded = string.Join("|", Magic, Version, endpoint, GrantId, Secret);
        if (encoded.Length > MaxTextLength)
            throw new InvalidOperationException(InvalidMessage);
        return encoded;
    }

    public static bool TryParse(string? text, out HostInvitation? invitation)
    {
        invitation = null;
        if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength)
            return false;

        var fields = text.Split('|');
        if (fields.Length != 5 || fields[0] != Magic || fields[1] != Version || fields[2].Length == 0)
            return false;

        if (!TryDecodeEndpoint(fields[2], out var endpoint) ||
            !IsGrantId(fields[3]) || !IsSecret(fields[4]))
            return false;

        invitation = new HostInvitation(endpoint!, fields[3], fields[4]);
        return true;
    }

    public override string ToString()
        => $"{nameof(HostInvitation)} {{ Endpoint = {Endpoint?.GetLeftPart(UriPartial.Authority)}, " +
           $"GrantId = {GrantId}, LocalOnly = {LocalOnly} }}";

    private static bool TryValidate(HostInvitation value, out string endpointText)
    {
        endpointText = string.Empty;
        if (value.Endpoint is null || value.GrantId is null || value.Secret is null ||
            !IsGrantId(value.GrantId) || !IsSecret(value.Secret))
            return false;

        endpointText = value.Endpoint.ToString();
        // Same endpoint rules as the room invitation: wss for public, ws only for literal
        // loopback. Nothing here relaxes the URI, SSRF or TLS boundary.
        return RoomInvitation.TryEndpoint(endpointText, false, out _) ||
            RoomInvitation.TryEndpoint(endpointText, true, out _);
    }

    private static bool TryDecodeEndpoint(string encoded, out Uri? endpoint)
    {
        endpoint = null;
        if (!IsBase64Url(encoded) || !TryBase64UrlDecode(encoded, out var bytes))
            return false;

        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return false; }
        if (text.Length > MaxEndpointTextLength ||
            !string.Equals(Base64UrlEncode(bytes), encoded, StringComparison.Ordinal))
            return false;
        if (!RoomInvitation.TryEndpoint(text, false, out endpoint) &&
            !RoomInvitation.TryEndpoint(text, true, out endpoint))
        {
            endpoint = null;
            return false;
        }
        return true;
    }

    private static bool IsGrantId(string? value)
    {
        if (value is null || value.Length != GrantIdLength)
            return false;
        foreach (var c in value)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                return false;
        return true;
    }

    private static bool IsSecret(string? value)
    {
        if (value is null || value.Length != SecretLength)
            return false;
        foreach (var c in value)
            if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F')))
                return false;
        return true;
    }

    private static bool IsBase64Url(string value)
    {
        if (value.Length == 0 || value.Length % 4 == 1)
            return false;
        foreach (var c in value)
            if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                  (c >= '0' && c <= '9') || c is '-' or '_'))
                return false;
        return true;
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryBase64UrlDecode(string value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        try
        {
            bytes = Convert.FromBase64String(padded);
            return bytes.Length != 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
