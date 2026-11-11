using System;
using AnoMech.Core.SimObjects;
using Dalamud.Bindings.ImGui;

namespace AnoMech.Scenarios.Umad.P1TeleTrouncing;

public sealed class UmadP1TeleTrouncingSettingsWindow
{
    public UmadP1TeleTrouncingStateOverrides Overrides { get; } = new();

    // fireAppear/fireWindUp: fire the statue beats right now (null while no run is active), so
    // the props can be exercised without waiting out the timeline each time.
    public void Draw(Action? fireAppear = null, Action? fireWindUp = null)
    {
        if (ImGui.Button("自動")) ResetAll();
        if (SettingsGrid.Begin("##p1teletrouncing"))
        {
            SettingsGrid.FightOnlyNote();
            DrawDifferentArrows();
            TriStateRow("混亂：", "confused", "DPS", "坦補", Overrides.DpsGetsConfused, v => Overrides.DpsGetsConfused = v);
            TriStateRow("凝視：", "gaze", "反向", "正常", Overrides.GazeInverted, v => Overrides.GazeInverted = v);
            TriStateRow("火：", "fire", "Stack", "Spread", Overrides.FireIsStack, v => Overrides.FireIsStack = v);
            TriStateRow("火球：", "firelie", "說謊", "真話", Overrides.FireIsLie, v => Overrides.FireIsLie = v);
            TriStateRow("雷球：", "thunlie", "說謊", "真話", Overrides.ThunderIsLie, v => Overrides.ThunderIsLie = v);
            TriStateRow("雷翻轉：", "thunflip", "翻轉", "正常", Overrides.ThunderOrientationFlipped, v => Overrides.ThunderOrientationFlipped = v);
            DrawThunderOffset();
#if DEBUG
            DrawPropKnobs();
            DrawPropBeatRow(fireAppear, fireWindUp);
#endif
            SettingsGrid.End();
        }
    }

#if DEBUG
    private void DrawPropBeatRow(Action? fireAppear, Action? fireWindUp)
    {
        SettingsGrid.Row("石像動畫（除錯）：");
        ImGui.BeginDisabled(fireAppear == null);
        if (ImGui.Button("立即出現##propappear")) fireAppear?.Invoke();
        ImGui.SameLine();
        if (ImGui.Button("立即蓄力##propwindup")) fireWindUp?.Invoke();
        ImGui.EndDisabled();
    }
#endif

    private void ResetAll()
    {
        Overrides.DpsGetsDifferent = null;
        Overrides.DpsGetsConfused = null;
        Overrides.GazeInverted = null;
        Overrides.FireIsStack = null;
        Overrides.FireIsLie = null;
        Overrides.ThunderIsLie = null;
        Overrides.ThunderOrientationFlipped = null;
        Overrides.ThunderRealOffset = null;
        Overrides.PropsBindDirector = true;
    }

#if DEBUG
    private void DrawPropKnobs()
    {
        SettingsGrid.Row("石像物件（除錯）：");
        var bind = Overrides.PropsBindDirector;
        if (ImGui.Checkbox("綁定 director##propbind", ref bind)) Overrides.PropsBindDirector = bind;
    }
#endif

    private void DrawDifferentArrows()
    {
        var d = Overrides.DpsGetsDifferent;
        SettingsGrid.Row("不同方向箭頭：");
        if (ImGui.RadioButton("自動##diffarrows", d == null)) Overrides.DpsGetsDifferent = null;
        ImGui.SameLine();
        if (ImGui.RadioButton("DPS##diffarrows", d == true)) Overrides.DpsGetsDifferent = true;
        ImGui.SameLine();
        if (ImGui.RadioButton("坦補##diffarrows", d == false)) Overrides.DpsGetsDifferent = false;
    }

    // Auto / <trueLabel> / <falseLabel> tri-state row over a nullable-bool override
    // (null = Auto/random). Mirrors UmadP4KefkaSaysSettingsWindow.RealFakeRow.
    private static void TriStateRow(string label, string id, string trueLabel, string falseLabel, bool? value, Action<bool?> set)
    {
        SettingsGrid.Row(label);
        if (ImGui.RadioButton($"自動##{id}", value == null)) set(null);
        ImGui.SameLine();
        if (ImGui.RadioButton($"{trueLabel}##{id}", value == true)) set(true);
        ImGui.SameLine();
        if (ImGui.RadioButton($"{falseLabel}##{id}", value == false)) set(false);
    }

    private void DrawThunderOffset()
    {
        var v = Overrides.ThunderRealOffset;
        SettingsGrid.Row("真雷位置：");
        if (ImGui.RadioButton("自動##thunoff", v == null)) Overrides.ThunderRealOffset = null;
        ImGui.SameLine();
        if (ImGui.RadioButton("第 0、2 道##thunoff", v == 0)) Overrides.ThunderRealOffset = 0;
        ImGui.SameLine();
        if (ImGui.RadioButton("第 1、3 道##thunoff", v == 1)) Overrides.ThunderRealOffset = 1;
    }
}
