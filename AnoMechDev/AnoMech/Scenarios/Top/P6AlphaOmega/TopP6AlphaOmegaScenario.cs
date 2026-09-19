using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using static AnoMech.Scenarios.Top.TopConstants;

namespace AnoMech.Scenarios.Top.P6AlphaOmega;

// 20260919-151921-t1122.jsonl, t0 = 5155.160 (Cosmo Memory cast).
// Cast packets encode rotation in [-pi, pi], NOT [0, 2pi]. Use the original
// Exasquares/WC2 placements and propagation rather than treating packet angles as radians.
// Continue through the first Wave Cannon, two autos and the second Cosmo Arrow.
public sealed class TopP6AlphaOmegaScenario : IProgressScenario
{
    public string Name => "P6 阿爾法歐米茄（開場）";
    public IPhase Phase => TopZone.P6;
    public bool SupportsSolo => true;
    public IReadOnlyList<IScenarioAi> AiStrats { get; } = [new TopP6AlphaOmegaAi()];
    public IReadOnlyList<ScenarioProgress> Progresses { get; } =
        [new("full", "完整開場"), new("unlimited", "波動砲：限制解除")];

    private SimWorld world = null!;
    private SimParty party = null!;
    private DamageSolver damage = null!;
    private SimEnemy? boss;
    private bool failed;
    private bool solo;
    private bool unlimitedOnly;
    private readonly Rng rng = new();

    // Actual effects, not cast-end estimates: packets include ~0.3s cast slide.
    private static readonly (float Swing, float Hit)[] AutoAttacks =
        [(10.143f, 10.951f), (13.281f, 14.089f), (44.327f, 45.128f), (47.451f, 48.253f)];
    // Owner-approved cactbot TOP timeline (not the incomplete local capture):
    // Wild Charge 1244.3; swings 1248.4/1251.6; hits 1249.3/1252.5;
    // Cosmo Arrow effect 1258.7. Recheck against the complete gameplay video.
    // https://github.com/OverlayPlugin/cactbot/blob/main/ui/raidboss/data/06-ew/ultimate/the_omega_protocol.txt
    private static readonly (float Swing, float Hit)[] FollowUpAutoAttacks =
        [(4.1f, 5f), (7.3f, 8.2f)];
    internal const float SecondArrowDelay = 6.5f; // Sequence cast starts at +1.9s.
    private static readonly float[] ExaflareOffsets = [0f, 1.033f, 2.006f, 3.033f];
    private const float UnlimitedAt = 48.562f;
    internal const float FirstPuddleAt = 10.033f; // Relative to Unlimited's cast.
    internal const int PuddleCount = 6;
    internal const float PuddleInterval = 2.002f;
    internal const float PuddleDelay = 2.990f;
    internal const float LastPuddleAt = FirstPuddleAt + (PuddleCount - 1) * PuddleInterval;
    // The opening capture ends after bait four. Follow-up offsets use the existing
    // P6 Wave Cannon sequence, starting as the last puddle resolves.
    internal const float CannonAt = LastPuddleAt + PuddleDelay;
    internal const float SecondProteanAt = CannonAt + 5.04f;
    private const float WildChargeAt = CannonAt + 11.37f;

    // 32626 盲信: recorded fixed south knockback to local Z≈22.45.
    // Skip the 54.6s non-interactive movie, but retain the 7.179s return window.
    private const uint TransitionKnockback = 32626;
    private const float KnockbackAt = 1.166f;
    private const float ReturnAt = 1.4f;
    private const float ReturnWindow = 7.179f;
    private const float KnockbackLandingZ = 22.45f;

    public void Run(SimWorld worldParam, int? selectedAi) => Run(worldParam, selectedAi, "full");

    public void Run(SimWorld worldParam, int? selectedAi, string progressKey)
    {
        if (!Progresses.Any(progress => progress.Key == progressKey))
            throw new ArgumentOutOfRangeException(nameof(progressKey), progressKey, "Unknown P6 progress key.");
        unlimitedOnly = progressKey == "unlimited";
        if (selectedAi is not null and not 0)
        {
            worldParam.FailScenario($"P6 開場不存在第 {selectedAi} 個打法。");
            return;
        }
        world = worldParam;
        party = world.Party;
        solo = selectedAi is null;
        damage = new DamageSolver(party);
        damage.SetStatuses(DamageType.Magic, StatusId.MagicVulnerabilityUp);
        failed = false;
        boss = null;
        world.Events.Add(0.2f, Start);
    }

    public void Tick(float delta, float elapsed) { }

    private void Start()
    {
        boss = world.SpawnEnemy(new EnemySpawnConfig(
            BNpcBaseId: BNpcBaseId.AlphaOmega, NameId: BNpcNameId.AlphaOmega, Level: 90,
            Targetable: false, EnemyList: EnemyListMode.Always, IsVisible: true,
            Placement: new Placement(Vector3.Zero, -MathF.PI)));
        if (boss == null)
        {
            Fail("P6 阿爾法歐米茄未能生成。");
            return;
        }
        if (unlimitedOnly)
        {
            world.EnforceArenaBoundary(Geometry.ArenaRadius);
            boss.SetTargetable(true);
            boss.AddStatusParam(StatusId.CodeMi, 0);
            foreach (var member in party.ActiveMembers())
                member.SetPosition(new Placement(Vector3.Zero, MathF.PI));
            world.Events.Add(1f, ScheduleUnlimitedWaveCannon);
            return;
        }
        // Replace the zone's normal fence: the transition explicitly puts us outside it.
        world.EnforceArenaBoundary(23f);
        foreach (var member in party.ActiveMembers())
            member.SetPosition(new Placement(new Vector3(0f, 0f, 4f), MathF.PI));
        // TopZone initializes its map effects at t=1; apply the recorded wall-hide after that.
        world.Events.Add(0.9f, () => world.Map.AddEffect(0x00040008, 0));
        ScheduleHelper(0f, KnockbackAt, new Placement(Vector3.Zero, 0f),
            TransitionKnockback, 0.9f, 0f, false);
        world.Events.Add(KnockbackAt, () =>
        {
            foreach (var member in party.ActiveMembers())
            {
                // Fixed southward displacement; measured local-player slide is ~100y/s.
                var distance = KnockbackLandingZ - member.Position.Z;
                ((ISimPartyMember)member).Knockback(member.Position - Vector3.UnitZ, distance, 100f);
            }
        });
        world.Events.Add(ReturnAt, () =>
        {
            if (failed || boss == null) return;
            boss.SetTargetable(true);
            boss.SetTarget(party.Get(PartyRole.MainTank), follow: false);
            world.Map.AddEffect(0x00010002, 0);
            Core.ChatOutput.Coach("[AnoMech] 擊退後可往場中移動；7.179 秒後開始宇宙記憶。已略過不可操作過場。");
            foreach (var member in party.ActiveMembers())
            {
                if (member is SimPlayer) continue;
                var role = ((ISimPartyMember)member).Role;
                member.MoveTo(new Vector3(0f, 0f, role == PartyRole.MainTank ? -8f : role == PartyRole.OffTank ? 16f : 6f));
            }
        });
        world.Events.Add(ReturnAt + ReturnWindow, StartCombat);
    }

    private void StartCombat()
    {
        if (failed || boss == null) return;
        world.EnforceArenaBoundary(Geometry.ArenaRadius);
        ScheduleBossCast(0f, ActionId.CosmoMemory, 5.7f, 5.996f);
        world.Events.Add(5.996f, () => damage.Resolve(boss, ActionId.CosmoMemory, [DamageType.Magic], []));
        // Recorded status+ at t0+8.324, before the first 31747 swing.
        world.Events.Add(8.324f, () => boss?.AddStatusParam(StatusId.CodeMi, 0));
        var inFirst = rng.NextBool();
        ScheduleCosmoArrow(inFirst);
        if (!solo) ((IScenarioAi<bool>)AiStrats[0]).Run(inFirst, world);
        ScheduleCosmoDive();
        ScheduleAutoAttacks(AutoAttacks);
        world.Events.Add(UnlimitedAt, ScheduleUnlimitedWaveCannon);
    }

    private void ScheduleBossCast(float at, uint action, float cast, float hit)
    {
        world.Events.Add(at, () =>
        {
            if (!failed && boss != null)
                boss.BeginRecordedCast(action, ActionType.Action, cast, 0f,
                    boss.Rotation, boss.Position, boss.GameObjectId);
        });
        world.Events.Add(hit, () =>
        {
            if (!failed && boss != null) Release(boss, action, boss);
        });
    }

    private void ScheduleCosmoArrow(bool inFirst)
    {
        Core.ChatOutput.Coach($"[AnoMech] 宇宙天箭：{(inFirst ? "內先" : "外先")}；兩點集合，雙坦離群引導宇宙龍炎。");
        world.Events.Add(14.40f, () => boss?.Cast(ActionId.CosmoArrow,
            targetLocation: new Vector3(-0.008f, -0.015f, -0.008f), targetId: boss?.GameObjectId));
        TopP6CosmoArrowSequence.Run(world, damage, inFirst, 12.5f);
    }

    private void ScheduleAutoAttacks((float Swing, float Hit)[] attacks)
    {
        var firstHelper = SpawnHelper(new Placement(Vector3.Zero, 0f));
        var farthestHelper = SpawnHelper(new Placement(Vector3.Zero, 0f));
        foreach (var (swing, hit) in attacks)
        {
            SimCharacter? first = null;
            SimCharacter? farthest = null;
            world.Events.Add(swing, () =>
            {
                if (failed || boss == null) return;
                Span<float> distances = stackalloc float[8];
                distances.Fill(float.PositiveInfinity);
                foreach (var member in party.ActiveMembers())
                    distances[(int)((ISimPartyMember)member).Role] =
                        Vector3.DistanceSquared(member.Position, boss.Position);
                var targets = TopP6AutoAttackTargets.Select(distances, solo, party.PlayerRole);
                first = party.Get(targets.First);
                farthest = party.Get(targets.Farthest);
                boss.SetTarget(first, follow: false);
                if (first != null) boss.Face(first);
                Release(boss, ActionId.AlphaOmegaAutoAttack, boss);
            });
            world.Events.Add(hit, () =>
            {
                // Select both BEFORE damage: MT can bait both and die to the second hit.
                HitTarget(first, ActionId.Unknown7ddf, false, 0, true, firstHelper);
                HitTarget(farthest, ActionId.Unknown7ddf, false, 0, true, farthestHelper);
            });
        }
    }

    private void ScheduleCosmoDive()
    {
        ScheduleBossCast(29.592f, ActionId.CosmoDive, 5.3f, 35.189f);
        SimCharacter[] targets = [];
        world.Events.Add(35.189f, () =>
        {
            if (boss != null)
                targets = party.ActiveMembers().OrderBy(member => Vector3.DistanceSquared(member.Position, boss.Position)).Take(3).ToArray();
        });
        // 31654 is only the boss animation. Real 8y tank circles / 6y stack hit 2.4s later.
        world.Events.Add(37.592f, () =>
        {
            if (targets.Length < 3) return;
            HitTarget(targets[0], ActionId.CosmoDive_7BA7, true, 0, true);
            HitTarget(targets[1], ActionId.CosmoDive_7BA7, true, 0, true);
            HitTarget(targets[2], ActionId.CosmoDive_7BA8, false, 6, false);
        });
    }

    private void HitTarget(SimCharacter? target, uint action, bool tankbuster, int stack, bool applyVulnerability,
        SimEnemy? preparedHelper = null)
    {
        if (failed || target == null || !target.IsAlive()) return;
        var placement = new Placement(target.Position, boss?.Rotation ?? 0f);
        var helper = preparedHelper ?? SpawnHelper(placement);
        if (helper == null) return;
        helper.SetPosition(placement);
        Release(helper, action, target);
        damage.Resolve(helper, action, tankbuster ? [DamageType.Magic, DamageType.TankBuster] : [DamageType.Magic],
            applyVulnerability ? [(StatusId.MagicVulnerabilityUp, 2f)] : [], stackMinTargets: stack);
        if (preparedHelper == null) world.Events.Add(2f, helper.Despawn);
    }

    private void ScheduleUnlimitedWaveCannon()
    {
        ScheduleBossCast(0f, ActionId.UnlimitedWaveCannon, 4.7f, 4.993f);
        var clockwise = rng.NextBool();
        Core.ChatOutput.Coach($"[AnoMech] 首次波動砲：限制解除：{(clockwise ? "順時針" : "逆時針")}；前兩圈直走、第三圈轉斜向，第六圈放下即回八方。");
        for (var lane = 0; lane < ExaflareOffsets.Length; lane++)
        {
            // Recorded start NE -> N -> NW -> W (CCW). Mirror order around NE for CW.
            var angle = -MathF.PI / 4 + (clockwise ? -1 : 1) * lane * MathF.PI / 4;
            var inward = new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle));
            var at = ExaflareOffsets[lane];
            var firstHit = at + 12.001f;
            ScheduleHelper(at, firstHit, new Placement(-24f * inward, angle),
                ActionId.WaveCannon_7BAD, 11.7f, 4f, true);
            // Seven circles across the diameter: -24,-16,-8,0,8,16,24; 8y radius.
            // First repeat is ~1.1s after the arrow, subsequent hits ~1s apart.
            for (var step = 1; step <= 6; step++)
            {
                var hit = firstHit + 1.105f + (step - 1) * 1.004f;
                ScheduleHelper(hit, hit, new Placement((-24f + 8f * step) * inward, angle),
                    ActionId.WaveCannon_7BAE, 0f, 0f, true);
            }
        }
        for (var wave = 0; wave < PuddleCount; wave++)
            world.Events.Add(FirstPuddleAt + wave * PuddleInterval, DropPuddles);
        if (!solo) TopP6AlphaOmegaAi.RunUnlimited(clockwise, world);
        ScheduleWaveCannon();
        world.Events.Add(WildChargeAt, ScheduleSecondCosmoArrow);
    }

    private void ScheduleSecondCosmoArrow()
    {
        if (failed || boss == null) return;
        ScheduleAutoAttacks(FollowUpAutoAttacks);
        var inFirst = rng.NextBool();
        ScheduleBossCast(8.4f, ActionId.CosmoArrow, 5.7f, 14.4f);
        TopP6CosmoArrowSequence.Run(world, damage, inFirst, SecondArrowDelay);
        if (!solo) TopP6AlphaOmegaAi.RunSecondArrow(inFirst, world);
        var lastArrowAt = SecondArrowDelay + (inFirst ? 23.91f : 21.91f);
        world.Events.Add(lastArrowAt + 2.1f, () =>
        {
            if (!failed) world.CompleteScenario();
        });
    }

    private void ScheduleWaveCannon()
    {
        var order = RoleList.Random(party);
        world.Events.Add(CannonAt, () => boss?.Cast(ActionId.WaveCannon_7BA9,
            targetLocation: new Vector3(-0.008f, -0.015f, -0.008f), targetId: boss?.GameObjectId));
        for (var i = 0; i < 4; i++)
        {
            var index = i;
            var helper = SpawnHelper(new Placement(Vector3.Zero, 0f));
            for (var wave = 0; wave < 2; wave++)
            {
                var targetIndex = index + wave * 4;
                var hitAt = CannonAt + 3.04f + wave * 2f;
                world.Events.Add(hitAt - 0.07f, () =>
                {
                    if (party.Get(order[targetIndex]) is { } target) helper?.Face(target);
                });
                world.Events.Add(hitAt, () =>
                {
                    if (failed || helper == null || party.Get(order[targetIndex]) is not { } target) return;
                    helper.Cast(ActionId.WaveCannonProtean, castSeconds: 0f,
                        targetId: target.GameObjectId);
                    damage.Resolve(helper, ActionId.WaveCannonProtean, [DamageType.Magic],
                        [(StatusId.MagicVulnerabilityUp, 2.5f)]);
                });
            }
            world.Events.Add(SecondProteanAt + 2f, () => helper?.Despawn());
        }
        var chargeTarget = solo ? party.PlayerRole : order[0];
        world.Events.Add(WildChargeAt - 0.1f, () =>
        {
            if (party.Get(chargeTarget) is { } target) boss?.Face(target);
        });
        world.Events.Add(WildChargeAt, () =>
        {
            if (failed || boss == null) return;
            boss.Cast(ActionId.WaveCannonWildCharge, castSeconds: 0f,
                targetId: party.Get(chargeTarget)?.GameObjectId);
            damage.Resolve(boss, ActionId.WaveCannonWildCharge, [DamageType.Magic], [],
                stackMinTargets: solo ? 1 : 8, wildChargeTargets: solo ? 0 : 2,
                wildChargeDamageType: [DamageType.TankBuster]);
        });
    }

    private void DropPuddles()
    {
        if (failed) return;
        // Snapshot ALL living players, not one farthest target and not their later positions.
        foreach (var member in party.ActiveMembers())
            ScheduleHelper(0f, PuddleDelay, new Placement(member.Position, 0f),
                ActionId.WaveCannon_7BAF, 2.7f, 0f, true);
    }

    private void ScheduleHelper(float at, float hit, Placement placement, uint action,
        float castSeconds, float omenDelay, bool lethal)
    {
        SimEnemy? helper = null;
        world.Events.Add(MathF.Max(0f, at - 0.1f), () => helper = SpawnHelper(placement));
        if (castSeconds > 0)
            world.Events.Add(at, () =>
            {
                if (!failed && helper != null)
                    helper.BeginRecordedCast(action, ActionType.Action, castSeconds, omenDelay,
                        helper.Rotation, helper.Position, helper.GameObjectId);
            });
        world.Events.Add(hit, () =>
        {
            if (failed || helper == null) return;
            Release(helper, action, helper);
            if (lethal) damage.Resolve(helper, action, [DamageType.Lethal], []);
        });
        world.Events.Add(hit + 2f, () => helper?.Despawn());
    }

    private SimEnemy? SpawnHelper(Placement placement)
    {
        if (failed) return null;
        var helper = world.SpawnEnemy(new EnemySpawnConfig(
            BNpcBaseId: BNpcBaseId.OmegaHelper, NameId: BNpcNameId.AlphaOmega, Level: 1,
            Targetable: false, EnemyList: EnemyListMode.Never, IsVisible: false, Placement: placement));
        if (helper == null) Fail("P6 機制 helper 未能生成，停止本輪。");
        return helper;
    }

    private static void Release(SimEnemy caster, uint action, SimCharacter target)
    {
        Span<GameObjectId> targets = stackalloc GameObjectId[1];
        targets[0] = target.GameObjectId;
        caster.NativeActionEffect(action, 1.1f, checked((ushort)action), 0, ActionType.Action, 0,
            targets, caster.Rotation, null, target.GameObjectId, null);
    }

    private void Fail(string message)
    {
        failed = true;
        world.FailScenario(message);
    }
}
