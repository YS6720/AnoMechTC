using System;

namespace AnoMech.Core;

/// <summary>
/// User-controlled visibility for the scenario menu.
/// Territory IDs are the existing <c>IZone.TerritoryId</c> values, not a new game data table.
/// </summary>
public sealed class ScenarioVisibility
{
    // 維護者's initial display preference: TOP and UWU. An empty array is a valid choice.
    private uint[] visibleTerritories = [1122, 777];

    /// <summary>
    /// Gets or replaces the selected territory IDs. The array setter is intentional:
    /// Newtonsoft.Json deserialization replaces the user's choices instead of populating
    /// the initializer and bringing the defaults back after a reload.
    /// </summary>
    public uint[] VisibleTerritories
    {
        get => visibleTerritories;
        set => visibleTerritories = value ?? Array.Empty<uint>();
    }

    public bool IsVisible(uint territoryId)
    {
        foreach (var visibleTerritory in visibleTerritories)
            if (visibleTerritory == territoryId) return true;
        return false;
    }

    public void SetVisible(uint territoryId, bool visible)
    {
        if (visible)
        {
            if (IsVisible(territoryId)) return;

            var next = new uint[visibleTerritories.Length + 1];
            Array.Copy(visibleTerritories, next, visibleTerritories.Length);
            next[^1] = territoryId;
            visibleTerritories = next;
            return;
        }

        var remaining = 0;
        foreach (var existing in visibleTerritories)
            if (existing != territoryId) remaining++;

        if (remaining == visibleTerritories.Length) return;

        var without = new uint[remaining];
        var index = 0;
        foreach (var existing in visibleTerritories)
            if (existing != territoryId) without[index++] = existing;
        visibleTerritories = without;
    }
}
