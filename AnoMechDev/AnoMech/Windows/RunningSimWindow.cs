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
        // 只設定初次開啟尺寸；收起選項不改變使用者調整過的外框。
        Size = new Vector2(560, 400);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoCollapse;
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        IsOpen = false;
    }

    public override void PreOpenCheck()
        => IsOpen = plugin.Configuration.CompactSimulationControls && plugin.Game.World.Map.IsInInstance;

    private bool showSelectors = true;

    public override void Draw()
    {
        var game = plugin.Game;
        var running = game.ActiveScenario is not null && !game.Paused;
        ImGui.TextColored(running ? new Vector4(0.4f, 0.9f, 0.4f, 1f) : new Vector4(1f, 0.8f, 0.3f, 1f),
            running ? "練習進行中" : game.Paused ? "練習已暫停" : "尚未開始，選好場景後按「開始」即可練習");
        if (game.ActiveScenario is { } scenario)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("｜");
            ImGui.SameLine();
            ImGui.TextUnformatted(Game.DisplayName(scenario));
        }
        if (game.ConsecutiveWins > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"｜連勝 {game.ConsecutiveWins}");
        }
        if (plugin.Multiplayer.HasSession && plugin.Multiplayer.Session is { } session)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f),
                $"｜多人 {(session.IsHost ? "房主" : "成員")}・{session.Members.Count} 人");
        }
        ImGui.Separator();
        // 場景／進度／打法／場標直接在小窗調，不必開完整面板（維護者 2026-09-17）。
        if (ImGui.SmallButton(showSelectors ? "▾ 場景與打法" : "▸ 場景與打法")) showSelectors = !showSelectors;
        if (showSelectors)
        {
            ImGui.Indent();
            main.DrawQuickSelectors();
            ImGui.Unindent();
        }
        ImGui.Separator();
        main.DrawRunControls();
        ImGui.Separator();
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
