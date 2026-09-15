using System;
using System.Text;

namespace AnoMech.Multiplayer;

/// <summary>
/// A bounded, versioned invitation containing only the public relay endpoint, room code and join
/// token. The token is an authorization secret, not an encrypted payload; callers must treat the
/// encoded text as sensitive and only copy it when the user explicitly asks.
/// </summary>
public sealed record RoomInvitation(Uri Endpoint, string RoomCode, string Token, bool LocalOnly)
{
    public const int MaxTextLength = 2048;

    private const string Magic = "ANOMECH";
    private const string Version = "1";
    private const string PublicMode = "p";
    private const string LocalMode = "l";
    private const int MaxEndpointTextLength = 256;
    private const int TokenLength = 64;
    private const string InvalidMessage = "Invalid room invitation.";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public string Encode()
    {
        if (!TryValidate(this, out var endpointText, out var mode))
            throw new InvalidOperationException(InvalidMessage);

        var endpoint = Base64UrlEncode(StrictUtf8.GetBytes(endpointText));
        var encoded = string.Join("|", Magic, Version, mode, endpoint, RoomCode, Token);
        if (encoded.Length > MaxTextLength)
            throw new InvalidOperationException(InvalidMessage);
        return encoded;
    }

    public static bool TryParse(string? text, out RoomInvitation? invitation)
    {
        invitation = null;
        if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength)
            return false;

        var fields = text.Split('|');
        if (fields.Length != 6 || fields[0] != Magic || fields[1] != Version ||
            fields[2] is not (PublicMode or LocalMode) || fields[3].Length == 0)
            return false;

        if (!TryDecodeEndpoint(fields[3], fields[2] == LocalMode, out var endpoint) ||
            !WireProtocol.IsRoomCode(fields[4]) || !IsToken(fields[5]))
            return false;

        invitation = new RoomInvitation(endpoint!, fields[4], fields[5], fields[2] == LocalMode);
        return true;
    }

    public static bool TryEndpoint(string text, bool localOnly, out Uri? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrEmpty(text) || text.Length > MaxEndpointTextLength ||
            text.Trim() != text || HasControlOrWhitespace(text) ||
            !Uri.TryCreate(text, UriKind.Absolute, out var parsed) ||
            !parsed.IsAbsoluteUri || parsed.Host.Length == 0 || parsed.UserInfo.Length != 0 ||
            parsed.Query.Length != 0 || parsed.Fragment.Length != 0 ||
            parsed.AbsolutePath != "/" || !HasExactRootPath(text))
            return false;

        if (localOnly)
        {
            if (!string.Equals(parsed.Scheme, Uri.UriSchemeWs, StringComparison.OrdinalIgnoreCase) ||
                !IsLiteralLoopback(parsed))
                return false;
        }
        else if (!string.Equals(parsed.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
            return false;

        endpoint = parsed;
        return true;
    }
    public override string ToString()
        => $"{nameof(RoomInvitation)} {{ RoomCode = {RoomCode}, LocalOnly = {LocalOnly} }}";

    private static bool TryValidate(RoomInvitation value, out string endpointText, out string mode)
    {
        endpointText = string.Empty;
        mode = value.LocalOnly ? LocalMode : PublicMode;
        if (value.Endpoint is null || value.RoomCode is null || value.Token is null ||
            !WireProtocol.IsRoomCode(value.RoomCode) || !IsToken(value.Token))
            return false;

        endpointText = value.Endpoint.ToString();
        return TryEndpoint(endpointText, value.LocalOnly, out _);
    }

    private static bool TryDecodeEndpoint(string encoded, bool localOnly, out Uri? endpoint)
    {
        endpoint = null;
        if (!IsBase64Url(encoded) || !TryBase64UrlDecode(encoded, out var bytes))
            return false;

        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return false; }
        if (text.Length > MaxEndpointTextLength ||
            !TryEndpoint(text, localOnly, out endpoint) ||
            !string.Equals(Base64UrlEncode(bytes), encoded, StringComparison.Ordinal))
        {
            endpoint = null;
            return false;
        }
        return true;
    }

    private static bool IsToken(string? token)
    {
        if (token is null || token.Length != TokenLength)
            return false;
        foreach (var c in token)
            if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f')))
                return false;
        return true;
    }

    private static bool IsLiteralLoopback(Uri endpoint)
    {
        if (endpoint.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6))
            return false;
        var host = endpoint.Host;
        if (host.Length > 1 && host[0] == '[' && host[^1] == ']')
            host = host[1..^1];
        return System.Net.IPAddress.TryParse(host, out var address) &&
            System.Net.IPAddress.IsLoopback(address);
    }

    private static bool HasControlOrWhitespace(string value)
    {
        foreach (var c in value)
            if (char.IsControl(c) || char.IsWhiteSpace(c))
                return true;
        return false;
    }

    private static bool HasExactRootPath(string text)
    {
        var schemeSeparator = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator < 1)
            return false;

        var authorityAndPath = text[(schemeSeparator + 3)..];
        if (authorityAndPath.IndexOf('\\') >= 0)
            return false;
        var slash = authorityAndPath.IndexOf('/');
        var authority = slash < 0 ? authorityAndPath : authorityAndPath[..slash];
        if (authority.Length == 0 || authority.IndexOf('@') >= 0 || authority.IndexOf('%') >= 0 ||
            authority.IndexOf('?') >= 0 || authority.IndexOf('#') >= 0)
            return false;
        return slash < 0 || (slash == authority.Length && authorityAndPath.Length == slash + 1);
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
