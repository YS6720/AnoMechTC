using System;
using Dalamud.Plugin.Services;

namespace AnoMech.Compat.Server;

/// <summary>
/// TC 伺服器層（Dalamud API 13）：區域切換與副本狀態事件收斂成同一個內部簽章（territory＝uint）。
/// API 13 的 TerritoryChanged 是 <c>Action&lt;ushort&gt;</c>，IDutyState 事件是 <c>EventHandler&lt;ushort&gt;</c>。
/// </summary>
internal sealed class ServerGameEvents : IDisposable
{
    private readonly IClientState clientState;
    private readonly IDutyState dutyState;
    private readonly Action<uint> territoryChanged;
    private readonly Action<uint> dutyStarted;
    private readonly Action<uint> dutyWiped;
    private readonly Action<uint> dutyCompleted;

    public ServerGameEvents(IClientState clientState, IDutyState dutyState, Action<uint> territoryChanged,
        Action<uint> dutyStarted, Action<uint> dutyWiped, Action<uint> dutyCompleted)
    {
        this.clientState = clientState;
        this.dutyState = dutyState;
        this.territoryChanged = territoryChanged;
        this.dutyStarted = dutyStarted;
        this.dutyWiped = dutyWiped;
        this.dutyCompleted = dutyCompleted;
        clientState.TerritoryChanged += OnTerritoryChanged;
        dutyState.DutyStarted += OnDutyStarted;
        dutyState.DutyWiped += OnDutyWiped;
        dutyState.DutyCompleted += OnDutyCompleted;
    }

    private void OnTerritoryChanged(ushort territory) => territoryChanged(territory);
    private void OnDutyStarted(object? sender, ushort territory) => dutyStarted(territory);
    private void OnDutyWiped(object? sender, ushort territory) => dutyWiped(territory);
    private void OnDutyCompleted(object? sender, ushort territory) => dutyCompleted(territory);

    public void Dispose()
    {
        clientState.TerritoryChanged -= OnTerritoryChanged;
        dutyState.DutyStarted -= OnDutyStarted;
        dutyState.DutyWiped -= OnDutyWiped;
        dutyState.DutyCompleted -= OnDutyCompleted;
    }
}
