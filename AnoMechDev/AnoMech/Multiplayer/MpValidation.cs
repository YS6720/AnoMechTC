using System;
using System.Collections.Generic;
using AnoMech.Core.Game.Party;

namespace AnoMech.Multiplayer;

public sealed class MpProtocolException(MpError error) : Exception(error.ToString())
{
    public MpError Error { get; } = error;
}

public static class MpValidation
{
    public static bool Role(PartyRole role) => (uint)role < MpLimits.Members;
    public static bool Finite(float value) => float.IsFinite(value);
    public static bool Position(MpVector value) =>
        Finite(value.X) && Finite(value.Y) && Finite(value.Z) &&
        MathF.Abs(value.X) <= MpLimits.CoordinateLimit &&
        MathF.Abs(value.Y) <= MpLimits.CoordinateLimit &&
        MathF.Abs(value.Z) <= MpLimits.CoordinateLimit;
    public static bool Pose(MpPose value) => Position(value.Position) && Finite(value.Rotation);
    public static bool Error(MpError error) => Enum.IsDefined(error);

    public static bool Fingerprint(string? value)
    {
        if (value is not { Length: 64 }) return false;
        foreach (var c in value)
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) return false;
        return true;
    }

    public static bool ProgressKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64) return false;
        foreach (var c in value)
            if (c is not (>= 'a' and <= 'z') and not (>= 'A' and <= 'Z') and
                not (>= '0' and <= '9') and not ('-' or '_'))
                return false;
        return true;
    }

    // Diagnostic tag on CheckedRun: absent, or a short ASCII token list. It is
    // rendered into the host's chat verbatim, so the alphabet is deliberately
    // narrow (no whitespace, no markup) and the length small.
    public static bool Detail(string? value)
    {
        if (value is null) return true;
        if (value.Length is 0 or > MpLimits.DetailLength) return false;
        foreach (var c in value)
            if (c is not (>= 'a' and <= 'z') and not (>= 'A' and <= 'Z') and
                not (>= '0' and <= '9') and not ('-' or '_' or ':' or ',' or '='))
                return false;
        return true;
    }

    // An alias is rendered into the engine's 64-byte fixed name buffer (NUL included),
    // so it must encode to at most 63 UTF-8 bytes. Rejecting is the only correct answer:
    // truncating a multi-byte name produces a corrupt name, not a shortened one. The
    // MpLimits.AliasCharacters precheck stays as a cheap upper bound on the char count.
    public const int AliasUtf8Bytes = 63;

    public static bool Alias(string? value)
        => value is { Length: <= MpLimits.AliasCharacters } && SingleLineText(value, AliasUtf8Bytes);

    internal static bool SingleLineText(string? value, int maxUtf8Bytes)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxUtf8Bytes) return false;
        var bytes = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsControl(c)) return false;
            if (char.IsHighSurrogate(c))
            {
                // A lone half of a surrogate pair is not text: it cannot be encoded and
                // would otherwise reach the native buffer as a replacement character.
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) return false;
                i++;
                bytes += 4;
            }
            else if (char.IsLowSurrogate(c)) return false;
            else bytes += c <= 0x7F ? 1 : c <= 0x7FF ? 2 : 3;
            if (bytes > maxUtf8Bytes) return false;
        }
        return true;
    }

    public static bool Descriptor(RunDescriptor? value) => value != null &&
        Fingerprint(value.SceneKey) && ProgressKey(value.ProgressKey) &&
        Fingerprint(value.SceneFingerprint) && Fingerprint(value.ResourceFingerprint) &&
        value.AiIndex is >= 0 and <= 1024 &&
        value.WaymarkIndex is >= 0 and <= 1024 && Finite(value.EventTimeScale) &&
        value.EventTimeScale is > 0 and <= 100;

    public static bool Lobby(LobbyMember[]? members)
    {
        if (members == null || members.Length is < 1 or > MpLimits.Members) return false;
        var peers = new HashSet<Guid>();
        var roleMask = 0;
        foreach (var member in members)
        {
            if (member == null || member.PeerId == Guid.Empty || !peers.Add(member.PeerId) ||
                !Alias(member.Alias) || !Fingerprint(member.BuildFingerprint)) return false;
            if (member.Role is not { } role) continue;
            if (!Role(role) || (roleMask & (1 << (int)role)) != 0) return false;
            roleMask |= 1 << (int)role;
        }
        return true;
    }

    public static bool Validate(MpMessage? message) => message switch
    {
        HelloMessage hello => Alias(hello.Alias) && Fingerprint(hello.BuildFingerprint),
        LobbyMessage lobby => Lobby(lobby.Members),
        ClaimRoleMessage claim => Role(claim.Role),
        RejectedMessage rejected => rejected.RecipientId != Guid.Empty && Error(rejected.Error) && rejected.Error != MpError.None,
        CheckRunMessage check => Descriptor(check.Descriptor),
        CheckedRunMessage check => Error(check.Error) && Detail(check.Detail),
        PreparedRunMessage prepared => Error(prepared.Error),
        PrepareRunMessage or CommitRunMessage or PauseMessage => true,
        EndRunMessage end => Error(end.Reason),
        ControlRequestMessage request => Enum.IsDefined(request.Control),
        PartyMarkerRequestMessage marker => Enum.IsDefined(marker.Sign) &&
            (marker.Role is not { } role || Role(role)),
        SelfPoseMessage pose => Pose(pose.Pose),
        AbilityUseMessage ability => ability.ActionId != 0 && ability.ClassJob != 0 &&
            ability.Level is >= 1 and <= 100 &&
            (ability.Target is not { } target || WorldValidation.ValidateEntity(target)),
        RunStatusMessage status => Enum.IsDefined(status.Outcome) &&
            status.ConsecutiveWins is >= 0 and <= MpLimits.Streak &&
            Finite(status.RetrySeconds) &&
            status.RetrySeconds >= 0f && status.RetrySeconds <= MpLimits.RetryDisplaySeconds,
        WorldSnapshotMessage or RolesSnapshotMessage or WorldEventMessage => WorldValidation.Validate(message),
        _ => false,
    };
}
