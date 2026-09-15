using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using static AnoMech.Scenarios.Top.TopConstants;

namespace AnoMech.Scenarios.Top.P3Monitors;

public enum TopP3BossSideOption
{
    Auto,
    Left,
    Right,
}

public enum TopP3PlayerMonitorOption
{
    Auto,
    WithMonitor,
    WithoutMonitor,
}

public sealed class TopP3MonitorsOverrides
{
    public TopP3BossSideOption BossSide { get; set; } = TopP3BossSideOption.Auto;
    public TopP3PlayerMonitorOption PlayerMonitor { get; set; } = TopP3PlayerMonitorOption.Auto;
}

/// <summary>One randomized P3 draw. It is recreated for every scenario start.</summary>
public sealed class TopP3MonitorsState
{
    private readonly Dictionary<PartyRole, TopP3MonitorStatus> statuses = new();

    public PartyRole PlayerRole { get; }
    public TopP3BossSide BossSide { get; }
    public TopP3MonitorAssignment Assignment { get; }
    public IReadOnlyList<PartyRole> MonitorRoles => Assignment.MonitorRoles;
    public IReadOnlyList<PartyRole> NormalRoles => Assignment.NormalRoles;
    public IReadOnlyDictionary<PartyRole, TopP3MonitorStatus> MonitorStatuses => statuses;
    public bool BuffStarted { get; private set; }

    public void MarkBuffStarted() => BuffStarted = true;

    public TopP3MonitorsState(SimParty party, TopP3MonitorsOverrides overrides)
    {
        PlayerRole = party.PlayerRole;
        BossSide = overrides.BossSide switch
        {
            TopP3BossSideOption.Left => TopP3BossSide.Left,
            TopP3BossSideOption.Right => TopP3BossSide.Right,
            _ => Random.Shared.Next(2) == 0 ? TopP3BossSide.Left : TopP3BossSide.Right,
        };

        var monitorRoles = PickMonitorRoles(PlayerRole, overrides.PlayerMonitor);
        Assignment = TopP3MonitorRules.Assign(monitorRoles);
        foreach (var role in MonitorRoles)
            statuses[role] = Random.Shared.Next(2) == 0
                ? TopP3MonitorStatus.Left
                : TopP3MonitorStatus.Right;
    }

    public bool PlayerHasMonitor => Assignment.IsMonitor(PlayerRole);

    public int PlayerSlot => Assignment.SlotOf(PlayerRole);

    public string PlayerSlotLabel => PlayerSlot < 3
        ? $"M{PlayerSlot + 1}（有螢幕）"
        : $"N{PlayerSlot - 2}（無螢幕）";

    public int PlayerPriority => TopP3MonitorRules.PriorityIndex(PlayerRole);

    public TopP3MonitorStatus StatusOf(PartyRole role) => statuses[role];

    public string BossSideLabel => BossSide == TopP3BossSide.Right ? "右（31595）" : "左（31596）";

    public string PlayerStatusLabel => statuses.TryGetValue(PlayerRole, out var status)
        ? status == TopP3MonitorStatus.Right ? "右" : "左"
        : "—";

    private static IReadOnlyList<PartyRole> PickMonitorRoles(
        PartyRole player,
        TopP3PlayerMonitorOption option)
    {
        var priority = TopP3MonitorRules.Priority;
        var others = priority.Where(role => role != player).ToList();
        Shuffle(others);

        if (option == TopP3PlayerMonitorOption.WithMonitor)
            return new[] { player, others[0], others[1] };
        if (option == TopP3PlayerMonitorOption.WithoutMonitor)
            return others.Take(3).ToArray();

        var all = priority.ToList();
        Shuffle(all);
        return all.Take(3).ToArray();
    }

    private static void Shuffle<T>(IList<T> values)
    {
        for (var i = values.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }
}

public sealed class TopP3MonitorsScenario : IScenario
{
    private sealed record CircleWork(
        SimCharacter Target,
        Vector3 Center,
        IReadOnlyList<PartyRole> HitRoles);

    public string Name => "螢幕砲";
    // 維護者-facing label; Name stays the stored identity (scene key, practice document).
    public string MenuName => "小電視";
    public IPhase Phase => TopZone.P3;
    public IReadOnlyList<IScenarioAi> AiStrats => [new TopP3MonitorsAi()];

    private readonly TopP3MonitorsSettingsWindow settingsWindow = new();
    private SimWorld world = null!;
    private SimParty party = null!;
    private TopP3MonitorsState state = null!;
    private TopUtils topUtils = null!;
    private SimEnemy? boss;

    public void DrawSettings() => settingsWindow.Draw();
    public string SettingsIdentity => ScenarioSettings.Identity(settingsWindow.Overrides);
    public bool HasSettings => true;

    public void Run(SimWorld worldParam, int? selectedAi)
    {
        world = worldParam;
        party = worldParam.Party;
        state = new TopP3MonitorsState(party, settingsWindow.Overrides);
        settingsWindow.CurrentState = state;
        topUtils = new TopUtils(world);

        if (selectedAi is { } index && index < AiStrats.Count)
            ((IScenarioAi<TopP3MonitorsState>)AiStrats[index]).Run(state, world);

        world.Events.Add(0.1f, SpawnBoss);
        world.Events.Add(TopP3MonitorRules.BuffAt, ApplyMonitorStatuses);
        world.Events.Add(TopP3MonitorRules.CastStartAt, StartMonitorCast);
        world.Events.Add(TopP3MonitorRules.StatusRemoveAt, RemoveMonitorStatuses);
        world.Events.Add(TopP3MonitorRules.CircleAt, ResolveMonitors);
    }

    public void Tick(float delta, float elapsed) { }

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

    private void ApplyMonitorStatuses()
    {
        foreach (var role in state.MonitorRoles)
        {
            var member = party.Get(role);
            if (member == null) continue;
            // AddStatusInit (inside AddStatusParam) produces the native status VFX;
            // do not manually spawn a second VFX for the status observation packet.
            member.AddStatusParam(
                state.StatusOf(role) == TopP3MonitorStatus.Right
                    ? StatusId.PlayerMonitorRight
                    : StatusId.PlayerMonitorLeft,
                param: 0,
                duration: 0f);
        }
        state.MarkBuffStarted();
    }

    private void StartMonitorCast()
    {
        if (boss is not { IsActive: true } source) return;
        var actionId = state.BossSide == TopP3BossSide.Right
            ? ActionId.P3MonitorRight
            : ActionId.P3MonitorLeft;
        source.Cast(
            actionId,
            castSeconds: TopP3MonitorRules.CastSeconds,
            targetId: source.GameObjectId,
            omenDelay: 0f,
            // Keep the native 9.7s cast; the recorded effect is +9.971s after cast start.
            fireDelay: TopP3MonitorRules.EffectAfterCast - TopP3MonitorRules.CastSeconds);
    }

    private void RemoveMonitorStatuses()
    {
        foreach (var role in state.MonitorRoles)
            party.Get(role)?.RemoveStatus(
                state.StatusOf(role) == TopP3MonitorStatus.Right
                    ? StatusId.PlayerMonitorRight
                    : StatusId.PlayerMonitorLeft);
    }

    private void ResolveMonitors()
    {
        if (boss is not { IsActive: true } source)
        {
            world.FailScenario("P3 螢幕砲失敗：來源不存在");
            Plugin.Log.Info("[AnoMech] P3 螢幕砲失敗：來源不存在");
            return;
        }
        // All four source target lists are selected before any circle or result is touched.
        var sourceTargets = new List<IReadOnlyList<SimCharacter>>
        {
            party.Find.OnSideN(
                source.Placement(),
                TopP3MonitorRules.BossSideMultiplier(state.BossSide),
                count: 2),
        };
        foreach (var role in state.MonitorRoles)
        {
            if (party.Get(role) is not { } monitor) continue;
            sourceTargets.Add(party.Find.OnSideN(
                monitor.Placement(),
                TopP3MonitorRules.SideMultiplier(state.StatusOf(role)),
                count: 2,
                exclude: monitor));
        }

        // Snapshot every party position, then all eight circle centres and victims.
        var positions = new List<TopP3MonitorPosition>();
        foreach (var role in TopP3MonitorRules.Priority)
            if (party.Get(role) is { } member)
                positions.Add(new TopP3MonitorPosition(role, member.Position));

        var circles = new List<CircleWork>(8);
        foreach (var targets in sourceTargets)
            foreach (var target in targets)
            {
                var center = target.Position;
                var hitRoles = TopP3MonitorRules.MembersInsideCircle(positions, center).ToArray();
                circles.Add(new CircleWork(target, center, hitRoles));
            }

        // Native 31597 is kept as one targeted helper per live circle centre.
        foreach (var circle in circles) SpawnCircle(circle);

        var snapshots = circles
            .Select(circle => new TopP3MonitorCircleSnapshot(circle.Center, circle.HitRoles))
            .ToArray();
        var hitCounts = TopP3MonitorRules.CountHits(snapshots);

        foreach (var hit in hitCounts)
        {
            var member = party.Get(hit.Role);
            if (member == null)
            {
                if (TopP3MonitorRules.ResultFor(hit.Count) == TopP3HitResult.Missing)
                {
                    var reason =
                        $"P3 螢幕砲失敗：{TopP3MonitorRules.RoleLabel(hit.Role)} 未承傷（0次，訓練判定）";
                    world.FailScenario(reason, hit.Role);
                    Plugin.Log.Info($"[AnoMech] {reason}");
                }
                continue;
            }
            switch (TopP3MonitorRules.ResultFor(hit.Count))
            {
                case TopP3HitResult.Missing:
                {
                    var reason =
                        $"P3 螢幕砲失敗：{TopP3MonitorRules.RoleLabel(hit.Role)} 未承傷（0次，訓練判定）";
                    world.FailScenario(reason, hit.Role);
                    Plugin.Log.Info($"[AnoMech] {reason}");
                    break;
                }
                case TopP3HitResult.Overlap:
                {
                    var reason =
                        $"P3 螢幕砲失敗：{TopP3MonitorRules.RoleLabel(hit.Role)} 重疊承傷 ×{hit.Count}";
                    world.FailScenario(reason, hit.Role);
                    Plugin.Log.Info($"[AnoMech] {reason}");
                    break;
                }
            }

            for (var i = 0; i < hit.Count && member.IsAlive(); i++)
            {
                if (topUtils.IsDamageLethal(member, ruin: true))
                {
                    member.Die("探測式波動砲");
                    break;
                }
                member.AddStatus(StatusId.MagicVulnerabilityUp, 4.96f);
                member.AddStatus(StatusId.TwiceComeRuin, 6.96f);
            }
        }

        // CircleAt is the native release boundary; let every helper's VFX lifetime
        // elapse before marking the scenario complete. This is added after
        // SpawnCircle queued its equal-time despawns, so teardown runs first.
        world.Events.Add(Duration.MonitorHelperLifetime, world.CompleteScenario);
    }

    private void SpawnCircle(CircleWork circle)
    {
        var helper = world.SpawnEnemy(new EnemySpawnConfig(
            BNpcBaseId: BNpcBaseId.P3MonitorHelper,
            NameId: BNpcNameId.P3MonitorHelper,
            Level: Level,
            Targetable: false,
            EnemyList: EnemyListMode.Never,
            Placement: new Placement(circle.Center, 0f)));
        if (helper != null) world.Events.Add(Duration.MonitorHelperLifetime, helper.Despawn);
        helper?.Cast(ActionId.P3MonitorCircle, castSeconds: 0f, targetId: circle.Target.GameObjectId);
    }
}
