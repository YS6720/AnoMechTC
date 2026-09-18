using System;
using System.Linq;
using System.Numerics;
using AnoMech.Core;
using AnoMech.Core.Game;
using AnoMech.Core.SimObjects;
using AnoMech.Scenarios.Top.P3Intermission;
using static AnoMech.Scenarios.Top.TopConstants;

namespace AnoMech.Scenarios.Top.P4BlueScreen;

public sealed class TopP4BlueScreenMechanics(SimWorld world, TopP4BlueScreenState state)
{
    private SimEnemy? boss;
    private Vector2[] echoDirections = [];
    private bool failed;
    public bool? Passed { get; private set; }

    // 時間軸以 2026-09-16 實戰錄製（20260916-220320-t1122，pull 9）對齊：t0＝P4 可選中，
    // 31617 讀條在 9.30。逐招時刻、詠唱長度與命中易傷都取自錄製；對照表在
    // docs/verification/top-p1-p4-vs-recording-20260916.md §2.6。
    // 分散砲：回音讀條（31616，5.0s）比命中（31614）早 0.54s 起手；地靈脈第 1 圈讀條 4.7s。
    public void Run()
    {
        boss = world.SpawnEnemy(new EnemySpawnConfig(BNpcBaseId.P3MonitorBoss, BNpcNameId.OmegaFinal,
            Level: Level, Targetable: true, EnemyList: EnemyListMode.Always, ModelCharaId: 3775, Scale: 1.4f,
            HitboxRadius: 12.502f, Placement: new Placement(Vector3.Zero, MathF.PI)));
        world.Events.Add(9.3f, () => boss?.Cast(ActionId.P4WaveCannonCast, castSeconds: 4.7f, targetId: boss.GameObjectId));
        world.Events.Add(11.84f, () => MarkStacks(0));
        world.Events.Add(14.36f, () => StartEcho());
        world.Events.Add(14.90f, () => ResolveSpread(0));
        world.Events.Add(17.35f, () => Visual(TopP3IntermissionRules.ActionId.WaveRepeaterFirst, Vector2.Zero, 4.7f));
        world.Events.Add(19.66f, () => ResolveEcho(ActionId.P4WaveCannonVisual2));
        world.Events.Add(19.84f, () => ResolveStacks(0));
        world.Events.Add(21.98f, () => MarkStacks(1));
        world.Events.Add(22.34f, () => ResolveRing(0));
        world.Events.Add(24.42f, () => ResolveRing(1));
        world.Events.Add(24.42f, () => boss?.Cast(ActionId.P4WaveCannonVisual2, castSeconds: 0f, targetId: boss.GameObjectId));
        world.Events.Add(24.55f, () => StartEcho());
        world.Events.Add(25.09f, () => ResolveSpread(1));
        world.Events.Add(26.47f, () => ResolveRing(2));
        world.Events.Add(28.52f, () => ResolveRing(3));
        world.Events.Add(29.81f, () => ResolveEcho(ActionId.P4WaveCannonVisual1));
        world.Events.Add(29.99f, () => ResolveStacks(1));
        world.Events.Add(32.22f, () => MarkStacks(2));
        world.Events.Add(34.80f, () => boss?.Cast(ActionId.P4WaveCannonVisual3, castSeconds: 0f, targetId: boss.GameObjectId));
        world.Events.Add(34.80f, () => Visual(TopP3IntermissionRules.ActionId.WaveRepeaterFirst, Vector2.Zero, 4.7f));
        world.Events.Add(34.80f, () => StartEcho());
        world.Events.Add(35.34f, () => ResolveSpread(2));
        world.Events.Add(39.80f, () => ResolveRing(0));
        world.Events.Add(40.06f, () => ResolveEcho(ActionId.P4WaveCannonVisual4));
        world.Events.Add(40.24f, () => ResolveStacks(2));
        world.Events.Add(41.89f, () => ResolveRing(1));
        world.Events.Add(43.94f, () => ResolveRing(2));
        world.Events.Add(45.98f, () => ResolveRing(3));
        world.Events.Add(47.05f, () => boss?.Cast(ActionId.P4BlueScreen, castSeconds: 7.7f, targetId: boss.GameObjectId));
        world.Events.Add(55.11f, () => Visual(ActionId.P4BlueScreenSuccess, Vector2.Zero, 0.7f));
        world.Events.Add(57.1f, Finish);
    }

    // 命中後的魔法耐性低下（錄製每次 31614／31615 命中都掛 ~1.9s）：帶著它再吃一發就死。
    private const float HitVulnerabilitySeconds = 1.93f;

    private void ApplyHitVulnerability(string cause)
    {
        for (var i = 0; i < 8; i++)
        {
            var member = world.Party.Get(i);
            if (member == null || !member.IsAlive()) continue;
            if (member.HasStatus(StatusId.MagicVulnerabilityUp))
            {
                member.Die($"{cause}（帶魔法耐性低下再次命中）");
                continue;
            }
            member.AddStatus(StatusId.MagicVulnerabilityUp, HitVulnerabilitySeconds);
        }
    }

    // 回音讀條（31616）在分散砲命中前 0.54s 起手：線的方向取讀條起手時的站位。
    private void StartEcho()
    {
        var positions = Snapshot();
        echoDirections = positions.Where(p => p != null).Select(p => p!.Value).ToArray();
        foreach (var direction in echoDirections)
            Visual(ActionId.WaveCannon_7B80, direction, 5.0f);
    }

    // 錄製裡 22393 的點名特效（n4r1_b2_g06x.avfx）每輪持續 10.04s：從點名一直掛到分攤命中後約 2s
    // 才消失。施法的 helper 要活過這段，提早 despawn 會把特效一起收掉（維護者 2026-09-17 實機回報）。
    private const float StackMarkerSeconds = 10.5f;

    private void MarkStacks(int round)
    {
        foreach (var role in state.StackTargets[round])
        {
            var member = world.Party.Get(role);
            if (member == null || !member.IsAlive()) continue;
            var helper = Helper(Vector2.Zero);
            helper?.Cast(ActionId.P4StackTarget, castSeconds: 0f, targetId: member.GameObjectId);
            if (helper != null) world.Events.Add(StackMarkerSeconds, helper.Despawn);
        }
    }

    private void ResolveSpread(int round)
    {
        var positions = Snapshot();
        var directions = positions.Where(p => p != null).Select(p => p!.Value).ToArray();
        Fail(TopP4BlueScreenRules.FailedSpreads(positions, directions), $"P4 第 {round + 1} 輪：分散砲重疊");
        foreach (var direction in directions)
            Visual(ActionId.P4Spread, direction);
        ApplyHitVulnerability($"P4 第 {round + 1} 輪：分散砲");
    }

    private void ResolveEcho(uint animation)
    {
        boss?.Cast(animation, castSeconds: 0f, targetId: boss.GameObjectId);
        var positions = Snapshot();
        Fail(positions.Select(p => p is { } pos && echoDirections.Any(d => TopP4BlueScreenRules.LineContains(d, pos))).ToArray(),
            "P4：未離開原本的分散砲線");
    }

    private void ResolveStacks(int round)
    {
        var positions = Snapshot();
        var targets = state.StackTargets[round].Select(role => (int)role).ToArray();
        Fail(TopP4BlueScreenRules.FailedStacks(positions, targets), $"P4 第 {round + 1} 輪：分攤不足四人、重疊或未承傷");
        foreach (var target in targets)
            if (positions[target] is { } direction) Visual(ActionId.P4Stack, direction);
        ApplyHitVulnerability($"P4 第 {round + 1} 輪：分攤");
    }

    private void ResolveRing(int ring)
    {
        if (ring > 0)
        {
            var action = ring switch
            {
                1 => TopP3IntermissionRules.ActionId.WaveRepeaterSecond,
                2 => TopP3IntermissionRules.ActionId.WaveRepeaterThird,
                3 => TopP3IntermissionRules.ActionId.WaveRepeaterFourth,
                _ => 0U,
            };
            if (action != 0) Visual(action, Vector2.Zero);
        }
        Fail(Snapshot().Select(p => p is { } pos && TopP4BlueScreenRules.RingContains(pos, ring)).ToArray(),
            $"P4：踩到地靈脈第 {ring + 1} 圈");
    }

    private void Finish()
    {
        Passed = !failed && world.Result != ScenarioResult.Failed
            && Enumerable.Range(0, 8).All(i => world.Party.Get(i)?.IsAlive() == true);
        if (Passed == true)
        {
            world.CompleteScenario();
            ChatOutput.Coach("[AnoMech] P4 藍屏練習完成：機制通過（不含輸出／減傷檢定）。");
            return;
        }

        const string reason = "P4 藍屏練習失敗：本輪有機制失誤";
        world.FailScenario(reason);
        Plugin.Log.Info($"[AnoMech] {reason}");
    }

    // SnapshotPosition, not Position: the game reads the position slightly before the omen
    // ends, so stepping in at the last moment is still safe (維護者 2026-09-18）。
    private Vector2?[] Snapshot() => Enumerable.Range(0, 8).Select(i =>
        world.Party.Get(i) is { } member && member.IsAlive()
            ? (Vector2?)new Vector2(member.SnapshotPosition.X, member.SnapshotPosition.Z) : null).ToArray();

    private void Fail(bool[] failures, string cause)
    {
        if (!failures.Any(failure => failure)) return;
        failed = true;
        for (var i = 0; i < 8; i++)
        {
            if (!failures[i]) continue;
            if (world.Party.Get(i) is { } member && member.IsAlive())
                member.Die(cause);
        }
    }

    private SimEnemy? Helper(Vector2 direction) => world.SpawnEnemy(new EnemySpawnConfig(
        BNpcBaseId.P3MonitorHelper, BNpcNameId.OmegaFinal, Targetable: false,
        EnemyList: EnemyListMode.Never, IsVisible: false,
        Placement: new Placement(Vector3.Zero, MathF.Atan2(direction.X, direction.Y))));

    private void Visual(uint actionId, Vector2 direction, float duration = 0f)
    {
        var helper = Helper(direction);
        helper?.Cast(actionId, castSeconds: duration, targetId: helper.GameObjectId);
        if (helper != null) world.Events.Add(duration + 3f, helper.Despawn);
    }
}
