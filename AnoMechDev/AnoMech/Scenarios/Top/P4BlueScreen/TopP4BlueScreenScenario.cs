using System.Collections.Generic;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.SimObjects;
using Dalamud.Bindings.ImGui;

namespace AnoMech.Scenarios.Top.P4BlueScreen;

public sealed class TopP4BlueScreenScenario : IScenario
{
    public string Name => "藍屏";
    public IPhase Phase => TopZone.P4;
    public IReadOnlyList<IScenarioAi> AiStrats { get; } = [new TopP4BlueScreenAi(), new TopP4BlueScreenAi(moogle: true)];
    private bool? playerStackTarget;
    private TopP4BlueScreenMechanics? mechanics;

    public void Run(SimWorld world, int? selectedAi)
    {
        var state = new TopP4BlueScreenState(world.Party, playerStackTarget);
        mechanics = new TopP4BlueScreenMechanics(world, state);
        mechanics.Run();
        if (selectedAi is { } index && index >= 0 && index < AiStrats.Count)
            ((IScenarioAi<TopP4BlueScreenState>)AiStrats[index]).Run(state, world);
    }

    public string SettingsIdentity => $"stack={playerStackTarget?.ToString() ?? "auto"}";
    public bool HasSettings => true;

    public void DrawSettings()
    {
        if (SettingsGrid.Begin("##p4bluescreen"))
        {
            SettingsGrid.Row("分攤點名：");
            if (ImGui.RadioButton("自動##p4stack", playerStackTarget == null)) playerStackTarget = null;
            ImGui.SameLine();
            if (ImGui.RadioButton("每輪有##p4stack", playerStackTarget == true)) playerStackTarget = true;
            ImGui.SameLine();
            if (ImGui.RadioButton("每輪無##p4stack", playerStackTarget == false)) playerStackTarget = false;
            SettingsGrid.End();
        }
        ImGui.TextWrapped("北至南：坦克、遠程、治療、近戰；同側雙點名時，較南的點名者與對側近戰交換。建議 1 倍速練習，不含輸出／減傷檢定。");
        if (mechanics?.Passed is { } passed)
            ImGui.TextUnformatted(passed ? "本輪機制通過" : "本輪有機制失誤");
    }
}
