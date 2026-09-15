using Dalamud.Bindings.ImGui;

namespace AnoMech.Scenarios.Top.P5Sigma;

public sealed class TopP5SigmaSettingsWindow
{
    public TopP5SigmaStateOverrides Overrides { get; } = new();

    public void Draw()
    {
        if (ImGui.Button("自動")) ResetAll();
        if (SettingsGrid.Begin("##p5sigma"))
        {
            DrawCloseFar();
            DrawTowerNorthFlip();
            DrawSpinnerRotation();
            DrawOmegaFForm();
            DrawHelloWorld();
            DrawDynamis();
            DrawMarkers();
            SettingsGrid.End();
        }
    }

    private void ResetAll()
    {
        Overrides.CloseFarTether = null;
        Overrides.TowerNorthFlip = null;
        Overrides.SpinnerRotation = null;
        Overrides.OmegaFForm = null;
        Overrides.HelloWorld = HelloWorldOption.Auto;
        Overrides.Dynamis = null;
        Overrides.Markers = MarkerMode.System;
    }

    private void DrawMarkers()
    {
        var v = Overrides.Markers;
        SettingsGrid.Row("標記方式：");
        if (ImGui.RadioButton("系統標記##marks", v == MarkerMode.System)) Overrides.Markers = MarkerMode.System;
        ImGui.SameLine();
        if (ImGui.RadioButton("手動標記##marks", v == MarkerMode.Manual)) Overrides.Markers = MarkerMode.Manual;
    }

    private void DrawCloseFar()
    {
        var v = Overrides.CloseFarTether;
        SettingsGrid.Row("連線距離：");
        if (ImGui.RadioButton("自動##cf",  v == null))            Overrides.CloseFarTether = null;
        ImGui.SameLine();
        if (ImGui.RadioButton("中##cf", v == GlitchType.Mid))  Overrides.CloseFarTether = GlitchType.Mid;
        ImGui.SameLine();
        if (ImGui.RadioButton("遠##cf",   v == GlitchType.Far))    Overrides.CloseFarTether = GlitchType.Far;
    }

    private void DrawTowerNorthFlip()
    {
        var v = Overrides.TowerNorthFlip;
        SettingsGrid.Row("塔位北側翻轉：");
        if (ImGui.RadioButton("自動##flip", v == null))  Overrides.TowerNorthFlip = null;
        ImGui.SameLine();
        if (ImGui.RadioButton("是##flip",  v == true))  Overrides.TowerNorthFlip = true;
        ImGui.SameLine();
        if (ImGui.RadioButton("否##flip",   v == false)) Overrides.TowerNorthFlip = false;
    }

    private void DrawSpinnerRotation()
    {
        var v = Overrides.SpinnerRotation;
        SettingsGrid.Row("旋轉方向：");
        if (ImGui.RadioButton("自動##spin", v == null))                       Overrides.SpinnerRotation = null;
        ImGui.SameLine();
        if (ImGui.RadioButton("順時針##spin",   v == Rotation.Clockwise))         Overrides.SpinnerRotation = Rotation.Clockwise;
        ImGui.SameLine();
        if (ImGui.RadioButton("逆時針##spin",  v == Rotation.CounterClockwise))  Overrides.SpinnerRotation = Rotation.CounterClockwise;
    }

    private void DrawOmegaFForm()
    {
        var v = Overrides.OmegaFForm;
        SettingsGrid.Row("Omega-F型態：");
        if (ImGui.RadioButton("自動##form",       v == null))                  Overrides.OmegaFForm = null;
        ImGui.SameLine();
        if (ImGui.RadioButton("腿刀##form", v == OmegaAttack.Legs))  Overrides.OmegaFForm = OmegaAttack.Legs;
        ImGui.SameLine();
        if (ImGui.RadioButton("法杖##form",      v == OmegaAttack.Staff))      Overrides.OmegaFForm = OmegaAttack.Staff;
    }

    private void DrawHelloWorld()
    {
        var h = Overrides.HelloWorld;
        SettingsGrid.Row("Hello, World：");
        if (ImGui.RadioButton("自動##hw", h == HelloWorldOption.Auto)) Overrides.HelloWorld = HelloWorldOption.Auto;
        ImGui.SameLine();
        if (ImGui.RadioButton("近##hw", h == HelloWorldOption.Near)) Overrides.HelloWorld = HelloWorldOption.Near;
        ImGui.SameLine();
        if (ImGui.RadioButton("遠##hw",  h == HelloWorldOption.Far))  Overrides.HelloWorld = HelloWorldOption.Far;
        ImGui.SameLine();
        if (ImGui.RadioButton("無##hw",   h == HelloWorldOption.No))   Overrides.HelloWorld = HelloWorldOption.No;
    }

    private void DrawDynamis()
    {
        var d = Overrides.Dynamis;
        SettingsGrid.Row("起始Dynamis：");
        if (ImGui.RadioButton("自動##dyn", d == null))  Overrides.Dynamis = null;
        ImGui.SameLine();
        if (ImGui.RadioButton("是##dyn",  d == true))  Overrides.Dynamis = true;
        ImGui.SameLine();
        if (ImGui.RadioButton("否##dyn",  d == false)) Overrides.Dynamis = false;
    }
}
