using System;
using System.Security.Cryptography;
using System.Text;

namespace AnoMech.Multiplayer;

/// <summary>
/// Protects an imported <see cref="HostInvitation"/> at rest with Windows DPAPI
/// (<see cref="DataProtectionScope.CurrentUser"/>). Only the ciphertext is ever persisted; there is
/// no plaintext fallback and no migration of existing settings. A failure to unprotect (different
/// Windows account, different machine, edited config) means the user must import again.
/// </summary>
public static class HostCredentialProtection
{
    /// <summary>Upper bound for a stored ciphertext; a longer value is rejected without decrypting.</summary>
    public const int MaxProtectedTextLength = 4096;

    // Versioned, non-secret entropy: binds the blob to this feature so an unrelated DPAPI blob
    // from the same user cannot be replayed into the hosting credential slot.
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("AnoMech.HostInvitation.v1");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// Encrypts an invitation for the current Windows user. Returns false — with no ciphertext —
    /// on any unsupported platform, invalid invitation or protection failure.
    /// </summary>
    public static bool TryProtect(HostInvitation? invitation, out string? ciphertext)
    {
        ciphertext = null;
        if (invitation is null || !OperatingSystem.IsWindows())
            return false;

        string encoded;
        try { encoded = invitation.Encode(); }
        catch (InvalidOperationException) { return false; }

        try
        {
            var protectedBytes = ProtectedData.Protect(
                StrictUtf8.GetBytes(encoded), Entropy, DataProtectionScope.CurrentUser);
            var text = Convert.ToBase64String(protectedBytes);
            if (text.Length > MaxProtectedTextLength)
                return false;
            ciphertext = text;
            return true;
        }
        catch (CryptographicException) { return false; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>
    /// Decrypts a previously stored ciphertext. Returns false for anything that is not a valid
    /// current-user blob holding a valid host invitation; callers must ask the user to re-import
    /// instead of falling back to any plaintext source.
    /// </summary>
    public static bool TryUnprotect(string? ciphertext, out HostInvitation? invitation)
    {
        invitation = null;
        if (string.IsNullOrEmpty(ciphertext) || ciphertext.Length > MaxProtectedTextLength ||
            !OperatingSystem.IsWindows())
            return false;

        byte[] plaintext;
        try
        {
            plaintext = ProtectedData.Unprotect(
                Convert.FromBase64String(ciphertext), Entropy, DataProtectionScope.CurrentUser);
        }
        catch (FormatException) { return false; }
        catch (CryptographicException) { return false; }
        catch (ArgumentException) { return false; }

        try
        {
            if (plaintext.Length > HostInvitation.MaxTextLength)
                return false;
            return HostInvitation.TryParse(StrictUtf8.GetString(plaintext), out invitation);
        }
        catch (DecoderFallbackException) { return false; }
        finally { Array.Clear(plaintext); }
    }
}
