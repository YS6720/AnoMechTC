using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AnoMech.Core;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Party;
using AnoMech.Core.Map;
using AnoMech.Core.Native;
using AnoMech.Core.SimObjects;
using AnoMech.Scenarios;
using AnoMech.Scenarios.Recorded;

namespace AnoMech.Multiplayer;

// UI only queues commands. Connection completion, session transitions, and all
// native calls are consumed by the framework thread, including while paused.
internal sealed partial class MultiplayerManager : IMultiplayerGame, IDisposable
{
    private readonly Game game;
    private readonly Queue<Action> commands = new();
    private WorldReplicator? replicator;
    private NetworkResourceCatalog? resources;
    private string? resourceSceneFingerprint;
    private string? buildFingerprint;
    private bool disposed;
    private bool closedCleaned;
    private MpError reportedError;
    // Manual is the host's explicit in-room retry: it is not the auto-retry token, so
    // it is re-checked only against the dispatch generation it was queued under.
    private sealed record PendingNetworkRetry(ScenarioRunSnapshot Snapshot, long Generation, bool Manual);
    private PendingNetworkRetry? pendingRetry;
    private bool preserveStreakOnEnd;
    // One read of one Data file: parsed, validated and hashed together. Retained so
    // the resource catalog, prepare and commit all consume the same approved bytes.
    private sealed record ApprovedScene(IScenario Scenario, string Fingerprint, RecordedTimelineSnapshot? Recorded);
    private ApprovedScene? approvedScene;
    // Display-only catch-up bound for the remote-pose pump, in seconds.
    private const double MaxVisualFrameSeconds = 0.25;
    private double lastVisualFrame;
    public MultiplayerManager(Game game) => this.game = game;
    public MultiplayerSession? Session { get; private set; }
    public bool HasSession => Session is { Phase: not MultiplayerPhase.Closed };
    public MpError LastError { get; private set; }
    // Wall-clock of the last non-None error so the panel can say "斷線於 HH:mm:ss" instead
    // of showing a stale reason with no idea when it happened.
    public DateTime? LastErrorAt { get; private set; }
    public bool CanStart => Session is { IsHost: true, Phase: MultiplayerPhase.Lobby } && !game.NetworkIsRestoring;
    // Only the host, and only while its own run is live: the in-place end opens the
    // next run in the same room, either the same one again (retry) or the scenario
    // the host just selected (switch). Peers never may.
    internal bool CanManualRetry =>
        Session is { IsHost: true, Phase: MultiplayerPhase.Running } && !game.NetworkIsRestoring;
    private static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;


    public void Disconnect() => Queue(DisconnectInternal);
    public void ClaimRole(PartyRole role) => ForSession(session => Report(session.ClaimRole(role)));
    public void SetPaused(bool paused) => ForSession(session => Report(session.SetPaused(paused)));
    public void GiveInvulnerability() => ForSession(session => Report(session.RequestControl(MpControl.GiveInvulnerability)));
    // Ending the room's run is host authority (the session refuses it from anyone
    // else); a member's local reset must not even be put on the wire.
    public void ResetScenario() => ForSession(session =>
        Report(session.IsHost ? session.RequestControl(MpControl.Reset) : MpError.Busy));
    public void LeaveScenario() => ForSession(session =>
    {
        if (session.Phase == MultiplayerPhase.Running) Report(session.RequestControl(MpControl.Leave));
        else DisconnectInternal();
    });
    public void StartScenario(ScenarioRunSnapshot snapshot)
        => ForSession(session =>
        {
            if (!session.IsHost || snapshot.SelectedAi is not { } ai ||
                !Game.IsValidProgress(snapshot.Scenario, snapshot.ProgressKey) ||
                !Game.IsValidAi(snapshot.Scenario, ai, network: true))
            {
                Report(MpError.InvalidMessage);
                return;
            }
            var scene = ApproveScene(snapshot);
            var catalog = ResourcesFor(scene);
            var descriptor = new RunDescriptor(SceneKey(snapshot.Scenario), snapshot.ProgressKey,
                scene.Fingerprint, catalog.Fingerprint, ai, snapshot.SelectedWaymark,
                snapshot.EventTimeScale, snapshot.GodMode);
            Report(session.Start(descriptor, Now));
        });

    internal void RetryScenario(ScenarioRunSnapshot snapshot, long generation)
    {
        if (pendingRetry != null) return;
        ForSession(session =>
        {
            if (!session.IsHost || session.Phase != MultiplayerPhase.Running ||
                !game.IsRetryInFlight(snapshot, generation))
            {
                game.CancelRetryDispatch(snapshot, generation, resetStreak: true);
                Report(MpError.Busy);
                return;
            }
            pendingRetry = new PendingNetworkRetry(snapshot, generation, Manual: false);
            preserveStreakOnEnd = true;
            var error = session.RequestControl(MpControl.Reset);
            if (error != MpError.None)
            {
                pendingRetry = null;
                preserveStreakOnEnd = false;
                game.CancelRetryDispatch(snapshot, generation, resetStreak: true);
                Report(error);
            }
        });
    }

    // Host-only explicit in-place start: end the live run in place (no inn restore, no
    // map unload, no socket teardown), then open a fresh RunId in the same room on the
    // very same tick the session reaches the lobby again. The snapshot was just built
    // from the current settings, so this serves both the retry of the loaded scenario
    // and a switch to another one; there is no auto-retry token to honour. The queued
    // start is anchored to the dispatch generation left behind by that end, which any
    // newer Start/Reset/Leave bumps (and those also cancel it outright).
    internal void ManualRetry(ScenarioRunSnapshot snapshot)
    {
        ForSession(session =>
        {
            if (!CanManualRetry || pendingRetry != null || snapshot.SelectedAi is not { } ai ||
                !Game.IsValidProgress(snapshot.Scenario, snapshot.ProgressKey) ||
                !Game.IsValidAi(snapshot.Scenario, ai, network: true))
            {
                Report(MpError.Busy);
                return;
            }
            var error = session.RequestControl(MpControl.Reset);
            if (error != MpError.None)
            {
                Report(error);
                return;
            }
            pendingRetry = new PendingNetworkRetry(snapshot, game.ScenarioDispatchGeneration, Manual: true);
        });
    }
    internal void CancelPendingRetry()
    {
        pendingRetry = null;
        preserveStreakOnEnd = false;
    }

    public void BeforeGameTick()
    {
        if (disposed) return;
        try
        {
            for (var n = 0; n < MpLimits.DrainPerTickMax; n++)
            {
                Action? command;
                lock (commands) command = commands.Count == 0 ? null : commands.Dequeue();
                if (command == null) break;
                command();
            }
            if (!AdvanceConnection()) return;
            Session?.BeforeGameTick(Now);
            UpdateInvitation();
            ObserveSession();
            TryStartPendingRetry();
        }
        catch (Exception error)
        {
            Session?.StopForError(error);
            OnFault(error);
            ObserveSession();
        }
    }

    public void AfterGameTick()
    {
        if (disposed) return;
        Session?.AfterGameTick(Now);
        ObserveSession();
        AdvanceRemoteVisuals();
    }

    // Remote characters are display-smoothed here, not in Game.Tick: this pump also
    // runs while the local game is paused, which is exactly when the 20 Hz owner
    // samples still have to be interpolated instead of freezing mid-step. Fixed
    // eight-slot walk, no per-frame allocation, and the authoritative
    // Position/Rotation the mechanics read are untouched.
    private void AdvanceRemoteVisuals()
    {
        var now = Now;
        var elapsed = now - lastVisualFrame;
        lastVisualFrame = now;
        // First frame after load, and any stall/load-screen gap, advances nothing
        // rather than teleporting the model across a whole missing interval.
        if (!HasSession || elapsed <= 0d || elapsed > MaxVisualFrameSeconds) return;
        var delta = (float)elapsed;
        for (var slot = 0; slot < MpLimits.Members; slot++)
            if (game.World.Party.Get(slot) is SimNetworkPuppet puppet)
                puppet.UpdateVisualPose(delta);
    }

    public void StopForError(Exception error)
    {
        if (!HasSession) return;
        Session!.StopForError(error);
        ObserveSession();
    }

    private void ObserveSession()
    {
        if (Session == null) return;
        if (Session.TakeNotice() is { } notice)
            ChatOutput.Coach($"[多人同步] {notice}");
        if (Session.TakeRejection() is { } rejection)
        {
            var who = rejection.Role is { } role ? $"{rejection.Alias}({role})" : rejection.Alias;
            CrashTrace.Log($"[多人] 成員 {who} 拒絕開始：{rejection.Error} {rejection.Detail}");
            ChatOutput.Error($"[多人同步] {DescribeRejection(who, rejection)}，本次開始已取消。");
        }
        if (Session.LastError != MpError.None) Report(Session.LastError);
        if (Session.Phase != MultiplayerPhase.Closed || closedCleaned) return;
        // This also retires the owned relay/tunnel when the host socket closes.
        // Reset may retain the mapped zone even without an active run.
        var error = LastError;
        DisconnectInternal();
        Report(error);
    }

    // The wire detail is an internal tag ("not-in-inn:1122"); the host reads chat, not
    // trace, so say what the member is actually doing and what fixes it. Maintainer
    // 2026-09-16: a bare "not in room/inn" read as if the member had not joined the room.
    private static string DescribeRejection(string who, MemberRejection rejection)
    {
        var detail = rejection.Detail ?? "";
        var colon = detail.IndexOf(':');
        var tag = colon < 0 ? detail : detail[..colon];
        var arg = colon < 0 ? "" : detail[(colon + 1)..];
        return rejection.Error switch
        {
            MpError.Busy => tag switch
            {
                "phase-restoring" or "zone-restoring" => $"成員「{who}」的場地還在還原中，請等幾秒再開始",
                "zone-restore-failed" => $"成員「{who}」的場地還原失敗，請該成員輸入 /anomech leave 後再開始",
                "not-in-inn" => $"成員「{who}」人不在旅館房間裡（目前在{PlaceName(arg)}），請該成員回到旅館房間再開始",
                "player-busy" => $"成員「{who}」的角色正在{DescribeBusyFlag(arg)}，請該成員完成後再開始",
                _ => $"成員「{who}」尚未就緒",
            },
            MpError.RoleRequired => $"成員「{who}」還沒選分工",
            MpError.SceneMismatch or MpError.ResourceMismatch => $"成員「{who}」的副本資料與房主不一致（插件或遊戲版本不同）",
            MpError.PrepareFailed or MpError.NativeFailure => $"成員「{who}」載入場地失敗",
            _ => $"成員「{who}」拒絕開始（{rejection.Error}）",
        };
    }

    private static string PlaceName(string territoryId)
    {
        if (!uint.TryParse(territoryId, out var id)) return "未知區域";
        var row = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()?.GetRowOrDefault(id);
        var name = row?.PlaceName.ValueNullable?.Name.ExtractText();
        return string.IsNullOrWhiteSpace(name) ? $"區域 {id}" : name;
    }

    private static string DescribeBusyFlag(string flag) => flag switch
    {
        "WatchingCutscene" or "WatchingCutscene78" or "OccupiedInCutSceneEvent" => "看過場動畫",
        "OccupiedInEvent" or "OccupiedInQuestEvent" or "Occupied" or "Occupied30" or "Occupied33"
            or "Occupied38" or "Occupied39" => "跟 NPC 或介面互動",
        "OccupiedSummoningBell" => "使用僱員鈴",
        "Crafting" or "ExecutingCraftingAction" or "PreparingToCraft" => "製作",
        "Gathering" or "ExecutingGatheringAction" or "Fishing" => "採集",
        "TradeOpen" => "交易",
        "BetweenAreas" => "切換區域（載入中）",
        "LoggingOut" => "登出",
        "WaitingForDutyFinder" or "InDutyQueue" => "排任務搜尋器",
        "InCombat" => "戰鬥",
        "Mounted" => "騎乘坐騎",
        "Jumping" => "跳躍",
        _ => $"忙碌（{flag}）",
    };
    private void TryStartPendingRetry()
    {
        if (pendingRetry is not { } pending) return;
        if (Session is not { IsHost: true, Phase: MultiplayerPhase.Lobby } ||
            game.NetworkIsRestoring)
            return;
        // Automatic retries must still own a live token; the manual one only has to
        // still be the newest local intent.
        var current = pending.Manual
            ? pending.Generation == game.ScenarioDispatchGeneration
            : game.IsRetryTokenCurrent(pending.Snapshot, pending.Generation);
        if (!current)
        {
            pendingRetry = null;
            preserveStreakOnEnd = false;
            if (!pending.Manual)
                game.CancelRetryDispatch(pending.Snapshot, pending.Generation, resetStreak: true);
            return;
        }

        var scene = ApproveScene(pending.Snapshot);
        var catalog = ResourcesFor(scene);
        var descriptor = new RunDescriptor(SceneKey(pending.Snapshot.Scenario),
            pending.Snapshot.ProgressKey, scene.Fingerprint, catalog.Fingerprint,
            pending.Snapshot.SelectedAi!.Value, pending.Snapshot.SelectedWaymark,
            pending.Snapshot.EventTimeScale, pending.Snapshot.GodMode);
        var error = Session.Start(descriptor, Now);
        if (error != MpError.None)
        {
            pendingRetry = null;
            preserveStreakOnEnd = false;
            if (!pending.Manual)
                game.CancelRetryDispatch(pending.Snapshot, pending.Generation, resetStreak: true);
            Report(error);
        }
        else
            pendingRetry = null;
    }

    private void Queue(Action command)
    {
        lock (commands)
        {
            if (disposed) return;
            if (commands.Count >= MpLimits.SendQueue)
            {
                LastError = MpError.QueueOverflow;
                return;
            }
            commands.Enqueue(command);
        }
    }

    private void ForSession(Action<MultiplayerSession> command)
    {
        var expected = Session;
        Queue(() =>
        {
            if (expected != null && ReferenceEquals(expected, Session) && HasSession)
                command(expected);
        });
    }


    private void Report(MpError error)
    {
        LastError = error;
        if (error == reportedError) return;
        if (error != MpError.None) LastErrorAt = DateTime.Now;
        reportedError = error;
        if (error != MpError.None)
            ChatOutput.Error($"[多人同步] {error}；目前場次不會自動續接。");
    }

    private void OnFault(Exception error)
    {
        CrashTrace.Exception("Multiplayer", error);
        Report(error is MpProtocolException protocol ? protocol.Error : MpError.NativeFailure);
    }

    private static string OwnBuildIdentity()
    {
        var pairId = BuildEditionPair.Id;
        if (pairId.Length == 0)
            return $"mvid:{typeof(Game).Assembly.ManifestModule.ModuleVersionId}";
        if (pairId.Length != 64 || !pairId.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
            throw new MpProtocolException(MpError.BuildMismatch);
        return $"pair:{pairId}";
    }
    public string BuildFingerprint => buildFingerprint ??= Hash(string.Join("|",
        OwnBuildIdentity(),
        typeof(FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara).Assembly.ManifestModule.ModuleVersionId,
        typeof(Lumina.GameData).Assembly.ManifestModule.ModuleVersionId));
    bool IMultiplayerGame.Paused { get => game.Paused; set => game.Paused = value; }
    bool IMultiplayerGame.IsPrepared => game.NetworkIsPrepared;
    bool IMultiplayerGame.IsRestoring => game.NetworkIsRestoring;

    // Mirrors the Busy gate in CheckRun, in the same order, as a wire-safe tag.
    public string BusyDetail()
    {
        if (game.NetworkIsRestoring)
            return game.World.Map.Session.HasRestoreFailed ? "zone-restore-failed" : "zone-restoring";
        if (!ZoneSession.IsInInn())
            return $"not-in-inn:{Plugin.ClientState.TerritoryType}";
        return ZoneSession.DescribePlayerBusy() is { } flag ? $"player-busy:{flag}" : "none";
    }

    public bool IsMomentarilyBusy => Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Jumping];

    public MpError CheckRun(RunDescriptor descriptor) => CheckRun(descriptor, includeMomentary: true);

    private MpError CheckRun(RunDescriptor descriptor, bool includeMomentary)
    {
        if (!MpValidation.Descriptor(descriptor)) return MpError.InvalidMessage;
        var scenario = game.Scenarios.SingleOrDefault(candidate => SceneKey(candidate) == descriptor.SceneKey);
        if (scenario == null) return MpError.SceneMismatch;
        var scene = ApproveScene(scenario, descriptor.SceneFingerprint);
        if (scene.Fingerprint != descriptor.SceneFingerprint)
            return MpError.SceneMismatch;
        if (!Game.IsValidProgress(scenario, descriptor.ProgressKey) ||
            !Game.IsValidAi(scenario, descriptor.AiIndex, network: true) ||
            descriptor.WaymarkIndex < 0 ||
            descriptor.WaymarkIndex >= Math.Max(1, scenario.Phase.Zone.WaymarkPresets.Count))
            return MpError.InvalidMessage;
        if (game.NetworkIsRestoring || !ZoneSession.IsInInn() || ZoneSession.IsPlayerBusy(includeMomentary)) return MpError.Busy;
        if (!NativeCompatGate.CanEnterSimZone) return MpError.NativeFailure;
        var catalog = ResourcesFor(scene);
        if (!catalog.RuntimeDataAvailable) return MpError.UnsupportedResource;
        if (catalog.Fingerprint != descriptor.ResourceFingerprint) return MpError.ResourceMismatch;
        var local = Session?.Members.SingleOrDefault(member => member.PeerId == Session.Identity?.PeerId);
        if (local?.Role is not { } role || Plugin.ObjectTable.LocalPlayer is not { } player) return MpError.RoleRequired;
        var presets = (scenario as IScenarioPartyPreset)?.GetPartyPreset(player.ClassJob.RowId, role, false)
            ?? PartyPresets.ForRole(role);
        if (presets.Count != MpLimits.Members || presets[(int)role] != null || presets.Count(preset => preset == null) != 1)
            return MpError.RoleRequired;
        return MpError.None;
    }

    public MpError BeginPrepare(RunScope scope, RunDescriptor descriptor,
        IReadOnlyList<LobbyMember> roster, Guid localPeerId, bool host, Func<bool> isCurrent)
    {
        var error = CheckRun(descriptor, includeMomentary: false);
        if (error != MpError.None) return error;
        // CheckRun leaves exactly the approval it just verified; prepare consumes
        // that, never a fresh read of the file.
        if (approvedScene is not { } scene || scene.Fingerprint != descriptor.SceneFingerprint ||
            !ReferenceEquals(scene.Scenario, resources!.Scenario))
            return MpError.SceneMismatch;
        error = game.PrepareNetworkScenario(scope, scene.Scenario, descriptor, roster, localPeerId, host,
            isCurrent, scene.Recorded);
        if (error != MpError.None) return error;
        var role = roster.Single(member => member.PeerId == localPeerId).Role!.Value;
        replicator = new WorldReplicator(game, resources, host, role, isCurrent);
        return MpError.None;
    }

    public MpError Commit(RunScope scope, bool host) => game.CommitNetworkScenario(scope, host);
    public void EndRun(bool returnToInn)
    {
        replicator?.Dispose();
        replicator = null;
        var preserveStreak = preserveStreakOnEnd;
        preserveStreakOnEnd = false;
        try
        {
            game.EndNetworkScenario(returnToInn);
        }
        finally
        {
            if (!preserveStreak)
                game.ClearStreak();
        }
    }
    public SelfPoseMessage? CaptureSelfPose() => game.Player?.SampleNetworkPose(sampleActivity: true);
    public void ApplySelfPose(PartyRole role, SelfPoseMessage pose)
    {
        if (!MpValidation.Validate(pose) || game.World.Party.Get(role) is not SimNetworkPuppet puppet)
            throw new MpProtocolException(MpError.InvalidMessage);
        puppet.ApplyNetworkPose(pose.Pose, pose.IsMoving, pose.IsActing);
    }
    public void GiveInvulnerability(PartyRole role) => game.World.Party.GiveInvuln(role);
    public void OrphanRole(PartyRole role)
    {
        if (game.World.Party.Get(role) is SimNetworkPuppet puppet) puppet.Orphan();
    }
    internal bool SubmitAbilityUse(uint actionId, byte classJob, byte level, SimCharacter? target)
    {
        if (Session is not { Phase: MultiplayerPhase.Running } session ||
            replicator is null || !replicator.TryMapAbilityTarget(target, out var entity)) return false;
        return session.SubmitAbilityUse(new AbilityUseMessage(actionId, classJob, level, entity));
    }
    public bool ApplyAbilityUse(PartyRole role, AbilityUseMessage ability)
    {
        if (Session is not { IsHost: true, Phase: MultiplayerPhase.Running } ||
            game.Paused || replicator is null || !MpValidation.Validate(ability)) return false;
        SimCharacter? target = null;
        if (ability.Target is { } entity && !replicator.TryResolveAbilityTarget(entity, out target)) return false;
        return game.Abilities.TryUse(role, ability.ActionId, ability.ClassJob, ability.Level, target);
    }
    public void ResetAbilityState() => game.Abilities.Reset();
    public IReadOnlyList<PartyMarkerRequestMessage> CaptureLocalPartyMarkers() => Replicator.CaptureLocalPartyMarkers();
    public void ApplyPartyMarker(Guid sender, PartyMarkerRequestMessage marker) => Replicator.ApplyPartyMarker(sender, marker);
    public WorldSnapshotMessage CaptureWorld() => Replicator.CaptureWorld();
    public RolesSnapshotMessage CaptureRoles() => Replicator.CaptureRoles();
    public IReadOnlyList<WorldEvent> DrainEvents() => Replicator.DrainEvents();
    public WorldSnapshotMessage? TakeWorldAfterEvents() => Replicator.TakeWorldAfterEvents();
    public void ApplyWorld(WorldSnapshotMessage snapshot, long ackedMarkerRequest) => Replicator.ApplyWorld(snapshot, ackedMarkerRequest);
    public void ApplyRoles(RolesSnapshotMessage snapshot) => Replicator.ApplyRoles(snapshot);
    public void ApplyEvent(WorldEvent item) => Replicator.ApplyEvent(item);
    public RunStatusMessage CaptureRunStatus() => game.CaptureRunStatus();
    public void ApplyRunStatus(RunStatusMessage status)
    {
        if (!MpValidation.Validate(status)) throw new MpProtocolException(MpError.InvalidMessage);
        game.ApplyRunStatus(status);
    }
    private WorldReplicator Replicator => replicator ?? throw new MpProtocolException(MpError.Cancelled);

    private NetworkResourceCatalog ResourcesFor(ApprovedScene scene)
    {
        if (resources == null || !ReferenceEquals(resources.Scenario, scene.Scenario) ||
            resourceSceneFingerprint != scene.Fingerprint)
        {
            resources = NetworkResourceCatalog.Create(scene.Scenario, scene.Recorded?.Timeline);
            resourceSceneFingerprint = scene.Fingerprint;
        }
        return resources;
    }
    private static string SceneKey(IScenario scenario) => Hash(string.Join("|", scenario.GetType().FullName,
        scenario.Name, scenario.Phase.Zone.TerritoryId, (scenario as RecordedScenario)?.DataFile ?? ""));

    // Parse, validate and hash one single read of the Data file. Never "parse one
    // read, hash another": the fingerprint always describes the retained timeline.
    // Only the peer's own CheckRun reaches this — a host run is pinned at Start.
    private ApprovedScene ApproveScene(IScenario scenario)
    {
        RecordedTimelineSnapshot? recorded = null;
        if (scenario is RecordedScenario recordedScenario)
            recorded = RecordedTimeline.LoadSnapshot(recordedScenario.DataFile)
                ?? throw new MpProtocolException(MpError.SceneMismatch);
        return Approve(scenario, recorded);
    }

    // The host's run snapshot already pinned its bytes at the explicit Start, so the
    // first run and every auto-retry advertise that same fingerprint without ever
    // reopening the file. A rewritten Data file only takes effect on the next Start.
    private ApprovedScene ApproveScene(ScenarioRunSnapshot snapshot)
    {
        if ((snapshot.Scenario is RecordedScenario) != (snapshot.Recorded is not null))
            throw new MpProtocolException(MpError.SceneMismatch);
        return Approve(snapshot.Scenario, snapshot.Recorded);
    }

    private ApprovedScene Approve(IScenario scenario, RecordedTimelineSnapshot? recorded)
        => approvedScene = new ApprovedScene(scenario,
            Hash($"{BuildFingerprint}|{SceneKey(scenario)}|{recorded?.Sha256 ?? ""}"), recorded);

    // Reusing the retained approval is not a weaker check: it is consumed only when
    // it reproduces the descriptor's own fingerprint, and it is the very bytes that
    // produced it. Anything else re-reads and is compared again by the caller.
    private ApprovedScene ApproveScene(IScenario scenario, string expectedFingerprint)
        => approvedScene is { } cached && ReferenceEquals(cached.Scenario, scenario) &&
            string.Equals(cached.Fingerprint, expectedFingerprint, StringComparison.Ordinal)
            ? cached
            : ApproveScene(scenario);

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lock (commands) commands.Clear();
        DisconnectInternal();
    }
}
