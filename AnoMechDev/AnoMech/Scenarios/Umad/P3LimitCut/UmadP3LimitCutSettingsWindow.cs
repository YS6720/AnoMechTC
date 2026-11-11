using System;
using AnoMech.Core.Game.Party;
using AnoMech.Multiplayer;
using AnoMech.Scenarios.Umad.P3BlackHole;
using Dalamud.Bindings.ImGui;

namespace AnoMech.Scenarios.Umad.P3LimitCut;

public sealed class UmadP3LimitCutSettingsWindow
{
    public UmadP3LimitCutStateOverrides Overrides { get; } = new();

    // Which seat the per-player rows are showing. UI state only; never broadcast.
    private PartyRole editingSeat = PartyRole.MainTank;

    private static readonly string[] SpotLabels = ["隨機", "南", "東南", "東", "東北", "北", "西北", "西", "西南"];
    private static readonly string[] OrderLabels = ["隨機", "順時針", "逆時針"];
    private static readonly string[] NumberLabels = ["隨機", "1", "2", "3", "4", "5", "6", "7", "8"];
    private static readonly string[] WindLabels = ["隨機", "Headwind", "Tailwind"];
    private static readonly string[] BossSpotLabels = ["隨機", "東北", "東南", "西南", "西北"];
    private static readonly int[] BossSpotValues = [3, 1, 7, 5];
    private static readonly string[] BaitLabels =
        ["D3（預設）", "MT", "ST", "H1", "H2", "D1", "D2", "D4"];
    private static readonly PartyRole[] BaitRoles =
    [
        PartyRole.MainTank, PartyRole.OffTank, PartyRole.RegenHealer, PartyRole.ShieldHealer,
        PartyRole.MeleeDpsA, PartyRole.MeleeDpsB, PartyRole.CasterDps,
    ];

    // Solo, the player's own picks sit in this panel; a host assigns seats from the Multiplayer
    // window.
    public void Draw()
    {
        var solo = !PerRole.SeatsActive;
        if (ImGui.Button("自動"))
        {
            ResetAll();
            if (solo) ResetMine();
        }

        if (SettingsGrid.Begin("##p3limitcut"))
        {
            SettingsGrid.Row("第一個分身：");
            var spot = (Overrides.StartSpot ?? -1) + 1;
            SettingsGrid.ItemWidth(140);
            if (ImGui.Combo("##lcstartspot", ref spot, SpotLabels, SpotLabels.Length))
                Overrides.StartSpot = spot == 0 ? null : spot - 1;

            SettingsGrid.Row("分身順序：");
            var order = Overrides.Clockwise switch { true => 1, false => 2, null => 0 };
            SettingsGrid.ItemWidth(160);
            if (ImGui.Combo("##lcorder", ref order, OrderLabels, OrderLabels.Length))
                Overrides.Clockwise = order switch { 1 => true, 2 => false, _ => null };

            SettingsGrid.Row("Boss 定點：");
            var boss = Overrides.BossSpot is { } b ? Array.IndexOf(BossSpotValues, b) + 1 : 0;
            SettingsGrid.ItemWidth(140);
            if (ImGui.Combo("##lcbossspot", ref boss, BossSpotLabels, BossSpotLabels.Length))
                Overrides.BossSpot = boss == 0 ? null : BossSpotValues[boss - 1];

            SettingsGrid.Row("Umbra Smash 誘導：");
            var bait = Overrides.BaitRole is { } r ? Array.IndexOf(BaitRoles, r) + 1 : 0;
            SettingsGrid.ItemWidth(180);
            if (ImGui.Combo("##lcbait", ref bait, BaitLabels, BaitLabels.Length))
                Overrides.BaitRole = bait == 0 ? null : BaitRoles[bait - 1];

            if (solo) DrawPlayerRows();
            SettingsGrid.End();
        }
        // The Thunder III plan is drawn by UmadP3LimitCutScenario.DrawMultiplayerSettings so it
        // stays editable while a multiplayer fight is running, as Black Hole's does.
    }

    public void DrawPerPlayer()
    {
        if (ImGui.Button("自動")) ResetPerPlayer();
        if (SettingsGrid.Begin("##p3limitcutplayers"))
        {
            editingSeat = SettingsGrid.SeatRow("##lcseat", editingSeat);
            DrawPlayerRows();
            SettingsGrid.ForcedRecapRow("已指定號碼：", Overrides.Number);
            SettingsGrid.ForcedRecapRow("已指定風：", Overrides.Wind);
            SettingsGrid.End();
        }
        SettingsGrid.ConflictRows(Overrides.Validate());
    }

    private void DrawPlayerRows()
    {
        var whose = PerRole.SeatsActive ? "" : "你的";

        SettingsGrid.Row($"{whose}號碼：");
        var number = Overrides.Number.Effective(editingSeat) ?? 0;
        SettingsGrid.ItemWidth(140);
        if (ImGui.Combo("##lcnumber", ref number, NumberLabels, NumberLabels.Length))
            Overrides.Number.Set(editingSeat, number == 0 ? null : number);

        SettingsGrid.Row($"{whose}風：");
        var wind = Overrides.Wind.Effective(editingSeat) switch { Wind.Headwind => 1, Wind.Tailwind => 2, _ => 0 };
        SettingsGrid.ItemWidth(140);
        if (ImGui.Combo("##lcwind", ref wind, WindLabels, WindLabels.Length))
            Overrides.Wind.Set(editingSeat, wind switch { 1 => Wind.Headwind, 2 => Wind.Tailwind, _ => null });
    }

    private void ResetAll()
    {
        Overrides.StartSpot = null;
        Overrides.Clockwise = null;
        Overrides.BossSpot = null;
        Overrides.BaitRole = null;
        Overrides.ThunderPlan = ThunderIIIAssignment.MtInvulnsBoth;
    }

    private void ResetPerPlayer()
    {
        Overrides.Number.Clear();
        Overrides.Wind.Clear();
    }

    // Solo's own picks only: a host's seat assignments are kept apart.
    private void ResetMine()
    {
        Overrides.Number.Mine = null;
        Overrides.Wind.Mine = null;
    }

    // Black Hole's planner for the one Thunder III set here (the two hits at ~38.6s and ~41.6s):
    // which tank stands on Exdeath, with an invuln or a swap. Only the host's pick is read.
    public void DrawThunderIIIPlan()
    {
        ImGui.Separator();
        // 我方：上游 Plugin.Multiplayer 的「已連線且不是房主」改看本機 MultiplayerSession。
        var mpGuest = Plugin.GameInstance?.Multiplayer?.Session is { IsHost: false, Phase: not (MultiplayerPhase.Connecting or MultiplayerPhase.Closed) };
        ImGui.TextUnformatted("Thunder III 減傷計畫（AI 坦克照此執行）：");
        ImGui.BeginDisabled(mpGuest);
        ImGui.TextUnformatted("衝鋒之後（約 38.6 秒）：");
        ImGui.SameLine();
        DrawThunderIIIOption("MT 開無敵##lcthunder", ThunderIIIAssignment.MtInvulnsBoth);
        ImGui.SameLine();
        DrawThunderIIIOption("ST 開無敵##lcthunder", ThunderIIIAssignment.OtInvulnsBoth);
        ImGui.SameLine();
        DrawThunderIIIOption("分擔，MT 先##lcthunder", ThunderIIIAssignment.ShareMtFirst);
        ImGui.SameLine();
        DrawThunderIIIOption("分擔，ST 先##lcthunder", ThunderIIIAssignment.ShareOtFirst);
        ImGui.EndDisabled();
        if (mpGuest && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("多人時只採用房主的計畫。");
    }

    private void DrawThunderIIIOption(string label, ThunderIIIAssignment option)
    {
        if (ImGui.RadioButton(label, Overrides.ThunderPlan == option)) Overrides.ThunderPlan = option;
    }
}
