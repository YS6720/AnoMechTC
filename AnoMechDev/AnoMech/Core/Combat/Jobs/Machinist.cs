using System;
using System.Collections.Generic;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;

namespace AnoMech.Core.Combat.Jobs;

// Machinist has two deliberately separate pieces of state:
//   * statuses are host-owned and go through JobActionContext;
//   * Heat/Battery and the native timers are local-gauge state.
// No turret damage or pet AI is simulated here.  The gauge only preserves the
// eligibility/timer information needed by a manually driven rotation.
internal sealed unsafe class Machinist : IJobStatusRules, IJobGaugeRules
{
    internal const byte JobId = 31;
    internal static readonly Machinist Instance = new();

    // Actions (Taiwan client action rows, verified in tmp/eight-job-data/MCH.json).
    private const uint RookAutoturret = 2864;
    private const uint SplitShot = 2866, SlugShot = 2868, SpreadShot = 2870;
    private const uint HotShot = 2872, CleanShot = 2873;
    private const uint Reassemble = 2876, Wildfire = 2878, Dismantle = 2887;
    private const uint Ricochet = 2890;
    private const uint HeatBlast = 7410, HeatSplitShot = 7411, HeatSlugShot = 7412;
    private const uint HeatCleanShot = 7413, BarrelStabilizer = 7414;
    private const uint RookOverdrive = 7415, Flamethrower = 7418;
    private const uint Hypercharge = 17209;
    private const uint AutoCrossbow = 16497, Drill = 16498, Bioblaster = 16499;
    private const uint AirAnchor = 16500, AutomatonQueen = 16501;
    private const uint QueenOverdrive = 16502, PileBunker = 16503, CrownedCollider = 16504;
    private const uint Detonator = 16766, Tactician = 16889;
    private const uint Scattergun = 25786, CrownedColliderUpgrade = 25787;
    private const uint ChainSaw = 25788, BlazingShot = 36978;
    private const uint DoubleCheck = 36979, Checkmate = 36980;
    private const uint Excavator = 36981, FullMetalField = 36982;
    private const uint GaussRound = 2874;

    // Status rows used by the current client.  1946 is the caster-side
    // Wildfire marker; 861 is the target-side hit window.
    private const ushort ReassembleStatus = 851;
    private const ushort DismantledStatus = 860;
    private const ushort WildfireTargetStatus = 861;
    private const ushort WildfireSelfStatus = 1946;
    private const ushort TacticianStatus = 1951;
    private const ushort OverheatedStatus = 2688;
    private const ushort HyperchargeReadyStatus = 3864;
    private const ushort ExcavatorReadyStatus = 3865;
    private const ushort FullMetalReadyStatus = 3866;
    private const ushort FlamethrowerStatus = 1455;

    private const int HeatCap = 100;
    private const int BatteryCap = 100;
    private const int HyperchargeHeat = 50;
    private const int MaxWildfireHits = 6;
    private const float WildfireSeconds = 10f;
    private const float ReadySeconds = 30f;
    private const float TacticianSeconds = 15f;
    private const float DismantleSeconds = 10f;
    private const float FlamethrowerSeconds = 10f;
    private const short HyperchargeMilliseconds = 10_000;
    private const short RookMilliseconds = 9_000;
    private const short QueenMilliseconds = 12_000;

    private static readonly ushort[] ReassembleTouched = [ReassembleStatus];
    private static readonly ushort[] WildfireTouched = [WildfireTargetStatus, WildfireSelfStatus];
    private static readonly ushort[] WeaponTouched = [ReassembleStatus, WildfireTargetStatus];
    private static readonly ushort[] WeaponWithoutReassembleTouched = [WildfireTargetStatus];
    private static readonly ushort[] HyperchargeTouched = [HyperchargeReadyStatus, OverheatedStatus];
    private static readonly ushort[] HeatBlastTouched = [OverheatedStatus, ReassembleStatus, WildfireTargetStatus];
    private static readonly ushort[] DetonatorTouched = [WildfireTargetStatus, WildfireSelfStatus];
    private static readonly ushort[] DismantleTouched = [DismantledStatus];
    private static readonly ushort[] TacticianTouched = [TacticianStatus];
    private static readonly ushort[] ChainSawTouched = [ExcavatorReadyStatus, ReassembleStatus, WildfireTargetStatus];
    private static readonly ushort[] ExcavatorTouched = [ExcavatorReadyStatus, ReassembleStatus, WildfireTargetStatus];
    private static readonly ushort[] FullMetalTouched = [FullMetalReadyStatus, WildfireTargetStatus];
    private static readonly ushort[] FlamethrowerTouched = [FlamethrowerStatus];
    private readonly Dictionary<PartyRole, WildfireState> wildfire = new();
    private readonly Dictionary<PartyRole, Core.Game.Placement> flamethrower = new();

    private sealed class WildfireState(SimCharacter target)
    {
        internal SimCharacter Target { get; } = target;
        internal int Hits { get; set; }
        internal float Remaining { get; set; } = WildfireSeconds;
    }

    // TC ActionCombo keeps the original shot IDs after the Heated upgrades.
    internal static uint ComboActionId(uint actionId) => actionId switch
    {
        HeatSplitShot => SplitShot,
        HeatSlugShot => SlugShot,
        HeatCleanShot => CleanShot,
        _ => actionId,
    };

    public bool IsKnownAction(uint actionId) => actionId is
        RookAutoturret or SplitShot or SlugShot or SpreadShot or HotShot or CleanShot or
        Reassemble or Wildfire or Dismantle or Ricochet or HeatBlast or HeatSplitShot or
        HeatSlugShot or HeatCleanShot or BarrelStabilizer or RookOverdrive or Flamethrower or
        Hypercharge or AutoCrossbow or Drill or Bioblaster or AirAnchor or AutomatonQueen or
        QueenOverdrive or PileBunker or CrownedCollider or Detonator or Tactician or
        Scattergun or CrownedColliderUpgrade or ChainSaw or BlazingShot or DoubleCheck or
        Checkmate or Excavator or FullMetalField;

    public IReadOnlyList<ushort> TouchedStatuses(uint actionId, bool comboOk) => actionId switch
    {
        Reassemble => ReassembleTouched,
        Wildfire => WildfireTouched,
        Detonator => DetonatorTouched,
        Dismantle => DismantleTouched,
        Tactician => TacticianTouched,
        Hypercharge => HyperchargeTouched,
        HeatBlast or BlazingShot => HeatBlastTouched,
        BarrelStabilizer => [HyperchargeReadyStatus, FullMetalReadyStatus],
        ChainSaw => ChainSawTouched,
        Excavator => ExcavatorTouched,
        FullMetalField => FullMetalTouched,
        Flamethrower => FlamethrowerTouched,
        _ when IsWeaponSkill(actionId) => IsReassembleExcluded(actionId)
            ? WeaponWithoutReassembleTouched
            : WeaponTouched,
        _ => Array.Empty<ushort>(),
    };

    public void Apply(in JobActionContext context)
    {
        var actionId = context.ActionId;
        if (!IsKnownAction(actionId)) return;

        switch (actionId)
        {
            case Reassemble:
                context.Grant(ReassembleStatus, 0, 5f);
                return;

            case Wildfire:
                StartWildfire(context);
                return;

            case Detonator:
                Detonate(context);
                return;

            case Dismantle:
                if (context.Target is not null)
                    context.Grant(DismantledStatus, 0, DismantleSeconds, context.Target);
                return;

            case Tactician:
                // Tactician is represented as a party visual/eligibility marker;
                // this simulator deliberately has no mitigation engine.
                context.GrantParty(TacticianStatus, 0, TacticianSeconds, 30f);
                return;

            case Hypercharge:
                context.Remove(HyperchargeReadyStatus);
                context.Grant(OverheatedStatus, 5, 10f);
                return;

            case HeatBlast or BlazingShot:
                context.Consume(OverheatedStatus);
                ApplyWeaponHit(context);
                return;

            case BarrelStabilizer:
                context.Grant(HyperchargeReadyStatus, 0, ReadySeconds);
                if (context.Level >= 100) context.Grant(FullMetalReadyStatus, 0, ReadySeconds);
                return;

            case ChainSaw:
                if (context.Level >= 96)
                    context.Grant(ExcavatorReadyStatus, 0, ReadySeconds);
                ApplyWeaponHit(context);
                return;

            case Excavator:
                if (context.Level < 96) return;
                context.Remove(ExcavatorReadyStatus);
                ApplyWeaponHit(context);
                return;

            case FullMetalField:
                if (context.Level < 100) return;
                context.Remove(FullMetalReadyStatus);
                CountWildfireHit(context);
                return;

            case Flamethrower:
                context.Grant(FlamethrowerStatus, 0, FlamethrowerSeconds);
                flamethrower[context.SourceRole] = context.Player.Placement();
                return;
        }

        if (IsWeaponSkill(actionId)) ApplyWeaponHit(context);
    }

    private void ApplyWeaponHit(in JobActionContext context)
    {
        if (!IsReassembleExcluded(context.ActionId))
            context.Remove(ReassembleStatus);
        CountWildfireHit(context);
    }

    private void StartWildfire(in JobActionContext context)
    {
        if (context.Target is not { IsActive: true } target) return;
        context.Grant(WildfireTargetStatus, 0, WildfireSeconds, target);
        context.Grant(WildfireSelfStatus, 0, WildfireSeconds);
        wildfire[context.SourceRole] = new WildfireState(target);
    }

    private void Detonate(in JobActionContext context)
    {
        if (wildfire.Remove(context.SourceRole, out var state))
            context.Remove(WildfireTargetStatus, state.Target);
        else if (context.Target is not null)
            context.Remove(WildfireTargetStatus, context.Target);
        context.Remove(WildfireSelfStatus);
    }

    private void CountWildfireHit(in JobActionContext context)
    {
        if (!wildfire.TryGetValue(context.SourceRole, out var state) ||
            state.Hits >= MaxWildfireHits || context.Target is null ||
            !ReferenceEquals(state.Target, context.Target)) return;

        state.Hits++;
        context.Grant(WildfireTargetStatus, state.Hits, state.Remaining, state.Target);
    }

    public void OnPartyAction(in JobActionContext context, SimCharacter actor)
    {
        if (ReferenceEquals(actor, context.Player) && context.ActionId != Flamethrower &&
            flamethrower.Remove(context.SourceRole))
            context.Remove(FlamethrowerStatus);
    }

    public void Tick(in JobActionContext context, float deltaSeconds)
    {
        if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0f) return;
        if (flamethrower.TryGetValue(context.SourceRole, out var origin) &&
            (context.Find(FlamethrowerStatus) is null || context.Player.Placement() != origin))
        {
            context.Remove(FlamethrowerStatus);
            flamethrower.Remove(context.SourceRole);
        }
        if (!wildfire.TryGetValue(context.SourceRole, out var state)) return;

        state.Remaining -= deltaSeconds;
        if (state.Remaining > 0f && state.Target.IsActive) return;

        context.Remove(WildfireTargetStatus, state.Target);
        context.Remove(WildfireSelfStatus);
        wildfire.Remove(context.SourceRole);
    }

    // Main invokes this at a run reset.  Keeping it public also permits the
    // default interface method added by Main to call the job-specific cleanup.
    public void ResetStatusState()
    {
        wildfire.Clear();
        flamethrower.Clear();
    }

    // ---- local native gauge -------------------------------------------------

    private static MachinistGauge* Gauge()
    {
        var manager = JobGaugeManager.Instance();
        if (manager == null || manager->ClassJobId != JobId || manager->CurrentGauge == null)
            return null;
        return (MachinistGauge*)manager->CurrentGauge;
    }

    public void OnLocalFire(uint actionId, bool comboOk, byte level)
    {
        var gauge = Gauge();
        if (gauge == null) return;

        var heat = (int)gauge->Heat;
        var battery = (int)gauge->Battery;
        switch (actionId)
        {
            case Hypercharge:
                var free = LocalJobResources.HasStatus(HyperchargeReadyStatus);
                if (free || heat >= HyperchargeHeat)
                {
                    if (!free) heat -= HyperchargeHeat;
                    gauge->OverheatTimeRemaining = HyperchargeMilliseconds;
                }
                break;


            case RookAutoturret:
                if (battery >= 50) { Summon(gauge, queen: false); battery = 0; }
                break;

            case AutomatonQueen:
                if (battery >= 50) { Summon(gauge, queen: true); battery = 0; }
                break;

            case RookOverdrive:
            case QueenOverdrive:
                if (gauge->SummonTimeRemaining > 0) gauge->SummonTimeRemaining = 0;
                break;
            case SplitShot or HeatSplitShot or SpreadShot:
                heat += 5;
                break;
            case SlugShot or HeatSlugShot or CleanShot or HeatCleanShot:
                if (comboOk) heat += 5;
                break;
            case Scattergun:
                heat += 10;
                break;
        }

        switch (actionId)
        {
            case CleanShot or HeatCleanShot:
                if (comboOk) battery += 10;
                break;
            case HotShot:
                battery += 20;
                break;
            case AirAnchor or ChainSaw or Excavator:
                battery += 20;
                break;
        }
        if (actionId is HeatBlast or BlazingShot)
        {
            LocalJobResources.ReduceCooldown(GaussRound, 15f);
            LocalJobResources.ReduceCooldown(Ricochet, 15f);
        }


        gauge->Heat = (byte)Math.Clamp(heat, 0, HeatCap);
        gauge->Battery = (byte)Math.Clamp(battery, 0, BatteryCap);
        gauge->TimerActive = (byte)(gauge->OverheatTimeRemaining > 0 || gauge->SummonTimeRemaining > 0 ? 1 : 0);
    }

    private static void Summon(MachinistGauge* gauge, bool queen)
    {
        var battery = gauge->Battery;
        if (battery < 50) return;

        gauge->LastSummonBatteryPower = battery;
        gauge->SummonTimeRemaining = queen ? QueenMilliseconds : RookMilliseconds;
    }

    public void Tick(float deltaSeconds, byte level)
    {
        var gauge = Gauge();
        if (gauge == null || !float.IsFinite(deltaSeconds) || deltaSeconds <= 0f) return;

        var milliseconds = Math.Max(0, (int)MathF.Round(deltaSeconds * 1000f));
        if (gauge->OverheatTimeRemaining > 0)
            gauge->OverheatTimeRemaining = (short)Math.Max(0, gauge->OverheatTimeRemaining - milliseconds);
        if (gauge->SummonTimeRemaining > 0)
            gauge->SummonTimeRemaining = (short)Math.Max(0, gauge->SummonTimeRemaining - milliseconds);
        gauge->TimerActive = (byte)(gauge->OverheatTimeRemaining > 0 || gauge->SummonTimeRemaining > 0 ? 1 : 0);
    }

    public void Reset(byte level)
    {
        var gauge = Gauge();
        if (gauge == null) return;
        gauge->OverheatTimeRemaining = 0;
        gauge->SummonTimeRemaining = 0;
        gauge->Heat = 0;
        gauge->Battery = 0;
        gauge->LastSummonBatteryPower = 0;
        gauge->TimerActive = 0;
    }

    public bool AllowsNaturalManaRecovery => true;

    private static bool IsReassembleExcluded(uint actionId) => actionId == FullMetalField;

    private static bool IsWeaponSkill(uint actionId) => actionId is
        SplitShot or SlugShot or SpreadShot or HotShot or CleanShot or HeatBlast or
        HeatSplitShot or HeatSlugShot or HeatCleanShot or AutoCrossbow or Drill or
        Bioblaster or AirAnchor or Scattergun or CrownedColliderUpgrade or ChainSaw or
        BlazingShot or Excavator or FullMetalField;
}
