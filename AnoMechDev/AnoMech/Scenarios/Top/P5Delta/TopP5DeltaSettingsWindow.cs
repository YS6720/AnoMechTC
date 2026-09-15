using Dalamud.Bindings.ImGui;

namespace AnoMech.Scenarios.Top.P5Delta;

public sealed class TopP5DeltaSettingsWindow
{
    public TopP5DeltaStateOverrides Overrides { get; } = new();

    public void Draw()
    {
        if (ImGui.Button("自動")) ResetAll();
        if (SettingsGrid.Begin("##p5delta"))
        {
            DrawTetherAssignment();

            var closeOnly = Overrides.TetherAssignment is
                PlayerTetherAssignment.FarAny or
                PlayerTetherAssignment.FarInner or
                PlayerTetherAssignment.FarOuter;
            var bdOnly = closeOnly || Overrides.TetherAssignment == PlayerTetherAssignment.CloseOuter;

            if (closeOnly) ImGui.BeginDisabled();
            DrawMonitor();
            DrawHelloWorld();
            if (closeOnly) ImGui.EndDisabled();

            if (bdOnly) ImGui.BeginDisabled();
            DrawBeyondDefence();
            if (bdOnly) ImGui.EndDisabled();

            SettingsGrid.End();
        }
    }

    private void ResetAll()
    {
        Overrides.TetherAssignment = PlayerTetherAssignment.Auto;
        Overrides.Monitor = null;
        Overrides.HelloWorld = HelloWorldOption.Auto;
        Overrides.BeyondDefence = null;
    }

    private void DrawTetherAssignment()
    {
        var t = Overrides.TetherAssignment;
        SettingsGrid.Row("連線：");
        if (ImGui.RadioButton("自動##tether",        t == PlayerTetherAssignment.Auto))       Overrides.TetherAssignment = PlayerTetherAssignment.Auto;
        ImGui.SameLine();
        if (ImGui.RadioButton("近任意##tether",   t == PlayerTetherAssignment.CloseAny))   Overrides.TetherAssignment = PlayerTetherAssignment.CloseAny;
        ImGui.SameLine();
        if (ImGui.RadioButton("近內側##tether", t == PlayerTetherAssignment.CloseInner)) Overrides.TetherAssignment = PlayerTetherAssignment.CloseInner;
        ImGui.SameLine();
        if (ImGui.RadioButton("近外側##tether", t == PlayerTetherAssignment.CloseOuter)) Overrides.TetherAssignment = PlayerTetherAssignment.CloseOuter;
        // 第二列：移除 SameLine，讓遠距選項在欄位內換行。
        if (ImGui.RadioButton("遠任意##tether",     t == PlayerTetherAssignment.FarAny))     Overrides.TetherAssignment = PlayerTetherAssignment.FarAny;
        ImGui.SameLine();
        if (ImGui.RadioButton("遠內側##tether",   t == PlayerTetherAssignment.FarInner))   Overrides.TetherAssignment = PlayerTetherAssignment.FarInner;
        ImGui.SameLine();
        if (ImGui.RadioButton("遠外側##tether",   t == PlayerTetherAssignment.FarOuter))   Overrides.TetherAssignment = PlayerTetherAssignment.FarOuter;
    }

    private void DrawMonitor()
    {
        var m = Overrides.Monitor;
        SettingsGrid.Row("監視：");
        if (ImGui.RadioButton("自動##mon", m == null))  Overrides.Monitor = null;
        ImGui.SameLine();
        if (ImGui.RadioButton("是##mon",  m == true))  Overrides.Monitor = true;
        ImGui.SameLine();
        if (ImGui.RadioButton("否##mon",   m == false)) Overrides.Monitor = false;
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

    private void DrawBeyondDefence()
    {
        var b = Overrides.BeyondDefence;
        SettingsGrid.Row("盾連擊S：");
        if (ImGui.RadioButton("自動##bd", b == null))  Overrides.BeyondDefence = null;
        ImGui.SameLine();
        if (ImGui.RadioButton("是##bd",  b == true))  Overrides.BeyondDefence = true;
        ImGui.SameLine();
        if (ImGui.RadioButton("否##bd",  b == false)) Overrides.BeyondDefence = false;
    }
}
