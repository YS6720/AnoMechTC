using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core;
using AnoMech.Core.Game.Party;
using AnoMech.Core.Map;
using AnoMech.Core.Native;
using AnoMech.Core.SimObjects;
using AnoMech.Multiplayer;
using AnoMech.Scenarios;
using AnoMech.Scenarios.Recorded;
using AnoMech.Scenarios.Top.P2PartySynergy;
using AnoMech.Scenarios.Top.P3Monitors;
using AnoMech.Scenarios.Top.P3Intermission;
using AnoMech.Scenarios.Top.P3HelloWorld;
using AnoMech.Scenarios.Top.P4BlueScreen;
using AnoMech.Scenarios.Top.P5Delta;
using AnoMech.Scenarios.Top.P5Omega;
using AnoMech.Scenarios.Top.P5Sigma;
using AnoMech.Scenarios.Top.P6WaveCannon2;
// UMAD 相關 using 已移除（scenario 排除，見下方 Scenarios 陣列註解）
using AnoMech.Scenarios.Uwu.UltimatePredation;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using AnoMech.Core.Combat;

namespace AnoMech.Core.Game;

public enum GameScenarioState
{
    Idle,
    Preparing,
    Running,
    Paused,
    Completed,
    Failed,
}

// Every mutable input the run actually consumes is latched here at start time.
// SettingsIdentity is the scenario's own override panel, PositionsVersion is the
// practice-position store's save counter, and Recorded is the exact recording bytes
// (parsed and hashed together) for a RecordedScenario. All three are part of the
// identity, so a retry or a streak can never silently switch to content the user —
// or a converter rerun — changed afterwards. Only an explicit manual Start reads
// a Data file again; prepare, commit and every retry consume this snapshot.
internal sealed record ScenarioRunSnapshot(
    IScenario Scenario,
    PartyRole? RoleOverride,
    int? SelectedAi,
    int SelectedWaymark,
    string ProgressKey,
    float EventTimeScale,
    bool GodMode,
    string SettingsIdentity,
    long PositionsVersion,
    RecordedTimelineSnapshot? Recorded)
{
    public string IdentityKey => string.Join("|",
        Scenario.GetType().FullName,
        Scenario.Name,
        ProgressKey,
        RoleOverride?.ToString() ?? "auto",
        SelectedAi?.ToString() ?? "solo",
        SelectedWaymark,
        EventTimeScale.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        GodMode,
        SettingsIdentity,
        PositionsVersion,
        Recorded?.Sha256 ?? "");

    // Preserve the pre-versioned document key. Saved edits invalidate a run,
    // but must never move the document the next explicit start will read.
    public string PracticeKey => string.Join("|",
        Scenario.GetType().FullName,
        Scenario.Name,
        ProgressKey,
        RoleOverride?.ToString() ?? "auto",
        SelectedAi?.ToString() ?? "solo",
        SelectedWaymark,
        EventTimeScale.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        GodMode,
        (Scenario as Scenarios.Recorded.RecordedScenario)?.DataFile ?? "");
}

// High-level orchestrator: owns the World, holds the scenario catalog, drives
// the active scenario's lifecycle, and is the single entry point UI talks to.
public sealed partial class Game : IDisposable
{
    // EventObj for the duty Exit portal — hidden on every scenario start so the
    // teleport-out interactable doesn't sit inside the simulated arena.
    private const uint ExitObjectBaseId = 2000139;

    public EventScheduler Events { get; } = new();
    public SimWorld World { get; }
    internal AnoMech.Core.Combat.RecordedAbilityRuntime Abilities { get; }
    public SimPlayer? Player => World.Party.Player;
    // Flat registry; the zone -> phase -> scenario tree is derived from it in
    // first-appearance order.
    public IReadOnlyList<IScenario> Scenarios { get; }
    public IReadOnlyList<IZone> Zones { get; }
    private readonly Dictionary<IZone, List<IPhase>> phasesByZone = new();
    private readonly Dictionary<IPhase, List<IScenario>> scenariosByPhase = new();
    public Bgm Bgm { get; } = new();

    // Fixed scenario-local player spawn (16y south of centre).
    public static readonly Vector3 PlayerSpawnLocal = new(0f, 0f, 16f);

    // Multiplier applied only to the EventScheduler's delta. Intentionally does not
    // scale enemy/party/tether/status ticks so cast bars, animations, and movement
    // run at real time — only the timeline of scheduled events stretches/compresses.
    public float EventTimeScale { get; set; } = 1f;

    // Manual pause, or an authoritative full-party KO. An individual failure
    // never schedules a delayed freeze of the remaining players or mechanics.
    public bool Paused { get; set; }

    // When true, Game.Kill still posts the chat line for learning but skips every
    // gameplay side effect (HP=0, KO timeline, stun hooks, wipe pause).
    public bool GodMode { get; set; }

    /// <summary>最近一次防火牆自檢結果（B-001）。UI 用它顯示為何拒絕啟動。</summary>
    internal FirewallCheckResult? LastFirewallCheck { get; private set; }

    private IScenario? activeScenario;
    internal IScenario? ActiveScenario => activeScenario;
    private float scenarioElapsed;
    private ScenarioRunSnapshot? activeRun;
    private ScenarioRunSnapshot? pendingRetry;
    private ScenarioRunSnapshot? retryInFlight;
    private long pendingRetryGeneration;
    private long retryInFlightGeneration;
    private float retryRemaining;
    private int consecutiveWins;
    private string? streakIdentity;
    // Peer-only: the last host-authored run status. A peer never derives any of
    // this locally — no completion latch, no streak, no retry.
    private RunStatusMessage? peerRunStatus;
    private RunStatusMessage? capturedRunStatus;
    private GameScenarioState scenarioState;
    private bool firstDeathScheduled;
    private readonly OpcodeUpdater opcodeUpdater;
    private bool leaveCleanupPending;

    public GameScenarioState ScenarioState
    {
        get
        {
            var state = networkPeer ? PeerScenarioState : scenarioState;
            return Paused && state == GameScenarioState.Running ? GameScenarioState.Paused : state;
        }
    }
    private GameScenarioState PeerScenarioState => peerRunStatus?.Outcome switch
    {
        MpRunOutcome.Completed => GameScenarioState.Completed,
        MpRunOutcome.Failed => GameScenarioState.Failed,
        MpRunOutcome.Running => GameScenarioState.Running,
        // No authoritative status yet: show the local prepare/idle progress only.
        _ => scenarioState,
    };
    public int ConsecutiveWins => networkPeer ? peerRunStatus?.ConsecutiveWins ?? 0 : consecutiveWins;
    public bool HasPendingRetry => networkPeer
        ? peerRunStatus?.RetryPending == true
        : pendingRetry != null || retryInFlight != null;
    public float RetryRemaining => networkPeer ? peerRunStatus?.RetrySeconds ?? 0f : retryRemaining;
    public string? ActiveProgressKey => activeRun?.ProgressKey;
    public bool AutoRetryEnabled => Plugin.Config.AutoRetry;
    internal ScenarioRunSnapshot? ActiveRun => activeRun;
    // Failed/completed are outcomes, not teardown. Pause keeps queued practice
    // work alive; callers separately decide whether to advance or accept input.
    internal bool HasActivePractice => activeRun is not null &&
        ScenarioState is GameScenarioState.Running or GameScenarioState.Paused or
            GameScenarioState.Failed or GameScenarioState.Completed;
    internal ScenarioRunSnapshot? PendingRetry => pendingRetry;
    // 連戰排下來的下一場：走與自動重試相同的 pendingRetry 管線（含多人房主換副本），
    // 只是快照是清單裡的下一個場景，且不要求 AutoRetry 打開。
    private bool pendingIsChain;
    internal bool PendingIsChain => pendingIsChain;
    internal long ScenarioDispatchGeneration => scenarioDispatchGeneration;
    internal bool IsRetryInFlight(ScenarioRunSnapshot snapshot, long generation)
        => ReferenceEquals(retryInFlight, snapshot) &&
            retryInFlightGeneration == generation &&
            (ReferenceEquals(activeRun, snapshot) || retryIsChain) &&
            scenarioState == GameScenarioState.Completed &&
            !Paused &&
            (AutoRetryEnabled || retryIsChain) &&
            RunInputsCurrent(snapshot);
    internal void CancelRetryDispatch(ScenarioRunSnapshot snapshot, long generation, bool resetStreak)
    {
        if (!ReferenceEquals(retryInFlight, snapshot) || retryInFlightGeneration != generation)
            return;
        retryInFlight = null;
        retryIsChain = false;
        retryRemaining = 0f;
        if (resetStreak) ClearStreak();
    }
    internal bool IsRetryTokenCurrent(ScenarioRunSnapshot snapshot, long generation)
        => ReferenceEquals(retryInFlight, snapshot) &&
            retryInFlightGeneration == generation &&
            !Paused &&
            (AutoRetryEnabled || retryIsChain) &&
            RunInputsCurrent(snapshot);
    private bool retryIsChain;

    // The scenario's own settings panel and the practice-position store are both
    // mutable while a run is loaded. A pending retry keeps the identity it was
    // started with, so a change to either must retire that retry instead of
    // quietly running a different practice under the same streak.
    // PositionsVersion is a plain long compare, so it runs first: the reflective
    // settings identity is only built when the cheap check already passed, and only
    // on the bounded occasions a retry token is examined — never on a plain tick.
    internal bool RunInputsCurrent(ScenarioRunSnapshot snapshot)
        => snapshot.PositionsVersion == World.PracticePositions.ContentVersion &&
            string.Equals(snapshot.SettingsIdentity, snapshot.Scenario.SettingsIdentity, StringComparison.Ordinal);

    // Sent at snapshot rate for the whole run, so the steady state must not allocate:
    // a new record is built only when the presentation actually changed.
    internal RunStatusMessage CaptureRunStatus()
    {
        var outcome = scenarioState switch
        {
            GameScenarioState.Completed => MpRunOutcome.Completed,
            GameScenarioState.Failed => MpRunOutcome.Failed,
            _ => MpRunOutcome.Running,
        };
        var wins = Math.Clamp(consecutiveWins, 0, MpLimits.Streak);
        var pending = pendingRetry != null || retryInFlight != null;
        var seconds = Math.Clamp(retryRemaining, 0f, MpLimits.RetryDisplaySeconds);
        if (capturedRunStatus is { } cached && cached.Outcome == outcome && cached.ConsecutiveWins == wins &&
            cached.RetryPending == pending && cached.RetrySeconds.Equals(seconds))
            return cached;
        return capturedRunStatus = new(outcome, wins, pending, seconds);
    }

    internal void ApplyRunStatus(RunStatusMessage status)
    {
        if (!networkPeer) throw new MpProtocolException(MpError.InvalidMessage);
        peerRunStatus = status;
    }

    public Game()
    {
        World = new SimWorld(Events);
        Abilities = new AnoMech.Core.Combat.RecordedAbilityRuntime(this);
        World.Map.Session.SetRestoreCompletionGuard(() => !leaveCleanupPending);
        opcodeUpdater = new OpcodeUpdater(World.Map.Session.IncomingOpcodes);
        var handWritten = new List<IScenario>
        {
            new TopP2PartySynergyScenario(),
            new TopP3MonitorsScenario(),
            new TopP3IntermissionScenario(),
            new TopP3HelloWorldScenario(),
            new TopP4BlueScreenScenario(),
            new TopP5DeltaScenario(),
            new TopP5SigmaScenario(),
            new TopP5OmegaScenario(),
            new Scenarios.Top.P6AlphaOmega.TopP6AlphaOmegaScenario(),
            new TopP6WaveCannon2Scenario(),
        };
        if (BuildEdition.IsDeveloper)
        {
            handWritten.Add(new UltimatePredationScenario());
            // 這一組場景僅在具備對應內容的建置中註冊；其餘建置連型別都不存在。
        }

        // 只有手寫場景進 registry。TOP P1/P2 錄影入口已由 維護者 取消，安裝目錄裡
        // 殘留或新增的 JSON 不會再產生任何場景。
        Scenarios = handWritten.ToArray();

        // Derive the zone tree from the flat registry (first-appearance order).
        var zoneOrder = new List<IZone>();
        foreach (var scenario in Scenarios)
        {
            var phase = scenario.Phase;
            var zone = phase.Zone;
            if (!phasesByZone.TryGetValue(zone, out var phases))
            {
                phases = new List<IPhase>();
                phasesByZone[zone] = phases;
                zoneOrder.Add(zone);
            }
            if (!phases.Contains(phase)) phases.Add(phase);
            if (!scenariosByPhase.TryGetValue(phase, out var phaseScenarios))
            {
                phaseScenarios = new List<IScenario>();
                scenariosByPhase[phase] = phaseScenarios;
            }
            phaseScenarios.Add(scenario);
        }
        Zones = zoneOrder;
    }

    internal bool SubmitAbility(uint actionId, byte classJob, byte level, ulong targetId, long requestId,
        Vector3? location = null)
    {
        if (Paused || !World.Map.IsInInstance || !HasActivePractice) return false;
        var hasTarget = (uint)targetId is not (0 or 0xE0000000);
        var target = hasTarget ? ResolveAbilityActor(targetId) : null;
        if (hasTarget && target is null) return false;
        if (Multiplayer?.HasSession == true)
            return Multiplayer.SubmitAbilityUse(actionId, classJob, level, target, location, actionRequestId: requestId);
        if (networkPeer) return false;
        var accepted = Abilities.TryUse(World.Party.PlayerRole, actionId, classJob, level, target, out var comboOk, location);
        RotationSim.Instance?.CompleteOrdinaryAction(requestId, accepted, accepted && comboOk);
        return true;
    }

    internal SimCharacter? ResolveAbilityActor(ulong targetId)
    {
        foreach (var (_, member) in World.Party.FilledSlots())
            if (member.IsActive && (ulong)member.GameObjectId == targetId) return member;
        foreach (var child in World.Children)
            if (child is SimCharacter { IsActive: true } actor &&
                (ulong)actor.GameObjectId == targetId) return actor;
        return null;
    }

    // Derived zone-tree accessors, in registry order.
    public IReadOnlyList<IPhase> PhasesOf(IZone zone) => phasesByZone[zone];
    public IReadOnlyList<IScenario> ScenariosOf(IPhase phase) => scenariosByPhase[phase];

    // selectedAi: index into the scenario's AiStrats of the strat to run, or null for
    // solo (no doppels, no AI). Empty AiStrats is a deliberate built-in default:
    // multiplayer accepts only index 0 for it.
    // selectedWaymark: index into the scenario's WaymarkPresets; ignored when it has none.
    public void RunScenario(IScenario scenario, PartyRole? roleOverride = null, int? selectedAi = 0,
        int selectedWaymark = 0, string progressKey = "full")
    {
        if (!TryCreateRunSnapshot(scenario, roleOverride, selectedAi, selectedWaymark, progressKey, out var snapshot))
            return;

        CancelPendingRetry();
        ClearStreak();
        InvalidateQueuedScenarioWork();

        if (Multiplayer?.HasSession == true)
        {
            // In a room the host may also swap to another scenario without leaving:
            // the live run ends in place (no inn restore, no map unload, no socket
            // teardown) and the selected one opens next in the same room.
            if (Multiplayer.CanManualRetry) Multiplayer.ManualRetry(snapshot);
            else Multiplayer.StartScenario(snapshot);
            return;
        }

        DispatchLocalStart(snapshot,
            $"Start: {scenario.GetType().Name} role={roleOverride} ai={selectedAi} wm={selectedWaymark} progress={progressKey}");
    }

    // One dispatch path for every local (non-network) entry into the world: an explicit
    // Start and an explicit manual retry re-enter it exactly the same way.
    private void DispatchLocalStart(ScenarioRunSnapshot snapshot, string trace)
    {
        var dispatchGeneration = scenarioDispatchGeneration;
        Plugin.Framework.Run(() =>
        {
            if (scenarioDisposed || dispatchGeneration != scenarioDispatchGeneration || Multiplayer?.HasSession == true) return;
            CrashTrace.Session(trace);
            try
            {
                RunScenarioInternal(snapshot);
                CrashTrace.Log("Start 正常結束");
            }
            catch (Exception ex)
            {
                World.PracticePositions.End();
                scenarioState = GameScenarioState.Failed;
                CrashTrace.Exception("RunScenarioInternal", ex);
                try
                {
                    Core.ChatOutput.Error($"[AnoMech] 啟動失敗：{ex.GetType().Name}: {ex.Message}");
                    Core.ChatOutput.Error($"[AnoMech] 詳細追蹤：{CrashTrace.FilePath}");
                }
                catch { /* chat 尚未就緒 */ }
            }
        });
    }

    internal static bool IsValidProgress(IScenario scenario, string? progressKey)
    {
        if (string.IsNullOrWhiteSpace(progressKey)) return false;
        if (scenario is not IProgressScenario progress)
            return string.Equals(progressKey, "full", StringComparison.Ordinal);
        return progress.Progresses.Any(item =>
            item is not null &&
            !string.IsNullOrWhiteSpace(item.Key) &&
            string.Equals(item.Key, progressKey, StringComparison.Ordinal));
    }

    internal static bool IsValidAi(IScenario scenario, int? selectedAi, bool network)
    {
        if (selectedAi is null)
            return !network && scenario.SupportsSolo;
        if (selectedAi < 0) return false;
        return scenario.AiStrats.Count == 0
            ? selectedAi == 0
            : selectedAi < scenario.AiStrats.Count;
    }

    internal bool TryCreateRunSnapshot(IScenario scenario, PartyRole? roleOverride, int? selectedAi,
        int selectedWaymark, string? progressKey, out ScenarioRunSnapshot snapshot)
    {
        snapshot = null!;
        if (scenario is null || !Scenarios.Contains(scenario) ||
            !IsValidProgress(scenario, progressKey) ||
            !IsValidAi(scenario, selectedAi, network: false) ||
            selectedWaymark < 0 || selectedWaymark >= Math.Max(1, scenario.Phase.Zone.WaymarkPresets.Count) ||
            !float.IsFinite(EventTimeScale) || EventTimeScale <= 0f || EventTimeScale > 100f ||
            roleOverride is not null && !Enum.IsDefined(roleOverride.Value))
        {
            Core.ChatOutput.Error("[AnoMech] 無效的場景、進度、打法、角色、標點或時間設定。");
            return false;
        }

        // The one and only point a Data file is read for a run. Everything after this
        // — prepare, commit, every auto-retry — replays these exact bytes.
        RecordedTimelineSnapshot? recorded = null;
        if (scenario is RecordedScenario recordedScenario)
        {
            recorded = RecordedTimeline.LoadSnapshot(recordedScenario.DataFile);
            if (recorded is null)
            {
                Core.ChatOutput.Error($"[AnoMech] 場景資料 {recordedScenario.DataFile} 載入失敗，拒絕啟動。");
                return false;
            }
        }

        snapshot = new ScenarioRunSnapshot(scenario, roleOverride, selectedAi, selectedWaymark,
            progressKey!, EventTimeScale, GodMode, scenario.SettingsIdentity,
            World.PracticePositions.ContentVersion, recorded);
        return true;
    }

    internal void ClearStreak()
    {
        consecutiveWins = 0;
        streakIdentity = null;
    }

    internal void CancelPendingRetry()
    {
        pendingRetry = null;
        retryInFlight = null;
        timelineDrainWaited = 0f;
        retryIsChain = false;
        pendingIsChain = false;
        retryRemaining = 0f;
        InvalidateQueuedScenarioWork();
        Multiplayer?.CancelPendingRetry();
    }

    private void RunScenarioInternal(ScenarioRunSnapshot snapshot)
    {
        var prepared = PrepareScenarioWorld(snapshot);
        if (prepared != null) CommitScenarioWorld(prepared, peer: false);
    }
    private static IReadOnlyList<Waymark> ResolveWaymarks(IZone zone, int selectedWaymark)
    {
        var presets = zone.WaymarkPresets;
        if (presets.Count == 0) return Array.Empty<Waymark>();
        if (selectedWaymark >= 0 && selectedWaymark < presets.Count)
            return presets[selectedWaymark].Markers;
        return presets[0].Markers;
    }

    // Sprint goes on cooldown when the player presses it inside a scenario
    // (LocalPlayerInputHooks lets Original run so the recast starts). Clear it
    // here so each scenario starts with Sprint ready, regardless of whether
    // the player pressed it just before clicking Start.
    internal static unsafe void ResetSprintCooldown()
    {
        var am = ActionManager.Instance();
        if (am == null) return;
        // Start＝全技能 CD 歸零（維護者 2026-08-22：「每次start請重置技能CD」）——
        // 練循環每輪都要從乾淨狀態起手；模擬區 CD 純本地，直接清 recast group 全表。
        // Native charge consumers also read ActionId/Total after IsActive is cleared;
        // leaving either value behind keeps a queued multi-charge action waiting.
        for (var g = 0; g < 88; g++)
        {
            var detail = am->GetRecastGroupDetail(g);
            if (detail == null) continue;
            detail->IsActive = false;
            detail->ActionId = 0;
            detail->Elapsed = 0f;
            detail->Total = 0f;
        }
    }

    // Start＝清掉玩家殘留 buff（維護者 2026-08-22：「入場時有些持續BUFF也要歸零」）——
    // 戰逃／安魂／上一輪的預備狀態帶進場會污染循環練習。豁免進食（48）＝真副本也不清。
    private static unsafe void ResetPlayerStatuses()
    {
        var lp = Plugin.ObjectTable.LocalPlayer;
        if (lp == null) return;
        var bc = (FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara*)lp.Address;
        var slots = bc->StatusManager.Status;
        for (int i = 0; i < slots.Length; i++)
        {
            var id = slots[i].StatusId;
            if (id == 0 || id == 48) continue;
            bc->StatusManager.SetStatus(i, 0, 0f, 0, default, true);
            if (slots[i].StatusId == id)
                bc->StatusManager.RemoveStatus(i, 1);
        }
    }

    public void Tick(float deltaSeconds)
    {
        if (Paused) return;
        if (networkPeer)
        {
            // Cosmetic world maintenance only; no scenario or mechanism scheduler.
            World.Tick(deltaSeconds);
            return;
        }
        Events.Tick(deltaSeconds * EventTimeScale);
        World.Tick(deltaSeconds);
        HitRangeDebug.Tick(deltaSeconds);
        Abilities.Tick(deltaSeconds);
        if (activeScenario != null)
        {
            scenarioElapsed += deltaSeconds;
            activeScenario.Tick(deltaSeconds, scenarioElapsed);
        }
        ObserveScenarioResult();
        AdvanceRetry(deltaSeconds);
    }

    private const float AutoRetryDelaySeconds = 2f;
    // 場景在「檢定通過」那一刻就呼叫 CompleteScenario，但後面通常還排著收尾的施法／動畫／退場事件。
    // 自動重試與連戰的倒數要等這些事件全部播完才開始，否則會在機制還沒演完就跳下一關
    // （維護者 2026-09-17）。上限是保險：場景若掛著永不結束的週期事件，不會卡死。
    private const float MaxTimelineDrainSeconds = 30f;
    private float timelineDrainWaited;

    private void ObserveScenarioResult()
    {
        if (activeRun is null) return;
        if (World.Result == ScenarioResult.Failed)
        {
            MarkScenarioFailed();
            return;
        }
        if (World.Result != ScenarioResult.Completed || scenarioState == GameScenarioState.Completed)
            return;

        scenarioState = GameScenarioState.Completed;
        // The practice the user is now looking at is not the one this run played.
        // Credit nothing to the old identity and do not re-enter it automatically.
        if (!RunInputsCurrent(activeRun))
        {
            pendingRetry = null;
            retryRemaining = 0f;
            ClearStreak();
            Core.ChatOutput.Coach("[AnoMech] 場景設定或站位已變更，本輪不計入連勝，也不會自動重試。");
            return;
        }
        if (string.Equals(streakIdentity, activeRun.IdentityKey, StringComparison.Ordinal))
            consecutiveWins++;
        else
        {
            streakIdentity = activeRun.IdentityKey;
            consecutiveWins = 1;
        }

        if (TryQueueChainNext(activeRun)) return;
        if (AutoRetryEnabled)
        {
            pendingRetry = activeRun;
            pendingIsChain = false;
            pendingRetryGeneration = scenarioDispatchGeneration;
            retryRemaining = AutoRetryDelaySeconds;
            timelineDrainWaited = 0f;
        }
    }

    // 連戰：完成的場景在清單裡且還有下一個 → 把下一個排進 pendingRetry。清單走到底：
    // 有開自動重試就從頭再來，否則停。角色沿用本場（多人由房主分工決定，不在快照裡改）。
    private bool TryQueueChainNext(ScenarioRunSnapshot finished)
    {
        if (!Plugin.Config.ChainEnabled) return false;
        var chain = Plugin.Config.Chain;
        if (chain.Count == 0) return false;
        var finishedType = finished.Scenario.GetType().FullName;
        var index = chain.FindIndex(entry => entry.ScenarioType == finishedType);
        if (index < 0) return false;
        var nextIndex = index + 1;
        if (nextIndex >= chain.Count)
        {
            if (!AutoRetryEnabled)
            {
                Core.ChatOutput.Coach("[AnoMech] 連戰完成（清單已走到底）。");
                return true;
            }
            nextIndex = 0;
        }
        var entry = chain[nextIndex];
        var scenario = Scenarios.FirstOrDefault(candidate => candidate.GetType().FullName == entry.ScenarioType);
        if (scenario is null ||
            !TryCreateRunSnapshot(scenario, finished.RoleOverride, entry.SelectedAi, entry.SelectedWaymark,
                entry.ProgressKey, out var next))
        {
            Core.ChatOutput.Error($"[AnoMech] 連戰：找不到或無法啟動「{entry.ScenarioName}」，已停止。");
            return true;
        }
        pendingRetry = next;
        pendingIsChain = true;
        pendingRetryGeneration = scenarioDispatchGeneration;
        retryRemaining = AutoRetryDelaySeconds;
        timelineDrainWaited = 0f;
        Core.ChatOutput.Coach($"[AnoMech] 連戰：本場收尾播完後 {AutoRetryDelaySeconds:0} 秒接「{scenario.Name}」。");
        return true;
    }

    private void AdvanceRetry(float deltaSeconds)
    {
        if (!AutoRetryEnabled && !pendingIsChain && !retryIsChain)
        {
            if (pendingRetry != null || retryInFlight != null) CancelPendingRetry();
            return;
        }
        if (pendingRetry is null || scenarioState != GameScenarioState.Completed || Paused)
            return;
        if (Events.Pending > 0 && timelineDrainWaited < MaxTimelineDrainSeconds)
        {
            timelineDrainWaited += Math.Max(0f, deltaSeconds);
            return;
        }
        retryRemaining = Math.Max(0f, retryRemaining - Math.Max(0f, deltaSeconds));
        if (retryRemaining > 0f) return;
        if (!RunInputsCurrent(pendingRetry))
        {
            CancelPendingRetry();
            ClearStreak();
            Core.ChatOutput.Coach("[AnoMech] 場景設定或站位已變更，已取消自動重試；請手動重新開始。");
            return;
        }

        var snapshot = pendingRetry;
        var generation = pendingRetryGeneration;
        pendingRetry = null;
        retryInFlight = snapshot;
        retryIsChain = pendingIsChain;
        pendingIsChain = false;
        retryInFlightGeneration = generation;
        retryRemaining = 0f;
        if (Multiplayer?.HasSession == true)
        {
            Multiplayer.RetryScenario(snapshot, generation);
            return;
        }

        try
        {
            RunScenarioInternal(snapshot);
            if (ReferenceEquals(retryInFlight, snapshot))
            {
                retryInFlight = null;
                retryIsChain = false;
                retryRemaining = 0f;
                ClearStreak();
                Core.ChatOutput.Error("[AnoMech] 自動重試未能啟動，已停止連勝。");
            }
        }
        catch (Exception ex)
        {
            retryInFlight = null;
            retryIsChain = false;
            ClearStreak();
            Core.ChatOutput.Error($"[AnoMech] 自動重試啟動失敗：{ex.GetType().Name}: {ex.Message}");
        }
    }

    internal void MarkScenarioFailed()
    {
        if (activeRun is null) return;
        scenarioState = GameScenarioState.Failed;
        pendingRetry = null;
        retryInFlight = null;
        retryIsChain = false;
        retryRemaining = 0f;
        ClearStreak();
    }

    // Godmode preview: how long a swallowed-death HP-bar drop stays down before healing back.
    private const float GodmodeHealSeconds = 1.2f;

    public bool Kill(ISimPartyMember target, string cause, bool reportProtectedFailure = true)
    {
        // Peer deaths arrive as authoritative state, never as local decisions.
        if (networkPeer) return false;
        if (target == null) return false;
        if (target.Dead) return false;

        // A kill request is a failed attempt even when godmode or an explicit
        // invulnerability status suppresses the native KO side effect. This
        // latch is separate from member.Dead and is never cleared by a revive.
        World.LatchFailure();
        MarkScenarioFailed();

        if (target is SimCharacter sc && sc.HasStatus(SimParty.InvulnStatusId))
        {
            if (reportProtectedFailure)
                World.ReportFailure(cause, target.Role, false);
            return false;
        }

        if (GodMode)
        {
            if (reportProtectedFailure)
                ReportDeathAttempt(target, cause, false);
            if (target is SimPlayer player)
            {
                player.DropHpBar();
                Events.Add(GodmodeHealSeconds, player.RestoreHpBar);
            }
            return false;
        }
        target.OnKilled();
        ReportDeathAttempt(target, cause, true);
        // Failed is a result latch, not a pause request. Only an actual KO of
        // every occupied slot stops the authoritative simulation; peers receive
        // that pause through the existing host-controlled synchronization.
        if (World.Party.IsWiped)
        {
            Paused = true;
        }
        return true;
    }

    private void ReportDeathAttempt(ISimPartyMember target, string cause, bool death)
    {
        var text = World.ReportFailure(cause, target.Role, death);
        if (!firstDeathScheduled)
        {
            firstDeathScheduled = true;
            ShowFirstDeathOverlay(text);
        }
    }

    private static unsafe void ShowFirstDeathOverlay(string text)
    {
        var ui = UIModule.Instance();
        if (ui == null) return;
        ui->ShowErrorText(text, true);
    }

    public void Reset()
    {
        ClearStreak();
        CancelPendingRetry();
        if (Multiplayer?.HasSession == true)
        {
            Multiplayer.ResetScenario();
            return;
        }
        var dispatchGeneration = scenarioDispatchGeneration;
        Plugin.Framework.Run(() =>
        {
            if (scenarioDisposed || dispatchGeneration != scenarioDispatchGeneration || Multiplayer?.HasSession == true) return;
            if (activeScenario is not null) TeleportPlayerToSpawnIfOutsideArena();
            ResetInternal();
            Bgm.Reset();
        });
    }

    // Explicit manual retry. Re-opens the run that is loaded right now — same scenario,
    // practice role, strat, progress and waymarks — without leaving the room, the relay
    // socket or the mapped zone. It is a fresh Start intent, not the auto-retry token:
    // it does not need AutoRetry enabled, does not wait for a completed run, and
    // deliberately re-reads the current settings, practice positions and recording
    // bytes. A peer never has this authority; in a session only the host does.
    public bool CanRetryActiveRun
    {
        get
        {
            if (networkPeer || activeRun is null || NetworkIsRestoring) return false;
            var multiplayer = Multiplayer;
            return multiplayer is not { HasSession: true } || multiplayer.CanManualRetry;
        }
    }

    public void RetryActiveRun()
    {
        if (!CanRetryActiveRun || activeRun is not { } current) return;
        if (!TryCreateRunSnapshot(current.Scenario, current.RoleOverride, current.SelectedAi,
                current.SelectedWaymark, current.ProgressKey, out var snapshot))
            return;

        CancelPendingRetry();
        ClearStreak();
        InvalidateQueuedScenarioWork();

        if (Multiplayer?.HasSession == true)
        {
            Multiplayer.ManualRetry(snapshot);
            return;
        }

        DispatchLocalStart(snapshot, $"Retry: {current.Scenario.GetType().Name} progress={current.ProgressKey}");
    }

    // Pull the player back to the scenario's spawn point only if they're standing
    // outside the arena ring (e.g. knocked out of bounds, or wandered off). No-op
    // when the scenario enforces no boundary. Reads the live game-object position:
    // at scenario start the SimPlayer was just created and hasn't ticked, so its
    // cached Position is still zero. At reset this must run before ResetInternal
    // clears Party / ScenarioOrigin.
    private void TeleportPlayerToSpawnIfOutsideArena()
    {
        var lp = Plugin.ObjectTable.LocalPlayer;
        if (lp == null) return;
        if (!World.IsOutsideArena(World.Coordinates.ToLocal(lp.Position))) return;
        TeleportPlayerToSpawn();
    }

    // ScenarioOrigin must already be set (SetPosition resolves local -> world through it).
    private void TeleportPlayerToSpawn() => Player?.SetPosition(PlayerSpawnLocal);

    // Menu label, e.g. "P5 Delta". MenuName is the display label; the stored identity
    // (Name) is deliberately not part of what the menu shows.
    public static string DisplayName(IScenario scenario)
    {
        var phase = scenario.Phase;
        return string.IsNullOrEmpty(phase.Name) ? scenario.MenuName : $"{phase.Name} {scenario.MenuName}";
    }

    public static string FullName(IScenario scenario)
        => $"{scenario.Phase.Zone.Name} — {DisplayName(scenario)}";

    public void Leave()
    {
        ClearStreak();
        CancelPendingRetry();
        var dispatchGeneration = scenarioDispatchGeneration;
        if (World.Map.Session.HasRestoreFailed)
        {
            // 多人已退回lobby時也能重試本機恢復，不再轉送一次LeaveScenario。
            Plugin.Framework.Run(() =>
            {
                if (scenarioDisposed || dispatchGeneration != scenarioDispatchGeneration ||
                    !World.Map.Session.HasRestoreFailed) return;
                if (leaveCleanupPending) CleanupThenUnload();
                else World.Map.Unload();
            });
            return;
        }
        if (Multiplayer?.HasSession == true)
        {
            Multiplayer.LeaveScenario();
            return;
        }
        Plugin.Framework.Run(() =>
        {
            if (scenarioDisposed || dispatchGeneration != scenarioDispatchGeneration || Multiplayer?.HasSession == true) return;
            CleanupThenUnload();
        });
    }

    private void CleanupThenUnload()
    {
        try
        {
            ResetInternal();
        }
        catch (Exception ex)
        {
            Paused = true;
            CrashTrace.Exception("Leave.ResetInternal", ex);
        }
        try
        {
            Bgm.Reset();
        }
        catch (Exception ex)
        {
            CrashTrace.Exception("Leave.Bgm.Reset", ex);
        }
        // 即使前置清理失敗仍嘗試回原地；completion guard禁止未清乾淨就放行。
        World.Map.Unload();
    }

    private void ResetInternal()
    {
        leaveCleanupPending = true;
        Abilities.Reset();
        World.PracticePositions.End();
        activeScenario = null;
        activeRun = null;
        scenarioState = GameScenarioState.Idle;
        peerRunStatus = null;
        scenarioElapsed = 0f;
        Events.Clear();
        World.Despawn();
        if (!networkPeer)
            Markings.ClearAll();
        // BGM is owned by the callers: a scenario start reconciles it to the new
        // track (keeping it playing when unchanged); Reset/Leave stop it. Resetting
        // here would force a same-track restart on every scenario switch.

        Paused = false;
        firstDeathScheduled = false;
        // Input-lock flags are owned by SimPlayer (reconciled each tick, cleared on
        // its Despawn during World.Reset above) — nothing to clear here.
        leaveCleanupPending = false;
    }

    // Plugin.Dispose is invoked on the framework thread during unload — run
    public void Dispose()
    {
        scenarioDisposed = true;
        opcodeUpdater.Dispose();
        CancelPendingRetry();
        ClearStreak();
        World.PracticePositions.End();
        activeScenario = null;
        activeRun = null;
        scenarioState = GameScenarioState.Idle;
        Events.Clear();
        Bgm.Dispose();
        World.Dispose();
        Markings.ClearAll();
    }
}
