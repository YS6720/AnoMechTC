using System;
using AnoMech.Core.Game.Party;
using Dalamud.Bindings.ImGui;

namespace AnoMech.Scenarios.Umad.P5Flood;

// Settings panel for the P5 Flood scenario: pick or randomize each diagonal's march direction.
// Mirrors UmadP5ExaflaresSettingsWindow.
public sealed class UmadP5FloodSettingsWindow
{
    public UmadP5FloodStateOverrides Overrides { get; } = new();

    // Index-aligned with the FloodDirection enum.
    private static readonly string[] DirectionLabels = ["隨機", "正向", "反向"];

    private static readonly string[] FirstLabels = ["隨機", "東北－西南", "西北－東南"];

    // Index-aligned with AnchorRoles below (0 = a fresh random non-tank every tick).
    private static readonly string[] AnchorLabels =
        ["每次隨機", "H1", "H2", "D1", "D2", "D3", "D4"];
    private static readonly PartyRole[] AnchorRoles =
    [
        PartyRole.RegenHealer, PartyRole.ShieldHealer,
        PartyRole.MeleeDpsA, PartyRole.MeleeDpsB, PartyRole.PhysRangedDps, PartyRole.CasterDps,
    ];

    // Index-aligned with UmadP5FloodState.StartQuadrant; "Random" = derive it from the line rolls.
    private static readonly string[] QuadrantLabels = ["隨機", "北", "東", "南", "西"];
    private static readonly string[] RotationLabels = ["隨機", "順時針", "逆時針"];

    public void Draw()
    {
        if (ImGui.Button("自動"))
        {
            Overrides.LineNeSw = FloodDirection.Random;
            Overrides.LineNwSe = FloodDirection.Random;
            Overrides.NeSwFirst = null;
            Overrides.AnchorRole = null;
            Overrides.StartQuadrant = null;
            Overrides.RotationClockwise = null;
        }

        if (SettingsGrid.Begin("##p5flood"))
        {
            SettingsGrid.FightOnlyNote();
            DrawDirectionRow("東北－西南線：", "##nesw", Overrides.LineNeSw, v => Overrides.LineNeSw = v);
            DrawDirectionRow("西北－東南線：", "##nwse", Overrides.LineNwSe, v => Overrides.LineNwSe = v);

            SettingsGrid.Row("先行線：");
            var idx = Overrides.NeSwFirst switch { true => 1, false => 2, null => 0 };
            SettingsGrid.ItemWidth(140);
            if (ImGui.Combo("##firstline", ref idx, FirstLabels, FirstLabels.Length))
                Overrides.NeSwFirst = idx switch { 1 => true, 2 => false, _ => null };

            SettingsGrid.Row("Stack 目標：");
            var anchorIdx = Overrides.AnchorRole is { } r ? Array.IndexOf(AnchorRoles, r) + 1 : 0;
            SettingsGrid.ItemWidth(140);
            if (ImGui.Combo("##anchorrole", ref anchorIdx, AnchorLabels, AnchorLabels.Length))
                Overrides.AnchorRole = anchorIdx == 0 ? null : AnchorRoles[anchorIdx - 1];

            SettingsGrid.Row("起始象限：");
            var quadIdx = (Overrides.StartQuadrant ?? -1) + 1;
            SettingsGrid.ItemWidth(140);
            if (ImGui.Combo("##startquadrant", ref quadIdx, QuadrantLabels, QuadrantLabels.Length))
                Overrides.StartQuadrant = quadIdx == 0 ? null : quadIdx - 1;

            SettingsGrid.Row("轉動方向：");
            var rotIdx = Overrides.RotationClockwise switch { true => 1, false => 2, null => 0 };
            SettingsGrid.ItemWidth(160);
            if (ImGui.Combo("##rotationdir", ref rotIdx, RotationLabels, RotationLabels.Length))
                Overrides.RotationClockwise = rotIdx switch { 1 => true, 2 => false, _ => null };


            SettingsGrid.End();
        }
    }

    private static void DrawDirectionRow(string label, string id, FloodDirection current, Action<FloodDirection> set)
    {
        SettingsGrid.Row(label);
        var idx = (int)current;
        SettingsGrid.ItemWidth(140);
        if (ImGui.Combo(id, ref idx, DirectionLabels, DirectionLabels.Length))
            set((FloodDirection)idx);
    }
}
