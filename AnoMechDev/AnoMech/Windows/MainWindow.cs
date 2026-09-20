using System;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;
using AnoMech.Core.Map;
using AnoMech.Core.Game;
using AnoMech.Core;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;
using AnoMech.Scenarios;
using AnoMech.Scenarios.Top.P3Monitors;
using AnoMech.Scenarios.Top.P3Practice;
using AnoMech.Scenarios.Top.P3Intermission;
using AnoMech.Scenarios.Top.P3HelloWorld;
using static AnoMech.Core.Game.Game;

namespace AnoMech.Windows;

public unsafe class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly MultiplayerPanel multiplayerPanel;
    private bool _leftPanelOpen = true;
    internal IScenario? SelectedScenario => _selectedScenario;
    private IScenario? _selectedScenario;

    internal PartyRole? SelectedRoleOverride => _roleOverride;
    private PartyRole? _roleOverride;

    // Index into the selected scenario's AiStrats; reset to the first strat whenever the
    // selected scenario changes. Passed to RunScenario as selectedAi on a (non-solo) Start.
    // Empty AiStrats uses the built-in default at index 0.
    internal int SelectedStrat => _selectedStrat;
    private int _selectedStrat;

    // Index into the selected scenario's WaymarkPresets; ignored when it has none.
    internal int SelectedWaymark => _selectedWaymark;
    private int _selectedWaymark;

    // Stable progress key selected for this scenario. Non-progress scenarios expose "full".
    internal string SelectedProgressKey => _selectedProgressKey;
    private string _selectedProgressKey = "full";

    // Cached on scenario selection; every label retains its original AiStrats index.
    private string[] _stratLabels = [];

    // Presentation only: indices remain the original PartyRole slots.
    private static readonly string[] RoleLabels =
        ["自動", PartyRole.MainTank.ShortName(), PartyRole.OffTank.ShortName(),
         PartyRole.RegenHealer.ShortName(), PartyRole.ShieldHealer.ShortName(),
         PartyRole.MeleeDpsA.ShortName(), PartyRole.MeleeDpsB.ShortName(),
         PartyRole.PhysRangedDps.ShortName(), PartyRole.CasterDps.ShortName()];

    // <Version> from AnoMech.csproj flows into the assembly version; surface it in the
    // title bar. Use a ### id so the window identity stays "MainWindow" across versions.
    private static string TitleWithVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        var version = v is null ? "" : $" v{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        return $"AnoMech {BuildEdition.DisplayName}{version}###MainWindow";
    }

    public MainWindow(Plugin plugin)
        : base(TitleWithVersion())
    {
        // 固定邏輯尺寸，避免分頁內容改變時自動收縮；由 Dalamud 套用 UI 縮放。
        Size = new Vector2(800, 600);
        SizeCondition = ImGuiCond.Always;
        Flags |= ImGuiWindowFlags.NoResize;

        this.plugin = plugin;
        multiplayerPanel = new MultiplayerPanel(plugin);
        IsOpen = false;

        // Small gear in the title bar opens the settings window (same toggle as /anomech config).
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            IconOffset = new Vector2(2f, 1f),
            Click = _ => plugin.ToggleConfigUi(),
            ShowTooltip = () => ImGui.SetTooltip("設定"),
        });
    }

    public void Dispose() { }

    private bool _wasInInstance;
    private bool fullPanelRequested;

    internal void TogglePanel()
    {
        if (plugin.Game.World.Map.IsInInstance && plugin.Configuration.CompactSimulationControls)
        {
            fullPanelRequested = !fullPanelRequested;
            IsOpen = fullPanelRequested;
        }
        else
            Toggle();
    }

    // 模擬中由精簡操作列常駐提供 Retry／Leave；完整面板只在明示開啟時顯示。
    // 關閉精簡偏好時，沿用原本固定展開完整面板的行為。
    public override void PreOpenCheck()
    {
        var inInstance = plugin.Game.World.Map.IsInInstance;
        if (inInstance)
        {
            if (plugin.Configuration.CompactSimulationControls && !fullPanelRequested)
            {
                IsOpen = false;
                _wasInInstance = true;
                return;
            }
            IsOpen = true;
            ShowCloseButton = false;
            RespectCloseHotkey = false;
            Flags |= ImGuiWindowFlags.NoCollapse;
            if (!_wasInInstance)
            {
                Collapsed = false;
                CollapsedCondition = ImGuiCond.Always;
            }
        }
        else
        {
            ShowCloseButton = true;
            RespectCloseHotkey = true;
            Flags &= ~ImGuiWindowFlags.NoCollapse;
            if (_wasInInstance)
            {
                IsOpen = true;
                fullPanelRequested = false;
                CollapsedCondition = ImGuiCond.FirstUseEver;
            }
        }
        _wasInInstance = inInstance;
    }

    // 分頁：練習／多人連線／連戰／開發工具。多人連線以前是折疊區塊擠在最上方，
    // 場景選單與設定要往下捲；改成分頁後每頁只做一件事，狀態帶常駐不受分頁影響。
    public override void Draw()
    {
        DrawInInstanceReloadWarning();
        DrawStatusStrip();
        if (!ImGui.BeginTabBar("##main-tabs", ImGuiTabBarFlags.None)) return;
        if (ImGui.BeginTabItem("練習"))
        {
            DrawPracticeTab();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(plugin.Multiplayer.HasSession ? "多人連線 ●" : "多人連線"))
        {
            multiplayerPanel.Draw();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(plugin.Configuration.ChainEnabled ? "連戰 ●" : "連戰"))
        {
            DrawChainControls(plugin.Game);
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private void DrawPracticeTab()
    {
        var leftWidth = _leftPanelOpen ? ScenarioPanelWidth() : 30f;
        if (!ImGui.BeginTable("##layout", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit)) return;
        ImGui.TableSetupColumn("##left", ImGuiTableColumnFlags.WidthFixed, leftWidth);
        ImGui.TableSetupColumn("##right", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        DrawScenariosPanel();
        ImGui.TableSetColumnIndex(1);
        DrawMainContent();
        ImGui.EndTable();
    }

    // 常駐一行：場景狀態｜目前場景｜連勝｜多人房間。任何分頁都看得到，不用切回去確認。
    private void DrawStatusStrip()
    {
        var game = plugin.Game;
        var (label, color) = ScenarioStateLabel(game);
        ImGui.TextColored(color, label);
        if (game.ActiveScenario is { } active)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("｜");
            ImGui.SameLine();
            ImGui.TextUnformatted(Game.DisplayName(active));
        }
        if (game.ConsecutiveWins > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"｜連勝 {game.ConsecutiveWins}");
        }
        if (plugin.Multiplayer.HasSession && plugin.Multiplayer.Session is { } session)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("｜");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f),
                $"多人：{(session.IsHost ? "房主" : "成員")}・{session.Members.Count} 人・{session.Phase}");
        }
        else if (plugin.Multiplayer.IsConnecting)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("｜多人：連線中…");
        }
        ImGui.Separator();
    }

    private static (string Label, Vector4 Color) ScenarioStateLabel(Game game) => game.ScenarioState switch
    {
        GameScenarioState.Preparing => ("準備中", new Vector4(1f, 0.8f, 0.2f, 1f)),
        GameScenarioState.Running => ("執行中", new Vector4(0.4f, 1f, 0.6f, 1f)),
        GameScenarioState.Paused => ("已暫停", new Vector4(1f, 0.8f, 0.2f, 1f)),
        GameScenarioState.Completed => ("已完成", new Vector4(0.4f, 1f, 0.6f, 1f)),
        GameScenarioState.Failed => ("已失誤", new Vector4(1f, 0.35f, 0.35f, 1f)),
        _ => ("未開始", new Vector4(0.7f, 0.7f, 0.7f, 1f)),
    };

    // 場景進行中卸載插件會走 ZoneSession.Revert(dispose:true)，那條路徑跳過所有安全延遲
    // （位置還原、Occupied 解除、防火牆關閉全部同一幀做完），與正常 Leave 不等價。
    //
    // ⚠️ 這是提醒不是硬擋：插件無法否決 Dalamud 的 reload／停用——`/xlplugins` 按下去
    // 直接呼叫 Dispose()，沒有任何 hook 點可以取消或延後。能做的只有讓使用者在按之前看見。
    internal void DrawInInstanceReloadWarning()
    {
        if (!plugin.Game.World.Map.IsInInstance) return;

        ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f),
            "請先離開場景；更新插件請重啟遊戲，勿熱重新載入。");
        ImGui.Separator();
    }

    private bool IsZoneVisible(IZone zone)
        => plugin.Configuration.ScenarioVisibility.IsVisible(zone.TerritoryId);

    // Size the left panel to the widest visible scenario label so names never clip as scenarios are added.
    private float ScenarioPanelWidth()
    {
        var style = ImGui.GetStyle();
        var widest = 0f;
        foreach (var zone in plugin.Game.Zones)
        {
            if (!IsZoneVisible(zone)) continue;
            widest = Math.Max(widest, ImGui.CalcTextSize(zone.Name).X);
            foreach (var phase in plugin.Game.PhasesOf(zone))
                foreach (var scenario in plugin.Game.ScenariosOf(phase))
                    widest = Math.Max(widest, ImGui.CalcTextSize(DisplayName(scenario)).X);
        }
        var measured = widest + style.FramePadding.X * 2 + style.CellPadding.X * 2;
        return Math.Max(180f, measured);
    }

    private void DrawScenariosPanel()
    {
        if (_leftPanelOpen)
        {
            ImGui.TextUnformatted("副本場景");
            ImGui.SameLine();
            if (ImGui.SmallButton("<##collapse")) _leftPanelOpen = false;
            ImGui.Separator();

            var anyVisibleZone = false;
            foreach (var zone in plugin.Game.Zones)
            {
                if (!IsZoneVisible(zone)) continue;
                anyVisibleZone = true;
                if (!ImGui.CollapsingHeader(zone.Name, ImGuiTreeNodeFlags.DefaultOpen)) continue;
                ImGui.Indent();
                foreach (var phase in plugin.Game.PhasesOf(zone))
                    foreach (var scenario in plugin.Game.ScenariosOf(phase))
                    {
                        var selected = _selectedScenario == scenario;
                        if (selected) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive));
                        ImGui.PushID(scenario.Name);
                        if (ImGui.Button(DisplayName(scenario), new Vector2(-1, 0)))
                            SelectScenario(scenario);
                        ImGui.PopID();
                        if (selected) ImGui.PopStyleColor();
                    }
                ImGui.Unindent();
            }

            if (!anyVisibleZone)
                ImGui.TextDisabled("沒有顯示中的副本，請從齒輪設定開啟。");
        }
        else
        {
            if (ImGui.Button(">##expand")) _leftPanelOpen = true;
        }
    }

    // Select a scenario and reset its per-scenario progress, strat and waymark.
    private void SelectScenario(IScenario scenario)
    {
        _selectedScenario = scenario;
        _selectedProgressKey = scenario is IProgressScenario progress && progress.Progresses.Count > 0
            ? progress.Progresses[0].Key : "full";
        _selectedStrat = 0;
        _selectedWaymark = 0;
        _stratLabels = BuildStratLabels(scenario.AiStrats);
    }

    private static string[] BuildStratLabels(IReadOnlyList<IScenarioAi> strats)
    {
        var labels = new string[strats.Count];
        for (var i = 0; i < strats.Count; i++)
        {
            var name = strats[i].Name;
            labels[i] = name;
            if (strats[i].Group is not { } group) continue;
            for (var j = 0; j < strats.Count; j++)
                if (i != j && strats[j].Name == name)
                {
                    labels[i] = $"{name}（{group}）";
                    break;
                }
        }
        return labels;
    }

    private void DrawMainContent()
    {
        if (_selectedScenario == null)
        {
            ImGui.TextDisabled("請先選擇場景；列在選單不代表已通過副本認證。");
            return;
        }

        var game = plugin.Game;
        var scenarioVisible = IsZoneVisible(_selectedScenario.Phase.Zone);

        ImGui.TextUnformatted(FullName(_selectedScenario));
        ImGui.Separator();
        if (!scenarioVisible)
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f),
                "此副本已在設定中隱藏；請從齒輪設定重新開啟。");
        DrawLocationHint();

        DrawProgressSelector();
        DrawRoleSelector();
        DrawStratSelector();
        DrawWaymarkSelector();

        DrawFirewallStatus(game);
        DrawScenarioStatus(game);
        DrawRunControls();

        // A scenario without its own panel would otherwise draw an empty collapsing
        // header; DrawSettings is a no-op by default, so ask the scenario directly.
        if (_selectedScenario.HasSettings)
        {
            ImGui.Spacing();
            if (ImGui.CollapsingHeader("場景設定", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Indent();
                ImGui.BeginDisabled(plugin.Multiplayer.HasSession && plugin.Multiplayer.Session?.IsHost != true);
                _selectedScenario.DrawSettings();
                ImGui.EndDisabled();
                ImGui.Unindent();
            }
        }

    }

    private static void DrawScenarioStatus(Game game)
    {
        var (label, color) = ScenarioStateLabel(game);
        ImGui.TextColored(color, $"狀態：{label}");
        if (game.ActiveProgressKey is { } progressKey)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"進度：{progressKey}");
        }
    }

    // Both the full panel and the compact bar use this exact control path.
    internal void DrawRunControls()
    {
        var game = plugin.Game;
        var inInn = ZoneSession.IsInInn();
        var busy = ZoneSession.IsPlayerBusy();
        var visible = _selectedScenario is not null && IsZoneVisible(_selectedScenario.Phase.Zone);
        var ready = inInn && !busy;
        var canStart = visible && ready && HasStartableStrat() && !plugin.Multiplayer.IsConnecting
            && (!plugin.Multiplayer.HasSession || plugin.Multiplayer.CanStart
                || plugin.Multiplayer.CanManualRetry);
        ImGui.BeginDisabled(!canStart);
        if (ImGui.Button("開始") && _selectedScenario is { } scenario)
            game.RunScenario(scenario, _roleOverride, _selectedStrat, _selectedWaymark, _selectedProgressKey);
        ImGui.EndDisabled();
        if (!canStart && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(_selectedScenario is null ? "請先從完整面板選擇場景。"
                : !visible ? "此副本已隱藏，請從設定重新開啟。"
                : !inInn ? "請從旅館、房屋內部或公寓啟動。"
                : busy ? "角色忙碌中，請先結束過場、對話、製作或換區。"
                : plugin.Multiplayer.IsConnecting ? "正在準備或清理多人連線，請稍候。"
                : plugin.Multiplayer.HasSession && plugin.Multiplayer.Session?.IsHost != true
                    ? "只有房主可以開始或切換場景。"
                : plugin.Multiplayer.HasSession ? "房間正在開始或結束一輪，請稍候。"
                : "請先選擇可用的打法。");
        if (game.World.Map.IsInInstance)
        {
            // No 重設 inside the instance: it ends the loaded run (and in a session it
            // ends everyone's), leaving the room parked in the lobby. The in-duty verb
            // is 重試 — same room, same connection, next round.
            ImGui.SameLine();
            var canRetry = game.CanRetryActiveRun;
            ImGui.BeginDisabled(!canRetry);
            if (ImGui.Button("重試")) game.RetryActiveRun();
            ImGui.EndDisabled();
            if (!canRetry && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(plugin.Multiplayer.HasSession && plugin.Multiplayer.Session?.IsHost != true
                    ? "只有房主可以重開這一輪。"
                    : "目前沒有進行中的場景；請先按「開始」。");
            ImGui.SameLine();
            if (ImGui.Button("離開場景")) game.Leave();
            if (game.World.PracticePositions.IsActive)
            {
                ImGui.SameLine();
                ImGui.BeginDisabled(plugin.Multiplayer.HasSession && plugin.Multiplayer.Session?.IsHost != true);
                if (ImGui.Button(game.Paused ? "繼續" : "暫停"))
                {
                    if (plugin.Multiplayer.HasSession) plugin.Multiplayer.SetPaused(!game.Paused);
                    else game.Paused = !game.Paused;
                }
                ImGui.EndDisabled();
            }
        }
        else
        {
            ImGui.SameLine();
            if (ImGui.Button("重設")) game.Reset();
        }
        if (_selectedScenario is { SupportsSolo: true } soloScenario)
        {
            ImGui.SameLine();
            ImGui.BeginDisabled(!visible || !ready || plugin.Multiplayer.HasSession || plugin.Multiplayer.IsConnecting);
            if (ImGui.Button("不帶隊友"))
                game.RunScenario(soloScenario, _roleOverride, selectedAi: null,
                    selectedWaymark: _selectedWaymark, progressKey: _selectedProgressKey);
            ImGui.EndDisabled();
        }
        var god = game.GodMode;
        ImGui.BeginDisabled(plugin.Multiplayer.HasSession
            && (!plugin.Multiplayer.CanStart));
        if (ImGui.Checkbox("無敵練習（仍顯示失誤）", ref god)) game.GodMode = god;
        ImGui.EndDisabled();
        var autoRetry = plugin.Configuration.AutoRetry;
        ImGui.BeginDisabled(plugin.Multiplayer.HasSession && plugin.Multiplayer.Session?.IsHost != true);
        if (ImGui.Checkbox("成功後自動重試", ref autoRetry))
        {
            plugin.Configuration.AutoRetry = autoRetry;
            plugin.Configuration.Save();
            if (!autoRetry) game.CancelPendingRetry();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled("僅在明確成功後重開；失敗不會自動重試。");
        if (plugin.Configuration.ChainEnabled)
            ImGui.TextDisabled($"連戰已開（{plugin.Configuration.Chain.Count} 場，到「連戰」分頁調整）");
        if (game.ConsecutiveWins > 0)
        {
            ImGui.TextDisabled($"連勝：{game.ConsecutiveWins}");
            if (game.HasPendingRetry)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"下一輪 {Math.Max(0f, game.RetryRemaining):0.0}s");
            }
        }
        if (game.Paused) ImGui.TextDisabled("模擬已暫停；可繼續觀察，或按「繼續」恢復、按「重試」重開這一輪。");
    }

    // Drawn below the strat picker for scenarios that declare WaymarkPresets. _selectedWaymark
    // is the index passed to RunScenario on Start; changing it while a scenario is loaded
    // re-places the markers immediately (same live-feedback loop as the position readout).
    // 連戰：清單裡的場景完成後自動接下一個。每一項記「加入當下」選的打法／標點／進度。
    private void DrawChainControls(Game game)
    {
        var config = plugin.Configuration;
        var hostGate = plugin.Multiplayer.HasSession && plugin.Multiplayer.Session?.IsHost != true;
        ImGui.BeginDisabled(hostGate);
        var chainEnabled = config.ChainEnabled;
        if (ImGui.Checkbox("連戰（完成後自動接清單裡的下一個）", ref chainEnabled))
        {
            config.ChainEnabled = chainEnabled;
            config.Save();
            if (!chainEnabled && game.PendingIsChain) game.CancelPendingRetry();
        }
        ImGui.SameLine();
        ImGui.TextDisabled(config.AutoRetry ? "清單走到底會從頭再來（自動重試已開）" : "清單走到底就停");
        ImGui.Separator();

        // 左：副本清單一鍵「＋」加入；右：清單每一項的打法／場標／進度直接在列上改。
        // 不用切回「練習」分頁選好再回來（維護者 2026-09-17）。
        if (ImGui.BeginTable("##chain-layout", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("##pick", ImGuiTableColumnFlags.WidthFixed, ScenarioPanelWidth() + 40f);
            ImGui.TableSetupColumn("##list", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            DrawChainPicker(config);
            ImGui.TableSetColumnIndex(1);
            DrawChainList(game, config);
            ImGui.EndTable();
        }
        ImGui.EndDisabled();
    }

    private void DrawChainPicker(Configuration config)
    {
        ImGui.TextUnformatted("加入副本");
        ImGui.Separator();
        var any = false;
        foreach (var zone in plugin.Game.Zones)
        {
            if (!IsZoneVisible(zone)) continue;
            any = true;
            if (!ImGui.CollapsingHeader($"{zone.Name}##chain-zone", ImGuiTreeNodeFlags.DefaultOpen)) continue;
            foreach (var phase in plugin.Game.PhasesOf(zone))
                foreach (var scenario in plugin.Game.ScenariosOf(phase))
                {
                    if (scenario.AiStrats.Count == 0) continue;
                    ImGui.PushID(scenario.Name);
                    if (ImGui.SmallButton("＋"))
                    {
                        config.Chain.Add(new ScenarioChainEntry
                        {
                            ScenarioType = scenario.GetType().FullName ?? "",
                            ScenarioName = scenario.Name,
                            SelectedAi = 0,
                            SelectedWaymark = 0,
                            ProgressKey = scenario is IProgressScenario progress && progress.Progresses.Count > 0
                                ? progress.Progresses[0].Key : "full",
                        });
                        config.Save();
                    }
                    ImGui.SameLine();
                    ImGui.TextUnformatted(DisplayName(scenario));
                    ImGui.PopID();
                }
        }
        if (!any) ImGui.TextDisabled("沒有顯示中的副本，請從齒輪設定開啟。");
    }

    private void DrawChainList(Game game, Configuration config)
    {
        ImGui.TextUnformatted("連戰順序");
        ImGui.SameLine();
        ImGui.BeginDisabled(config.Chain.Count == 0);
        if (ImGui.SmallButton("清空")) { config.Chain.Clear(); config.Save(); }
        ImGui.EndDisabled();
        ImGui.Separator();
        if (config.Chain.Count == 0)
        {
            ImGui.TextDisabled("清單是空的：按左邊的「＋」加入副本，完成後會依這裡的順序自動接下一個。");
            return;
        }
        for (var i = 0; i < config.Chain.Count; i++)
        {
            var entry = config.Chain[i];
            var scenario = game.Scenarios.FirstOrDefault(s => s.GetType().FullName == entry.ScenarioType);
            ImGui.PushID(i);
            if (ImGui.SmallButton("×")) { config.Chain.RemoveAt(i); config.Save(); ImGui.PopID(); break; }
            ImGui.SameLine();
            ImGui.BeginDisabled(i == 0);
            if (ImGui.SmallButton("↑")) { (config.Chain[i - 1], config.Chain[i]) = (config.Chain[i], config.Chain[i - 1]); config.Save(); }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(i == config.Chain.Count - 1);
            if (ImGui.SmallButton("↓")) { (config.Chain[i + 1], config.Chain[i]) = (config.Chain[i], config.Chain[i + 1]); config.Save(); }
            ImGui.EndDisabled();
            ImGui.SameLine();
            var active = game.ActiveScenario is { } a && a.GetType().FullName == entry.ScenarioType;
            if (active) ImGui.TextColored(new Vector4(0.4f, 1f, 0.6f, 1f), $"{i + 1}. {entry.ScenarioName}");
            else ImGui.TextUnformatted($"{i + 1}. {entry.ScenarioName}");
            if (scenario is null)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.6f, 0.6f, 1f), "（找不到這個場景，請移除）");
                ImGui.PopID();
                continue;
            }
            ImGui.Indent(28f);
            // 打法
            var strats = BuildStratLabels(scenario.AiStrats);
            if (strats.Length > 1)
            {
                var ai = Math.Clamp(entry.SelectedAi ?? 0, 0, strats.Length - 1);
                ImGui.TextDisabled("打法");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(220);
                if (ImGui.Combo("##ai", ref ai, strats, strats.Length)) { entry.SelectedAi = ai; config.Save(); }
                ImGui.SameLine();
            }
            // 進度
            if (scenario is IProgressScenario progress && progress.Progresses.Count > 1)
            {
                var keys = progress.Progresses;
                var sel = 0;
                for (var k = 0; k < keys.Count; k++) if (keys[k].Key == entry.ProgressKey) sel = k;
                var names = new string[keys.Count];
                for (var k = 0; k < keys.Count; k++) names[k] = keys[k].Name;
                ImGui.TextDisabled("進度");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(160);
                if (ImGui.Combo("##progress", ref sel, names, names.Length)) { entry.ProgressKey = keys[sel].Key; config.Save(); }
                ImGui.SameLine();
            }
            // 場標
            var presets = scenario.Phase.Zone.WaymarkPresets;
            if (presets.Count > 1)
            {
                var wm = Math.Clamp(entry.SelectedWaymark, 0, presets.Count - 1);
                var names = new string[presets.Count];
                for (var k = 0; k < presets.Count; k++) names[k] = presets[k].Name;
                ImGui.TextDisabled("場標");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(140);
                if (ImGui.Combo("##wm", ref wm, names, names.Length)) { entry.SelectedWaymark = wm; config.Save(); }
            }
            ImGui.NewLine();
            ImGui.Unindent(28f);
            ImGui.PopID();
        }
    }

    // 模擬中的精簡小窗：不用開完整面板就能換場景／進度／打法／場標。
    // 場景 combo 依副本→階段分組；選擇走同一個 SelectScenario，與完整面板同步。
    internal void DrawQuickSelectors()
    {
        var game = plugin.Game;
        var hostGate = plugin.Multiplayer.HasSession && plugin.Multiplayer.Session?.IsHost != true;
        ImGui.BeginDisabled(hostGate);
        ImGui.TextUnformatted("場景：");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(260);
        var current = _selectedScenario is { } s ? DisplayName(s) : "（未選擇）";
        if (ImGui.BeginCombo("##quick-scenario", current))
        {
            foreach (var zone in game.Zones)
            {
                if (!IsZoneVisible(zone)) continue;
                ImGui.TextDisabled(zone.Name);
                foreach (var phase in game.PhasesOf(zone))
                    foreach (var scenario in game.ScenariosOf(phase))
                    {
                        var selected = _selectedScenario == scenario;
                        ImGui.PushID(scenario.Name);
                        if (ImGui.Selectable(DisplayName(scenario), selected)) SelectScenario(scenario);
                        if (selected) ImGui.SetItemDefaultFocus();
                        ImGui.PopID();
                    }
            }
            ImGui.EndCombo();
        }
        ImGui.EndDisabled();
        DrawProgressSelector();
        DrawStratSelector();
        DrawWaymarkSelector();
    }

    private void DrawWaymarkSelector()
    {
        if (_selectedScenario is null) return;
        var presets = _selectedScenario.Phase.Zone.WaymarkPresets;
        if (presets.Count == 0) return;
        if (_selectedWaymark < 0 || _selectedWaymark >= presets.Count) _selectedWaymark = 0;

        var labels = new string[presets.Count];
        for (var i = 0; i < presets.Count; i++) labels[i] = presets[i].Name;

        ImGui.TextUnformatted("場標：");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180);
        ImGui.BeginDisabled(plugin.Multiplayer.HasSession
            && !plugin.Multiplayer.CanStart && !plugin.Multiplayer.CanManualRetry);
        if (ImGui.Combo("##waymarks", ref _selectedWaymark, labels, labels.Length)
            && plugin.Game.World.Map.IsInInstance && !plugin.Multiplayer.HasSession)
            plugin.Game.World.PlaceWaymarks(presets[_selectedWaymark].Markers);
        ImGui.EndDisabled();
    }
    private void DrawProgressSelector()
    {
        if (_selectedScenario is not IProgressScenario progress || progress.Progresses.Count == 0)
            return;

        var progresses = progress.Progresses;
        var selected = -1;
        for (var i = 0; i < progresses.Count; i++)
            if (progresses[i].Key == _selectedProgressKey)
            {
                selected = i;
                break;
            }
        if (selected < 0) selected = 0;
        var labels = new string[progresses.Count];
        for (var i = 0; i < progresses.Count; i++) labels[i] = progresses[i].Name;

        ImGui.TextUnformatted("進度：");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220);
        ImGui.BeginDisabled(plugin.Multiplayer.HasSession
            && !plugin.Multiplayer.CanStart && !plugin.Multiplayer.CanManualRetry);
        if (ImGui.Combo("##progress", ref selected, labels, labels.Length))
            _selectedProgressKey = progresses[selected].Key;
        ImGui.EndDisabled();
    }

    private void DrawRoleSelector()
    {
        var p3Monitors = _selectedScenario is TopP3MonitorsScenario;
        var p3Intermission = _selectedScenario is TopP3IntermissionScenario;
        var p3HelloWorld = _selectedScenario is TopP3HelloWorldScenario;
        var idx = p3Monitors
            ? TopP3MonitorRules.SelectorIndex(_roleOverride)
            : p3Intermission || p3HelloWorld
                ? TopP3PracticeParty.SelectorIndex(_roleOverride)
                : _roleOverride is { } role ? (int)role + 1 : 0;
        var labels = p3Monitors
            ? TopP3MonitorRules.RoleSelectorLabels
            : p3Intermission || p3HelloWorld
                ? TopP3PracticeParty.RoleSelectorLabels : RoleLabels;

        ImGui.TextUnformatted("練習分工：");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        ImGui.BeginDisabled(plugin.Multiplayer.HasSession);
        if (ImGui.Combo("##role", ref idx, labels, labels.Length))
            _roleOverride = p3Monitors
                ? TopP3MonitorRules.RoleForSelectorIndex(idx)
                : p3Intermission || p3HelloWorld
                    ? TopP3PracticeParty.RoleForSelectorIndex(idx)
                    : idx == 0 ? null : (PartyRole)(idx - 1);
        ImGui.EndDisabled();
        ImGui.TextDisabled("僅選擇練習分工，不切換實際職業；下次開始生效。");
    }

    // Only meaningful when a scenario offers more than one strat; hidden otherwise.
    // When the scenario declares StratGroups, a region-button row is drawn above the
    // dropdown and the dropdown is filtered to the selected region.
    private void DrawStratSelector()
    {
        if (_selectedScenario is null || _stratLabels.Length <= 1) return;
        _selectedStrat = Math.Clamp(_selectedStrat, 0, _stratLabels.Length - 1);
        ImGui.TextUnformatted("打法：");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(280);
        ImGui.Combo("##strat", ref _selectedStrat, _stratLabels, _stratLabels.Length);
    }

    // 防火牆自檢狀態（紅線 1）。刻意畫在 Start 正上方而不是塞進設定分頁：
    // 這是「能不能安全進遊戲」的資訊，使用者必須在按下 Start 之前就看到。
    // 未跑過時不預先執行檢查——Passive 模式的觀測需要累積時間，開窗當下就跑
    // 只會在剛載入插件時給出誤導性的紅燈。真正的閘門在 Game.RunScenarioInternal。
    private static void DrawFirewallStatus(Core.Game.Game game)
    {
        // native 相容閘（擋 crash）畫在最前面——它比防火牆自檢更早擋下 Start。
        if (!Core.Map.NativeCompatGate.CanEnterSimZone)
        {
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), "⛔ 載入模擬區已停用（台服 native 相容性未驗證）");
            ImGui.TextWrapped(Core.Map.NativeCompatGate.LoadZoneBlockReason);
            ImGui.Separator();
        }

        var check = game.LastFirewallCheck;
        if (check == null)
        {
            ImGui.TextDisabled("防火牆自檢：尚未執行（按「開始」時會自動檢查並攔阻）");
            return;
        }

        if (check.Passed)
        {
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f), "防火牆自檢：安全項通過");
            // 功能警告（不擋，但要讓使用者看到——例如 opcode 清單為空）
            foreach (var w in check.Warnings)
                ImGui.TextColored(new Vector4(0.95f, 0.8f, 0.3f, 1f), $"  ⚠ {w.Name}：{w.Detail}");
            return;
        }

        ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), "防火牆自檢：安全項未通過——已拒絕啟動");
        foreach (var f in check.BlockingFailures)
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.6f, 1f), $"  ✖ {f.Name}：{f.Detail}");
        ImGui.TextDisabled("（這是保護帳號的硬閘，不提供略過選項）");
    }

    // 共用工具不依賴場景選取；Start 出問題時仍可直接找到診斷與錄製狀態。
    private void DrawTraceRow()
    {
        if (!BuildEdition.IsDeveloper) return;
        if (ImGui.Button("打開診斷追蹤檔資料夾"))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = System.IO.Path.GetDirectoryName(Core.CrashTrace.FilePath)!,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"開啟追蹤檔資料夾失敗：{ex.Message}");
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("站位編輯器")) plugin.TogglePositionEditor();
        ImGui.SameLine();
        if (ImGui.Button("場地圖層")) plugin.ToggleLayoutInspector();
        ImGui.SameLine();
        if (ImGui.Button("怪物採集")) plugin.ToggleNpcCollector();

        RecorderPanel.Draw(Plugin.Recorder);
    }

    // Empty AiStrats uses the built-in default; otherwise preserve the selected absolute index.
    private bool HasStartableStrat()
    {
        if (_selectedScenario is not { } scenario) return false;
        var strats = scenario.AiStrats;
        return strats.Count == 0 || (uint)_selectedStrat < strats.Count;
    }

    private void DrawLocationHint()
    {
        if (ZoneSession.IsInInn()) return;
        ImGui.TextDisabled("場景只能在旅館、房屋內部或公寓啟動");
        ImGui.SameLine();
        ImGuiComponents.HelpMarker("場景只能從旅館、房屋內部或公寓啟動；請先返回其中一處再開始。");
    }
}
