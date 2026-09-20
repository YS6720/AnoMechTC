using System;
using System.Collections.Generic;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;

namespace AnoMech.Core.Combat.Jobs;

// 賢者：Addersgall/Eukrasia 是本機原生量譜；狀態鏈則由房主按來源角色維護。
// Action/Status ids are the installed Taiwan Lumina rows in tmp/eight-job-data.
internal sealed unsafe class Sage : IJobStatusRules, IJobGaugeRules
{
    internal const byte JobId = 40;
    internal static readonly Sage Instance = new();

    private const uint Dosis = 24283;
    private const uint Diagnosis = 24284;
    private const uint Kardia = 24285;
    private const uint KardiaVariant = 28119;
    private const uint Prognosis = 24286;
    private const uint Physis = 24288;
    private const uint Phlegma = 24289;
    private const uint Eukrasia = 24290;
    private const uint EukrasianDiagnosis = 24291;
    private const uint EukrasianPrognosis = 24292;
    private const uint EukrasianDosis = 24293;
    private const uint Soteria = 24294;
    private const uint Druochole = 24296;
    private const uint Dyskrasia = 24297;
    private const uint Kerachole = 24298;
    private const uint Ixochole = 24299;
    private const uint Zoe = 24300;
    private const uint Pepsis = 24301;
    private const uint PhysisII = 24302;
    private const uint Taurochole = 24303;
    private const uint Toxikon = 24304;
    private const uint Haima = 24305;
    private const uint DosisII = 24306;
    private const uint PhlegmaII = 24307;
    private const uint EukrasianDosisII = 24308;
    private const uint Rhizomata = 24309;
    private const uint Holos = 24310;
    private const uint Panhaima = 24311;
    private const uint DosisIII = 24312;
    private const uint PhlegmaIII = 24313;
    private const uint EukrasianDosisIII = 24314;
    private const uint DyskrasiaII = 24315;
    private const uint ToxikonII = 24316;
    private const uint Krasis = 24317;
    private const uint Pneuma = 24318;
    private const uint EukrasianDyskrasia = 37032;
    private const uint Psyche = 37033;
    private const uint EukrasianPrognosisII = 37034;
    private const uint Philosophia = 37035;
    private const uint PneumaVariant = 27524;

    private const ushort KardiaStatus = 2604;
    private const ushort KardionStatus = 2605;
    private const ushort EukrasiaStatus = 2606;
    private const ushort EukrasianDiagnosisShieldStatus = 2607;
    private const ushort CatalyzingDiagnosisShieldStatus = 2608;
    private const ushort EukrasianPrognosisShieldStatus = 2609;
    private const ushort SoteriaStatus = 2610;
    private const ushort ZoeStatus = 2611;
    private const ushort HaimaShieldStatus = 2612;
    private const ushort PanhaimaShieldStatus = 2613;
    private const ushort EukrasianDosisStatus = 2614;
    private const ushort EukrasianDosisIIStatus = 2615;
    private const ushort EukrasianDosisIIIStatus = 2616;
    private const ushort PhysisRegenStatus = 2617;
    private const ushort KeracholeStatus = 2618;
    private const ushort TaurocholeStatus = 2619;
    private const ushort PhysisIIRegenStatus = 2620;
    private const ushort PhysisIIHealingReceivedStatus = 2621;
    private const ushort EukrasianDyskrasiaStatus = 3897;
    private const ushort KrasisStatus = 2622;
    private const ushort HolosShieldStatus = 3365;
    private const ushort HolosMitigationStatus = 3003;
    private const ushort HaimaStacksStatus = 2642;
    private const ushort PanhaimaStacksStatus = 2643;
    private const ushort PhilosophiaHealingStatus = 3898;
    private const ushort PhilosophiaPartyStatus = 3899;

    private const int AddersgallCap = 3;
    private const int TimerMilliseconds = 20_000;
    private const float ManaRestoreFraction = 0.07f;

    private readonly Dictionary<PartyRole, SimCharacter> kardiaTargets = new();

    public bool IsKnownAction(uint actionId)
        => actionId is Dosis or Diagnosis or Kardia or KardiaVariant or Prognosis or Physis or Phlegma or
            Eukrasia or EukrasianDiagnosis or EukrasianPrognosis or EukrasianDosis or
            Soteria or Druochole or Dyskrasia or Kerachole or Ixochole or Zoe or Pepsis or
            PhysisII or Taurochole or Toxikon or Haima or DosisII or PhlegmaII or
            EukrasianDosisII or Rhizomata or Holos or Panhaima or DosisIII or PhlegmaIII or
            EukrasianDosisIII or DyskrasiaII or ToxikonII or Krasis or Pneuma or PneumaVariant or
            EukrasianDyskrasia or Psyche or EukrasianPrognosisII or Philosophia;
    public IReadOnlyList<ushort> TouchedStatuses(uint actionId, bool comboOk)
        => actionId switch
        {
            Kardia or KardiaVariant => [KardiaStatus, KardionStatus],
            Eukrasia => [EukrasiaStatus],
            Diagnosis or Prognosis => [ZoeStatus],
            EukrasianDiagnosis => [EukrasiaStatus, EukrasianDiagnosisShieldStatus, ZoeStatus],
            EukrasianPrognosis or EukrasianPrognosisII =>
                [EukrasiaStatus, EukrasianPrognosisShieldStatus, ZoeStatus],
            EukrasianDosis => [EukrasiaStatus, EukrasianDosisStatus, SoteriaStatus],
            EukrasianDosisII => [EukrasiaStatus, EukrasianDosisIIStatus, SoteriaStatus],
            EukrasianDosisIII => [EukrasiaStatus, EukrasianDosisIIIStatus, SoteriaStatus],
            EukrasianDyskrasia => [EukrasiaStatus, EukrasianDyskrasiaStatus, SoteriaStatus],
            Dosis or Phlegma or Dyskrasia or DosisII or PhlegmaII or DosisIII or PhlegmaIII or
                DyskrasiaII or Toxikon or ToxikonII => [SoteriaStatus],
            Soteria => [SoteriaStatus],
            Zoe => [ZoeStatus],
            Pepsis => [EukrasianDiagnosisShieldStatus, CatalyzingDiagnosisShieldStatus, EukrasianPrognosisShieldStatus],
            Physis => [PhysisRegenStatus],
            PhysisII => [PhysisIIRegenStatus, PhysisIIHealingReceivedStatus],
            Kerachole => [KeracholeStatus],
            Taurochole => [TaurocholeStatus],
            Haima => [HaimaShieldStatus, HaimaStacksStatus],
            Holos => [HolosShieldStatus, HolosMitigationStatus],
            Panhaima => [PanhaimaShieldStatus, PanhaimaStacksStatus],
            Krasis => [KrasisStatus],
            Pneuma or PneumaVariant => [SoteriaStatus, ZoeStatus],
            Philosophia => [PhilosophiaHealingStatus, PhilosophiaPartyStatus],
            _ => Array.Empty<ushort>(),
        };

    public void Apply(in JobActionContext context)
    {
        switch (context.ActionId)
        {
            case Kardia:
            case KardiaVariant:
                ApplyKardia(in context);
                break;

            case Eukrasia:
                context.Grant(EukrasiaStatus, 0, 0f);
                break;

            case EukrasianDiagnosis:
                ApplyEukrasianDiagnosis(in context);
                break;

            case EukrasianPrognosis:
            case EukrasianPrognosisII:
                ApplyEukrasianPrognosis(in context);
                break;

            case EukrasianDosis:
            case EukrasianDosisII:
            case EukrasianDosisIII:
                ApplyEukrasianDosis(in context);
                break;

            case EukrasianDyskrasia:
                ApplyEukrasianDyskrasia(in context);
                break;

            case Soteria:
                context.Grant(SoteriaStatus, 4, 15f);
                break;

            case Zoe:
                context.Grant(ZoeStatus, 0, 30f);
                break;

            case Pepsis:
                ApplyPepsis(in context);
                break;

            case Physis:
                context.GrantParty(PhysisRegenStatus, 0, 15f, 20f);
                break;

            case PhysisII:
                context.GrantParty(PhysisIIRegenStatus, 0, 15f, 30f);
                context.GrantParty(PhysisIIHealingReceivedStatus, 0, context.Level >= 98 ? 15f : 10f, 30f);
                break;

            case Kerachole:
                context.GrantParty(KeracholeStatus, 0, 15f, 30f);
                break;

            case Taurochole:
                context.Grant(TaurocholeStatus, 0, 15f, context.Target ?? context.Player);
                break;

            case Toxikon:
            case ToxikonII:
                context.AddResource(JobResource.Addersting, -1);
                break;

            case Haima:
                ApplyHaima(in context);
                break;

            case Holos:
                context.GrantParty(HolosShieldStatus, 0, 30f, 30f);
                context.GrantParty(HolosMitigationStatus, 0, 20f, 30f);
                break;

            case Panhaima:
                ApplyPanhaima(in context);
                break;

            case Krasis:
                context.Grant(KrasisStatus, 0, 10f, context.Target ?? context.Player);
                break;

            case Philosophia:
                context.Grant(PhilosophiaHealingStatus, 0, 20f);
                context.GrantParty(PhilosophiaPartyStatus, 0, 20f, 30f);
                break;
        }
    }

    public void OnPartyAction(in JobActionContext context, SimCharacter actor)
    {
        if (!ReferenceEquals(actor, context.Player)) return;

        // 24294 is Soteria (four Kardia-amplifying stacks); 24300 is Zoe
        // (one healing-magic amplification). Their labels are intentionally kept
        // separate from the Taiwan action rows above.
        if (IsKardiaAttack(context.ActionId) && HasKardiaTarget(in context))
            context.Consume(SoteriaStatus);
        if (IsHealingAction(context.ActionId))
            context.Consume(ZoeStatus);
    }

    public void OnMechanicHit(in JobActionContext context, SimCharacter target)
    {
        if (ReapplyHaimaStack(in context, target) || ReapplyPanhaimaStack(in context, target))
            return;

        if (context.Find(EukrasianDiagnosisShieldStatus, target) is { RemainingTime: > 0f })
        {
            context.Remove(EukrasianDiagnosisShieldStatus, target);
            context.AddResource(JobResource.Addersting, 1);
            return;
        }
        if (context.Find(CatalyzingDiagnosisShieldStatus, target) is { RemainingTime: > 0f })
        {
            context.Remove(CatalyzingDiagnosisShieldStatus, target);
            context.AddResource(JobResource.Addersting, 1);
            return;
        }
        if (context.Find(EukrasianPrognosisShieldStatus, target) is { RemainingTime: > 0f })
        {
            context.Remove(EukrasianPrognosisShieldStatus, target);
            context.AddResource(JobResource.Addersting, 1);
            return;
        }
        if (context.Find(HolosShieldStatus, target) is { RemainingTime: > 0f })
            context.Remove(HolosShieldStatus, target);
    }

    public void ResetStatusState() => kardiaTargets.Clear();

    private void ApplyKardia(in JobActionContext context)
    {
        var target = context.Target ?? context.Player;
        if (kardiaTargets.TryGetValue(context.SourceRole, out var previous) &&
            !ReferenceEquals(previous, target))
            context.Remove(KardionStatus, previous);

        context.Grant(KardiaStatus, 0, 0f);
        context.Grant(KardionStatus, 0, 0f, target);
        kardiaTargets[context.SourceRole] = target;
    }

    private static void ApplyEukrasianDiagnosis(in JobActionContext context)
    {
        context.Remove(EukrasiaStatus);
        var target = context.Target ?? context.Player;
        context.Remove(EukrasianPrognosisShieldStatus, target);
        context.Remove(EukrasianDiagnosisShieldStatus, target);
        context.Remove(CatalyzingDiagnosisShieldStatus, target);
        context.Grant(EukrasianDiagnosisShieldStatus, 0, 30f, target);
    }

    private static void ApplyEukrasianDosis(in JobActionContext context)
    {
        context.Remove(EukrasiaStatus);
        if (context.Target is not { } target) return;
        var status = context.ActionId switch
        {
            EukrasianDosis => EukrasianDosisStatus,
            EukrasianDosisII => EukrasianDosisIIStatus,
            _ => EukrasianDosisIIIStatus,
        };
        context.Grant(status, 0, 30f, target);
    }

    private static void ApplyEukrasianDyskrasia(in JobActionContext context)
    {
        context.Remove(EukrasiaStatus);
        if (context.Target is { } target)
            context.Grant(EukrasianDyskrasiaStatus, 0, 30f, target);
    }

    private static void ApplyEukrasianPrognosis(in JobActionContext context)
    {
        context.Remove(EukrasiaStatus);
        context.Remove(EukrasianDiagnosisShieldStatus, context.Player);
        context.Remove(CatalyzingDiagnosisShieldStatus, context.Player);
        context.Remove(EukrasianPrognosisShieldStatus, context.Player);
        context.Grant(EukrasianPrognosisShieldStatus, 0, 30f, context.Player);
        foreach (var member in context.World.Party.ActiveMembers())
        {
            if (!member.IsActive || member is ISimPartyMember { Dead: true } ||
                context.Player.Placement().DistanceSq(member) > 20f * 20f)
                continue;
            if (ReferenceEquals(member, context.Player)) continue;
            context.Remove(EukrasianDiagnosisShieldStatus, member);
            context.Remove(CatalyzingDiagnosisShieldStatus, member);
            context.Remove(EukrasianPrognosisShieldStatus, member);
            context.Grant(EukrasianPrognosisShieldStatus, 0, 30f, member);
        }
    }

    private static void ApplyPepsis(in JobActionContext context)
    {
        foreach (var member in context.World.Party.ActiveMembers())
        {
            if (!member.IsActive || member is ISimPartyMember { Dead: true } ||
                context.Player.Placement().DistanceSq(member) > 20f * 20f)
                continue;
            context.Remove(EukrasianDiagnosisShieldStatus, member);
            context.Remove(CatalyzingDiagnosisShieldStatus, member);
            context.Remove(EukrasianPrognosisShieldStatus, member);
        }
    }

    private static void ApplyHaima(in JobActionContext context)
    {
        var target = context.Target ?? context.Player;
        context.Remove(HaimaShieldStatus, target);
        context.Remove(HaimaStacksStatus, target);
        context.Grant(HaimaShieldStatus, 0, 15f, target);
        context.Grant(HaimaStacksStatus, 5, 15f, target);
    }

    private static void ApplyPanhaima(in JobActionContext context)
    {
        foreach (var member in context.World.Party.ActiveMembers())
        {
            if (!member.IsActive || member is ISimPartyMember { Dead: true } ||
                context.Player.Placement().DistanceSq(member) > 30f * 30f)
                continue;
            context.Remove(PanhaimaShieldStatus, member);
            context.Remove(PanhaimaStacksStatus, member);
            context.Grant(PanhaimaShieldStatus, 0, 15f, member);
            context.Grant(PanhaimaStacksStatus, 5, 15f, member);
        }
    }

    private static bool ReapplyHaimaStack(in JobActionContext context, SimCharacter target)
        => ReapplyStackedShield(in context, target, HaimaShieldStatus, HaimaStacksStatus, 0);

    private static bool ReapplyPanhaimaStack(in JobActionContext context, SimCharacter target)
        => ReapplyStackedShield(in context, target, PanhaimaShieldStatus, PanhaimaStacksStatus, 0);

    private static bool ReapplyStackedShield(
        in JobActionContext context, SimCharacter target, ushort shieldStatus, ushort stacksStatus, int shieldParam)
    {
        var shield = context.Find(shieldStatus, target);
        if (shield is null) return false;
        context.Remove(shieldStatus, target);
        var stacks = context.Find(stacksStatus, target);
        if (shield.RemainingTime <= 0f || stacks is null || stacks.Stacks <= 1 || stacks.RemainingTime <= 0f)
        {
            if (stacks is not null) context.Remove(stacksStatus, target);
            return true;
        }
        var remaining = stacks.RemainingTime;
        context.Consume(stacksStatus, 1, target);
        context.Grant(shieldStatus, shieldParam, remaining, target);
        return true;
    }

    private bool HasKardiaTarget(in JobActionContext context)
    {
        if (context.Find(KardiaStatus) is null) return false;
        return kardiaTargets.TryGetValue(context.SourceRole, out var target) &&
            context.Find(KardionStatus, target) is not null;
    }

    private static bool IsKardiaAttack(uint actionId)
        => actionId is Dosis or Phlegma or EukrasianDosis or Dyskrasia or Toxikon or DosisII or
            PhlegmaII or EukrasianDosisII or DosisIII or PhlegmaIII or EukrasianDosisIII or
            DyskrasiaII or ToxikonII or Pneuma or PneumaVariant or EukrasianDyskrasia;

    private static bool IsHealingAction(uint actionId)
        => actionId is Diagnosis or Prognosis or EukrasianDiagnosis or EukrasianPrognosis or
            Pneuma or PneumaVariant or EukrasianPrognosisII;

    private static SageGauge* Gauge()
    {
        var manager = JobGaugeManager.Instance();
        if (manager == null || manager->ClassJobId != JobId || manager->CurrentGauge == null) return null;
        return (SageGauge*)manager->CurrentGauge;
    }

    public void OnLocalFire(uint actionId, bool comboOk, byte level)
    {
        var gauge = Gauge();
        if (gauge == null) return;

        switch (actionId)
        {
            case Eukrasia:
                gauge->Eukrasia = 1;
                break;

            case EukrasianDiagnosis:
            case EukrasianPrognosis:
            case EukrasianDosis:
            case EukrasianDosisII:
            case EukrasianDosisIII:
            case EukrasianDyskrasia:
            case EukrasianPrognosisII:
                gauge->Eukrasia = 0;
                break;

            case Rhizomata:
                if (level >= 74 && gauge->Addersgall < AddersgallCap)
                    gauge->Addersgall++;
                break;

            case Druochole:
            case Kerachole:
            case Ixochole:
            case Taurochole:
                if (gauge->Addersgall == 0) return;
                gauge->Addersgall--;
                LocalJobResources.RestoreMana((int)(LocalJobResources.MaxMana * ManaRestoreFraction));
                break;
        }
    }

    public void Reset(byte level)
    {
        var gauge = Gauge();
        if (gauge == null) return;
        gauge->AddersgallTimer = 0;
        gauge->Addersgall = 0;
        gauge->Addersting = 0;
        gauge->Eukrasia = 0;
    }

    public void Tick(float deltaSeconds, byte level)
    {
        var gauge = Gauge();
        if (gauge == null || level < 45 || !float.IsFinite(deltaSeconds) || deltaSeconds <= 0f)
            return;
        if (gauge->Addersgall >= AddersgallCap)
            return;

        var timer = Math.Max(0, (int)gauge->AddersgallTimer + (int)MathF.Round(deltaSeconds * 1000f));
        while (timer >= TimerMilliseconds && gauge->Addersgall < AddersgallCap)
        {
            gauge->Addersgall++;
            timer -= TimerMilliseconds;
        }
        gauge->AddersgallTimer = gauge->Addersgall == AddersgallCap ? (short)0 : (short)timer;
    }

    public bool AllowsNaturalManaRecovery => true;
}
