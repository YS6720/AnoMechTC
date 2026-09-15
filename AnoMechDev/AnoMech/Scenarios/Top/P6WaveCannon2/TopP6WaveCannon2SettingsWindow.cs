using Dalamud.Bindings.ImGui;

namespace AnoMech.Scenarios.Top.P6WaveCannon2;

// ImGui panel rendered in the main window's "Scenario config" pane when this
// scenario is active. Owns the StateOverrides instance and writes user choices
// into it. See TopP5DeltaSettingsWindow for the canonical shape.
public sealed class TopP6WaveCannon2SettingsWindow
{
    public TopP6WaveCannon2StateOverrides Overrides { get; } = new();

    public void Draw()
    {
        if (ImGui.Button("自動")) ResetAll();
        if (SettingsGrid.Begin("##p6wavecannon2"))
        {
            DrawCosmoArrow();
            SettingsGrid.End();
        }
    }

    private void ResetAll()
    {
        Overrides.InFirst = null;
    }

    private void DrawCosmoArrow()
    {
        var v = Overrides.InFirst;
        SettingsGrid.Row("Cosmo Arrow：");
        if (ImGui.RadioButton("自動##cosmo", v == null))  Overrides.InFirst = null;
        ImGui.SameLine();
        if (ImGui.RadioButton("內##cosmo",   v == true))  Overrides.InFirst = true;
        ImGui.SameLine();
        if (ImGui.RadioButton("外##cosmo",  v == false)) Overrides.InFirst = false;
    }
}
