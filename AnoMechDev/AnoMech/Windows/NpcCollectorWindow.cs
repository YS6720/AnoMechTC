using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;
using AnoMech.Core;

namespace AnoMech.Windows;

/// <summary>
/// 怪物採集器：把遊戲裡實際出現的敵人 id 撈下來。
///
/// 由來（2026-08-19）：模擬器要生出正確的怪，需要 **BNpcBase id**（決定模型），
/// 但 BNpcName→BNpcBase 的對應**不存在於任何離線 dump**
/// （datamining_tc 只有 BNpcName；game_ref.sqlite 沒有這兩張表），
/// 社群資料集又是國際服的。
///
/// 但 Dalamud 的 ObjectTable 直接就有：`IGameObject.DataId` ＝ BNpcBase、
/// `IBattleNpc.NameId` ＝ BNpcName。所以只要**進一次有那隻怪的副本**，
/// 就能把 id 抄下來——不必反編譯、不必猜。
///
/// 用法：開自動採集 → 去跑對應的舊副本（尼德霍格＝蒼天決戰、托爾丹＝最終決戰，
/// 都能單人不同步輾過）→ 回來看清單。模型相同即可用於模擬器。
/// </summary>
public sealed class NpcCollectorWindow : Window
{
    private sealed record Seen(uint BaseId, uint NameId, string Name, uint Territory, byte Level);

    private readonly Dictionary<(uint, uint), Seen> seen = new();
    private bool auto = true;
    private double lastSample;
    private string filter = "";

    public NpcCollectorWindow() : base($"AnoMech {BuildEdition.DisplayName} 怪物採集器###AnoMechNpc")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 300),
            MaximumSize = new Vector2(1400, 1400),
        };
    }

    /// <summary>由 Plugin 的 framework update 呼叫。每秒取樣一次就夠——這只是抄 id，不需要逐幀。</summary>
    public void Tick(float deltaSeconds)
    {
        if (!auto) return;
        lastSample += deltaSeconds;
        if (lastSample < 1.0) return;
        lastSample = 0;
        Sample();
    }

    private void Sample()
    {
        var territory = Plugin.ClientState.TerritoryType;
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IBattleNpc npc) continue;
            // 刻意**不**只收 BattleNpcSubKind.Enemy：機制的 AOE 多半由「隱形 helper」
            // 施放，它們的 kind 未必是 Enemy，濾掉就等於漏掉最關鍵的那批 id。
            var key = (obj.BaseId, npc.NameId);
            if (seen.ContainsKey(key)) continue;
            var entry = new Seen(obj.BaseId, npc.NameId, obj.Name.TextValue, territory, npc.Level);
            seen[key] = entry;
            // 立刻落盤：採集的重點是「跑完副本回來還在」，不能只留在記憶體裡。
            CrashTrace.Log($"[NPC] territory={entry.Territory} BNpcBase={entry.BaseId} BNpcName={entry.NameId} "
                         + $"kind={npc.BattleNpcKind} Lv{entry.Level} 「{entry.Name}」");
        }
    }

    public override void Draw()
    {
        ImGui.Checkbox("自動採集（每秒掃一次 ObjectTable）", ref auto);
        ImGui.SameLine();
        if (ImGui.Button("立刻掃一次")) Sample();
        ImGui.SameLine();
        if (ImGui.Button("清空")) seen.Clear();

        ImGui.TextDisabled($"已採集 {seen.Count} 種敵人。每筆一出現就寫進追蹤檔，跑完副本回來還在。");
        ImGui.TextDisabled("可採集任何副本中實際出現的 NPC 與隱形機制來源；不代表該副本已能模擬。");

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##f", "過濾名稱", ref filter, 64);
        ImGui.Separator();

        if (!ImGui.BeginTable("npc", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("BNpcBase", ImGuiTableColumnFlags.WidthStretch, 1.0f);
        ImGui.TableSetupColumn("BNpcName", ImGuiTableColumnFlags.WidthStretch, 1.0f);
        ImGui.TableSetupColumn("Lv", ImGuiTableColumnFlags.WidthStretch, 0.4f);
        ImGui.TableSetupColumn("territory", ImGuiTableColumnFlags.WidthStretch, 0.7f);
        ImGui.TableSetupColumn("名稱", ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var e in seen.Values.OrderBy(v => v.Territory).ThenBy(v => v.BaseId))
        {
            if (filter.Length > 0 && e.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.Text(e.BaseId.ToString());
            ImGui.TableNextColumn(); ImGui.Text(e.NameId.ToString());
            ImGui.TableNextColumn(); ImGui.Text(e.Level.ToString());
            ImGui.TableNextColumn(); ImGui.Text(e.Territory.ToString());
            ImGui.TableNextColumn(); ImGui.Text(e.Name);
        }
        ImGui.EndTable();
    }
}
