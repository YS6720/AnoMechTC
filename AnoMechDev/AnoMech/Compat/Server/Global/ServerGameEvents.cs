using System;
using Dalamud.Game.DutyState;
using Dalamud.Plugin.Services;

namespace AnoMech.Compat.Server;

/// <summary>
/// 國際服伺服器層（Dalamud API 15）：區域切換與副本狀態事件收斂成同一個內部簽章（territory＝uint）。
/// API 15 的 TerritoryChanged 是 <c>Action&lt;uint&gt;</c>，IDutyState 事件改帶 <see cref="IDutyStateEventArgs"/>。
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

    private void OnTerritoryChanged(uint territory) => territoryChanged(territory);
    private void OnDutyStarted(IDutyStateEventArgs args) => dutyStarted(args.TerritoryType.RowId);
    private void OnDutyWiped(IDutyStateEventArgs args) => dutyWiped(args.TerritoryType.RowId);
    private void OnDutyCompleted(IDutyStateEventArgs args) => dutyCompleted(args.TerritoryType.RowId);

    public void Dispose()
    {
        clientState.TerritoryChanged -= OnTerritoryChanged;
        dutyState.DutyStarted -= OnDutyStarted;
        dutyState.DutyWiped -= OnDutyWiped;
        dutyState.DutyCompleted -= OnDutyCompleted;
    }
}
