using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using AnoMech.Scenarios.Top.P3Practice;
using static AnoMech.Scenarios.Top.TopConstants;

namespace AnoMech.Scenarios.Top.P3HelloWorld;

public sealed partial class TopP3HelloWorldScenario : IScenario, IScenarioPartyPreset
{
    private readonly TopP3HelloWorldSettingsWindow settings = new();
    private readonly Dictionary<(int Round, TopP3HelloWorldRole Role), SimTether> tethers = [];
    private readonly Dictionary<(int Round, TopP3HelloWorldRole Role), SimTether> prepTethers = [];
    private readonly Dictionary<(int Round, int Index), SimTower> towers = [];
    private SimWorld world = null!;
    private SimParty party = null!;
    private TopP3HelloWorldState state = null!;
    private TopP3HelloWorldTimeline timeline = null!;
    private TopP3HelloWorldSnapshotHistory snapshots = null!;
    private SimEnemy? boss;
    private bool failureReported;
    private TopP3HelloWorldAi movementAi = new();

    public string Name => "P3 Hello World 四輪練習";
    // Drawn after the phase name, so the label must not repeat "P3"; Name stays the
    // stored identity (scene key, run identity, practice document).
    public string MenuName => "Hello World";
    public IPhase Phase => TopZone.P3;
    public IReadOnlyList<IScenarioAi> AiStrats => [movementAi];

    public void DrawSettings() => settings.Draw();
    public string SettingsIdentity => settings.Identity;
    public bool HasSettings => true;
    public IReadOnlyList<PartyMemberPreset?> GetPartyPreset(
        uint playerJob,
        PartyRole? roleOverride,
        bool solo)
        => TopP3PracticeParty.ForPlayerJob(playerJob, roleOverride);
    public void Run(SimWorld worldParam, int? selectedAi)
    {
        foreach (var tether in prepTethers.Values) tether.Despawn();
        prepTethers.Clear();
        foreach (var tether in tethers.Values) tether.Despawn();
        world = worldParam;
        party = world.Party;
        state = new TopP3HelloWorldState(settings.ContactRadius, party.PlayerRole,
            settings.CreatePattern(party.PlayerRole));
        state.Reset();
        timeline = new TopP3HelloWorldTimeline(state);
        snapshots = new TopP3HelloWorldSnapshotHistory();
        snapshots.Record(0f, Snapshot());
        tethers.Clear();
        towers.Clear();
        boss = null;
        failureReported = false;
        movementAi = new TopP3HelloWorldAi();
        movementAi.Run(state, world, selectedAi == 0);
        world.Events.Add(TopP3HelloWorldRules.RuntimeAt(0f), SpawnBoss);
        world.Events.Add(TopP3HelloWorldRules.RuntimeAt(TopP3HelloWorldRules.HelloWorldCastAt),
            StartHelloWorld);
        timeline.Add(TopP3HelloWorldRules.RuntimeAt(TopP3HelloWorldRules.InitialStatusAt),
            (_, _) => ApplyInitialStatuses());
        timeline.Add(TopP3HelloWorldRules.RuntimeAt(TopP3HelloWorldRules.InitialLineStatusAt),
            (_, _) => ApplyInitialLineStatuses());
        timeline.Add(TopP3HelloWorldRules.RuntimeAt(TopP3HelloWorldRules.InitialActiveStatusAt),
            (_, _) => ApplyInitialActiveStatuses());
        for (var round = 1; round <= 4; round++)
        {
            var currentRound = round;
            var times = TopP3HelloWorldRules.RoundTimes(round);
            timeline.Add(TopP3HelloWorldRules.RuntimeAt(times.TowerCastAt),
                (_, _) => StartRound(currentRound));
            timeline.Add(TopP3HelloWorldRules.RuntimeAt(times.LineStatusAt),
                (_, _) => ApplyRoundLineStatuses(currentRound));
            timeline.Add(TopP3HelloWorldRules.RuntimeAt(times.AoeAt),
                (_, snapshot) => ResolveAoes(currentRound, snapshot));
            timeline.Add(TopP3HelloWorldRules.RuntimeAt(times.TowerEffectAt),
                (_, snapshot) => ResolveTowers(currentRound, snapshot));
        }

        timeline.Add(TopP3HelloWorldRules.RuntimeAt(TopP3HelloWorldRules.CompleteAt),
            (_, _) => CompleteRun(), afterContact: true);
    }

    public void Tick(float delta, float elapsed)
    {
        if (state == null!) return;
        foreach (var role in TopP3HelloWorldRules.RecordingOrder)
        {
            if (party.Get(role) is { } member && !member.IsAlive()
                && state.Members[role].Alive)
                state.MarkPlayerDead(role);
        }
        if (!state.Failed && !state.Completed)
            snapshots.Record(elapsed, Snapshot());
        timeline.Advance(
            elapsed,
            snapshots.At,
            contacts =>
            {
                foreach (var contact in contacts)
                    ApplyContactStatus(contact, elapsed);
            },
            (role, snapshot) => ResolveColorExplosion(role, snapshot),
            (resolved, snapshot) => BreakTether(resolved, snapshot));
        movementAi.Tick(elapsed);

        if (state.Failed && !failureReported)
            ReportFailure();
    }

    private void SpawnBoss()
    {
        boss = world.SpawnEnemy(new EnemySpawnConfig(
            BNpcBaseId: BNpcBaseId.P3MonitorBoss,
            NameId: BNpcNameId.P3MonitorBoss,
            Level: Level,
            Targetable: true,
            EnemyList: EnemyListMode.Always,
            ModelCharaId: 3775,
            Scale: 1.4f,
            HitboxRadius: 12.502f,
            Placement: new Placement(Vector3.Zero, MathF.PI)));
    }

    private void StartHelloWorld()
    {
        boss?.Cast(
            TopP3HelloWorldRules.ActionId.HelloWorld,
            targetLocation: Vector3.Zero,
            castSeconds: TopP3HelloWorldRules.HelloWorldCastSeconds,
            omenDelay: 0f,
            fireDelay: 0f);
        ChatOutput.Coach("[AnoMech] 31573 Hello World —— 四輪練習開始");
    }

    private void ApplyInitialStatuses()
    {
        state.SeedInitial(TopP3HelloWorldRules.RuntimeAt(TopP3HelloWorldRules.InitialStatusAt));
        foreach (var role in TopP3HelloWorldRules.RecordingOrder)
        {
            AddStatus(role, TopP3HelloWorldRules.StatusId.NeedStack,
                TopP3HelloWorldRules.InitialNeedStackDuration);
            if (state.Members[role].NeedDefamation)
                AddStatus(role, TopP3HelloWorldRules.StatusId.NeedDefamation,
                    TopP3HelloWorldRules.InitialNeedDefamationDuration);
        }
        foreach (var role in state.Pattern.RoleGroup(1, TopP3HelloWorldRole.Defamation))
        {
            AddStatus(role, TopP3HelloWorldRules.StatusId.PrepDefamation, 2.987f);
            AddStatus(role, state.Pattern.DefamationColor == TopP3HelloWorldColor.Blue
                ? TopP3HelloWorldRules.StatusId.PrepBlueRot
                : TopP3HelloWorldRules.StatusId.PrepRedRot, 2.987f);
        }
        foreach (var role in state.Pattern.RoleGroup(1, TopP3HelloWorldRole.Stack))
        {
            AddStatus(role, TopP3HelloWorldRules.StatusId.PrepStack, 2.987f);
            AddStatus(role, state.Pattern.StackColor == TopP3HelloWorldColor.Blue
                ? TopP3HelloWorldRules.StatusId.PrepBlueRot
                : TopP3HelloWorldRules.StatusId.PrepRedRot, 2.987f);
        }
    }

    private void ApplyInitialLineStatuses()
    {
        for (var round = 1; round <= 4; round++)
        {
            var duration = TopP3HelloWorldRules.RoundTimes(round).LineStatusAt
                - TopP3HelloWorldRules.InitialLineStatusAt;
            AddInitialLinePair(state.Pattern.RoleGroup(round, TopP3HelloWorldRole.LocalTether),
                TopP3HelloWorldRules.StatusId.PrepLocalTether, duration);
            AddInitialLinePair(state.Pattern.RoleGroup(round, TopP3HelloWorldRole.RemoteTether),
                TopP3HelloWorldRules.StatusId.PrepRemoteTether, duration);
        }
        // Practice preview starts at the initial status grant, not a recorded tether event.
        AddRoundPrepTethers(1, TopP3HelloWorldRules.InitialLineStatusAt);
    }

    private void ApplyInitialActiveStatuses()
    {
        var now = TopP3HelloWorldRules.RuntimeAt(TopP3HelloWorldRules.InitialActiveStatusAt);
        state.ApplyInitialActiveStatuses(now);
        foreach (var role in state.Pattern.RoleGroup(1, TopP3HelloWorldRole.Defamation))
        {
            AddStatus(role, state.Pattern.DefamationColor == TopP3HelloWorldColor.Blue
                    ? TopP3HelloWorldRules.StatusId.BlueRot : TopP3HelloWorldRules.StatusId.RedRot,
                TopP3HelloWorldRules.InitialActiveDuration);
            AddStatus(role, TopP3HelloWorldRules.StatusId.Defamation,
                TopP3HelloWorldRules.ActiveExpiryAt(1) - now);
        }
        foreach (var role in state.Pattern.RoleGroup(1, TopP3HelloWorldRole.Stack))
        {
            AddStatus(role, state.Pattern.StackColor == TopP3HelloWorldColor.Blue
                    ? TopP3HelloWorldRules.StatusId.BlueRot : TopP3HelloWorldRules.StatusId.RedRot,
                TopP3HelloWorldRules.InitialActiveDuration);
            AddStatus(role, TopP3HelloWorldRules.StatusId.Stack,
                TopP3HelloWorldRules.ActiveExpiryAt(1) - now);
        }
    }

    private void StartRound(int round)
    {
        if (state.Failed) return;
        foreach (var tower in state.Pattern.TowersForRound(round))
        {
            var spawned = world.SpawnTower(new EventObjectSpawnConfig
            {
                EObjId = tower.Color == TopP3HelloWorldColor.Blue
                    ? TopP3HelloWorldRules.EObjId.TowerSolo
                    : TopP3HelloWorldRules.EObjId.TowerPair,
                Placement = new Placement(tower.Center, 0f),
                TargetableStatus = 5,
                SpawnVisible = true,
                Radius = TopP3HelloWorldRules.TowerRadius,
                Lifetime = 12f,
            }, [0, 1, 2], TopP3HelloWorldRules.TowerRadius);
            if (spawned != null) towers[(round, tower.Index)] = spawned;

            var helper = SpawnHelper(tower.Center, 12f);
            helper?.Cast(
                tower.Color == TopP3HelloWorldColor.Blue
                    ? TopP3HelloWorldRules.ActionId.BlueTower
                    : TopP3HelloWorldRules.ActionId.RedTower,
                targetLocation: tower.Center,
                castSeconds: 10f,
                omenDelay: 0f,
                fireDelay: 0f);
        }
        ChatOutput.Coach($"[AnoMech] R{round} 四塔出現 —— 每塔 1 人");
    }

    private void ApplyRoundLineStatuses(int round)
    {
        if (state.Failed) return;
        var times = TopP3HelloWorldRules.RoundTimes(round);
        state.ActivateTethers(round, TopP3HelloWorldRules.RuntimeAt(times.LineStatusAt));
        AddRoundPair(round, TopP3HelloWorldRole.RemoteTether,
            TopP3HelloWorldRules.StatusId.RemoteTether,
            TopP3HelloWorldRules.TetherId.Remote,
            TopP3HelloWorldRules.ActiveTetherDuration);
        AddRoundPair(round, TopP3HelloWorldRole.LocalTether,
            TopP3HelloWorldRules.StatusId.LocalTether,
            TopP3HelloWorldRules.TetherId.Local,
            TopP3HelloWorldRules.ActiveTetherDuration);
        ChatOutput.Coach($"[AnoMech] R{round} 線啟動 —— 近線靠近、遠線拉遠即觸發；未斷線逾時失敗");
    }

    private void ResolveAoes(
        int round,
        IReadOnlyDictionary<PartyRole, Vector3> snapshot)
    {
        if (state.Failed) return;
        foreach (var role in state.ActiveOwners(TopP3HelloWorldRole.Defamation))
            if (snapshot.TryGetValue(role, out var center))
                SpawnInstant(TopP3HelloWorldRules.ActionId.OverflowBug, center, party.Get(role));
        foreach (var role in state.ActiveOwners(TopP3HelloWorldRole.Stack))
            if (snapshot.TryGetValue(role, out var center))
                SpawnInstant(TopP3HelloWorldRules.ActionId.SynchronizationBug, center, party.Get(role));

        if (!state.ResolveAoes(round, snapshot))
        {
            ReportFailure();
            return;
        }
        ApplyMechanicStatuses(TopP3HelloWorldRules.RuntimeAt(
            TopP3HelloWorldRules.RoundTimes(round).AoeAt));
        if (round < 4)
            AddRoundPrepTethers(round + 1, TopP3HelloWorldRules.RoundTimes(round).AoeAt);
    }

    private void ResolveTowers(
        int round,
        IReadOnlyDictionary<PartyRole, Vector3> snapshot)
    {
        if (state.Failed) return;
        var times = TopP3HelloWorldRules.RoundTimes(round);
        if (!state.ResolveTowers(
                round,
                snapshot,
                TopP3HelloWorldRules.RuntimeAt(times.TowerEffectAt)))
        {
            ReportFailure();
            return;
        }
        foreach (var tower in state.Pattern.TowersForRound(round))
        {
            if (towers.Remove((round, tower.Index), out var spawned))
                spawned.Despawn();
            var hit = TopP3HelloWorldRules.RecordingOrder.First(role =>
                snapshot.TryGetValue(role, out var position)
                && TopP3HelloWorldRules.Inside(position, tower.Center,
                    TopP3HelloWorldRules.TowerRadius));
            AddStatus(hit,
                tower.Color == TopP3HelloWorldColor.Blue
                    ? TopP3HelloWorldRules.StatusId.BlueLatent
                    : TopP3HelloWorldRules.StatusId.RedLatent,
                TopP3HelloWorldRules.InitialLatentDuration);
        }
    }

    private void BreakTether(
        TopP3HelloWorldTetherBreak resolved,
        IReadOnlyDictionary<PartyRole, Vector3> snapshot)
    {
        if (!tethers.Remove((resolved.Round, resolved.Role), out var tether)) return;
        tether.Despawn();
        var status = resolved.Role == TopP3HelloWorldRole.LocalTether
            ? TopP3HelloWorldRules.StatusId.LocalTether : TopP3HelloWorldRules.StatusId.RemoteTether;
        foreach (var role in state.Pattern.RoleGroup(resolved.Round, resolved.Role))
        {
            party.Get(role)?.RemoveStatus(status);
            if (snapshot.TryGetValue(role, out var position))
                SpawnInstant(TopP3HelloWorldRules.ActionId.TetherBreak, position, party.Get(role));
        }
    }

}
