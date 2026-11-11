using System;
using Dalamud.Bindings.ImGui;

namespace AnoMech.Scenarios.Umad.P4KefkaSays;

// ImGui panel rendered in the main window's "Scenario config" pane when this
// scenario is active. Owns the StateOverrides instance and writes user choices into
// it. See UmadP2ForsakenSettingsWindow for the canonical shape.
public sealed class UmadP4KefkaSaysSettingsWindow
{
    public UmadP4KefkaSaysStateOverrides Overrides { get; } = new();

    public void Draw()
    {
        if (ImGui.Button("自動")) ResetAll();
        if (SettingsGrid.Begin("##umadp4kefkasays"))
        {
            SettingsGrid.FightOnlyNote();
#if DEBUG
            DrawFirstBlizzard();
            DrawFirstLightning();
            DrawFirstBlizzardOffset();
#endif
            RealFakeRow("Exdeath 第 1 次：", "ex1", Overrides.ExdeathCast1Real, v => Overrides.ExdeathCast1Real = v);
            DrawExdeath2();
            RealFakeRow("Exdeath 第 3 次：", "ex3", Overrides.ExdeathCast3Real, v => Overrides.ExdeathCast3Real = v);
            RealFakeRow("Exdeath 第 4 次：", "ex4", Overrides.ExdeathCast4Real, v => Overrides.ExdeathCast4Real = v);
            RealFakeRow("Chaos 第 1 次：",   "chaos1", Overrides.ChaosCast1Real, v => Overrides.ChaosCast1Real = v);
            RealFakeRow("Chaos 第 2 次：",   "chaos2", Overrides.ChaosCast2Real, v => Overrides.ChaosCast2Real = v);
            SettingsGrid.End();
        }
    }

    // Auto / Real / Fake tri-state row writing a nullable-bool override
    // (null = Auto/random, true = Real, false = Fake).
    private static void RealFakeRow(string label, string id, bool? value, Action<bool?> set)
    {
        SettingsGrid.Row(label);
        if (ImGui.RadioButton($"自動##{id}", value == null))  set(null);
        ImGui.SameLine();
        if (ImGui.RadioButton($"真##{id}", value == true))  set(true);
        ImGui.SameLine();
        if (ImGui.RadioButton($"假##{id}", value == false)) set(false);
    }

    // Exdeath 2 gets a fourth choice: mirror cast 1's resolution (Wave2True = !Wave1True).
    private void DrawExdeath2()
    {
        var v = Overrides.ExdeathCast2;
        SettingsGrid.Row("Exdeath 第 2 次：");
        if (ImGui.RadioButton("自動##ex2", v == ExdeathCast2Mode.Auto)) Overrides.ExdeathCast2 = ExdeathCast2Mode.Auto;
        ImGui.SameLine();
        if (ImGui.RadioButton("真##ex2", v == ExdeathCast2Mode.Real)) Overrides.ExdeathCast2 = ExdeathCast2Mode.Real;
        ImGui.SameLine();
        if (ImGui.RadioButton("假##ex2", v == ExdeathCast2Mode.Fake)) Overrides.ExdeathCast2 = ExdeathCast2Mode.Fake;
        ImGui.SameLine();
        if (ImGui.RadioButton("與第 1 次相反##ex2", v == ExdeathCast2Mode.OppositeTo1)) Overrides.ExdeathCast2 = ExdeathCast2Mode.OppositeTo1;
    }

#if DEBUG
    private void DrawFirstBlizzard()
    {
        var v = Overrides.BlizzardReal[0];
        SettingsGrid.Row("第一次 Blizzard：");
        if (ImGui.RadioButton("自動##firstblizz", v == null))  Overrides.BlizzardReal[0] = null;
        ImGui.SameLine();
        if (ImGui.RadioButton("真##firstblizz", v == true))  Overrides.BlizzardReal[0] = true;
        ImGui.SameLine();
        if (ImGui.RadioButton("假##firstblizz", v == false)) Overrides.BlizzardReal[0] = false;
    }

    private void DrawFirstLightning()
    {
        var v = Overrides.LightningReal[0];
        SettingsGrid.Row("第一次 Lightning：");
        if (ImGui.RadioButton("自動##firstlight", v == null))  Overrides.LightningReal[0] = null;
        ImGui.SameLine();
        if (ImGui.RadioButton("真##firstlight", v == true))  Overrides.LightningReal[0] = true;
        ImGui.SameLine();
        if (ImGui.RadioButton("假##firstlight", v == false)) Overrides.LightningReal[0] = false;
    }

    private void DrawFirstBlizzardOffset()
    {
        var v = Overrides.BlizzardOffset[0];
        SettingsGrid.Row("第一次 Blizzard 偏移：");
        if (ImGui.RadioButton("自動##firstoffset", v == null)) Overrides.BlizzardOffset[0] = null;
        ImGui.SameLine();
        if (ImGui.RadioButton("0##firstoffset", v == 0))       Overrides.BlizzardOffset[0] = 0;
        ImGui.SameLine();
        if (ImGui.RadioButton("1##firstoffset", v == 1))       Overrides.BlizzardOffset[0] = 1;
    }
#endif

    private void ResetAll()
    {
#if DEBUG
        Overrides.BlizzardReal[0] = null;
        Overrides.LightningReal[0] = null;
        Overrides.BlizzardOffset[0] = null;
#endif
        Overrides.ExdeathCast1Real = null;
        Overrides.ExdeathCast2 = ExdeathCast2Mode.Auto;
        Overrides.ExdeathCast3Real = null;
        Overrides.ExdeathCast4Real = null;
        Overrides.ChaosCast1Real = null;
        Overrides.ChaosCast2Real = null;
    }
}
