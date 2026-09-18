using System;
using System.Collections.Generic;
using System.Numerics;
using AnoMech.Core.Game.Party;
using AnoMech.Scenarios;
using AnoMech.Scenarios.Top.P3Monitors;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AnoMech.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private readonly IReadOnlyList<IZone> zones;

    public ConfigWindow(Plugin plugin) : base($"AnoMech {BuildEdition.DisplayName} 設定###AnoMechConfig")
    {
        Flags = ImGuiWindowFlags.NoCollapse;

        Size = new Vector2(460, 640);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
        configuration = plugin.Configuration;
        zones = plugin.Game.Zones;
    }

    public void Dispose() { }

    private void DrawPartyListDisplayOrder()
    {
        ImGui.TextUnformatted("練習小隊列表顯示排序");
        ImGui.TextDisabled("只在練習模擬小隊期間移動整列原生列；不交換真人欄位或機制角色。");

        var enabled = configuration.EnablePartyListDisplayOrder;
        if (ImGui.Checkbox("啟用小隊列表顯示排序", ref enabled))
        {
            configuration.EnablePartyListDisplayOrder = enabled;
            configuration.Save();
        }

        if (configuration.PartyListDisplayOrder is not { Length: PartyListDisplayOrderPlanner.RoleCount } order
            || !PartyListDisplayOrderPlanner.IsValidOrder(order))
        {
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.35f, 1f), "排序設定無效，為安全起見不套用。");
            if (ImGui.Button("重設顯示順序"))
            {
                configuration.PartyListDisplayOrder = PartyListDisplayOrderPlanner.CreateDefaultOrder();
                configuration.Save();
            }
            ImGui.TextDisabled($"狀態：{plugin.Game.World.PartyListDisplayOrderStatus}");
            return;
        }

        for (var i = 0; i < order.Length; i++)
        {
            ImGui.PushID($"party-list-order:{i}");
            ImGui.TextUnformatted($"{i + 1}. {TopP3MonitorRules.RoleLabel(order[i])}");
            ImGui.SameLine(240f);
            if (i > 0 && ImGui.Button("↑"))
            {
                (order[i - 1], order[i]) = (order[i], order[i - 1]);
                configuration.Save();
            }
            ImGui.SameLine();
            if (i + 1 < order.Length && ImGui.Button("↓"))
            {
                (order[i], order[i + 1]) = (order[i + 1], order[i]);
                configuration.Save();
            }
            ImGui.PopID();
        }

        if (ImGui.Button("重設顯示順序"))
        {
            configuration.PartyListDisplayOrder = PartyListDisplayOrderPlanner.CreateDefaultOrder();
            configuration.Save();
        }
        ImGui.TextDisabled($"狀態：{plugin.Game.World.PartyListDisplayOrderStatus}");
    }

    private void DrawScenarioVisibility()
    {
        ImGui.TextUnformatted("副本顯示");
        ImGui.TextDisabled("勾選要在主選單顯示的副本（可全部取消）");

        var seenTerritories = new HashSet<uint>();
        foreach (var zone in zones)
        {
            if (!seenTerritories.Add(zone.TerritoryId)) continue;

            var visible = configuration.ScenarioVisibility.IsVisible(zone.TerritoryId);
            ImGui.PushID($"territory:{zone.TerritoryId}");
            if (ImGui.Checkbox(zone.Name, ref visible))
            {
                configuration.ScenarioVisibility.SetVisible(zone.TerritoryId, visible);
                configuration.Save();
            }
            ImGui.PopID();
        }
    }

    public override void Draw()
    {
        var onInn = configuration.OpenSimMenuOnInn;
        if (ImGui.Checkbox("進入旅館時開啟主面板", ref onInn))
        {
            configuration.OpenSimMenuOnInn = onInn;
            configuration.Save();
        }

        var compact = configuration.CompactSimulationControls;
        if (ImGui.Checkbox("模擬時使用精簡操作列", ref compact))
        {
            configuration.CompactSimulationControls = compact;
            configuration.Save();
        }
        ImGui.TextDisabled("操作列保留開始、重試與離開；可隨時打開完整面板與站位工具。");
        var autoRetry = configuration.AutoRetry;
        if (ImGui.Checkbox("成功後自動重試（預設關閉）", ref autoRetry))
        {
            configuration.AutoRetry = autoRetry;
            configuration.Save();
            if (!autoRetry) plugin.Game.CancelPendingRetry();
        }
        ImGui.TextDisabled("只在場景明確完成後重開；死亡、失敗或中斷不會重試。");

        var suppressBgm = configuration.SuppressBgm;
        if (ImGui.Checkbox("關閉模擬背景音樂", ref suppressBgm))
        {
            configuration.SuppressBgm = suppressBgm;
            configuration.Save();
        }

        var showRanges = configuration.ShowHitRanges;
        if (ImGui.Checkbox("顯示判定範圍（練習用）", ref showRanges))
        {
            configuration.ShowHitRanges = showRanges;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("把每次傷害判定實際用到的範圍畫成外框，判定當下顯示 2 秒：\n" +
                "有人被判中是紅色、沒人中是黃色。原本就有地面預警的招式不受影響，\n" +
                "只是多一層外框；沒有預警的招式這樣才看得到真正的判定範圍。");

        ImGui.Separator();
        DrawScenarioVisibility();

        ImGui.Separator();
        DrawPartyListDisplayOrder();

        ImGui.Separator();

        if (BuildEdition.IsDeveloper)
        {
            // 錄影相關：進真副本時聊天視窗會被一起錄進去，預設不輸出。
            var quiet = configuration.SuppressChatOutput;
            if (ImGui.Checkbox("插件訊息不進聊天視窗（錄影用）", ref quiet))
            {
                configuration.SuppressChatOutput = quiet;
                configuration.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("只關「顯示」，內容照樣寫進 anomech-trace.log，診斷不受影響。");

            var autoRec = configuration.AutoRecordInDuty;
            if (ImGui.Checkbox("進副本自動錄製", ref autoRec))
            {
                configuration.AutoRecordInDuty = autoRec;
                configuration.Save();
            }

            ImGui.Separator();

            var logging = configuration.EnableEventLogging;
            if (ImGui.Checkbox("記錄模擬事件", ref logging))
            {
                configuration.EnableEventLogging = logging;
                configuration.Save();
                if (logging) Plugin.LogManager?.Open();
                else Plugin.LogManager?.Close();
            }
            ImGui.SameLine();
            if (ImGui.Button("開啟事件紀錄資料夾"))
                Plugin.LogManager?.OpenLogsFolder();
        }
    }
}
