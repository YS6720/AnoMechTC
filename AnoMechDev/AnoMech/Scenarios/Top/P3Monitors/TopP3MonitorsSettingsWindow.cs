using Dalamud.Bindings.ImGui;

namespace AnoMech.Scenarios.Top.P3Monitors;

public sealed class TopP3MonitorsSettingsWindow
{
    public TopP3MonitorsOverrides Overrides { get; } = new();
    public TopP3MonitorsState? CurrentState { get; set; }

    public void Draw()
    {
        if (ImGui.Button("重置為隨機")) ResetAll();
        if (SettingsGrid.Begin("##p3monitors"))
        {
            DrawBossSide();
            DrawPlayerMonitor();
            SettingsGrid.End();
        }

        if (CurrentState is not { } state) return;
        ImGui.Separator();
        if (!state.BuffStarted)
        {
            ImGui.TextUnformatted($"排隊順序：{TopP3MonitorRules.PriorityLabel}");
            ImGui.TextUnformatted(
                $"自身練習分工：{TopP3MonitorRules.RoleLabel(state.PlayerRole)}（第 {state.PlayerPriority + 1} 位）");
            ImGui.TextUnformatted("準備中：AI先到左側排隊，約等待 5 秒；真人請自行走到空位。");
            return;
        }

        ImGui.TextUnformatted($"本輪王側：{state.BossSideLabel}");
        ImGui.TextUnformatted($"Owner順位：{TopP3MonitorRules.PriorityLabel}");
        ImGui.TextUnformatted($"自身順位：{state.PlayerSlotLabel}");
        ImGui.TextUnformatted($"固定優先序：第 {state.PlayerPriority + 1} 位（{TopP3MonitorRules.RoleLabel(state.PlayerRole)}）");
        if (state.PlayerHasMonitor)
            ImGui.TextUnformatted($"自身螢幕方向：{state.PlayerStatusLabel}");
    }

    private void ResetAll()
    {
        Overrides.BossSide = TopP3BossSideOption.Auto;
        Overrides.PlayerMonitor = TopP3PlayerMonitorOption.Auto;
    }

    private void DrawBossSide()
    {
        var value = Overrides.BossSide;
        SettingsGrid.Row("王側（隨機／左／右）：");
        if (ImGui.RadioButton("隨機##p3boss", value == TopP3BossSideOption.Auto))
            Overrides.BossSide = TopP3BossSideOption.Auto;
        ImGui.SameLine();
        if (ImGui.RadioButton("左##p3boss", value == TopP3BossSideOption.Left))
            Overrides.BossSide = TopP3BossSideOption.Left;
        ImGui.SameLine();
        if (ImGui.RadioButton("右##p3boss", value == TopP3BossSideOption.Right))
            Overrides.BossSide = TopP3BossSideOption.Right;
    }

    private void DrawPlayerMonitor()
    {
        var value = Overrides.PlayerMonitor;
        SettingsGrid.Row("自身螢幕（隨機／有／無）：");
        if (ImGui.RadioButton("隨機##p3self", value == TopP3PlayerMonitorOption.Auto))
            Overrides.PlayerMonitor = TopP3PlayerMonitorOption.Auto;
        ImGui.SameLine();
        if (ImGui.RadioButton("有##p3self", value == TopP3PlayerMonitorOption.WithMonitor))
            Overrides.PlayerMonitor = TopP3PlayerMonitorOption.WithMonitor;
        ImGui.SameLine();
        if (ImGui.RadioButton("無##p3self", value == TopP3PlayerMonitorOption.WithoutMonitor))
            Overrides.PlayerMonitor = TopP3PlayerMonitorOption.WithoutMonitor;
    }
}
