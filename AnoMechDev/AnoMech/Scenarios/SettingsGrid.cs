using System.Numerics;
using AnoMech.Core.Game.Party;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace AnoMech.Scenarios;

// Two-column "Label : options" layout for scenario config panels. SizingFixedFit
// makes column 0 fit the widest label, so every option group in column 1 starts at
// the same x. See TopP5DeltaSettingsWindow for the canonical usage.
internal static class SettingsGrid
{
    public static bool Begin(string id) =>
        ImGui.BeginTable(id, 2, ImGuiTableFlags.SizingFixedFit);

    // Widths of the panels' combos, scaled down inside the lobby's compact settings section.
    public static float WidthScale = 1f;

    public static void ItemWidth(float width) => ImGui.SetNextItemWidth(width * WidthScale * ImGuiHelpers.GlobalScale);

    // 我方座位標籤沿用 PartyRole.ShortName()（MT／ST／H1／H2／D1–D4），與小隊列表、多人面板一致。
    public static string RoleLabel(PartyRole role) => role.ShortName();

    // A divider inside the grid: the rows below it are a different kind of setting. Used to
    // split a panel's fight-wide rolls from its per-player ones.
    public static void Section(string label, string? tooltip = null)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.Spacing();
        ImGui.TextDisabled(label);
        if (tooltip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        ImGui.TableSetColumnIndex(1);
        ImGui.Spacing();
    }

    // For a panel with no per-player rows at all. Only worth saying while hosting, where the
    // host would otherwise go looking for a seat picker that isn't there.
    public static void FightOnlyNote()
    {
        if (!PerRole.SeatsActive) return;
        Section("整場共用", "這個場景的每一項設定都是全隊共用的一次抽選，沒有個人分配。");
    }

    // Who the per-player rows below are editing. Every seat keeps its own values, so this only
    // chooses which one is on screen. Drawn while hosting a session, where the eight seats are
    // real and claimed; solo has a single player whose seat follows their job, so the rows just
    // say "you". Returns the seat to edit.
    public static PartyRole SeatRow(string id, PartyRole current)
    {
        var session = Plugin.GameInstance?.Multiplayer?.Session;
        if (!PerRole.SeatsActive || session == null) return current;
        Row("編輯座位：");
        var me = session.Identity?.PeerId;
        var labels = new string[8];
        for (var i = 0; i < 8; i++)
        {
            var role = (PartyRole)i;
            var seat = "AI";
            foreach (var member in session.Members)
            {
                if (member.Role != role) continue;
                seat = member.PeerId == me ? "你" : member.Alias;
                break;
            }
            labels[i] = $"{RoleLabel(role)} — {seat}";
        }
        var idx = (int)current;
        ItemWidth(220);
        if (ImGui.Combo(id, ref idx, labels, labels.Length) && idx is >= 0 and < 8)
            current = (PartyRole)idx;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("下方各列設定的是哪個座位。每個座位各自保存；留在隨機的座位照副本原本的抽選。");
        return current;
    }

    // Read-only recap of every seat a per-player setting is forced for, so the host can see the
    // whole assignment without clicking through eight seats.
    public static void ForcedRecapRow(string label, IPerRoleSetting setting)
    {
        if (!PerRole.SeatsActive || setting.Describe() is not { } text) return;
        Row(label);
        ImGui.TextDisabled(text);
    }

    // Combinations the fight can't produce, shown while the host is still editing rather than
    // left for the run to drop and log. Drawn at the bottom of the panel, after End().
    public static void ConflictRows(SettingsConflicts conflicts)
    {
        if (!conflicts.Any) return;
        foreach (var problem in conflicts.Problems)
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f), $"無法成立：{problem}");
    }

    // Begin a new option row: writes the label in column 0 and leaves the cursor
    // in column 1, ready for the option widgets.
    public static void Row(string label)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.AlignTextToFramePadding();   // vertically center label against the radio buttons
        ImGui.TextUnformatted(label);
        ImGui.TableSetColumnIndex(1);
    }

    // A per-player row. Solo it is one of the scenario panel's own rows, so it starts with a
    // capital; with seats it reads after the seat picker.
    public static void PlayerRow(string label) =>
        Row(PerRole.SeatsActive ? label : char.ToUpperInvariant(label[0]) + label[1..]);

    public static void End() => ImGui.EndTable();
}
