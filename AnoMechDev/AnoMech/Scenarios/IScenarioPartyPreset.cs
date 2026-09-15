using System.Collections.Generic;
using AnoMech.Core.Game.Party;

namespace AnoMech.Scenarios;

/// <summary>
/// Optional scenario-owned party composition.  The eight entries use the fixed
/// PartyRole slot order and contain exactly one null entry for the local player.
/// </summary>
public interface IScenarioPartyPreset
{
    IReadOnlyList<PartyMemberPreset?> GetPartyPreset(
        uint playerJob,
        PartyRole? roleOverride,
        bool solo);
}
