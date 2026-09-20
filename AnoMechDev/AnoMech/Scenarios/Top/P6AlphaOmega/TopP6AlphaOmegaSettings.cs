using Dalamud.Bindings.ImGui;

namespace AnoMech.Scenarios.Top.P6AlphaOmega;

public sealed partial class TopP6AlphaOmegaScenario
{
    // Null follows the encounter's random choice; explicit values force only
    // D3's inclusion, not the other two/three marked party members.
    private bool? meteorD3MarkedOverride;

    public bool HasSettings => true;
    public string SettingsIdentity => $"meteorD3={meteorD3MarkedOverride?.ToString() ?? "auto"}";

    public void DrawSettings()
    {
        if (SettingsGrid.Begin("##p6alphaomega"))
        {
            SettingsGrid.Row("宇宙流星核爆：");
            if (ImGui.RadioButton("隨機##p6meteor", meteorD3MarkedOverride == null))
                meteorD3MarkedOverride = null;
            ImGui.SameLine();
            if (ImGui.RadioButton("包含 D3##p6meteor", meteorD3MarkedOverride == true))
                meteorD3MarkedOverride = true;
            ImGui.SameLine();
            if (ImGui.RadioButton("不含 D3##p6meteor", meteorD3MarkedOverride == false))
                meteorD3MarkedOverride = false;
            SettingsGrid.End();
        }
        ImGui.TextWrapped("包含 D3：D3 去 A 場邊，其餘核爆就近去 D／B 場邊，人群在 C 集合。\n不含 D3：核爆就近去 D／C／B 場邊，人群在 A 集合。選項於下一輪開始時套用。");
    }
}
