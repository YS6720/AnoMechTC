using System;
using System.Collections.Generic;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;

namespace AnoMech.Core.Combat.Jobs;

// Reaper status transitions are host-owned. Soul/Shroud and the Lemure/Void
// counters remain local because they are fields of the native job gauge.
internal sealed unsafe class Reaper : IJobStatusRules, IJobGaugeRules
{
    internal const byte JobId = 39;
    internal static readonly Reaper Instance = new();

    // Actions
    private const uint Slice = 24373;
    private const uint WaxingSlice = 24374;
    private const uint InfernalSlice = 24375;
    private const uint SpinningScythe = 24376;
    private const uint NightmareScythe = 24377;
    private const uint ShadowOfDeath = 24378;
    private const uint WhorlOfDeath = 24379;
    private const uint SoulSlice = 24380;
    private const uint SoulScythe = 24381;
    private const uint Gibbet = 24382;
    private const uint Gallows = 24383;
    private const uint Guillotine = 24384;
    private const uint PlentifulHarvest = 24385;
    private const uint Harpe = 24386;
    private const uint Soulsow = 24387;
    private const uint HarvestMoon = 24388;
    private const uint BloodStalk = 24389;
    private const uint UnveiledGibbet = 24390;
    private const uint UnveiledGallows = 24391;
    private const uint GrimSwathe = 24392;
    private const uint Gluttony = 24393;
    private const uint Enshroud = 24394;
    private const uint VoidReaping = 24395;
    private const uint CrossReaping = 24396;
    private const uint GrimReaping = 24397;
    private const uint Communio = 24398;
    private const uint LemuresSlice = 24399;
    private const uint LemuresScythe = 24400;
    private const uint HellsIngress = 24401;
    private const uint HellsEgress = 24402;
    private const uint Regress = 24403;
    private const uint ArcaneCrest = 24404;
    private const uint ArcaneCircle = 24405;
    private const uint Sacrificium = 36969;
    private const uint ExecutionersGibbet = 36970;
    private const uint ExecutionersGallows = 36971;
    private const uint ExecutionersGuillotine = 36972;
    private const uint Perfectio = 36973;

    // Statuses
    private const ushort DeathsDesign = 2586;
    private const ushort SoulReaver = 2587;
    private const ushort EnhancedGibbet = 2588;
    private const ushort EnhancedGallows = 2589;
    private const ushort EnhancedVoidReaping = 2590;
    private const ushort EnhancedCrossReaping = 2591;
    private const ushort ImmortalSacrifice = 2592;
    private const ushort Enshrouded = 2593;
    private const ushort SoulsowStatus = 2594;
    private const ushort Threshold = 2595;
    private const ushort CrestOfTimeBorrowed = 2596;
    private const ushort CrestOfTimeReturned = 2598;
    private const ushort ArcaneCircleStatus = 2599;
    private const ushort CircleOfSacrifice = 2600;
    private const ushort BloodsownCircle = 2601;
    private const ushort EnhancedHarpe = 2845;
    private const ushort SacrificiumReady = 3857;
    private const ushort Executioner = 3858;
    private const ushort PerfectioOcculta = 3859;
    private const ushort PerfectioParata = 3860;

    public bool IsKnownAction(uint actionId) => actionId is
        Slice or WaxingSlice or InfernalSlice or SpinningScythe or NightmareScythe or
        ShadowOfDeath or WhorlOfDeath or SoulSlice or SoulScythe or Gibbet or Gallows or
        Guillotine or PlentifulHarvest or Harpe or Soulsow or HarvestMoon or BloodStalk or
        UnveiledGibbet or UnveiledGallows or GrimSwathe or Gluttony or Enshroud or
        VoidReaping or CrossReaping or GrimReaping or Communio or LemuresSlice or
        LemuresScythe or HellsIngress or HellsEgress or Regress or ArcaneCrest or
        ArcaneCircle or Sacrificium or ExecutionersGibbet or ExecutionersGallows or
        ExecutionersGuillotine or Perfectio;

    public IReadOnlyList<ushort> TouchedStatuses(uint actionId, bool comboOk) => actionId switch
    {
        Slice or WaxingSlice or InfernalSlice or SpinningScythe or NightmareScythe or
            SoulSlice or SoulScythe => [Executioner],
        ShadowOfDeath or WhorlOfDeath => [DeathsDesign, Executioner],
        BloodStalk or GrimSwathe => [SoulReaver],
        UnveiledGibbet => [SoulReaver, EnhancedGibbet],
        UnveiledGallows => [SoulReaver, EnhancedGallows],
        Gibbet => [SoulReaver, EnhancedGallows],
        Gallows => [SoulReaver, EnhancedGibbet],
        Guillotine => [SoulReaver],
        ExecutionersGibbet => [Executioner, EnhancedGallows],
        ExecutionersGallows => [Executioner, EnhancedGibbet],
        ExecutionersGuillotine => [Executioner],
        Gluttony => [SoulReaver, Executioner],
        Sacrificium => [SacrificiumReady],
        PlentifulHarvest => [ImmortalSacrifice, PerfectioOcculta, Executioner],
        Soulsow => [SoulsowStatus, Executioner],
        HarvestMoon => [SoulsowStatus, Executioner],
        Enshroud => [Enshrouded, SacrificiumReady, PerfectioParata],
        VoidReaping => [EnhancedVoidReaping, EnhancedCrossReaping, Executioner],
        CrossReaping => [EnhancedCrossReaping, EnhancedVoidReaping, Executioner],
        Communio => [Enshrouded, PerfectioOcculta, PerfectioParata, Executioner],
        HellsIngress or HellsEgress => [EnhancedHarpe, Threshold],
        Harpe => [EnhancedHarpe, Executioner],
        Regress => [Threshold],
        ArcaneCrest => [CrestOfTimeBorrowed, CrestOfTimeReturned],
        ArcaneCircle => [ArcaneCircleStatus, CircleOfSacrifice, BloodsownCircle, ImmortalSacrifice],
        Perfectio => [PerfectioParata, Executioner],
        _ => Array.Empty<ushort>(),
    };

    public void Apply(in JobActionContext context)
    {
        if (context.ActionId != Gluttony &&
            context.ActionId is not ExecutionersGibbet and not ExecutionersGallows and not ExecutionersGuillotine &&
            context.Find(Executioner) is not null &&
            IsWeaponskillOrMagic(context.ActionId))
        {
            context.Remove(Executioner);
        }

        switch (context.ActionId)
        {
            case ShadowOfDeath:
                ApplyDeathsDesign(context, context.Target);
                break;
            case WhorlOfDeath:
                ApplyDeathsDesignArea(context);
                break;
            case BloodStalk or GrimSwathe:
                if (context.Level >= 70)
                    context.Grant(SoulReaver, 1, 30f);
                break;
            case UnveiledGibbet:
                context.Remove(EnhancedGibbet);
                context.Grant(SoulReaver, 1, 30f);
                break;
            case UnveiledGallows:
                context.Remove(EnhancedGallows);
                context.Grant(SoulReaver, 1, 30f);
                break;
            case Gibbet:
                context.Consume(SoulReaver);
                context.Grant(EnhancedGallows, 0, 60f);
                break;
            case Gallows:
                context.Consume(SoulReaver);
                context.Grant(EnhancedGibbet, 0, 60f);
                break;
            case Guillotine:
                context.Consume(SoulReaver);
                break;
            case ExecutionersGibbet:
                context.Consume(Executioner);
                context.Grant(EnhancedGallows, 0, 60f);
                break;
            case ExecutionersGallows:
                context.Consume(Executioner);
                context.Grant(EnhancedGibbet, 0, 60f);
                break;
            case ExecutionersGuillotine:
                context.Consume(Executioner);
                break;
            case Gluttony:
                if (context.Level >= 96)
                {
                    context.Remove(SoulReaver);
                    context.Grant(Executioner, 2, 30f);
                }
                else if (context.Level >= 76)
                    context.Grant(SoulReaver, 2, 30f);
                break;

            case Sacrificium:
                context.Remove(SacrificiumReady);
                break;
            case PlentifulHarvest:
                context.Remove(ImmortalSacrifice);
                if (context.Level >= 100)
                    context.Grant(PerfectioOcculta, 0, 30f);
                break;
            case Soulsow:
                context.Grant(SoulsowStatus, 0, 0f);
                break;
            case HarvestMoon:
                context.Remove(SoulsowStatus);
                break;
            case Enshroud:
                context.Grant(Enshrouded, 0, 30f);
                if (context.Level >= 92)
                    context.Grant(SacrificiumReady, 0, 30f);
                context.Remove(PerfectioParata);
                break;
            case VoidReaping:
                context.Remove(EnhancedVoidReaping);
                context.Grant(EnhancedCrossReaping, 0, 30f);
                break;
            case CrossReaping:
                context.Remove(EnhancedCrossReaping);
                context.Grant(EnhancedVoidReaping, 0, 30f);
                break;
            case Communio:
                context.Remove(Enshrouded);
                if (context.Find(PerfectioOcculta) is not null)
                {
                    context.Remove(PerfectioOcculta);
                    context.Grant(PerfectioParata, 0, 30f);
                }
                break;
            case HellsIngress or HellsEgress:
                context.Grant(EnhancedHarpe, 0, 10f);
                if (context.Level >= 74)
                    context.Grant(Threshold, 0, 10f);
                break;
            case Harpe:
                context.Remove(EnhancedHarpe);
                break;
            case Regress:
                context.Remove(Threshold);
                break;
            case ArcaneCrest:
                context.Grant(CrestOfTimeBorrowed, 0, 5f);
                break;
            case ArcaneCircle:
                context.Grant(ArcaneCircleStatus, 0, 20f);
                if (context.Level >= 88)
                {
                    context.GrantParty(CircleOfSacrifice, 0, 5f, 15f);
                    context.Grant(BloodsownCircle, 0, 6f);
                }
                break;
            case Perfectio:
                context.Remove(PerfectioParata);
                break;
        }
    }

    public void OnPartyAction(in JobActionContext context, SimCharacter actor)
    {
        if (context.Level < 88 || !IsWeaponskillOrMagic(context.ActionId))
            return;
        if (context.Find(BloodsownCircle) is null)
            return;
        if (actor.FindStatus(CircleOfSacrifice, context.SourceRole) is null)
            return;

        var stacks = context.Find(ImmortalSacrifice)?.Stacks ?? 0;
        if (stacks >= 8)
            return;
        context.Grant(ImmortalSacrifice, stacks + 1, 30f);
    }

    public void OnMechanicHit(in JobActionContext context, SimCharacter target)
    {
        if (!ReferenceEquals(target, context.Player) || context.Find(CrestOfTimeBorrowed) is null)
            return;

        context.Remove(CrestOfTimeBorrowed);
        if (context.Level >= 84)
            context.GrantParty(CrestOfTimeReturned, 0, 15f, 15f);
    }

    public void ResetStatusState()
    {
    }

    private static bool IsWeaponskillOrMagic(uint actionId)
    {
        var actions = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        if (!actions.TryGetRow(actionId, out var action))
            return false;
        return action.ActionCategory.RowId is 2 or 3;
    }

    private static void ApplyDeathsDesign(in JobActionContext context, SimCharacter? target)
    {
        if (target is null)
            return;
        var previous = context.Find(DeathsDesign, target);
        var duration = MathF.Min(60f, 30f + (previous?.RemainingTime ?? 0f));
        context.Grant(DeathsDesign, 0, duration, target);
    }

    private static void ApplyDeathsDesignArea(in JobActionContext context)
    {
        var center = context.Target ?? context.Player;
        foreach (var child in context.World.Children)
        {
            if (child is not SimEnemy { IsActive: true, Targetable: true } enemy)
                continue;
            if (center.Placement().DistanceSq(enemy) > 25f)
                continue;
            ApplyDeathsDesign(context, enemy);
        }
    }

    // ---- Local native gauge ----

    private const ushort EnshroudDurationMilliseconds = 30_000;

    private static ReaperGauge* Gauge()
    {
        var manager = JobGaugeManager.Instance();
        if (manager == null || manager->ClassJobId != JobId || manager->CurrentGauge == null)
            return null;
        return (ReaperGauge*)manager->CurrentGauge;
    }

    public void OnLocalFire(uint actionId, bool comboOk, byte level)
    {
        _ = comboOk;
        var gauge = Gauge();
        if (gauge == null)
            return;

        var soul = (int)gauge->Soul;
        var shroud = (int)gauge->Shroud;
        var lemure = (int)gauge->LemureShroud;
        var voidShroud = (int)gauge->VoidShroud;
        var enshroudedTime = (int)gauge->EnshroudedTimeRemaining;

        switch (actionId)
        {
            case Slice or WaxingSlice or InfernalSlice or SpinningScythe or NightmareScythe or Harpe:
                if (level >= 50) soul += 10;
                break;
            case SoulSlice or SoulScythe:
                if (level >= 50) soul += 50;
                break;
            case HarvestMoon:
                if (level >= 82) soul += 10;
                break;
            case BloodStalk or GrimSwathe or UnveiledGibbet or UnveiledGallows or Gluttony:
                soul -= 50;
                break;
            case Gibbet or Gallows or Guillotine or ExecutionersGibbet or ExecutionersGallows or ExecutionersGuillotine:
                if (level >= 80) shroud += 10;
                break;
            case Enshroud:
                if (level >= 80)
                {
                    shroud -= 50;
                    lemure = 5;
                    voidShroud = 0;
                    enshroudedTime = EnshroudDurationMilliseconds;
                }
                break;
            case VoidReaping or CrossReaping or GrimReaping:
                if (level >= 80)
                {
                    lemure -= 1;
                    voidShroud += 1;
                }
                break;
            case Communio:
                if (level >= 90)
                {
                    lemure = 0;
                    voidShroud = 0;
                    enshroudedTime = 0;
                }
                break;
            case LemuresSlice or LemuresScythe:
                if (level >= 86)
                    voidShroud -= 2;
                break;
        }

        gauge->Soul = (byte)Math.Clamp(soul, 0, 100);
        gauge->Shroud = (byte)Math.Clamp(shroud, 0, 100);
        gauge->LemureShroud = (byte)Math.Clamp(lemure, 0, 5);
        gauge->VoidShroud = (byte)Math.Clamp(voidShroud, 0, 5);
        gauge->EnshroudedTimeRemaining = (ushort)Math.Clamp(enshroudedTime, 0, EnshroudDurationMilliseconds);

        if (actionId == Harpe && LocalJobResources.HasStatus(EnhancedHarpe))
            LocalJobResources.ReduceCooldown(HellsIngress, 5f);
    }

    public void Tick(float deltaSeconds, byte level)
    {
        var gauge = Gauge();
        if (gauge == null || level < 80)
            return;
        if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0f)
            return;

        if (gauge->EnshroudedTimeRemaining == 0)
        {
            gauge->LemureShroud = 0;
            gauge->VoidShroud = 0;
            return;
        }

        var milliseconds = Math.Max(0, (int)MathF.Round(deltaSeconds * 1000f));
        gauge->EnshroudedTimeRemaining = (ushort)Math.Max(
            0,
            gauge->EnshroudedTimeRemaining - milliseconds);
        if (gauge->EnshroudedTimeRemaining == 0)
        {
            gauge->LemureShroud = 0;
            gauge->VoidShroud = 0;
        }
    }

    public void Reset(byte level)
    {
        _ = level;
        var gauge = Gauge();
        if (gauge == null)
            return;
        gauge->Soul = 0;
        gauge->Shroud = 0;
        gauge->EnshroudedTimeRemaining = 0;
        gauge->LemureShroud = 0;
        gauge->VoidShroud = 0;
    }
}
