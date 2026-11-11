using System;
using AnoMech.Core.Game.Party;
using AnoMech.Multiplayer;
using AnoMech.Scenarios.Top;
using Dalamud.Bindings.ImGui;
using static AnoMech.Scenarios.Umad.UmadConstants;

namespace AnoMech.Scenarios.Umad.P3BlackHole;

// ImGui panel rendered in the main window's "Scenario config" pane when this scenario is
// active. Owns the StateOverrides instance and writes user choices into it.
public sealed class UmadP3BlackHoleSettingsWindow
{
    public UmadP3BlackHoleStateOverrides Overrides { get; } = new();

    // Which seat the per-player rows are showing. UI state only; never broadcast.
    private PartyRole editingSeat = PartyRole.MainTank;

    // Solo, the player's own picks sit in this panel; a host assigns seats from the Multiplayer
    // window.
    public void Draw()
    {
        var solo = !PerRole.SeatsActive;
#if !DEBUG
        if (!solo)
        {
            ImGui.TextDisabled("多人時這裡全是個人設定，請在個人設定逐座位指定。");
            return;
        }
#endif
        if (ImGui.Button("自動"))
        {
#if DEBUG
            Overrides.SlapAttacks[0] = null;
#endif
            if (solo) ResetMine();
        }
        if (SettingsGrid.Begin("##umadp3blackhole"))
        {
            if (solo)
            {
                DrawLineNumber();
                DrawAccretion();
            }
#if DEBUG
            DrawFirstSlap();
            if (solo) DrawFirstSlapTarget();
#endif
            SettingsGrid.End();
        }
    }

    public void DrawPerPlayer()
    {
        if (ImGui.Button("自動")) ResetPerPlayer();
        if (SettingsGrid.Begin("##umadp3blackholeplayers"))
        {
            editingSeat = SettingsGrid.SeatRow("##bhseat", editingSeat);
            DrawLineNumber();
            DrawAccretion();
#if DEBUG
            DrawFirstSlapTarget();
#endif
            SettingsGrid.ForcedRecapRow("已指定順位：", Overrides.LineNumber);
            SettingsGrid.ForcedRecapRow("已指定 Accretion：", Overrides.Accretion);
            SettingsGrid.End();
        }
        SettingsGrid.ConflictRows(Overrides.Validate());
    }

    // Forces the seat into the slot carrying that line number (Auto = the fight's own roll).
    private void DrawLineNumber()
    {
        var v = Overrides.LineNumber.Effective(editingSeat);
        SettingsGrid.PlayerRow("順位：");
        if (ImGui.RadioButton("自動##line",   v == null)) Overrides.LineNumber.Set(editingSeat, null);
        ImGui.SameLine();
        if (ImGui.RadioButton("第一##line",  v == 1))    Overrides.LineNumber.Set(editingSeat, 1);
        ImGui.SameLine();
        if (ImGui.RadioButton("第二##line", v == 2))    Overrides.LineNumber.Set(editingSeat, 2);
        ImGui.SameLine();
        if (ImGui.RadioButton("第三##line",  v == 3))    Overrides.LineNumber.Set(editingSeat, 3);
    }

    // Yes is dropped for tanks and third-in-line, which never carry Accretion in the fight.
    private void DrawAccretion()
    {
        var v = Overrides.Accretion.Effective(editingSeat);
        SettingsGrid.PlayerRow("Accretion：");
        if (ImGui.RadioButton("自動##accretion", v == null))  Overrides.Accretion.Set(editingSeat, null);
        ImGui.SameLine();
        if (ImGui.RadioButton("是##accretion",  v == true))  Overrides.Accretion.Set(editingSeat, true);
        ImGui.SameLine();
        if (ImGui.RadioButton("否##accretion",   v == false)) Overrides.Accretion.Set(editingSeat, false);
    }

#if DEBUG
    private void DrawFirstSlap()
    {
        var v = Overrides.SlapAttacks[0];
        SettingsGrid.Row("第一次 Slap：");
        if (ImGui.RadioButton("自動##firstslap",  v == null))                     Overrides.SlapAttacks[0] = null;
        ImGui.SameLine();
        if (ImGui.RadioButton("左##firstslap",  v == ActionId.SlapHappy_Left))  Overrides.SlapAttacks[0] = ActionId.SlapHappy_Left;
        ImGui.SameLine();
        if (ImGui.RadioButton("右##firstslap", v == ActionId.SlapHappy_Right)) Overrides.SlapAttacks[0] = ActionId.SlapHappy_Right;
    }

    private void DrawFirstSlapTarget()
    {
        var v = Overrides.FirstSlapAllOnMe.Effective(editingSeat);
        SettingsGrid.Row(PerRole.SeatsActive ? "第一次 Slap 指定座位：" : "第一次 Slap 目標：");
        if (ImGui.RadioButton("自動##firstslaptarget", v != true)) Overrides.FirstSlapAllOnMe.Set(editingSeat, null);
        ImGui.SameLine();
        if (ImGui.RadioButton($"{(PerRole.SeatsActive ? "此座位" : "玩家")}##firstslaptarget", v == true))
            Overrides.FirstSlapAllOnMe.Set(editingSeat, true);
    }
#endif

    // Only matters when a tank slot is bot-driven. Two rows: the sim casts Thunder III twice.
    // Drawn from DrawMultiplayerSettings so it stays editable while Multiplayer is open.
    public void DrawThunderIIIPlan()
    {
        ImGui.Separator();
        // Only the host's plan is read.
        // 我方：上游 Plugin.Multiplayer 的「已連線且不是房主」改看本機 MultiplayerSession。
        var mpGuest = Plugin.GameInstance?.Multiplayer?.Session is { IsHost: false, Phase: not (MultiplayerPhase.Connecting or MultiplayerPhase.Closed) };
        ImGui.TextUnformatted("Thunder III 減傷計畫（AI 坦克照此執行）：");
        ImGui.BeginDisabled(mpGuest);
        DrawThunderIIIRow("##thunder1", "第 1 組（約 42.6 秒）：", Overrides.ThunderSet1, Overrides.ThunderSet2, v => Overrides.ThunderSet1 = v);
        DrawThunderIIIRow("##thunder2", "第 2 組（約 83.9 秒）：", Overrides.ThunderSet2, Overrides.ThunderSet1, v => Overrides.ThunderSet2 = v);
        ImGui.EndDisabled();
        if (mpGuest && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("多人時只採用房主的計畫。");
    }

    // Fight-wide and read only by the YAN(211) strat; drawn beside the Thunder III plan so a host
    // can still set it while Multiplayer is open.
    public void DrawYanMarkers()
    {
        var mpGuest = Plugin.GameInstance?.Multiplayer?.Session is { IsHost: false, Phase: not (MultiplayerPhase.Connecting or MultiplayerPhase.Closed) };
        ImGui.BeginDisabled(mpGuest);
        if (SettingsGrid.Begin("##umadp3blackholeyan"))
        {
            var v = Overrides.Markers;
            SettingsGrid.Row("YAN(211) 標記：");
            if (ImGui.RadioButton("系統標記##yanmarks", v == MarkerMode.System)) Overrides.Markers = MarkerMode.System;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("約 10 秒時由插件依 buff 隨機下隊伍標記：I＝攻擊1～3、II＝鎖鏈1～3、III＝禁止1～2。");
            ImGui.SameLine();
            if (ImGui.RadioButton("手動標記##yanmarks", v == MarkerMode.Manual)) Overrides.Markers = MarkerMode.Manual;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("插件不下標記，約 25 秒時讀玩家自己下的標記。\n標記缺漏、標在 buff 不符的人身上時忽略那一筆，由同 buff 未被標到的人補上。");
            SettingsGrid.End();
        }
        ImGui.EndDisabled();
        if (mpGuest && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("多人時只採用房主的設定。");
    }

    private static void DrawThunderIIIRow(string idSuffix, string label, ThunderIIIAssignment current, ThunderIIIAssignment otherSet, Action<ThunderIIIAssignment> set)
    {
        ImGui.TextUnformatted(label);
        ImGui.SameLine();
        DrawThunderIIIOption("MT 兩次都開無敵" + idSuffix, ThunderIIIAssignment.MtInvulnsBoth, current, otherSet, set);
        ImGui.SameLine();
        DrawThunderIIIOption("ST 兩次都開無敵" + idSuffix, ThunderIIIAssignment.OtInvulnsBoth, current, otherSet, set);
        ImGui.SameLine();
        DrawThunderIIIOption("分擔，MT 先" + idSuffix, ThunderIIIAssignment.ShareMtFirst, current, otherSet, set);
        ImGui.SameLine();
        DrawThunderIIIOption("分擔，ST 先" + idSuffix, ThunderIIIAssignment.ShareOtFirst, current, otherSet, set);
    }

    // Invuln and Share are separate pools: neither an invuln nor the big self-mit cooldowns
    // recover in the ~41s between sets.
    private static void DrawThunderIIIOption(string label, ThunderIIIAssignment option, ThunderIIIAssignment current, ThunderIIIAssignment otherSet, Action<ThunderIIIAssignment> set)
    {
        var isShare = option is ThunderIIIAssignment.ShareMtFirst or ThunderIIIAssignment.ShareOtFirst;
        var otherIsShare = otherSet is ThunderIIIAssignment.ShareMtFirst or ThunderIIIAssignment.ShareOtFirst;
        var blocked = isShare ? otherIsShare : option == otherSet;
        if (blocked) ImGui.BeginDisabled();
        if (ImGui.RadioButton(label, current == option) && !blocked) set(option);
        if (blocked)
        {
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(isShare
                    ? "兩名坦克已在另一組分擔——大減傷（120 秒／90 秒）在約 41 秒內轉不好，誰都撐不到需要的減傷量。"
                    : "這名坦克已在另一組開無敵——無敵在約 41 秒內轉不好，兩次不能都由他開。");
        }
    }

    private void ResetPerPlayer()
    {
        Overrides.LineNumber.Clear();
        Overrides.Accretion.Clear();
        Overrides.FirstSlapAllOnMe.Clear();
    }

    // Solo's own picks only: a host's seat assignments are kept apart.
    private void ResetMine()
    {
        Overrides.LineNumber.Mine = null;
        Overrides.Accretion.Mine = null;
        Overrides.FirstSlapAllOnMe.Mine = null;
    }
}
