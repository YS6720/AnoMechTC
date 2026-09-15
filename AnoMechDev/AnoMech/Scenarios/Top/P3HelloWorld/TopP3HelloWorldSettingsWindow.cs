using System;
using AnoMech.Core.Game.Party;
using Dalamud.Bindings.ImGui;

namespace AnoMech.Scenarios.Top.P3HelloWorld;

public sealed class TopP3HelloWorldSettingsWindow
{
    private static readonly (TopP3HelloWorldRole Role, string Label)[] StartingRoles =
    [
        (TopP3HelloWorldRole.Defamation, "大圈"),
        (TopP3HelloWorldRole.RemoteTether, "遠線"),
        (TopP3HelloWorldRole.Stack, "分攤"),
        (TopP3HelloWorldRole.LocalTether, "近線"),
    ];
    private TopP3HelloWorldRole? startingRole;
    private TopP3HelloWorldColor? defamationColor;

    public float ContactRadius { get; set; } = TopP3HelloWorldRules.ContactRadiusDefault;

    /// <summary>Identity of the values the next Run consumes. "隨機" is itself a
    /// choice here, so switching to or from it also retires a pending retry.</summary>
    public string Identity => string.Join("|",
        startingRole?.ToString() ?? "random",
        defamationColor?.ToString() ?? "random",
        ContactRadius.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

    public TopP3HelloWorldPattern CreatePattern(PartyRole playerRole)
        => new(playerRole,
            startingRole ?? StartingRoles[Random.Shared.Next(StartingRoles.Length)].Role,
            defamationColor ?? (Random.Shared.Next(2) == 0
                ? TopP3HelloWorldColor.Blue : TopP3HelloWorldColor.Red));

    public void Draw()
    {
        if (SettingsGrid.Begin("##P3HelloWorldPattern"))
        {
            SettingsGrid.Row("起手職責：");
            if (ImGui.RadioButton("隨機##HWStart", startingRole == null)) startingRole = null;
            foreach (var (role, label) in StartingRoles)
            {
                ImGui.SameLine();
                if (ImGui.RadioButton(label, startingRole == role)) startingRole = role;
            }
            SettingsGrid.Row("大圈身上顏色：");
            if (ImGui.RadioButton("隨機##HWColor", defamationColor == null)) defamationColor = null;
            ImGui.SameLine();
            if (ImGui.RadioButton("藍色##HWColor", defamationColor == TopP3HelloWorldColor.Blue))
                defamationColor = TopP3HelloWorldColor.Blue;
            ImGui.SameLine();
            if (ImGui.RadioButton("紅色##HWColor", defamationColor == TopP3HelloWorldColor.Red))
                defamationColor = TopP3HelloWorldColor.Red;
            SettingsGrid.End();
        }
        ImGui.TextDisabled("大圈 → 遠線 → 分攤 → 近線；四種職責皆可起手。");
        ImGui.TextDisabled("塔色依身上顏色；分攤與大圈相反。隨機選項每次開始重抽。");
        var radius = ContactRadius;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderFloat("接毒接觸半徑（練習值）##P3HelloWorld", ref radius,
                TopP3HelloWorldRules.ContactRadiusMin,
                TopP3HelloWorldRules.ContactRadiusMax, "%.1f 碼"))
            ContactRadius = TopP3HelloWorldRules.ClampContactRadius(radius);
        ImGui.TextDisabled("預設 2.0 碼；錄製無法證實遊戲精確閾值。");
        ImGui.TextDisabled($"近線接毒後停留 {TopP3HelloWorldMovementPlan.NearContactHoldSeconds:0.00} 秒，再往大圈中間會合。");
        ImGui.TextDisabled("預備線只作提示；遠線接毒後直接往兩側散開。");
        ImGui.TextDisabled($"斷線距離 {TopP3HelloWorldRules.TetherBreakDistance:0.0} 碼：近線靠近、遠線拉遠即觸發。");
        ImGui.TextDisabled($"啟動後 {TopP3HelloWorldRules.ActiveTetherDuration:0} 秒未斷線才判失敗，不是倒數爆炸。");
        ImGui.TextDisabled("距離沿用既有機制值；錄製快照有延遲，無法視為伺服器精確門檻。");
    }
}
