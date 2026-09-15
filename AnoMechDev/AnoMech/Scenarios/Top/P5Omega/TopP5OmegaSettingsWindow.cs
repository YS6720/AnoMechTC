using System;
using Dalamud.Bindings.ImGui;

namespace AnoMech.Scenarios.Top.P5Omega;

public sealed class TopP5OmegaSettingsWindow
{
    public TopP5OmegaStateOverrides Overrides { get; } = new();

    public void Draw()
    {
        if (ImGui.Button("自動")) ResetAll();
        if (SettingsGrid.Begin("##p5omega"))
        {
            DrawAttack("第一輪F攻擊：",  "1f", OmegaAttack.Legs,   "腿刀",   OmegaAttack.Staff,  "法杖",
                       () => Overrides.FirstFAttack,  v => Overrides.FirstFAttack  = v);
            DrawAttack("第一輪M攻擊：",  "1m", OmegaAttack.Sword,  "刀",  OmegaAttack.Shield, "盾",
                       () => Overrides.FirstMAttack,  v => Overrides.FirstMAttack  = v);
            DrawAttack("第二輪F攻擊：", "2f", OmegaAttack.Legs,   "腿刀",   OmegaAttack.Staff,  "法杖",
                       () => Overrides.SecondFAttack, v => Overrides.SecondFAttack = v);
            DrawAttack("第二輪M攻擊：", "2m", OmegaAttack.Sword,  "刀",  OmegaAttack.Shield, "盾",
                       () => Overrides.SecondMAttack, v => Overrides.SecondMAttack = v);
            DrawWaveCannon();
            DrawMonitorSide();
            DrawBeetleSpawn();
            DrawExtraDynamis();
            DrawHelloWorldOrder();
            DrawHelloWorldType();
            DrawMarkers();
            SettingsGrid.End();
        }
        DrawForceButtons();
    }

    private void ResetAll()
    {
        Overrides.FirstFAttack = null;
        Overrides.FirstMAttack = null;
        Overrides.SecondFAttack = null;
        Overrides.SecondMAttack = null;
        Overrides.FirstWaveCannonFront = null;
        Overrides.MonitorSide = null;
        Overrides.BettleSpawnDirection = null;
        Overrides.ExtraDynamis = null;
        Overrides.HelloWorldOrder = HelloWorldOrderOption.Auto;
        Overrides.HelloWorldType = HelloWorldTypeOption.Auto;
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

    private static void DrawAttack(string label, string suffix,
                                   OmegaAttack optionA, string nameA,
                                   OmegaAttack optionB, string nameB,
                                   Func<OmegaAttack?> get, Action<OmegaAttack?> set)
    {
        var v = get();
        SettingsGrid.Row(label);
        if (ImGui.RadioButton($"自動##{suffix}",    v == null))     set(null);
        ImGui.SameLine();
        if (ImGui.RadioButton($"{nameA}##{suffix}", v == optionA))  set(optionA);
        ImGui.SameLine();
        if (ImGui.RadioButton($"{nameB}##{suffix}", v == optionB))  set(optionB);
    }

    private void DrawWaveCannon()
    {
        var v = Overrides.FirstWaveCannonFront;
        SettingsGrid.Row("擴散波動砲：");
        if (ImGui.RadioButton("自動##wc",       v == null))  Overrides.FirstWaveCannonFront = null;
        ImGui.SameLine();
        if (ImGui.RadioButton("橫向##wc", v == false)) Overrides.FirstWaveCannonFront = false;
        ImGui.SameLine();
        if (ImGui.RadioButton("縱向##wc",   v == true))  Overrides.FirstWaveCannonFront = true;
    }

    private void DrawMonitorSide()
    {
        var v = Overrides.MonitorSide;
        SettingsGrid.Row("監視側：");
        if (ImGui.RadioButton("自動##mon",  v == null))               Overrides.MonitorSide = null;
        ImGui.SameLine();
        if (ImGui.RadioButton("左##mon",  v == MonitorSide.Left))   Overrides.MonitorSide = MonitorSide.Left;
        ImGui.SameLine();
        if (ImGui.RadioButton("右##mon", v == MonitorSide.Right))  Overrides.MonitorSide = MonitorSide.Right;
    }

    private void DrawBeetleSpawn()
    {
        SettingsGrid.Row("甲蟲生成：");
        if (ImGui.RadioButton("自動##beetle", Overrides.BettleSpawnDirection == null)) Overrides.BettleSpawnDirection = null;
        foreach (var d in Direction.Cardinal)
        {
            ImGui.SameLine();
            if (ImGui.RadioButton($"{d.Name()}##beetle", Overrides.BettleSpawnDirection == d)) Overrides.BettleSpawnDirection = d;
        }
    }

    private void DrawExtraDynamis()
    {
        var v = Overrides.ExtraDynamis;
        SettingsGrid.Row("額外Dynamis集合：");
        if (ImGui.RadioButton("自動##dyn", v == null))  Overrides.ExtraDynamis = null;
        ImGui.SameLine();
        if (ImGui.RadioButton("否##dyn",   v == false)) Overrides.ExtraDynamis = false;
        ImGui.SameLine();
        if (ImGui.RadioButton("是##dyn",  v == true))  Overrides.ExtraDynamis = true;
    }

    private void DrawHelloWorldOrder()
    {
        var v = Overrides.HelloWorldOrder;
        SettingsGrid.Row("Hello, World 順序：");
        if (ImGui.RadioButton("自動##hwo",   v == HelloWorldOrderOption.Auto))   Overrides.HelloWorldOrder = HelloWorldOrderOption.Auto;
        ImGui.SameLine();
        if (ImGui.RadioButton("任意##hwo",    v == HelloWorldOrderOption.Any))    Overrides.HelloWorldOrder = HelloWorldOrderOption.Any;
        ImGui.SameLine();
        if (ImGui.RadioButton("第一組##hwo",  v == HelloWorldOrderOption.First))  Overrides.HelloWorldOrder = HelloWorldOrderOption.First;
        ImGui.SameLine();
        if (ImGui.RadioButton("第二組##hwo", v == HelloWorldOrderOption.Second)) Overrides.HelloWorldOrder = HelloWorldOrderOption.Second;
        ImGui.SameLine();
        if (ImGui.RadioButton("無##hwo",   v == HelloWorldOrderOption.None))   Overrides.HelloWorldOrder = HelloWorldOrderOption.None;
    }

    private void DrawHelloWorldType()
    {
        var v = Overrides.HelloWorldType;
        SettingsGrid.Row("Hello, World 類型：");
        if (ImGui.RadioButton("自動##hwt", v == HelloWorldTypeOption.Auto)) Overrides.HelloWorldType = HelloWorldTypeOption.Auto;
        ImGui.SameLine();
        if (ImGui.RadioButton("近##hwt", v == HelloWorldTypeOption.Near)) Overrides.HelloWorldType = HelloWorldTypeOption.Near;
        ImGui.SameLine();
        if (ImGui.RadioButton("遠##hwt",  v == HelloWorldTypeOption.Far))  Overrides.HelloWorldType = HelloWorldTypeOption.Far;
    }

    private void DrawForceButtons()
    {
        if (ImGui.Button("強制接監視"))
        {
            Overrides.ExtraDynamis = true;
            Overrides.HelloWorldOrder = HelloWorldOrderOption.Second;
        }
        ImGui.SameLine();
        if (ImGui.Button("強制接連線"))
        {
            Overrides.ExtraDynamis = true;
            Overrides.HelloWorldOrder = HelloWorldOrderOption.First;
        }
    }
}
