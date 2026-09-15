using Dalamud.Bindings.ImGui;

namespace AnoMech.Scenarios.Top.P2PartySynergy;

public sealed class TopP2PartySynergySettingsWindow
{
    public TopP2PartySynergyStateOverrides Overrides { get; } = new();

    public void Draw()
    {
        if (ImGui.Button("自動")) ResetAll();
        if (SettingsGrid.Begin("##partysynergy"))
        {
            DrawGlitch();
            DrawAttackM();
            DrawAttackF();
            SettingsGrid.End();
        }
    }

    private void ResetAll()
    {
        Overrides.Glitch    = null;
        Overrides.AttackM   = null;
        Overrides.AttackF   = null;
    }

    private void DrawGlitch()
    {
        var v = Overrides.Glitch;
        SettingsGrid.Row("故障：");
        if (ImGui.RadioButton("自動##glitch", v == null))           Overrides.Glitch = null;
        ImGui.SameLine();
        if (ImGui.RadioButton("中##glitch",  v == GlitchType.Mid)) Overrides.Glitch = GlitchType.Mid;
        ImGui.SameLine();
        if (ImGui.RadioButton("遠##glitch",  v == GlitchType.Far)) Overrides.Glitch = GlitchType.Far;
    }

    private void DrawAttackM()
    {
        var v = Overrides.AttackM;
        SettingsGrid.Row("Omega-M 型態：");
        if (ImGui.RadioButton("自動##atkM",   v == null))               Overrides.AttackM = null;
        ImGui.SameLine();
        if (ImGui.RadioButton("劍##atkM",  v == OmegaAttack.Sword))  Overrides.AttackM = OmegaAttack.Sword;
        ImGui.SameLine();
        if (ImGui.RadioButton("盾##atkM", v == OmegaAttack.Shield)) Overrides.AttackM = OmegaAttack.Shield;
    }

    private void DrawAttackF()
    {
        var v = Overrides.AttackF;
        SettingsGrid.Row("Omega-F 型態：");
        if (ImGui.RadioButton("自動##atkF",  v == null))              Overrides.AttackF = null;
        ImGui.SameLine();
        if (ImGui.RadioButton("法杖##atkF", v == OmegaAttack.Staff)) Overrides.AttackF = OmegaAttack.Staff;
        ImGui.SameLine();
        if (ImGui.RadioButton("腿##atkF",  v == OmegaAttack.Legs))  Overrides.AttackF = OmegaAttack.Legs;
    }
}
