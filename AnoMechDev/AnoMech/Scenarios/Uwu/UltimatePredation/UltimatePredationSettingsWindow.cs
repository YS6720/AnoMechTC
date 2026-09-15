using Dalamud.Bindings.ImGui;

namespace AnoMech.Scenarios.Uwu.UltimatePredation;

public class UltimatePredationSettingsWindow
{
    public UltimatePredationStateOverrides Overrides { get; } = new();

    public void Draw()
    {
        if (ImGui.Button("自動"))
        {
            ResetAll();
        }

        if (SettingsGrid.Begin("##ultimatepredation"))
        {
            DrawCenterDodge();
            SettingsGrid.End();
        }
    }

    private void DrawCenterDodge()
    {
        var v = Overrides.CenterDodge;
        SettingsGrid.Row("王位置：");
        if (ImGui.RadioButton("隨機##centerdodge", v == null)) Overrides.CenterDodge = null;
        ImGui.SameLine();
        if (ImGui.RadioButton("中心閃避##centerdodge", v == true)) Overrides.CenterDodge = true;
    }

    private void ResetAll()
    {
        Overrides.CenterDodge = null;
    }
}
