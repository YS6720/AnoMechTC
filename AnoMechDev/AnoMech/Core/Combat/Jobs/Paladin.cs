using System;
using System.Collections.Generic;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace AnoMech.Core.Combat.Jobs;

// Paladin's status transitions are deliberately host-owned. Native Confiteor
// combo fields are present in the installed schema, but their step/timer
// semantics are not proven; the source-qualified SimStatus chain is the
// authoritative practice state instead.
internal sealed unsafe class Paladin : IJobStatusRules, IJobGaugeRules
{
    internal const byte JobId = 19;
    internal static readonly Paladin Instance = new();

    // Passage of Arms is a channel. Existing input hooks call this symbol when
    // movement or another action cancels the channel.
    internal const ushort PassageOfArms = 1175;

    internal static void CancelChanneled(SimCharacter player)
        => player.RemoveStatusAnySource(PassageOfArms);

    // ---- actions ----
    private const uint Sentinel = 17;
    private const uint FightOrFlight = 20;
    private const uint Bulwark = 22;
    private const uint Cover = 27;
    private const uint HallowedGround = 30;
    private const uint GoringBlade = 3538;
    private const uint RoyalAuthority = 3539;
    private const uint DivineVeil = 3540;
    private const uint Sheltron = 3542;
    private const uint Intervention = 7382;
    private const uint Requiescat = 7383;
    private const uint HolySpirit = 7384;
    private const uint Passage = 7385;
    private const uint Prominence = 16457;
    private const uint HolyCircle = 16458;
    private const uint Confiteor = 16459;
    private const uint Atonement = 16460;
    private const uint HolySheltron = 25746;
    private const uint BladeOfFaith = 25748;
    private const uint BladeOfTruth = 25749;
    private const uint BladeOfValor = 25750;
    private const uint Supplication = 36918;
    private const uint Sepulchre = 36919;
    private const uint Guardian = 36920;
    private const uint Imperator = 36921;
    private const uint GloryBlade = 36922;

    // ---- statuses ----
    private const ushort SentinelStatus = 74;
    private const ushort FightOrFlightStatus = 76;
    private const ushort BulwarkStatus = 77;
    private const ushort HallowedGroundStatus = 82;
    private const ushort DivineVeilStatus = 1362;
    private const ushort DivineMight = 2673;
    private const ushort HolySheltronStatus = 2674;
    private const ushort KnightsResolve = 2675;
    private const ushort KnightsBenediction = 2676;
    private const ushort RequiescatStatus = 1368;
    private const ushort InterventionStatus = 1174;
    private const ushort ConfiteorReady = 3019;
    private const ushort GoringBladeReady = 3847;
    private const ushort AtonementReady = 1902;
    private const ushort SupplicationReady = 3827;
    private const ushort SepulchreReady = 3828;
    private const ushort GuardianStatus = 3829;
    private const ushort GuardianShield = 3830;
    private const ushort GloryBladeReady = 3831;

    public bool IsKnownAction(uint actionId) => actionId is
        Sentinel or FightOrFlight or Bulwark or HallowedGround or GoringBlade or
        RoyalAuthority or DivineVeil or Sheltron or Intervention or Requiescat or
        HolySpirit or Passage or Prominence or HolyCircle or Confiteor or Atonement or
        HolySheltron or BladeOfFaith or BladeOfTruth or BladeOfValor or Supplication or
        Sepulchre or Guardian or Imperator or GloryBlade;

    public IReadOnlyList<ushort> TouchedStatuses(uint actionId, bool comboOk)
        => actionId switch
        {
            Sentinel => [SentinelStatus],
            FightOrFlight => [FightOrFlightStatus, GoringBladeReady],
            Bulwark => [BulwarkStatus],
            HallowedGround => [HallowedGroundStatus],
            GoringBlade => [GoringBladeReady],
            RoyalAuthority when comboOk => [DivineMight, AtonementReady],
            DivineVeil => [DivineVeilStatus],
            Intervention => [InterventionStatus, KnightsResolve, KnightsBenediction],
            HolySpirit or HolyCircle => [DivineMight, RequiescatStatus],
            Prominence when comboOk => [DivineMight],
            Atonement => [AtonementReady, SupplicationReady],
            Supplication => [SupplicationReady, SepulchreReady],
            Sepulchre => [SepulchreReady],
            HolySheltron => [HolySheltronStatus, KnightsResolve, KnightsBenediction],
            Confiteor or BladeOfFaith or BladeOfTruth or BladeOfValor
                => [RequiescatStatus, ConfiteorReady, GloryBladeReady],
            Guardian => [GuardianStatus, GuardianShield],
            Passage => [PassageOfArms],
            GloryBlade => [GloryBladeReady],
            _ => Array.Empty<ushort>(),
        };

    public void Apply(in JobActionContext context)
    {
        switch (context.ActionId)
        {
            case Sentinel:
                context.Grant(SentinelStatus, 0, 15f);
                break;
            case FightOrFlight:
                context.Grant(FightOrFlightStatus, 0, 20f);
                context.Grant(GoringBladeReady, 0, 30f);
                break;
            case Bulwark:
                context.Grant(BulwarkStatus, 0, 10f);
                break;
            case HallowedGround:
                context.Grant(HallowedGroundStatus, 0, 10f);
                break;
            case GoringBlade:
                context.Remove(GoringBladeReady);
                break;
            case RoyalAuthority when context.ComboOk:
            case Prominence when context.ComboOk:
                context.Grant(DivineMight, 0, 30f);
                if (context.ActionId == RoyalAuthority)
                    context.Grant(AtonementReady, 0, 30f);
                break;
            case DivineVeil:
                // EffectRange in the installed Action row is 30m; GrantParty
                // also includes the caster when active.
                context.GrantParty(DivineVeilStatus, 0, 30f, 30f);
                break;
            case Intervention:
                if (context.Target is null) break;
                var interventionDuration = context.Level >= 82 ? 8f : 6f;
                context.Grant(InterventionStatus, 0, interventionDuration, context.Target);
                if (context.Level >= 82)
                {
                    context.Grant(KnightsResolve, 0, 4f, context.Target);
                    context.Grant(KnightsBenediction, 0, 12f, context.Target);
                }
                break;
            case Requiescat:
            case Imperator:
                context.Grant(RequiescatStatus, 4, 30f);
                context.Grant(ConfiteorReady, 0, 30f);
                break;
            case HolySpirit:
            case HolyCircle:
                context.Consume(RequiescatStatus);
                context.Consume(DivineMight);
                break;
            case Atonement:
                if (context.Find(AtonementReady) is not null)
                {
                    context.Grant(SupplicationReady, 0, 30f);
                    context.Consume(AtonementReady);
                }
                break;
            case Supplication:
                if (context.Find(SupplicationReady) is not null)
                {
                    context.Grant(SepulchreReady, 0, 30f);
                    context.Consume(SupplicationReady);
                }
                break;
            case Sepulchre:
                context.Consume(SepulchreReady);
                break;
            case HolySheltron:
                context.Grant(HolySheltronStatus, 0, 8f);
                context.Grant(KnightsResolve, 0, 4f);
                context.Grant(KnightsBenediction, 0, 12f);
                break;
            case Confiteor:
            case BladeOfFaith:
            case BladeOfTruth:
            case BladeOfValor:
                ConsumeConfiteorStep(in context);
                break;
            case Guardian:
                context.Grant(GuardianStatus, 0, 15f);
                context.Grant(GuardianShield, 0, 15f);
                break;
            case Passage:
                context.Grant(PassageOfArms, 0, 18f);
                break;
            case GloryBlade:
                context.Consume(GloryBladeReady);
                break;
        }
    }

    private static void ConsumeConfiteorStep(in JobActionContext context)
    {
        // ConfiteorReady belongs only to the first action in this chain. The
        // Requiescat stack is an independent four-cast resource: Holy Spirit
        // and Holy Circle consume it first, while any remaining stack is
        // consumed by the Confiteor follow-up sequence.
        if (context.ActionId == Confiteor)
            context.Remove(ConfiteorReady);

        var requiescat = context.Find(RequiescatStatus);
        if (requiescat is not null)
        {
            if (requiescat.Stacks <= 1)
                context.Remove(RequiescatStatus);
            else
                context.Grant(RequiescatStatus, requiescat.Stacks - 1,
                    requiescat.RemainingTime);
        }

        // Blade of Valor is the level-100 transition into Glory Blade. Grant
        // the next proc before consuming the last Requiescat stack so an
        // outcome invalidation can never erase both sides of the hand-off.
        if (context.ActionId == BladeOfValor && context.Level >= 100)
            context.Grant(GloryBladeReady, 0, 30f);
    }

    // ---- IJobGaugeRules: Oath gauge (local native gauge only) ----
    private const int OathMax = 100;
    private const int OathPerAutoAttack = 5;
    private const int OathSpend = 50;
    private const float FallbackWeaponDelaySeconds = 2.24f;
    private float autoAttackTimer;

    private static PaladinGauge* Gauge()
    {
        var manager = JobGaugeManager.Instance();
        if (manager == null || manager->ClassJobId != JobId || manager->CurrentGauge == null)
            return null;
        return (PaladinGauge*)manager->CurrentGauge;
    }

    private static float WeaponDelaySeconds()
    {
        var inventory = InventoryManager.Instance();
        var equipped = inventory == null
            ? null
            : inventory->GetInventoryContainer(InventoryType.EquippedItems);
        if (equipped == null || equipped->Size == 0) return FallbackWeaponDelaySeconds;
        var slot = equipped->GetInventorySlot(0);
        if (slot == null || slot->ItemId == 0) return FallbackWeaponDelaySeconds;
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        if (!sheet.TryGetRow(slot->ItemId, out var item) || item.Delayms == 0)
            return FallbackWeaponDelaySeconds;
        return item.Delayms / 1000f;
    }

    private static void AddOath(PaladinGauge* gauge, int amount)
        => gauge->OathGauge = (byte)Math.Clamp(gauge->OathGauge + amount, 0, OathMax);

    public void Tick(float deltaSeconds, byte level)
    {
        _ = level;
        var gauge = Gauge();
        if (gauge == null) return;
        if (Plugin.PlayerInputHooks?.IsAutoAttacking != true)
        {
            autoAttackTimer = 0f;
            return;
        }

        var delay = WeaponDelaySeconds();
        if (delay <= 0f) delay = FallbackWeaponDelaySeconds;
        autoAttackTimer += MathF.Max(0f, deltaSeconds);
        while (autoAttackTimer >= delay)
        {
            autoAttackTimer -= delay;
            AddOath(gauge, OathPerAutoAttack);
        }
    }

    public void OnLocalFire(uint actionId, bool comboOk, byte level)
    {
        _ = comboOk;
        _ = level;
        var gauge = Gauge();
        if (gauge == null) return;
        if (comboOk && actionId is 15 or Prominence)
            LocalJobResources.RestoreMana(1_000);
        if (actionId is Atonement or Supplication or Sepulchre)
            LocalJobResources.RestoreMana(400);
        if (actionId is 23 or 25747)
            LocalJobResources.RestoreMana(500);
        if (actionId is not Sheltron and not HolySheltron and not Intervention and not Cover)
            return;
        AddOath(gauge, -OathSpend);
        Core.CrashTrace.Log($"[量譜] PLD a={actionId} 忠義={gauge->OathGauge}");
    }

    public void Reset(byte level)
    {
        _ = level;
        autoAttackTimer = 0f;
        var gauge = Gauge();
        if (gauge != null) gauge->OathGauge = OathMax;
    }
}
