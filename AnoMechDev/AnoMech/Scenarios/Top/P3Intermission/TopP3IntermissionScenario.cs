using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using AnoMech.Scenarios.Top.P3Practice;
using FFXIVEventId = FFXIVClientStructs.FFXIV.Client.Game.Event.EventId;
using static AnoMech.Scenarios.Top.TopConstants;

namespace AnoMech.Scenarios.Top.P3Intermission;

public sealed class TopP3IntermissionState
{
    public PartyRole PlayerRole { get; }

    public TopP3CannonAssignment Assignment { get; }

    public TopP3IntermissionState(PartyRole playerRole, TopP3CannonAssignment assignment)
    {
        PlayerRole = playerRole;
        Assignment = assignment;
    }
}

/// <summary>
/// Standalone TOP P3 intermission: two expanding native 31567 rings, two sets
/// of native 31566 arms, then the fixed eight-position cannon rehearsal.  It is
/// intentionally separate from TopP3MonitorsScenario; selecting this scenario
/// never changes or reuses the monitor assignment state.
/// </summary>
public sealed class TopP3IntermissionScenario : IScenario, IScenarioPartyPreset
{
    private SimWorld world = null!;
    private SimParty party = null!;
    private TopP3IntermissionState state = null!;
    private TopUtils topUtils = null!;
    private SimEnemy? boss;
    private readonly SimEnemy?[] arms = new SimEnemy?[6];
    private SimEventObject? centralVoidzone;
    private bool centralVoidzoneActive;

    public string Name => "P3 轉場練習";
    public IPhase Phase => TopZone.P3;
    public IReadOnlyList<IScenarioAi> AiStrats => [new TopP3IntermissionAi()];

    public void DrawSettings() { }

    public IReadOnlyList<PartyMemberPreset?> GetPartyPreset(
        uint playerJob,
        PartyRole? roleOverride,
        bool solo)
        => TopP3PracticeParty.ForPlayerJob(playerJob, roleOverride);

    public void Run(SimWorld worldParam, int? selectedAi)
    {
        world = worldParam;
        party = world.Party;
        // 每次開始重新點名：六人被點（四狙擊、兩大功率）、兩人不點，站位依優先序推導。
        state = new TopP3IntermissionState(party.PlayerRole, TopP3CannonAssignment.FromShuffled(new Rng().Shuffle(TopP3CannonAssignment.Priority.ToArray())));
        topUtils = new TopUtils(world);
        boss = null;
        centralVoidzone = null;
        centralVoidzoneActive = false;
        Array.Clear(arms);

        if (selectedAi is { } index && index < AiStrats.Count)
            ((IScenarioAi<TopP3IntermissionState>)AiStrats[index]).Run(state, world);

        var runtimeAt = TopP3IntermissionRules.RuntimeAt;
        // AI and mechanism events consume the same runtime clock. The route's
        // first command remains at +5.1s, preserving the preparation movement.
        world.Events.Add(runtimeAt(TopP3IntermissionRules.QueueAt), SpawnBoss);
        world.Events.Add(runtimeAt(TopP3IntermissionRules.QueueAt), PrepareArms);
        world.Events.Add(runtimeAt(TopP3IntermissionRules.FirstArmAppearanceAt),
            () => ShowArmSet(1));
        world.Events.Add(runtimeAt(TopP3IntermissionRules.SecondArmAppearanceAt),
            () => ShowArmSet(2));
        world.Events.Add(runtimeAt(TopP3IntermissionRules.PointMarkerAt), ApplyPointMarkers);
        world.Events.Add(runtimeAt(TopP3IntermissionRules.CentralVoidzoneSpawnAt),
            SpawnCentralVoidzone);
        world.Events.Add(runtimeAt(TopP3IntermissionRules.FirstWaveCastAt), () => StartWave(1));
        world.Events.Add(runtimeAt(TopP3IntermissionRules.SecondWaveCastAt), () => StartWave(2));

        for (var ring = 0; ring < 4; ring++)
        {
            var ringIndex = ring;
            world.Events.Add(runtimeAt(TopP3IntermissionRules.Wave1RingTimes[ringIndex]),
                () => ResolveWave(1, ringIndex));
            world.Events.Add(runtimeAt(TopP3IntermissionRules.Wave2RingTimes[ringIndex]),
                () => ResolveWave(2, ringIndex));
        }

        world.Events.Add(runtimeAt(TopP3IntermissionRules.FirstArmsCastAt),
            () => StartArms(1));
        world.Events.Add(runtimeAt(TopP3IntermissionRules.SecondArmsCastAt),
            () => StartArms(2));
        world.Events.Add(runtimeAt(TopP3IntermissionRules.FirstArmsAt), () => ResolveArms(1));
        world.Events.Add(runtimeAt(TopP3IntermissionRules.SecondArmsAt), () => ResolveArms(2));
        world.Events.Add(runtimeAt(TopP3IntermissionRules.CannonAt), ResolveCannons);
        world.Events.Add(runtimeAt(TopP3IntermissionRules.CannonAt + 0.35f), RemovePointMarkers);
        world.Events.Add(runtimeAt(TopP3IntermissionRules.CentralVoidzoneDespawnAt),
            DespawnCentralVoidzone);
        world.Events.Add(runtimeAt(TopP3IntermissionRules.CompleteAt),
            () =>
            {
                Core.ChatOutput.Coach("[AnoMech] P3 轉場練習完成（31567 → 31566 → 砲）");
                world.CompleteScenario();
            });
    }

    public void Tick(float delta, float elapsed)
    {
        if (!centralVoidzoneActive) return;
        foreach (var role in TopP3IntermissionRules.RecordingOrder)
        {
            var member = party.Get(role);
            if (member is { } character
                && character.IsAlive()
                && TopP3IntermissionRules.IsInsideCentralVoidzone(character.Position))
                character.Die("P3 場中電圈");
        }
    }

    private void PrepareArms()
    {
        foreach (var definition in TopP3IntermissionArmRules.Definitions)
        {
            arms[definition.Slot] = world.SpawnEnemy(new EnemySpawnConfig(
                BNpcBaseId: definition.BnpcBaseId,
                NameId: definition.NameId,
                Level: Level,
                Targetable: false,
                EnemyList: EnemyListMode.Never,
                IsVisible: false,
                Scale: TopP3IntermissionArmRules.NativeScale,
                HitboxRadius: TopP3IntermissionArmRules.NativeHitboxRadius,
                Placement: new Placement(Vector3.Zero, 0f)));
        }
    }

    private void ShowArmSet(int set)
    {
        foreach (var definition in TopP3IntermissionArmRules.ForSet(set))
        {
            var arm = arms[definition.Slot];
            if (arm is not { IsActive: true }) continue;
            arm.SetPosition(new Placement(definition.Center, definition.Rotation));
            arm.SetVisible(true);
            // These are the verified raw appearance controls. They are an
            // early visual cue only; 31566 below owns damage timing.
            arm.PlayActionTimeline(definition.TimelineId);
        }
    }

    private void SpawnCentralVoidzone()
    {
        centralVoidzoneActive = true;
        centralVoidzone = world.SpawnEventObject(new EventObjectSpawnConfig
        {
            EObjId = TopP3IntermissionRules.EventObjectId.P3IntermissionVoidzone,
            Placement = new Placement(Vector3.Zero, 0f),
            TargetableStatus = 5,
            EventId = (FFXIVEventId)TopP3IntermissionRules.EventObjectId.P3IntermissionVoidzoneEvent,
            Radius = TopP3IntermissionRules.CentralVoidzonePacketRadius,
            SpawnVisible = true,
        });
        Core.ChatOutput.Coach("[AnoMech] 場中電圈出現 —— 半徑 6，踩入即死");
    }

    private void DespawnCentralVoidzone()
    {
        centralVoidzoneActive = false;
        centralVoidzone?.Despawn();
        centralVoidzone = null;
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

    private void ApplyPointMarkers()
    {
        foreach (var assignment in state.Assignment.MarkerAssignments)
        {
            var status = assignment.Kind == TopP3CannonKind.Spread
                ? TopP3IntermissionRules.StatusId.SniperCannonMarker
                : TopP3IntermissionRules.StatusId.HighPoweredSniperCannonMarker;
            // AddStatusParam is required for native status-init/VFX; AddStatus
            // with stacks=0 does not create the marker packet in this client.
            party.Get(assignment.Role)?.AddStatusParam(
                status,
                param: 0,
                duration: TopP3IntermissionRules.MarkerDuration);
        }
        var player = state.Assignment.CannonGroupFor(state.PlayerRole);
        var role = player.Kind == TopP3CannonKind.Spread ? "狙擊式波動泡"
            : player.MarkerRole == state.PlayerRole ? "狙擊式大功率波動砲" : "未點名，去分攤";
        Core.ChatOutput.Coach($"[AnoMech] P3 轉場點名 —— 你是「{role}」，等待點 {state.Assignment.WaitLabelFor(state.PlayerRole)}，砲位 {state.Assignment.LabelFor(state.PlayerRole)}（A 為北；優先序 H1 MT ST D1 D2 D3 D4 H2）");
    }

    private void RemovePointMarkers()
    {
        foreach (var role in TopP3IntermissionRules.RecordingOrder)
        {
            party.Get(role)?.RemoveStatus(TopP3IntermissionRules.StatusId.SniperCannonMarker);
            party.Get(role)?.RemoveStatus(TopP3IntermissionRules.StatusId.HighPoweredSniperCannonMarker);
        }
    }

    private void StartWave(int wave)
    {
        // The recording caster is O_1073748025/O_1073748024 (BNpc helper 9020),
        // not BossP3. Keep the boss as scenery and use the verified helper path.
        var source = SpawnHelper(Vector3.Zero, 22f);
        if (source == null) return;
        source.Cast(
            TopP3IntermissionRules.ActionId.WaveRepeaterFirst,
            targetLocation: Vector3.Zero,
            castSeconds: TopP3IntermissionRules.WaveCastSeconds,
            omenDelay: 0f,
            fireDelay: wave == 1
                ? TopP3IntermissionRules.Wave1FireDelay
                : TopP3IntermissionRules.Wave2FireDelay);
        Core.ChatOutput.Coach($"[AnoMech] 地靈脈第{wave}圈 —— 31567 讀條");
    }

    private void ResolveWave(int wave, int ring)
    {
        // Snapshot all live positions before the native effect and before any
        // death/status processing. The two wave sequences are independent.
        var snapshot = Snapshot();
        // Ring 1 is the native 31567 cast that began before this effect.  The
        // later three rings are instant native effects at the same center.
        if (ring > 0)
        {
            var action = ring switch
            {
                1 => TopP3IntermissionRules.ActionId.WaveRepeaterSecond,
                2 => TopP3IntermissionRules.ActionId.WaveRepeaterThird,
                _ => TopP3IntermissionRules.ActionId.WaveRepeaterFourth,
            };
            SpawnInstant(action, Vector3.Zero, target: null);
        }

        foreach (var role in TopP3IntermissionRules.MembersInsideWaveRing(snapshot, ring))
            party.Get(role)?.Die($"P3 地靈脈（31567，第{wave}波第{ring + 1}圈）");
    }

    private void StartArms(int set)
    {
        foreach (var definition in TopP3IntermissionArmRules.ForSet(set))
        {
            var arm = arms[definition.Slot];
            if (arm is not { IsActive: true }) continue;
            arm.Cast(
                TopP3IntermissionRules.ActionId.ColossalBlow,
                targetId: arm.GameObjectId,
                castSeconds: TopP3IntermissionRules.ArmCastSeconds,
                omenDelay: 0f,
                fireDelay: set == 1
                    ? TopP3IntermissionRules.FirstArmFireDelay
                    : TopP3IntermissionRules.SecondArmFireDelay);
            world.Events.Add((set == 1
                    ? TopP3IntermissionRules.FirstArmEffectAfterCast
                    : TopP3IntermissionRules.SecondArmEffectAfterCast) + 1f,
                arm.Despawn);
        }
        Core.ChatOutput.Coach($"[AnoMech] 轉場手臂第{set}組讀條 —— 半徑 {TopP3IntermissionRules.ArmRadius:F0}");
    }

    private void ResolveArms(int set)
    {
        var snapshot = Snapshot();
        var centers = set == 1
            ? TopP3IntermissionRules.FirstArmCenters
            : TopP3IntermissionRules.SecondArmCenters;
        var hits = TopP3IntermissionRules.CountArmHits(snapshot, centers);
        foreach (var hit in hits)
        {
            if (hit.Count == 0) continue;
            party.Get(hit.Role)?.Die($"P3 31566 手臂攻擊 ×{hit.Count}");
        }
    }

    private void ResolveCannons()
    {
        // Every group, marker centre, and victim is derived from one
        // pre-resolution snapshot. Fixed coordinates are AI destinations only.
        var snapshot = Snapshot();
        foreach (var group in state.Assignment.CannonGroups)
            SpawnCannon(group, snapshot);

        var results = state.Assignment.ResolveCannons(snapshot);
        var hitCounts = state.Assignment.CountCannonHits(snapshot);
        foreach (var result in results)
        {
            if (result.Result == TopP3CannonResult.Correct) continue;
            var resultReason = result.Result switch
            {
                TopP3CannonResult.Missing => "未承傷",
                TopP3CannonResult.Overlap => "重疊承傷",
                _ => "判定異常",
            };
            var reason =
                $"P3 {result.Group.Label} 砲失敗：{resultReason}（{result.HitCount}人）";
            var markerPresent = snapshot.Any(
                member => member.Role == result.Group.MarkerRole);
            var hasLiveHit = hitCounts.Any(hit =>
                hit.Count > 0
                && state.Assignment.CannonGroupFor(hit.Role).Label
                    == result.Group.Label);
            if (markerPresent && !hasLiveHit)
            {
                world.FailScenario(reason, result.Group.MarkerRole);
            }
            Plugin.Log.Info($"[AnoMech] {reason}");
        }

        foreach (var hit in hitCounts)
        {
            if (hit.Count == 0) continue;
            var member = party.Get(hit.Role);
            if (member == null || !member.IsAlive()) continue;
            var group = state.Assignment.CannonGroupFor(hit.Role);
            var failedShare = results.Any(result => result.Group.Label == group.Label
                                                    && result.Result != TopP3CannonResult.Correct);
            // A wrong share is a failed resolution for the sole soaker; an
            // overlap is represented by two independent native hits.  Keep the
            // status/damage decision after all hit counts were collected.
            if (hit.Count > 1 || failedShare || topUtils.IsDamageLethal(member, ruin: false))
            {
                member.Die($"P3 {group.Label} 砲攻擊（命中 {hit.Count} 次）");
                continue;
            }
            member.AddStatusParam(
                TopP3IntermissionRules.StatusId.MagicVulnerabilityUp,
                param: 220,
                duration: 4.96f);
        }
    }

    private void SpawnCannon(
        TopP3CannonGroup group,
        IReadOnlyList<TopP3Position> snapshot)
    {
        if (!TopP3IntermissionRules.TryMarkerPosition(
                snapshot, group.MarkerRole, out var center))
        {
            var reason = $"P3 {group.Label} 砲失敗：找不到點名者";
            world.FailScenario(reason, group.MarkerRole);
            Plugin.Log.Info($"[AnoMech] {reason}");
            return;
        }

        var target = party.Get(group.MarkerRole);
        SpawnInstant(
            group.Kind == TopP3CannonKind.Spread
                ? TopP3IntermissionRules.ActionId.SniperCannon
                : TopP3IntermissionRules.ActionId.HighPoweredSniperCannon,
            center,
            target);
    }

    private void SpawnInstant(uint actionId, Vector3 center, SimCharacter? target)
    {
        var helper = SpawnHelper(center, 2f);
        helper?.Cast(actionId, targetLocation: center, targetId: target?.GameObjectId);
    }

    private SimEnemy? SpawnHelper(Vector3 position, float lifetime)
    {
        var helper = world.SpawnEnemy(new EnemySpawnConfig(
            BNpcBaseId: BNpcBaseId.P3MonitorHelper,
            NameId: BNpcNameId.P3MonitorHelper,
            Level: Level,
            Targetable: false,
            EnemyList: EnemyListMode.Never,
            Placement: new Placement(position, 0f)));
        if (helper != null) world.Events.Add(lifetime, helper.Despawn);
        return helper;
    }

    private List<TopP3Position> Snapshot()
    {
        var result = new List<TopP3Position>(TopP3IntermissionRules.RecordingOrder.Count);
        foreach (var role in TopP3IntermissionRules.RecordingOrder)
            if (party.Get(role) is { } member && member.IsAlive())
                result.Add(new TopP3Position(role, member.Position));
        return result;
    }
}
