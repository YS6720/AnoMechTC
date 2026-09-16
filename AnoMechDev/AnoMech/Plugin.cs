using Dalamud.Game;                      // API13: ISigScanner（API15 移到 Plugin.Services）
using Dalamud.Game.ClientState.Objects;  // API13: ITargetManager（同上）
using Dalamud.Game.Command;
using Dalamud.Game.DutyState;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using AnoMech.Core;
using AnoMech.Core.Game;
using AnoMech.Core.Map;
using AnoMech.Core.Native;
using AnoMech.Core.Recording;
using AnoMech.Windows;
using AnoMech.Pointers;
using CSFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace AnoMech;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IFlyTextGui FlyText { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;

    private const string CommandName = "/anomech";
    private const string CommandAlias = "/ano";

    public Configuration Configuration { get; init; }
    internal static Configuration Config { get; private set; } = null!;

    public readonly WindowSystem WindowSystem = new("AnoMech");
    public Game Game { get; }
    internal AnoMech.Multiplayer.MultiplayerManager Multiplayer { get; }
    // SimObjects reach engine singletons through these statics (mirrors the
    // Plugin.* PluginService pattern).
    internal static Game GameInstance { get; private set; } = null!;
    private Core.Combat.RotationSim? rotationSim;
    // Session-lifetime input hooks, owned here (not Game) so they're hooked once
    // per load rather than per scenario. SimPlayer is the sole writer of their
    // flags — it reconciles them from its own state each tick.
    internal static LocalPlayerInputHooks PlayerInputHooks { get; private set; } = null!;
    internal static LogManager? LogManager { get; private set; }
    internal static CombatRecorder? Recorder { get; private set; }
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    private RunningSimWindow RunningSimWindow { get; init; }
    internal PositionEditorWindow? PositionEditorWindow { get; init; }
    internal NpcCollectorWindow? NpcCollectorWindow { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config = Configuration;

        if (BuildEdition.IsDeveloper)
        {
            var logManager = new LogManager();
            LogManager = logManager;
            if (Config.EnableEventLogging) logManager.Open();
        }

        PlayerInputHooks = new LocalPlayerInputHooks(GameInterop);
        Game = new Game();
        GameInstance = Game;
        Multiplayer = new AnoMech.Multiplayer.MultiplayerManager(Game);
        Game.Multiplayer = Multiplayer;
        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);
        RunningSimWindow = new RunningSimWindow(this, MainWindow);

        if (BuildEdition.IsDeveloper)
        {
            PositionEditorWindow = new PositionEditorWindow();
            NpcCollectorWindow = new NpcCollectorWindow();
        }

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(RunningSimWindow);
        if (PositionEditorWindow is { } positionEditorWindow)
            WindowSystem.AddWindow(positionEditorWindow);
        if (NpcCollectorWindow is { } npcCollectorWindow)
            WindowSystem.AddWindow(npcCollectorWindow);

        if (Config.OpenSimMenuOnInn && ZoneSession.IsInInn())
            MainWindow.IsOpen = true;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = BuildEdition.IsDeveloper
                ? "開啟 AnoMech。子命令：config（設定）、start（開始）、reset（重設）、leave（離開）、pos（站位）、layout（場地）、npc（怪物）、rec（錄製）"
                : "開啟 AnoMech。子命令：config（設定）、start（開始）、reset（重設）、leave（離開）"
        });
        CommandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = "/anomech 的別名"
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;

        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        ClientState.TerritoryChanged += OnTerritoryChanged;
        DutyState.DutyStarted += OnDutyStarted;
        DutyState.DutyWiped += OnDutyWiped;
        DutyState.DutyCompleted += OnDutyCompleted;

        // Initialize Pointers
        CharacterManagerPointers.Initialize();
        EventFrameworkPointers.Initialize();
        EventObjectManagerPointers.Initialize();
        EventObjectPointers.Initialize();
        GameMainPointers.Initialize();
        ModelContainerPointers.Initialize();
        PacketDispatcherPointers.Initialize();
        RsfPointers.Initialize();
        StatusManagerPointers.Initialize();
        TimelineContainerPointers.Initialize();
        VfxContainerPointers.Initialize();
        VfxDataPointers.Initialize();
        VfxObjectPointers.Initialize();

        // 必須排在 PacketDispatcherPointers.Initialize() 之後——錄影機是從那些
        // 已解析的函式指標取位址來掛 hook 的。
        if (BuildEdition.IsDeveloper)
        {
            var recorder = new CombatRecorder();
            Recorder = recorder;
            if (Config.AutoRecordInDuty && ClientState.TerritoryType != 0 && DutyState.IsDutyStarted)
                recorder.Start("插件載入時已在副本內");
        }

        // 模擬區內的職業循環判定（連段＋職業 proc；🔒 只在模擬場景生效）
        rotationSim = new Core.Combat.RotationSim();

        // ⚠️ Framework.Update 一定要**最後**才掛：它掛上去的那一刻起就會每幀被呼叫，
        // 而下面那些 Pointers.Initialize() 是 13 次 50MB binary 的 signature 掃描，
        // 耗時約 100ms ≈ 6 幀。先掛的話那 6 幀會打到還沒建好的 Recorder ⇒ NullReference。
        // （2026-08-19 實測：載入瞬間噴 6 筆 NRE，時間戳全落在 0.03 秒內。）
        Framework.Update += OnFrameworkUpdate;

        Log.Information($"===A cool log message from {PluginInterface.Manifest.Name}===");
    }

    public void Dispose()
    {
        Core.CrashTrace.Close();
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        DutyState.DutyStarted -= OnDutyStarted;
        DutyState.DutyWiped -= OnDutyWiped;
        DutyState.DutyCompleted -= OnDutyCompleted;

        WindowSystem.RemoveAllWindows();

        rotationSim?.Dispose();
        Multiplayer.Dispose();
        Game.Dispose();
        // After Game.Dispose so World.Dispose → SimPlayer.Despawn can still clear
        // the lock flags through the hooks before they're torn down.
        PlayerInputHooks.Dispose();
        Recorder?.Dispose();
        LogManager?.Dispose();
        ConfigWindow.Dispose();
        MainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandAlias);
    }

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        Multiplayer.BeforeGameTick();
        // FrameDeltaTime, not framework.UpdateDelta: UpdateDelta is wall-clock
        // truncated to whole ms, so summing it drifts. FrameDeltaTime is the
        // full-precision delta the game ticks its own animations with.
        var fw = CSFramework.Instance();
        if (fw == null) return;
        var delta = fw->FrameDeltaTime;
        // 逐個判 null：任何一個還沒建好都不該讓整個 update 拋例外（Dalamud 會每幀記一筆，
        // 幾秒就把 log 洗爆，真正的錯誤反而被埋掉）。
        try { Game?.Tick(delta); }
        catch (System.Exception error) when (Multiplayer.HasSession)
        {
            Multiplayer.StopForError(error);
        }
        Multiplayer.AfterGameTick();
        if (BuildEdition.IsDeveloper)
        {
            Recorder?.Tick(delta);
            NpcCollectorWindow?.Tick(delta);
        }
    }

    // API13 的 TerritoryChanged 是 Action<ushort>（API15 才放寬成 uint）
    private void OnTerritoryChanged(ushort territory)
    {
        var row = DataManager.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(territory);
        var isInn = row?.TerritoryIntendedUse.RowId == 2; // TerritoryIntendedUse.Inn

        // 錄影起停以「有沒有 ContentFinderCondition」為準（＝這是不是一個副本），
        // 不等 DutyStarted——那是屏障落下才觸發，進場到開打之間的 spawn 會漏。
        if (BuildEdition.IsDeveloper && Config.AutoRecordInDuty && Recorder is { } recorder)
        {
            var isInstance = (row?.ContentFinderCondition.RowId ?? 0) != 0;
            if (isInstance) recorder.Start($"進入副本 territory={territory}");
            else recorder.Stop();
        }
        if (BuildEdition.IsDeveloper && !isInn)
        {
            var name = row?.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
            LogManager?.LogEnterInstance(territory, name);
        }

        if (!isInn)
        {
            MainWindow.IsOpen = false;
            return;
        }
        if (Config.OpenSimMenuOnInn)
            MainWindow.IsOpen = true;
    }

    // API13 的 IDutyState 事件是 EventHandler<ushort>（territoryType）；
    // API15 才改成帶 IDutyStateEventArgs（內含 TerritoryType RowRef）。
    private void OnDutyStarted(object? sender, ushort territoryType)
    {
        if (BuildEdition.IsDeveloper)
            LogManager?.LogCombatStart(territoryType);
        if (BuildEdition.IsDeveloper && Config.AutoRecordInDuty && Recorder is { } recorder)
            recorder.Start($"副本開始 territory={territoryType}");
    }

    private void OnDutyWiped(object? sender, ushort territoryType)
    {
        if (BuildEdition.IsDeveloper)
            LogManager?.LogCombatEnd(territoryType, wipe: true);
    }

    private void OnDutyCompleted(object? sender, ushort territoryType)
    {
        if (BuildEdition.IsDeveloper)
            LogManager?.LogCombatEnd(territoryType, wipe: false);
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim())
        {
            case "config":
                ConfigWindow.Toggle();
                break;
            case "start":
                StartSelectedScenario(solo: false);
                break;
            case "start solo":
                StartSelectedScenario(solo: true);
                break;
            case "reset":
                Game.Reset();
                break;
            case "leave":
                Game.Leave();
                break;
            case "pos":
                if (BuildEdition.IsDeveloper) PositionEditorWindow?.Toggle();
                break;
            case "layout":
                break;
            case "npc":
                if (BuildEdition.IsDeveloper) NpcCollectorWindow?.Toggle();
                break;
            case "rec":
                if (BuildEdition.IsDeveloper && Recorder is { } recorder)
                {
                    if (recorder.IsRecording) recorder.Stop(); else recorder.Start("手動");
                }
                break;
            case "rot 0" or "rot 1" or "rot 2":
                if (!BuildEdition.IsDeveloper) break;
                // A/B 實驗開關（2026-08-22 連段除錯）：0=關（不發合成回包，回到最原始）
                // 1=只確認序號（不帶招式演出的空回包） 2=完整合成回包（現行）
                Core.Combat.RotationSim.Mode = args.Trim()[^1] - '0';
                Core.ChatOutput.Coach($"[AnoMech] 循環模式={Core.Combat.RotationSim.Mode}（0關/1只確認/2完整回包）");
                break;
            default:
                ToggleMainUi();
                break;
        }
    }

    // 五條拒絕路徑每一條都要進聊天欄（健檢 2026-09-05 M21）：原本只寫 log、其中一條連 log 都沒有，
    // 按了 /anomech start 沒反應時使用者不知道是哪一關沒過。
    private void StartSelectedScenario(bool solo)
    {
        if (!ZoneSession.IsInInn())
        {
            Log.Warning("場景只能從旅館、房屋內部或公寓啟動。");
            Core.ChatOutput.Error("無法開始：只能在旅館房間內啟動場景。");
            return;
        }
        if (ZoneSession.IsPlayerBusy())
        {
            Log.Warning("Cannot start a scenario while you are busy (cutscene, NPC event, crafting, etc.).");
            Core.ChatOutput.Error("無法開始：角色忙碌中（過場／NPC 對話／製作等），結束後再試。");
            return;
        }
        if (MainWindow.SelectedScenario is not { } scenario)
        {
            Log.Warning("No scenario selected.");
            Core.ChatOutput.Error("無法開始：尚未選擇場景（/anomech 開主視窗選一個）。");
            return;
        }
        if (solo && !scenario.SupportsSolo)
        {
            Log.Warning($"{scenario.Name} does not support Solo mode.");
            Core.ChatOutput.Error($"無法開始：「{scenario.Name}」不支援單人模式，請改用小隊模式。");
            return;
        }
        if (!solo && MainWindow.SelectedStrat < 0)
        {
            Log.Warning("No strat selected for the current region.");
            Core.ChatOutput.Error("無法開始：這個地區沒有選定的打法，先在主視窗選一個。");
            return;
        }
        Game.RunScenario(scenario, MainWindow.SelectedRoleOverride,
            solo ? null : MainWindow.SelectedStrat, MainWindow.SelectedWaymark,
            MainWindow.SelectedProgressKey);

    }

    public void TogglePositionEditor()
    {
        if (BuildEdition.IsDeveloper) PositionEditorWindow?.Toggle();
    }

    public void ToggleLayoutInspector()
    {
    }

    public void ToggleNpcCollector()
    {
        if (BuildEdition.IsDeveloper) NpcCollectorWindow?.Toggle();
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.TogglePanel();
}
