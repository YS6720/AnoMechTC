using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Combat;
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
// Continue through both Wave Cannons and their intervening autos/Cosmo Arrow.
public sealed partial class TopP6AlphaOmegaScenario : IProgressScenario
{
    public string Name => "P6 阿爾法歐米茄（完整階段）";
    public IPhase Phase => TopZone.P6;
    public bool SupportsSolo => true;
    public IReadOnlyList<IScenarioAi> AiStrats { get; } = [new TopP6AlphaOmegaAi()];
    public IReadOnlyList<ScenarioProgress> Progresses { get; } =
        [new("full", "完整 P6"), new("unlimited", "波動砲：限制解除"),
            new("unlimited-second", "第二次波動砲：限制解除"), new("cosmo-meteor", "宇宙流星")];

    private SimWorld world = null!;
    private SimParty party = null!;
    private DamageSolver damage = null!;
    private SimEnemy? boss;
    private bool failed;
    private bool solo;
    private bool unlimitedOnly;
    private bool startAtSecondUnlimited;
    private bool meteorOnly;
    private bool? meteorD3MarkedAtRun;
    private TopP6MeteorFlarePlan? meteorFlarePlan;
    private readonly float[] lastLimitBreakAt = new float[8];
    private PartyRole? pendingMagicNumberHealer;
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
    internal const float SecondCannonAt = SecondArrowDelay + 16.03f;
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

    // Full-clear tail starts at the second Wild Charge (phase t=178.618).
    // All offsets below are measured from that charge or the second Unlimited
    // invocation, not from cast-end estimates.
    internal const float SecondUnlimitedAt = 8.370f;
    private const float SecondUnlimitedDiveAt = 18.123f;
    internal const float SecondUnlimitedMeteorAt = 40.316f;
    internal const float CasterLimitBreakBegin = SecondUnlimitedMeteorAt + 7.746f;
    internal const float RangedLimitBreakBegin = SecondUnlimitedMeteorAt + 18.164f;
    private const float FirstMagicNumberAt = 84.035f;
    private const float FirstMagicNumberStatusAt = 90.969f;
    private const float SecondMagicNumberAt = 100.186f;
    private const float SecondMagicNumberStatusAt = 107.112f;
    private const float FinalRunAt = 114.311f;
    private const float FinalRunReleaseAt = 130.269f;
    private const float CompleteAt = 141.102f;
    private static readonly (float Swing, float Hit)[] SecondCannonAutoAttacks =
        [(4.116f, 4.923f), (7.251f, 8.057f)];

    private const float MeteorMarkerAt = 20.087f;
    private const float MeteorResolveAt = 28.186f;
    private const float MeteorVisualAt = 29.216f;
    private const float MeteorDirectPreparation = 1f;

    private const uint CosmoMeteor = 31664;
    private const uint CosmoMeteorVisual = 31665;
    private const uint CosmoMeteorPuddle = 31666;
    private const uint CosmoMeteorStack = 31667;
    private const uint CosmoMeteorFlare = 31668;
    private const uint CosmoMeteorSpread = 32699;
    private const uint MagicNumber = 31670;
    private const uint FinalRun = 31648;
    private const ushort MagicNumberStatus = 3532;
    private const uint CosmoMeteorBaseId = 15726;
    private const uint CosmoCometBaseId = 15727;

    private static readonly Vector2[] CosmoMeteorPositions =
    [
        new(0f, -10f), new(0f, 10f),
    ];
    private static readonly Vector2[] CosmoCometPositions =
    [
        new(-6.5f, -11.26f), new(-13f, 0f), new(6.5f, -11.26f),
        new(-6.5f, 11.26f), new(13f, 0f), new(6.5f, 11.26f),
    ];
    private static readonly (float Swing, float Hit)[] SecondDiveAutoAttacks =
        [(14.810f, 15.615f), (17.942f, 18.748f)];

    private SimEnemy?[] cosmoMeteors = [];
    private SimEnemy?[] cosmoComets = [];
    // Skip the 54.6s non-interactive movie. Give manual players a short
    // initial hold, then retain the recorded 7.179s return window before Memory.
    private const float ReturnAt = 0.2f;
    private const float ReturnWindow = 7.179f;

    public void Run(SimWorld worldParam, int? selectedAi) => Run(worldParam, selectedAi, "full");

    public void Run(SimWorld worldParam, int? selectedAi, string progressKey)
    {
        if (!Progresses.Any(progress => progress.Key == progressKey))
            throw new ArgumentOutOfRangeException(nameof(progressKey), progressKey, "Unknown P6 progress key.");
        unlimitedOnly = progressKey is "unlimited" or "unlimited-second";
        startAtSecondUnlimited = progressKey == "unlimited-second";
        meteorOnly = progressKey == "cosmo-meteor";
        meteorD3MarkedAtRun = meteorD3MarkedOverride;
        if (selectedAi is not null and not 0)
        {
            worldParam.FailScenario($"P6 開場不存在第 {selectedAi} 個打法。");
            return;
        }
        world = worldParam;
        party = worldParam.Party;
        worldParam.LimitBreaks = new PracticeLimitBreakRuntime(worldParam, OnLimitBreakResolved);
        solo = selectedAi is null;
        damage = new DamageSolver(party);
        damage.SetStatuses(DamageType.Magic, StatusId.MagicVulnerabilityUp);
        failed = false;
        boss = null;
        meteorFlarePlan = null;
        Array.Fill(lastLimitBreakAt, float.NegativeInfinity);
        pendingMagicNumberHealer = null;
        cosmoMeteors = [];
        cosmoComets = [];
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
        InitializeLimitBreakState(postMemory: unlimitedOnly || meteorOnly);
        if (meteorOnly)
        {
            world.EnforceArenaBoundary(Geometry.ArenaRadius);
            boss.SetTargetable(true);
            boss.AddStatusParam(StatusId.CodeMi, 0);
            var gather = TopP6AlphaOmegaAi.MeteorGatherPosition();
            foreach (var member in party.ActiveMembers())
            {
                if (member is SimPlayer) continue;
                member.SetPosition(new Placement(new Vector3(gather.X, 0f, gather.Y), MathF.PI));
            }
            world.Events.Add(MeteorDirectPreparation, () =>
            {
                ScheduleCosmoMeteorSequence();
                if (!solo) TopP6AlphaOmegaAi.RunMeteorTail(world, 0f);
            });
            return;
        }
        if (unlimitedOnly)
        {
            world.EnforceArenaBoundary(Geometry.ArenaRadius);
            boss.SetTargetable(true);
            boss.AddStatusParam(StatusId.CodeMi, 0);
            foreach (var member in party.ActiveMembers())
                member.SetPosition(new Placement(Vector3.Zero, MathF.PI));
            world.Events.Add(1f, () => ScheduleUnlimitedWaveCannon(startAtSecondUnlimited));
            return;
        }
        // Start just inside the normal 20y fence. Keep the local player manual;
        // only NPCs are repositioned after the short opening hold.
        world.EnforceArenaBoundary(Geometry.ArenaRadius);
        foreach (var member in party.ActiveMembers())
            member.SetPosition(new Placement(new Vector3(0f, 0f, 18f), MathF.PI));
        world.Events.Add(ReturnAt, () =>
        {
            if (failed || boss == null) return;
            boss.SetTargetable(true);
            boss.SetTarget(party.Get(PartyRole.MainTank), follow: false);
            Core.ChatOutput.Coach("[AnoMech] 開場位於南側圍內；可手動往場中移動。7.179 秒後開始宇宙記憶。已略過不可操作過場。");
            var gather = TopP6AlphaOmegaAi.MeteorGatherPosition();
            foreach (var member in party.ActiveMembers())
            {
                if (member is SimPlayer) continue;
                var role = ((ISimPartyMember)member).Role;
                member.MoveTo(role == PartyRole.MainTank ? new Vector3(0f, 0f, -8f)
                    : role == PartyRole.OffTank ? new Vector3(0f, 0f, 16f) : new Vector3(gather.X, 0f, gather.Y));
            }
        });
        world.Events.Add(ReturnAt + ReturnWindow, StartCombat);
    }

    private void StartCombat()
    {
        if (failed || boss == null) return;
        world.EnforceArenaBoundary(Geometry.ArenaRadius);
        ScheduleBossCast(0f, ActionId.CosmoMemory, 5.7f, 5.996f);
        if (!solo) TopP6AlphaOmegaAi.StartLimitBreak(world, PartyRole.MainTank, null);
        world.Events.Add(5.996f, () =>
        {
            if (failed) return;
            // Either tank's completed LB3 may cover Memory; an opening button
            // press that expired before this damage packet does not count.
            if (world.Events.Time - lastLimitBreakAt[(int)PartyRole.MainTank] >= 8f &&
                world.Events.Time - lastLimitBreakAt[(int)PartyRole.OffTank] >= 8f)
            {
                Fail("宇宙記憶：傷害結算前未開啟有效坦克 LB。");
                return;
            }
            damage.Resolve(boss, ActionId.CosmoMemory, [DamageType.Magic], []);
            InitializeLimitBreakState(postMemory: true);
        });
        world.Events.Add(8.324f, () => boss?.AddStatusParam(StatusId.CodeMi, 0));
        var inFirst = rng.NextBool();
        ScheduleCosmoArrow(inFirst);
        if (!solo) ((IScenarioAi<bool>)AiStrats[0]).Run(inFirst, world);
        ScheduleCosmoDive();
        ScheduleAutoAttacks(AutoAttacks);
        world.Events.Add(UnlimitedAt, () => ScheduleUnlimitedWaveCannon());
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

    private void ScheduleAutoAttacks((float Swing, float Hit)[] attacks, float offset = 0f)
    {
        var firstHelper = SpawnHelper(new Placement(Vector3.Zero, 0f));
        var farthestHelper = SpawnHelper(new Placement(Vector3.Zero, 0f));
        foreach (var (swing, hit) in attacks)
        {
            SimCharacter? first = null;
            SimCharacter? farthest = null;
            world.Events.Add(offset + swing, () =>
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
            world.Events.Add(offset + hit, () =>
            {
                // Select both BEFORE damage: MT can bait both and die to the second hit.
                HitTarget(first, ActionId.Unknown7ddf, false, 0, true, firstHelper);
                HitTarget(farthest, ActionId.Unknown7ddf, false, 0, true, farthestHelper);
            });
        }
    }

    private void ScheduleCosmoDive(float beginAt = 29.592f, bool includeFollowUpAutos = false)
    {
        const float releaseDelay = 5.597f;
        ScheduleBossCast(beginAt, ActionId.CosmoDive, 5.3f, beginAt + releaseDelay);
        if (includeFollowUpAutos)
            ScheduleAutoAttacks(SecondDiveAutoAttacks, beginAt);
        // 龍炎在命中時近引導兩人，六人分攤落在最遠者；不能在王動畫提早鎖人。
        world.Events.Add(beginAt + 8f, () =>
        {
            if (failed || boss == null) return;
            SimCharacter? first = null, second = null, stack = null;
            var firstDistance = float.PositiveInfinity;
            var secondDistance = float.PositiveInfinity;
            var farthestDistance = -1f;
            var count = 0;
            foreach (var member in party.ActiveMembers())
            {
                count++;
                var distance = Vector3.DistanceSquared(member.Position, boss.Position);
                if (distance < firstDistance)
                {
                    second = first;
                    secondDistance = firstDistance;
                    first = member;
                    firstDistance = distance;
                }
                else if (distance < secondDistance)
                {
                    second = member;
                    secondDistance = distance;
                }
                if (distance > farthestDistance)
                {
                    stack = member;
                    farthestDistance = distance;
                }
            }
            if (count < 3) return;
            HitTarget(first, ActionId.CosmoDive_7BA7, true, 0, true);
            HitTarget(second, ActionId.CosmoDive_7BA7, true, 0, true);
            HitTarget(stack, ActionId.CosmoDive_7BA8, false, 6, false);
        });
    }

    private void HitTarget(SimCharacter? target, uint action, bool tankbuster, int stack, bool applyVulnerability,
        SimEnemy? preparedHelper = null, bool killTargets = true, float? size = null)
    {
        if (failed || target == null || !target.IsAlive()) return;
        var placement = new Placement(target.Position, boss?.Rotation ?? 0f);
        var helper = preparedHelper ?? SpawnHelper(placement);
        if (helper == null) return;
        helper.SetPosition(placement);
        Release(helper, action, target);
        damage.Resolve(helper, action, tankbuster ? [DamageType.Magic, DamageType.TankBuster] : [DamageType.Magic],
            applyVulnerability ? [(StatusId.MagicVulnerabilityUp, 2f)] : [],
            stackMinTargets: stack, size: size, killTargets: killTargets);
        if (preparedHelper == null) world.Events.Add(2f, helper.Despawn);
    }


    private void ScheduleUnlimitedWaveCannon(bool second = false)
    {
        ScheduleBossCast(0f, ActionId.UnlimitedWaveCannon, 4.7f, 4.993f);
        var clockwise = rng.NextBool();
        // A/1/B/2/C/3/D/4: sample one of eight start rays and share it with the AI.
        var startAngle = rng.NextInt(8) * MathF.PI / 4f;
        Core.ChatOutput.Coach($"[AnoMech] {(second ? "第二次" : "首次")}波動砲：限制解除：{(clockwise ? "順時針" : "逆時針")}；前兩圈直走、第三圈轉斜向，第六圈放下即回八方。");
        for (var lane = 0; lane < ExaflareOffsets.Length; lane++)
        {
            // Inward facing is the negative of the clockwise-from-north start ray.
            var angle = -startAngle + (clockwise ? -1 : 1) * lane * MathF.PI / 4;
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
        if (!solo) TopP6AlphaOmegaAi.RunUnlimited(startAngle, clockwise, world, second);
        if (!second)
        {
            ScheduleWaveCannon(CannonAt);
            world.Events.Add(WildChargeAt, ScheduleSecondCosmoArrow);
        }
        else
        {
            ScheduleCosmoDive(SecondUnlimitedDiveAt, includeFollowUpAutos: true);
            world.Events.Add(SecondUnlimitedMeteorAt, ScheduleCosmoMeteorSequence);
        }
    }
    private void ScheduleCosmoMeteorSequence()
    {
        ScheduleCosmoMeteor();
        ScheduleMagicNumber(
            FirstMagicNumberAt - SecondUnlimitedAt - SecondUnlimitedMeteorAt,
            FirstMagicNumberStatusAt - SecondUnlimitedAt - SecondUnlimitedMeteorAt,
            PartyRole.MainTank, PartyRole.RegenHealer);
        ScheduleMagicNumber(
            SecondMagicNumberAt - SecondUnlimitedAt - SecondUnlimitedMeteorAt,
            SecondMagicNumberStatusAt - SecondUnlimitedAt - SecondUnlimitedMeteorAt,
            PartyRole.OffTank, PartyRole.ShieldHealer);
        ScheduleBossCast(
            FinalRunAt - SecondUnlimitedAt - SecondUnlimitedMeteorAt, FinalRun, 15.7f,
            FinalRunReleaseAt - SecondUnlimitedAt - SecondUnlimitedMeteorAt);
        world.Events.Add(CompleteAt - SecondUnlimitedAt - SecondUnlimitedMeteorAt, () =>
        {
            if (!failed) world.CompleteScenario();
        });
    }

    private void ScheduleSecondCosmoArrow()
    {
        if (failed || boss == null) return;
        ScheduleAutoAttacks(FollowUpAutoAttacks);
        var inFirst = rng.NextBool();
        ScheduleBossCast(8.4f, ActionId.CosmoArrow, 5.7f, 14.4f);
        TopP6CosmoArrowSequence.Run(world, damage, inFirst, SecondArrowDelay);
        if (!solo) TopP6AlphaOmegaAi.RunSecondArrow(inFirst, world);
        ScheduleWaveCannon(SecondCannonAt);
        world.Events.Add(SecondCannonAt + 11.37f, ScheduleSecondTail);
    }

    private void ScheduleSecondTail()
    {
        if (failed || boss == null) return;
        Core.ChatOutput.Coach("[AnoMech] P6 後半：第二次限制解除、宇宙潛、宇宙隕石；依錄影時間軸持續至通關。");
        ScheduleAutoAttacks(SecondCannonAutoAttacks);
        world.Events.Add(SecondUnlimitedAt, () => ScheduleUnlimitedWaveCannon(second: true));
    }

    private void ScheduleMagicNumber(float castAt, float statusAt, PartyRole tank, PartyRole healer)
    {
        // Tank LB3 lasts 8s. Check the assigned tank's completed LB at damage,
        // not a button press or the other tank's party-wide status.
        world.Events.Add(castAt, () =>
        {
            if (failed) return;
            if (!solo) TopP6AlphaOmegaAi.StartLimitBreak(world, tank, null);
        });
        world.Events.Add(castAt + 4.968f, () =>
        {
            if (failed) return;
            if (world.Events.Time - lastLimitBreakAt[(int)tank] >= 8f)
                Fail($"魔數：{(tank == PartyRole.MainTank ? "MT" : "ST")} 未在傷害結算前開啟有效 LB。");
        });
        // The damage packet precedes the recorded six-second 3532 debuff.
        ScheduleBossCast(castAt, MagicNumber, 4.7f, castAt + 4.968f);
        world.Events.Add(statusAt, () =>
        {
            if (failed) return;
            pendingMagicNumberHealer = healer;
            for (var role = 0; role < 8; role++)
                party.Get(role)?.AddStatusParam(MagicNumberStatus, 0, duration: 6f);
            if (!solo) TopP6AlphaOmegaAi.StartLimitBreak(world, healer, null);
            world.Events.Add(6f, () =>
            {
                if (!failed && pendingMagicNumberHealer == healer)
                    Fail($"魔數：{(healer == PartyRole.RegenHealer ? "H1" : "H2")} 未在 DEBUFF 到期前完成 LB。");
            });
        });
    }
    private void ScheduleCosmoMeteor()
    {
        ScheduleBossCast(0f, CosmoMeteor, 4.7f, 4.969f);
        // FFLogs lacks actor-spawn packets; stage the adds at Meteor's cast release.
        world.Events.Add(4.969f, SpawnCosmoMeteorAdds);
        world.Events.Add(5.014f, DropCosmoMeteorPuddles);

        // The first and second four-person spread waves use different
        // recorded role partitions. Positions remain at separate clock spots.
        var firstSpread = RoleList.Random(party);
        var secondSpread = RoleList.Random(party);
        world.Events.Add(10.119f, () => ResolveCosmoMeteorSpread(firstSpread));
        world.Events.Add(11.147f, () => ResolveCosmoMeteorSpread(firstSpread, 4));
        world.Events.Add(16.155f, () => ResolveCosmoMeteorSpread(secondSpread));
        world.Events.Add(17.182f, () => ResolveCosmoMeteorSpread(secondSpread, 4));

        // 346 is the native large-triangle marker. The marked roles and their
        // movement plan are frozen at marker time from currently alive members.
        world.Events.Add(MeteorMarkerAt, () =>
        {
            meteorFlarePlan = TopP6MeteorFlarePlan.Create(
                party.ActiveMembers(), meteorD3MarkedAtRun, rng);
            foreach (var role in meteorFlarePlan.MarkedRoles)
                party.Get(role)?.AttachLockonVfx(346, persistent: false);
            if (!solo)
                TopP6AlphaOmegaAi.RunMeteorFlares(
                    world, meteorFlarePlan, MeteorResolveAt - MeteorMarkerAt);
        });
        world.Events.Add(MeteorResolveAt, ResolveCosmoMeteorFlares);
        world.Events.Add(MeteorVisualAt, () => boss?.Cast(
            CosmoMeteorVisual, targetId: boss?.GameObjectId));

        // Actual add lifecycle is owned by completed LB callbacks below. A miss,
        // wrong action, or absent LB leaves the actor alive for diagnosis.
    }

    private void InitializeLimitBreakState(bool postMemory)
    {
        foreach (var member in party.AllMembers())
        {
            member.RemoveStatus(StatusId.QuickeningDynamis);
            member.RemoveStatus(StatusId.BrilliantDynamis);
            member.RemoveStatus(StatusId.RadiantDynamis);
            if (postMemory)
                member.AddStatus(StatusId.BrilliantDynamis);
            else
                member.AddStatus(StatusId.QuickeningDynamis, stacks: 3, overrideStacks: true);
        }
        world.LimitBreaks?.Refill();
    }

    private void OnLimitBreakResolved(
        PartyRole role, uint actionId, Vector3? location, SimCharacter? target)
    {
        var caster = party.Get(role);
        if (caster == null) return;

        // The refund is a per-member state transition, not a global or per-run
        // refill. A cast from 3448 spends the bar without creating another one.
        if (caster.FindStatus(StatusId.BrilliantDynamis) != null)
        {
            caster.RemoveStatus(StatusId.BrilliantDynamis);
            caster.RemoveStatus(StatusId.RadiantDynamis);
            caster.AddStatus(StatusId.RadiantDynamis);
            world.LimitBreaks?.Refill();
        }

        lastLimitBreakAt[(int)role] = world.Events.Time;
        if (pendingMagicNumberHealer == role)
        {
            for (var member = 0; member < 8; member++)
                party.Get(member)?.RemoveStatus(MagicNumberStatus);
            pendingMagicNumberHealer = null;
        }

        if (role == PartyRole.CasterDps)
            ResolveLimitBreakGeometry(actionId, caster, location, target, cosmoComets, ground: true);
        else if (role == PartyRole.PhysRangedDps)
            ResolveLimitBreakGeometry(actionId, caster, location, target, cosmoMeteors, ground: false);
    }

    private void ResolveLimitBreakGeometry(
        uint actionId, SimCharacter caster, Vector3? location, SimCharacter? target,
        SimEnemy?[] adds, bool ground)
    {
        var live = false;
        for (var i = 0; i < adds.Length; i++)
            if (adds[i] is { IsActive: true, Targetable: true })
            {
                live = true;
                break;
            }
        if (!live) return;

        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        if (!sheet.TryGetRow(actionId, out var action)) return;
        var range = (float)action.EffectRange;
        if (range <= 0f) return;

        var center = location ?? default;
        if (ground && location == null) return;
        var origin = ground ? center : caster.Position;
        var rotation = caster.Rotation;
        if (!ground && (location ?? target?.Position) is { } aim)
            rotation = MathF.Atan2(aim.X - origin.X, aim.Z - origin.Z);
        var forwardX = MathF.Sin(rotation);
        var forwardZ = MathF.Cos(rotation);
        var halfWidth = action.XAxisModifier > 0
            ? (float)action.XAxisModifier * 0.5f
            : range;
        var circle = ground && action.CastType is 2 or 5 or 6;
        var rectangle = !ground && action.CastType is 4 or 8 or 12;
        if (!circle && !rectangle) return;

        var rangeSquared = range * range;
        for (var i = 0; i < adds.Length; i++)
        {
            var add = adds[i];
            if (add is not { IsActive: true, Targetable: true }) continue;
            var dx = add.Position.X - origin.X;
            var dz = add.Position.Z - origin.Z;
            var hit = circle
                ? dx * dx + dz * dz <= rangeSquared
                : MathF.Abs(dx * forwardZ - dz * forwardX) <= halfWidth
                    && dx * forwardX + dz * forwardZ >= 0f
                    && dx * forwardX + dz * forwardZ <= range;
            if (!hit) continue;
            add.SetTargetable(false);
            add.Despawn();
        }
    }

    private void DropCosmoMeteorPuddles()
    {
        if (failed) return;
        foreach (var member in party.ActiveMembers())
            ScheduleHelper(0f, 3.987f, new Placement(member.Position, 0f),
                CosmoMeteorPuddle, 3.7f, 0f, true);
    }

    private void ResolveCosmoMeteorSpread(RoleList order, int offset = 0)
    {
        for (var i = 0; i < 4; i++)
            HitTarget(party.Get(order[offset + i]), CosmoMeteorSpread, false, 0, true, size: 5f);
    }

    private void ResolveCosmoMeteorFlares()
    {
        if (meteorFlarePlan is not { } plan) return;
        if (plan.StackTargetRole is { } stackRole)
        {
            var stackTarget = party.Get(stackRole);
            if (stackTarget != null)
                HitTarget(stackTarget, CosmoMeteorStack, false,
                    Math.Min(5, plan.UnmarkedRoles.Count), false, size: 6f);
        }
        foreach (var role in plan.MarkedRoles)
            HitTarget(party.Get(role), CosmoMeteorFlare, false, 0, false,
                killTargets: false, size: 100f);
    }

    private void SpawnCosmoMeteorAdds()
    {
        cosmoMeteors = CosmoMeteorPositions
            .Select(position => SpawnCosmoAdd(CosmoMeteorBaseId, position)).ToArray();
        cosmoComets = CosmoCometPositions
            .Select(position => SpawnCosmoAdd(CosmoCometBaseId, position)).ToArray();
    }

    private SimEnemy? SpawnCosmoAdd(uint baseId, Vector2 position)
        => world.SpawnEnemy(new EnemySpawnConfig(
            // BNpcName 12259＝Cosmo Meteor／宇宙流星；12260＝Cosmo Comet／宇宙隕星。
            BNpcBaseId: baseId, NameId: baseId == CosmoMeteorBaseId ? 12259u : 12260u, Level: Level,
            Targetable: true, EnemyList: EnemyListMode.Always, IsVisible: true,
            Placement: new Placement(new Vector3(position.X, 0f, position.Y), 0f)));


    private void ScheduleWaveCannon(float cannonAt)
    {
        var secondProteanAt = cannonAt + 5.04f;
        var wildChargeAt = cannonAt + 11.37f;
        var order = RoleList.Random(party);
        // Full-clear WC2: 10.6s cast, four hits at +3.002/+5.017,
        // boss release +10.882, charge +11.284. Keep VFX on the scenario clock,
        // not SimCast's native cast-bar completion, so it cannot precede 4+4.
        ScheduleBossCast(cannonAt, ActionId.WaveCannon_7BA9, 10.6f, cannonAt + 10.882f);
        for (var i = 0; i < 4; i++)
        {
            var index = i;
            var helper = SpawnHelper(new Placement(Vector3.Zero, 0f));
            for (var wave = 0; wave < 2; wave++)
            {
                var targetIndex = index + wave * 4;
                var hitAt = cannonAt + 3.04f + wave * 2f;
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
            world.Events.Add(secondProteanAt + 2f, () => helper?.Despawn());
        }
        var chargeTarget = solo ? party.PlayerRole : order[0];
        world.Events.Add(cannonAt + 10.782f, () =>
        {
            if (party.Get(chargeTarget) is { } target) boss?.Face(target);
        });
        world.Events.Add(wildChargeAt, () =>
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
        float castSeconds, float omenDelay, bool lethal, float? size = null,
        int stackMinTargets = 0, bool killTargets = true)
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
            if (lethal)
                damage.Resolve(helper, action, [DamageType.Lethal], []);
            else if (size is not null || stackMinTargets > 0)
                damage.Resolve(helper, action, [DamageType.Magic], [],
                    stackMinTargets: stackMinTargets, size: size, killTargets: killTargets);
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
