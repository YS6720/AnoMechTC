using System.Numerics;
using AnoMech.Core.Game;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AnoMech.Windows;

// Compact alternative to the full picker, following Evernow's RunningSimWindow.
// All actions use MainWindow's existing control path and Game's safety gates.
internal sealed class RunningSimWindow : Window
{
    private readonly Plugin plugin;
    private readonly MainWindow main;

    public RunningSimWindow(Plugin plugin, MainWindow main)
        : base($"AnoMech {BuildEdition.DisplayName} 練習操作###AnoMechRunningSim")
    {
        this.plugin = plugin;
        this.main = main;
        Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse;
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        IsOpen = false;
    }

    public override void PreOpenCheck()
        => IsOpen = plugin.Configuration.CompactSimulationControls && plugin.Game.World.Map.IsInInstance;

    public override void Draw()
    {
        var game = plugin.Game;
        var running = game.ActiveScenario is not null && !game.Paused;
        ImGui.TextColored(running ? new Vector4(0.4f, 0.9f, 0.4f, 1f) : new Vector4(1f, 0.8f, 0.3f, 1f),
            running ? "練習進行中" : game.Paused ? "練習已暫停" : "尚未開始，選好場景後按「開始」即可練習");
        if (game.ActiveScenario is { } scenario)
            ImGui.TextUnformatted(Game.DisplayName(scenario));
        main.DrawRunControls();
        if (ImGui.Button("完整面板")) main.TogglePanel();
        if (BuildEdition.IsDeveloper)
        {
            ImGui.SameLine();
            if (ImGui.Button("站位編輯器")) plugin.TogglePositionEditor();
        }
        ImGui.SameLine();
        if (ImGui.Button("設定")) plugin.ToggleConfigUi();
        main.DrawInInstanceReloadWarning();
    }
}
