using System;
using System.Collections.Generic;
using System.Text;
using AnoMech.Multiplayer;

namespace AnoMech.Core.Game.Party;

// Projects the frozen lobby roster onto the eight party slots as *display names only*.
//
// The roster is already the run's authority for who owns which role (PeerId -> Role),
// so a connected player's chosen alias needs no extra wire field: the same frozen
// roster that grants the role also carries the alias. Arrival order is irrelevant —
// a slot is filled from the member that claims that role, never from list position.
//
// Deliberately narrow: this produces managed strings for the sim's own name surfaces
// (the spawned doppel's native name, SimNetworkPuppet.DisplayName, and the party-list
// row). It never touches the real local GameObject name, ContentId or AccountId, and
// it never changes job / equipment / level / role mapping — those stay exactly as the
// scenario preset defined them.
public static class NetworkPartyIdentity
{
    public const int Slots = 8;

    // Size of the engine's fixed name buffer, terminator included. MpValidation.Alias
    // caps an alias at 63 UTF-8 bytes precisely so an encoded alias always fits here
    // without truncation (a truncated multi-byte name is corrupt, not shortened).
    public const int NativeNameBytes = 64;

    /// <summary>
    /// Maps the frozen roster onto an 8-slot alias array indexed by <see cref="PartyRole"/>.
    /// A slot is null when no roster member holds that role (an AI stand-in keeps its
    /// preset name) or when the member's alias fails validation.
    /// </summary>
    public static string?[] Project(IReadOnlyList<LobbyMember> roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        var names = new string?[Slots];
        for (var i = 0; i < roster.Count; i++)
        {
            var member = roster[i];
            if (member?.Role is not { } role || (uint)role >= Slots) continue;
            if (!MpValidation.Alias(member.Alias)) continue;
            // First claim wins. Lobby validation already rejects a duplicated role,
            // so this only keeps the projection total rather than picking a winner.
            names[(int)role] ??= member.Alias;
        }
        return names;
    }

    /// <summary>
    /// Encodes <paramref name="name"/> as NUL-terminated UTF-8 into a fixed name buffer,
    /// zeroing the remainder so a longer previous occupant cannot leak. Returns false and
    /// leaves <paramref name="destination"/> untouched when the encoded name does not fit
    /// with its terminator — callers must reject rather than truncate.
    /// </summary>
    public static bool TryWriteName(string? name, Span<byte> destination)
    {
        if (string.IsNullOrEmpty(name) || destination.IsEmpty) return false;
        var required = Encoding.UTF8.GetByteCount(name);
        if (required >= destination.Length) return false;
        Encoding.UTF8.GetBytes(name, destination);
        destination[required..].Clear();
        return true;
    }
}
