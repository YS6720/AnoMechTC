using System;
using Dalamud.Bindings.ImGui;

namespace AnoMech.Scenarios.Umad.P5Exaflares;

// Settings panel for the P5 exaflares scenario: pick or randomize each side's column order.
// Mirrors TopP5DeltaSettingsWindow (SettingsGrid two-column layout, "Auto" reset).
public sealed class UmadP5ExaflaresSettingsWindow
{
    public UmadP5ExaflaresStateOverrides Overrides { get; } = new();

    // Index-aligned with the ExaFlareOrder enum (Random = 0, then the six fixed orders).
    private static readonly string[] OrderLabels =
    [
        "隨機",
        "1/4 - 2/5 - 3/6",
        "1/4 - 3/6 - 2/5",
        "2/5 - 1/4 - 3/6",
        "2/5 - 3/6 - 1/4",
        "3/6 - 1/4 - 2/5",
        "3/6 - 2/5 - 1/4",
    ];

    public void Draw()
    {
        if (ImGui.Button("自動"))
        {
            Overrides.LeftOrder = ExaFlareOrder.Random;
            Overrides.RightOrder = ExaFlareOrder.Random;
        }

        if (SettingsGrid.Begin("##p5exaflares"))
        {
            SettingsGrid.FightOnlyNote();
            DrawOrderRow("左側順序：", "##leftorder", Overrides.LeftOrder, v => Overrides.LeftOrder = v);
            DrawOrderRow("右側順序：", "##rightorder", Overrides.RightOrder, v => Overrides.RightOrder = v);
            SettingsGrid.End();
        }
    }

    private static void DrawOrderRow(string label, string id, ExaFlareOrder current, Action<ExaFlareOrder> set)
    {
        SettingsGrid.Row(label);
        var idx = (int)current;
        SettingsGrid.ItemWidth(180);
        if (ImGui.Combo(id, ref idx, OrderLabels, OrderLabels.Length))
            set((ExaFlareOrder)idx);
    }
}
