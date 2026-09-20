using System;
using System.Collections.Generic;
using System.Linq;
using AnoMech.Core.Combat;
using AnoMech.Core.Game.Party;
using AnoMech.Core.Map;
using AnoMech.Core.Native;
using AnoMech.Core.SimObjects;
using AnoMech.Multiplayer;
using AnoMech.Scenarios;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;

namespace AnoMech.Core.Game;

public sealed partial class Game
{
    private sealed record PreparedScenario(ScenarioRunSnapshot Snapshot, bool FreshLoad, bool Solo)
    {
        public IScenario Scenario => Snapshot.Scenario;
        public int? SelectedAi => Snapshot.SelectedAi;
        public string ProgressKey => Snapshot.ProgressKey;
        // Single authority: the approved recording lives on the run snapshot, never
        // duplicated here, so commit and retry cannot disagree about which bytes ran.
        public Scenarios.Recorded.RecordedTimelineSnapshot? Recorded => Snapshot.Recorded;
    }
    private PreparedScenario? networkPrepared;
    private RunScope networkScope;
    private Func<bool>? networkGuard;
    private bool networkOwnsWorld;
    private bool networkPeer;
    private bool scenarioDisposed;
    private long scenarioDispatchGeneration;

    internal MultiplayerManager? Multiplayer { get; set; }
    internal bool IsNetworkPeer => networkPeer;
    internal bool NetworkIsRestoring => World.Map.Session.IsRestoring;
    internal void InvalidateQueuedScenarioWork() => ++scenarioDispatchGeneration;

    internal bool NetworkIsPrepared
    {
        get
        {
            if (networkPrepared == null || networkGuard?.Invoke() != true || !World.Map.IsInInstance)
                return false;
            for (var i = 0; i < 8; i++)
            {
                var member = World.Party.Get(i);
                if (member == null || !member.IsActive || member is SimNpc npc && !npc.IsReadyForNetwork)
                    return false;
            }
            return true;
        }
    }

    internal MpError PrepareNetworkScenario(RunScope scope, IScenario scenario, RunDescriptor descriptor,
        IReadOnlyList<LobbyMember> roster, Guid localPeerId, bool host, Func<bool> isCurrent,
        Scenarios.Recorded.RecordedTimelineSnapshot? recorded)
    {
        if (networkPrepared != null || NetworkIsRestoring || !isCurrent())
            return MpError.Busy;
        var local = roster.SingleOrDefault(member => member.PeerId == localPeerId);
        if (local?.Role is not { } role)
            return MpError.RoleRequired;
        if (!IsValidProgress(scenario, descriptor.ProgressKey) ||
            !IsValidAi(scenario, descriptor.AiIndex, network: true))
            return MpError.InvalidMessage;
        // Checked before the first native side effect: a recorded scene must arrive
        // with the approved bytes for exactly its own Data file, and nothing else may.
        if (scenario is Scenarios.Recorded.RecordedScenario recordedScenario)
        {
            if (recorded is null ||
                !string.Equals(recorded.DataFile, recordedScenario.DataFile, StringComparison.Ordinal))
                return MpError.SceneMismatch;
        }
        else if (recorded is not null)
            return MpError.SceneMismatch;

        var remoteRoles = new HashSet<PartyRole>();
        for (var i = 0; i < 8; i++)
        {
            var candidate = (PartyRole)i;
            if (candidate != role && (!host || roster.Any(member => member.Role == candidate)))
                remoteRoles.Add(candidate);
        }
        InvalidateQueuedScenarioWork();
        networkScope = scope;
        networkGuard = isCurrent;
        networkPeer = !host;
        World.Map.Session.SetNetworkRunGuard(isCurrent);
        EventTimeScale = descriptor.EventTimeScale;
        GodMode = descriptor.GodMode;
        var snapshot = new ScenarioRunSnapshot(scenario, role, descriptor.AiIndex,
            descriptor.WaymarkIndex, descriptor.ProgressKey, descriptor.EventTimeScale, descriptor.GodMode,
            scenario.SettingsIdentity, World.PracticePositions.ContentVersion, recorded);
        // Names come from the frozen roster by PeerId -> Role -> Alias, so every client
        // shows the same alias on the same role regardless of who joined first or which
        // slot is locally owned. Only names are projected; the scenario preset keeps its
        // job / equipment / level and the local slot stays null.
        // 外觀與別名走同一份凍結名冊（PeerId → Role → Appearance），所以每個客戶端
        // 在同一個角色上看到同一張臉。驗不過的成員留 null＝沿用既有 preset 外觀。
        networkPrepared = PrepareScenarioWorld(snapshot, remoteRoles, !host, isCurrent,
            () => networkOwnsWorld = true, NetworkPartyIdentity.Project(roster),
            NetworkPartyIdentity.ProjectAppearance(roster));
        return networkPrepared == null ? MpError.PrepareFailed : MpError.None;
    }

    internal MpError CommitNetworkScenario(RunScope scope, bool host)
    {
        if (scope != networkScope || networkPrepared == null || !NetworkIsPrepared || host == networkPeer)
            return MpError.Cancelled;
        EnsureNetworkScope(networkGuard);
        CommitScenarioWorld(networkPrepared, networkPeer, networkGuard);
        return MpError.None;
    }

    internal void EndNetworkScenario(bool returnToInn)
    {
        InvalidateQueuedScenarioWork();
        try
        {
            if (!networkOwnsWorld)
                return;
            // Even if an owned actor fails cleanup, still attempt the established
            // zone restore. Never leave a half-prepared world as a successful lobby.
            try
            { ResetInternal(); }
            finally
            {
                try
                { Bgm.Reset(); }
                finally { if (returnToInn) World.Map.Unload(); }
            }
        }
        finally
        {
            networkPrepared = null;
            networkScope = default;
            networkGuard = null;
            networkOwnsWorld = !returnToInn && World.Map.IsInInstance;
            networkPeer = false;
            World.Map.Session.SetNetworkRunGuard(null);
        }
    }

    private static void EnsureNetworkScope(Func<bool>? isCurrent)
    {
        if (isCurrent != null && !isCurrent())
            throw new MpProtocolException(MpError.Cancelled);
    }

    private PreparedScenario? PrepareScenarioWorld(ScenarioRunSnapshot snapshot,
        IReadOnlySet<PartyRole>? remoteRoles = null, bool peer = false, Func<bool>? isCurrent = null,
        Action? acquireWorld = null, IReadOnlyList<string?>? aliasNames = null,
        IReadOnlyList<MpAppearance?>? appearances = null)
    {
        EnsureNetworkScope(isCurrent);
        if (NetworkIsRestoring)
            throw new InvalidOperationException("Zone restoration must finish before preparing another scenario.");
        var scenario = snapshot.Scenario;
        EventTimeScale = snapshot.EventTimeScale;
        GodMode = snapshot.GodMode;
        var roleOverride = snapshot.RoleOverride;
        var selectedAi = snapshot.SelectedAi;
        var selectedWaymark = snapshot.SelectedWaymark;
        var solo = remoteRoles == null && selectedAi is null;
        var phase = scenario.Phase;
        var zone = phase.Zone;
        scenarioState = GameScenarioState.Preparing;
        CrashTrace.Log($"A: 檢查起始區域 territory={Plugin.ClientState.TerritoryType}");
        if (!ZoneSession.IsInInn())
        {
            Plugin.Log.Warning("Game: scenarios can only run from a private zone (inn / house interior / apartment); aborting.");
            ChatOutput.Error("[AnoMech] 只能從旅館、房屋內部或公寓啟動。");
            scenarioState = GameScenarioState.Idle;
            return null;
        }
        if (remoteRoles != null && (ZoneSession.IsPlayerBusy(includeMomentary: false) || NetworkIsRestoring))
            throw new MpProtocolException(MpError.Busy);

        CrashTrace.Log("B: NativeCompatGate");
        if (!NativeCompatGate.CanEnterSimZone)
        {
            Plugin.Log.Error($"[NativeCompatGate] 拒絕啟動：{NativeCompatGate.LoadZoneBlockReason}");
            ChatOutput.Error($"[AnoMech] {NativeCompatGate.LoadZoneBlockReason}");
            scenarioState = GameScenarioState.Idle;
            return null;
        }
        CrashTrace.Log("C: 防火牆自檢");
        LastFirewallCheck = FirewallSelfCheck.Run(World.Map.Session);
        FirewallSelfCheck.LogResult(LastFirewallCheck);
        if (!LastFirewallCheck.Passed)
        {
            ChatOutput.Error($"[AnoMech] {LastFirewallCheck.Summary}");
            ChatOutput.Error("[AnoMech] 為保護帳號，已拒絕啟動 scenario（詳見 /xllog）。");
            scenarioState = GameScenarioState.Idle;
            return null;
        }

        EnsureNetworkScope(isCurrent);
        acquireWorld?.Invoke();
        CrashTrace.Log("D: ResetInternal");
        ResetInternal();
        scenarioState = GameScenarioState.Preparing;
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            Plugin.Log.Warning("Game: no local player; aborting scenario start");
            scenarioState = GameScenarioState.Idle;
            return null;
        }
        CrashTrace.Log("E: 取 freshLoad");
        var freshLoad = !World.Map.IsZoneLoaded;
        EnsureNetworkScope(isCurrent);
        CrashTrace.Log("F: HideObject(Exit)");
        World.HideObject(ExitObjectBaseId);
        EnsureNetworkScope(isCurrent);
        CrashTrace.Log($"G: Map.TryLoad territory={zone.TerritoryId} lv={zone.Level} ilv={zone.ItemLevel}");
        World.Map.TryLoad(new TargetInstance(zone.TerritoryId, zone.Origin,
            zone.Origin + PlayerSpawnLocal, phase.Weather, zone.LayerFilterKey), zone.Level, zone.ItemLevel);
        CrashTrace.Log("H: TryLoad 回來了");
        EnsureNetworkScope(isCurrent);
        World.ScenarioOrigin = zone.Origin;
        CrashTrace.Log("I: ArmColliderDrops");
        World.Map.ArmColliderDrops(zone.ColliderRemovalPoints.Select(World.Coordinates.ToGlobal));
        EnsureNetworkScope(isCurrent);
        CrashTrace.Log("J: PlaceWaymarks");
        World.PlaceWaymarks(ResolveWaymarks(zone, selectedWaymark));
        EnsureNetworkScope(isCurrent);
        CrashTrace.Log("K: CreateParty");
        var presetOverride = (scenario as IScenarioPartyPreset)?.GetPartyPreset(player.ClassJob.RowId, roleOverride, solo);
        World.CreateParty(player.ClassJob.RowId, roleOverride, solo, presetOverride, remoteRoles, aliasNames,
            appearances, isCurrent);
        EnsureNetworkScope(isCurrent);
        if (!peer)
        {
            var stratName = selectedAi is { } aiIndex && aiIndex >= 0 && aiIndex < scenario.AiStrats.Count
                ? scenario.AiStrats[aiIndex].Name : solo ? "不帶隊友" : "預設";
            World.PracticePositions.Begin(snapshot.PracticeKey, FullName(scenario), stratName);
        }
        return new PreparedScenario(snapshot, freshLoad, solo);
    }

    private void CommitScenarioWorld(PreparedScenario prepared, bool peer, Func<bool>? isCurrent = null)
    {
        var scenario = prepared.Scenario;
        var phase = scenario.Phase;
        var zone = phase.Zone;
        EnsureNetworkScope(isCurrent);
        if (!peer)
        {
            // Peer never creates an arena-death fence or schedules mechanics.
            CrashTrace.Log("L: zone.Run");
            zone.Run(World);
            EnsureNetworkScope(isCurrent);
            CrashTrace.Log("M: phase.Run");
            phase.Run(World);
            EnsureNetworkScope(isCurrent);
            CrashTrace.Log($"N: scenario.Run progress={prepared.ProgressKey}");
            if (scenario is Scenarios.Recorded.RecordedScenario recordedScenario)
            {
                // Solo and network alike: replay the bytes the snapshot pinned at Start.
                // A recorded run without them is a bug, not a cue to reopen the file.
                if (prepared.Recorded is not { } approved)
                    throw new MpProtocolException(MpError.SceneMismatch);
                recordedScenario.RunPrepared(World, prepared.SelectedAi, approved);
            }
            else if (prepared.Recorded is not null)
                throw new MpProtocolException(MpError.SceneMismatch);
            else if (scenario is IProgressScenario progress)
                progress.Run(World, prepared.SelectedAi, prepared.ProgressKey);
            else
                scenario.Run(World, prepared.SelectedAi);
        }
        EnsureNetworkScope(isCurrent);
        CrashTrace.Log("O: 傳送玩家");
        if (prepared.FreshLoad)
            TeleportPlayerToSpawn();
        else if (!peer)
            TeleportPlayerToSpawnIfOutsideArena();
        EnsureNetworkScope(isCurrent);
        RotationSim.Instance?.CaptureRecastsBeforePracticeReset();
        CrashTrace.Log("P: ResetSprintCooldown");
        ResetSprintCooldown();
        ResetPlayerStatuses();
        activeScenario = scenario;
        activeRun = prepared.Snapshot;
        scenarioState = GameScenarioState.Running;
        if (retryInFlight?.IdentityKey == activeRun.IdentityKey)
            retryInFlight = null;
        scenarioElapsed = 0f;
        if (Plugin.Config.SuppressBgm || phase.Bgm == 0)
            Bgm.Reset();
        else
            Bgm.Play(phase.Bgm);
        ChatOutput.Print(new XivChatEntry
        {
            Type = XivChatType.SystemMessage,
            Message = new SeStringBuilder().AddText($"[AnoMech] Starting: {FullName(scenario)}{(prepared.Solo ? " (Solo)" : "")} [{prepared.ProgressKey}]").Build(),
        });
    }
}
