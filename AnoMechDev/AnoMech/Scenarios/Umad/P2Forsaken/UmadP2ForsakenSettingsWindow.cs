using AnoMech.Core.Game.Party;
using Dalamud.Bindings.ImGui;
using static AnoMech.Scenarios.Umad.UmadConstants;

namespace AnoMech.Scenarios.Umad.P2Forsaken;

// ImGui panel rendered in the main window's "Scenario config" pane when this
// scenario is active. Owns the StateOverrides instance and writes user choices
// into it. See TopP5DeltaSettingsWindow for the canonical shape.
public sealed class UmadP2ForsakenSettingsWindow
{
    private static readonly (string Label, PartyRole Role)[] SupportRoles =
    [
        ("MT", PartyRole.MainTank), ("ST", PartyRole.OffTank),
        ("H1", PartyRole.RegenHealer), ("H2", PartyRole.ShieldHealer),
    ];

    private static readonly (string Label, PartyRole Role)[] DpsRoles =
    [
        ("D1", PartyRole.MeleeDpsA), ("D2", PartyRole.MeleeDpsB),
        ("D3", PartyRole.PhysRangedDps), ("D4", PartyRole.CasterDps),
    ];

    public UmadP2ForsakenStateOverrides Overrides { get; } = new();

    public void Draw()
    {
        if (ImGui.Button("自動")) ResetAll();
        if (SettingsGrid.Begin("##umadp2forsaken"))
        {
            // The two Stack rows name a seat, but the fight picks one player out of a group --
            // there is no per-seat choice to make, the way a number or a tether is.
            SettingsGrid.FightOnlyNote();
#if DEBUG
            DrawFirstEndAttack();
            DrawNewNorth();
#endif
            DrawSupportLockon();
            DrawSupportStack();
            DrawDpsStack();
            SettingsGrid.End();
        }
    }

#if DEBUG
    private void DrawFirstEndAttack()
    {
        var v = Overrides.EndAttacks[0];
        SettingsGrid.Row("第一次 End：");
        if (ImGui.RadioButton("自動##firstend",   v == null))                 Overrides.EndAttacks[0] = null;
        ImGui.SameLine();
        if (ImGui.RadioButton("Future##firstend", v == EndAttack.FuturesEnd)) Overrides.EndAttacks[0] = EndAttack.FuturesEnd;
        ImGui.SameLine();
        if (ImGui.RadioButton("Past##firstend",   v == EndAttack.PastsEnd))   Overrides.EndAttacks[0] = EndAttack.PastsEnd;
    }

    private void DrawNewNorth()
    {
        var v = Overrides.NewNorth;
        SettingsGrid.Row("新北方：");
        if (ImGui.RadioButton("自動##newnorth", v == null)) Overrides.NewNorth = null;
        foreach (var d in Direction.All)
        {
            ImGui.SameLine();
            if (ImGui.RadioButton($"{d.Name()}##newnorth", v == d)) Overrides.NewNorth = d;
        }
    }
#endif

    private void DrawSupportLockon()
    {
        var v = Overrides.SupportLockon;
        SettingsGrid.Row("坦補頭標：");
        if (ImGui.RadioButton("自動##supportlockon",    v == null))                   Overrides.SupportLockon = null;
        ImGui.SameLine();
        if (ImGui.RadioButton("Chariot##supportlockon", v == LockonId.ForsakenChariot)) Overrides.SupportLockon = LockonId.ForsakenChariot;
        ImGui.SameLine();
        if (ImGui.RadioButton("Cone##supportlockon",    v == LockonId.ForsakenCone))    Overrides.SupportLockon = LockonId.ForsakenCone;
    }

    private void DrawSupportStack()
    {
        var v = Overrides.SupportStackRole;
        SettingsGrid.Row("坦補 Stack：");
        if (ImGui.RadioButton("自動##supportstack", v == null)) Overrides.SupportStackRole = null;
        foreach (var (label, role) in SupportRoles)
        {
            ImGui.SameLine();
            if (ImGui.RadioButton($"{label}##supportstack", v == role)) Overrides.SupportStackRole = role;
        }
    }

    private void DrawDpsStack()
    {
        var v = Overrides.DpsStackRole;
        SettingsGrid.Row("DPS Stack：");
        if (ImGui.RadioButton("自動##dpsstack", v == null)) Overrides.DpsStackRole = null;
        foreach (var (label, role) in DpsRoles)
        {
            ImGui.SameLine();
            if (ImGui.RadioButton($"{label}##dpsstack", v == role)) Overrides.DpsStackRole = role;
        }
    }

    private void ResetAll()
    {
#if DEBUG
        Overrides.EndAttacks[0] = null;
        Overrides.NewNorth = null;
#endif
        Overrides.SupportLockon = null;
        Overrides.SupportStackRole = null;
        Overrides.DpsStackRole = null;
    }
}
