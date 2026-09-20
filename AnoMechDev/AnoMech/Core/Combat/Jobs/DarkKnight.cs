using System;
using System.Collections.Generic;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace AnoMech.Core.Combat.Jobs;

// Dark Knight has two separate state planes. Host status rules own visible
// procs and their source-qualified transitions; the local gauge rule writes
// only this client's native Blood/timer fields. DeliriumStep is intentionally
// untouched because the installed schema exposes no verified enum semantics.
internal sealed unsafe class DarkKnight : IJobStatusRules, IJobGaugeRules
{
    internal const byte JobId = 32;
    internal static readonly DarkKnight Instance = new();

    // ---- actions ----
    private const uint BloodWeapon = 3625;
    private const uint DarkMind = 3634;
    private const uint ShadowWall = 3636;
    private const uint LivingDead = 3638;
    private const uint SaltedEarth = 3639;
    private const uint Delirium = 7390;
    private const uint Quietus = 7391;
    private const uint Bloodspiller = 7392;
    private const uint TheBlackestNight = 7393;
    private const uint FloodOfDarkness = 16466;
    private const uint EdgeOfDarkness = 16467;
    private const uint StalwartSoul = 16468;
    private const uint FloodOfShadow = 16469;
    private const uint EdgeOfShadow = 16470;
    private const uint DarkMissionary = 16471;
    private const uint LivingShadow = 16472;
    private const uint Oblation = 25754;
    private const uint SaltAndDarkness = 25755;
    private const uint Shadowbringer = 25757;
    private const uint ShadowedVigil = 36927;
    private const uint BloodspillerFirst = 36928;
    private const uint BloodspillerSecond = 36929;
    private const uint BloodspillerThird = 36930;
    private const uint QuietusEnhanced = 36931;
    private const uint Disesteem = 36932;

    // ---- statuses ----
    private const ushort BloodWeaponStatus = 742;
    private const ushort DeliriumStatus = 1972;
    private const ushort TbnShieldStatus = 1178;
    private const ushort DarksideStatus = 751;
    private const ushort DarkMindStatus = 746;
    private const ushort ShadowWallStatus = 747;
    private const ushort LivingDeadStatus = 810;
    private const ushort SaltedEarthStatus = 749;
    private const ushort DarkMissionaryStatus = 1894;
    private const ushort OblationStatus = 2682;
    private const ushort ShadowedVigilStatus = 3835;
    private const ushort DeliriumComboStatus = 3836;
    private const ushort DisesteemReady = 3837;

    // A TBN status has no shield-capacity field in SimStatus. Keep an armed
    // target window so OnMechanicHit can distinguish an actual hit from a
    // natural seven-second expiry; the Owner simplification treats a real
    // mechanic hit in that window as exhausting the shield.
    private readonly Dictionary<(PartyRole Role, GameObjectId Target), float> tbnArmed = [];
    private readonly List<(PartyRole Role, GameObjectId Target)> tbnExpired = [];

    public bool IsKnownAction(uint actionId) => actionId is
        BloodWeapon or DarkMind or ShadowWall or LivingDead or SaltedEarth or Delirium or
        Quietus or Bloodspiller or TheBlackestNight or FloodOfDarkness or EdgeOfDarkness or
        FloodOfShadow or EdgeOfShadow or DarkMissionary or LivingShadow or Oblation or
        SaltAndDarkness or Shadowbringer or ShadowedVigil or BloodspillerFirst or
        BloodspillerSecond or BloodspillerThird or QuietusEnhanced or Disesteem;

    public IReadOnlyList<ushort> TouchedStatuses(uint actionId, bool comboOk)
        => actionId switch
        {
            BloodWeapon => [BloodWeaponStatus],
            Delirium => [BloodWeaponStatus, DeliriumStatus, DeliriumComboStatus],
            Quietus or Bloodspiller => [DeliriumStatus, DeliriumComboStatus],
            TheBlackestNight => [TbnShieldStatus],
            DarkMind => [DarkMindStatus],
            ShadowWall => [ShadowWallStatus],
            LivingDead => [LivingDeadStatus],
            SaltedEarth => [SaltedEarthStatus],
            FloodOfDarkness or EdgeOfDarkness or FloodOfShadow or EdgeOfShadow
                => [DarksideStatus],
            DarkMissionary => [DarkMissionaryStatus],
            LivingShadow => [DisesteemReady],
            Oblation => [OblationStatus],
            SaltAndDarkness => [SaltedEarthStatus],
            ShadowedVigil => [ShadowedVigilStatus],
            BloodspillerFirst or BloodspillerSecond or BloodspillerThird or QuietusEnhanced
                => [DeliriumStatus, DeliriumComboStatus],
            Disesteem => [DisesteemReady],
            _ => Array.Empty<ushort>(),
        };

    public void Apply(in JobActionContext context)
    {
        switch (context.ActionId)
        {
            case BloodWeapon:
                context.Grant(BloodWeaponStatus, 3, 15f);
                break;
            case Delirium:
                context.Grant(BloodWeaponStatus, 3, 15f);
                context.Grant(DeliriumStatus, 3, 15f);
                if (context.Level >= 96)
                    context.Grant(DeliriumComboStatus, 3, 15f);
                break;
            case Quietus:
            case Bloodspiller:
                context.Consume(DeliriumStatus);
                context.Consume(DeliriumComboStatus);
                break;
            case BloodspillerFirst:
            case BloodspillerSecond:
            case BloodspillerThird:
            case QuietusEnhanced:
                context.Consume(DeliriumStatus);
                context.Consume(DeliriumComboStatus);
                break;
            case TheBlackestNight:
                ArmTbn(in context);
                break;
            case DarkMind:
                context.Grant(DarkMindStatus, 0, 10f);
                break;
            case ShadowWall:
                context.Grant(ShadowWallStatus, 0, 15f);
                break;
            case LivingDead:
                context.Grant(LivingDeadStatus, 0, 10f);
                break;
            case SaltedEarth:
                context.Grant(SaltedEarthStatus, 0, 15f);
                break;
            case FloodOfDarkness:
            case EdgeOfDarkness:
            case FloodOfShadow:
            case EdgeOfShadow:
                context.AddResource(JobResource.DarkArts, -1);
                RefreshDarkside(in context);
                break;
            case DarkMissionary:
                context.GrantParty(DarkMissionaryStatus, 0, 15f, 30f);
                break;
            case LivingShadow:
                if (context.Level >= 100)
                    context.Grant(DisesteemReady, 0, 30f);
                break;
            case Oblation:
                context.Grant(OblationStatus, 0, 10f, context.Target ?? context.Player);
                break;
            case SaltAndDarkness:
                context.Remove(SaltedEarthStatus);
                break;
            case ShadowedVigil:
                context.Grant(ShadowedVigilStatus, 0, 20f);
                break;
            case Disesteem:
                context.Consume(DisesteemReady);
                break;
        }
    }

    private void ArmTbn(in JobActionContext context)
    {
        var target = context.Target ?? context.Player;
        var key = (context.SourceRole, target.GameObjectId);
        context.Grant(TbnShieldStatus, 0, 7f, target);
        tbnArmed[key] = 7f;
    }


    private static void RefreshDarkside(in JobActionContext context)
    {
        var current = context.Find(DarksideStatus);
        var remaining = current?.RemainingTime ?? 0f;
        context.Grant(DarksideStatus, 0, MathF.Min(60f, MathF.Max(0f, remaining) + 30f));
    }

    public void OnPartyAction(in JobActionContext context, SimCharacter actor)
    {
        if (!ReferenceEquals(actor, context.Player)) return;
        if (!IsBloodWeaponHit(context.ActionId)) return;
        if (context.Find(BloodWeaponStatus) is not null)
            context.Consume(BloodWeaponStatus);
    }

    public void OnMechanicHit(in JobActionContext context, SimCharacter target)
    {
        var key = (context.SourceRole, target.GameObjectId);
        if (!tbnArmed.TryGetValue(key, out var remaining) || remaining <= 0f) return;

        // The mechanism callback is the only accepted break signal. Natural
        // expiry is removed by Tick and never reaches this path.
        tbnArmed.Remove(key);
        context.Remove(TbnShieldStatus, target);
        // Dark Arts is a native gauge resource in the current client schema;
        // only the host resource delta is replicated, not the retired 752
        // status row.
        context.AddResource(JobResource.DarkArts, +1);
    }

    public void Tick(in JobActionContext context, float deltaSeconds)
    {
        if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0f) return;
        tbnExpired.Clear();
        foreach (var pair in tbnArmed)
        {
            if (pair.Key.Role != context.SourceRole) continue;
            var left = pair.Value - deltaSeconds;
            if (left <= 0f)
                tbnExpired.Add(pair.Key);
            else
                tbnArmed[pair.Key] = left;
        }
        foreach (var key in tbnExpired)
            tbnArmed.Remove(key);
    }

    // Main calls this at a new practice/run boundary once JobRules exposes its
    // default ResetStatusState hook. It is intentionally separate from native
    // gauge Reset: peer-owned host status state must not leak across runs.
    public void ResetStatusState()
    {
        tbnArmed.Clear();
        tbnExpired.Clear();
    }

    // ---- IJobGaugeRules: local native gauge only ----
    private const int BloodMax = 100;
    private const ushort DarksideMaxMilliseconds = 60_000;
    private const ushort DarksideGrantMilliseconds = 30_000;
    private const ushort ShadowDurationMilliseconds = 20_000;
    private const int BloodPerHit = 10;
    private const int BloodCost = 50;
    private const int BloodWeaponMana = 600;

    private static DarkKnightGauge* Gauge()
    {
        var manager = JobGaugeManager.Instance();
        if (manager == null || manager->ClassJobId != JobId || manager->CurrentGauge == null)
            return null;
        return (DarkKnightGauge*)manager->CurrentGauge;
    }

    private static bool IsBloodWeaponHit(uint actionId)
    {
        var actions = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        return actions.TryGetRow(actionId, out var action) &&
            action.ActionCategory.RowId is 2 or 3;
    }

    private static bool IsBloodSpender(uint actionId)
        => actionId is Quietus or Bloodspiller;
    private static bool IsDeliriumSpender(uint actionId) => actionId is
        Quietus or Bloodspiller or BloodspillerFirst or BloodspillerSecond or
        BloodspillerThird or QuietusEnhanced;

    private static bool IsDarksideAction(uint actionId) => actionId is
        FloodOfDarkness or EdgeOfDarkness or FloodOfShadow or EdgeOfShadow;

    private static void RefreshNativeDarkside(DarkKnightGauge* gauge)
    {
        var current = gauge->DarksideTimer;
        gauge->DarksideTimer = (ushort)Math.Min(
            DarksideMaxMilliseconds,
            current + DarksideGrantMilliseconds);
    }

    public void OnLocalFire(uint actionId, bool comboOk, byte level)
    {
        _ = comboOk;
        _ = level;
        var gauge = Gauge();
        if (gauge == null) return;

        var blood = (int)gauge->Blood;
        if (comboOk && actionId is 3623 or StalwartSoul)
            LocalJobResources.RestoreMana(600);
        if (comboOk && level >= 62 && actionId is 3632 or StalwartSoul)
            blood += 20;
        if (actionId is 3641 or 3643)
            LocalJobResources.RestoreMana(600);
        var hasBloodWeapon = LocalJobResources.HasStatus(BloodWeaponStatus);
        var hasDelirium = LocalJobResources.HasStatus(DeliriumStatus) ||
                          LocalJobResources.HasStatus(DeliriumComboStatus);

        // Status 742 supplies the three-stack weaponskill/magic proc. Status
        // 1972/3836 separately makes Bloodspiller/Quietus free and restores
        // MP on those spenders; it does not turn every later GCD into a new
        // Blood Weapon proc after status 742 has been exhausted.
        var isWeaponskillOrMagic = (hasBloodWeapon || hasDelirium) && IsBloodWeaponHit(actionId);
        if (isWeaponskillOrMagic && hasBloodWeapon)
        {
            LocalJobResources.RestoreMana(BloodWeaponMana);
            if (level >= 66)
                blood += BloodPerHit;
        }
        else if (isWeaponskillOrMagic && hasDelirium &&
                 IsDeliriumSpender(actionId))
        {
            LocalJobResources.RestoreMana(BloodWeaponMana);
        }

        if (IsBloodSpender(actionId) && !hasDelirium)
            blood -= BloodCost;

        if (IsDarksideAction(actionId))
        {
            // Dark Arts is consumed by the host; local feedback writes its absolute value.
            RefreshNativeDarkside(gauge);
        }

        if (actionId == LivingShadow)
            gauge->ShadowTimer = ShadowDurationMilliseconds;

        gauge->Blood = (byte)Math.Clamp(blood, 0, BloodMax);
    }

    public void Reset(byte level)
    {
        _ = level;
        var gauge = Gauge();
        if (gauge == null) return;
        gauge->Blood = 0;
        gauge->DarkArtsState = 0;
        gauge->DarksideTimer = 0;
        gauge->ShadowTimer = 0;
        // DeliriumStep's enum/value semantics are not present in the installed
        // schema, so this implementation does not write it.
    }

    public void Tick(float deltaSeconds, byte level)
    {
        _ = level;
        var gauge = Gauge();
        if (gauge == null || !float.IsFinite(deltaSeconds) || deltaSeconds <= 0f)
            return;
        var milliseconds = (uint)MathF.Round(deltaSeconds * 1000f);
        gauge->DarksideTimer = (ushort)(gauge->DarksideTimer > milliseconds
            ? gauge->DarksideTimer - milliseconds
            : 0);
        gauge->ShadowTimer = (ushort)(gauge->ShadowTimer > milliseconds
            ? gauge->ShadowTimer - milliseconds
            : 0);
    }
}
