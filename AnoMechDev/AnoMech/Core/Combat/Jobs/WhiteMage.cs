using System;
using System.Collections.Generic;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;

namespace AnoMech.Core.Combat.Jobs;

// 白魔法師：狀態由房主依來源角色維護，量譜只在本機原生 WHM gauge 上寫入。
// Action/Status ids are the installed Taiwan Lumina rows in tmp/eight-job-data.
internal sealed unsafe class WhiteMage : IJobStatusRules, IJobGaugeRules
{
    internal const byte JobId = 24;
    internal static readonly WhiteMage Instance = new();
    private readonly Dictionary<PartyRole, HashSet<SimCharacter>> caressTargets = new();
    private readonly Dictionary<PartyRole, float> lilybellCooldowns = new();

    private const uint PresenceOfMind = 136;
    private const uint ThinAir = 7430;
    private const uint AfflatusSolace = 16531;
    private const uint AfflatusRapture = 16534;
    private const uint AfflatusMisery = 16535;
    private const uint Temperance = 16536;
    private const uint Aquaveil = 25861;
    private const uint Lilybell = 25862;
    private const uint MedicaIII = 37010;
    private const uint GlareIV = 37009;
    private const uint DivineCaress = 37011;
    private const uint LilybellGroundVariant = 28509;

    private const ushort PresenceOfMindStatus = 157;
    private const ushort ThinAirStatus = 1217;
    private const ushort TemperanceHealingStatus = 1872;
    private const ushort TemperanceMitigationStatus = 1873;
    private const ushort AquaveilStatus = 2708;
    private const ushort LilybellStatus = 2709;
    private const ushort GlareIVReadyStatus = 3879;
    private const ushort MedicaIIIRegenStatus = 3880;
    private const ushort DivineCaressReadyStatus = 3881;
    private const ushort DivineCaressShieldStatus = 3903;
    private const ushort DivineCaressRegenStatus = 3904;

    private const int LilyCap = 3;
    private const int BloodLilyCap = 3;
    private const int TimerMilliseconds = 20_000;

    public bool IsKnownAction(uint actionId)
        => actionId is PresenceOfMind or ThinAir or AfflatusSolace or AfflatusRapture or
            AfflatusMisery or Temperance or Aquaveil or Lilybell or LilybellGroundVariant or
            MedicaIII or GlareIV or DivineCaress;

    public IReadOnlyList<ushort> TouchedStatuses(uint actionId, bool comboOk)
        => actionId switch
        {
            PresenceOfMind => [PresenceOfMindStatus, GlareIVReadyStatus],
            ThinAir => [ThinAirStatus],
            AfflatusSolace or AfflatusRapture => Array.Empty<ushort>(),
            AfflatusMisery => Array.Empty<ushort>(),
            Temperance => [TemperanceHealingStatus, TemperanceMitigationStatus, DivineCaressReadyStatus],
            Aquaveil => [AquaveilStatus],
            Lilybell or LilybellGroundVariant => [LilybellStatus],
            MedicaIII => [MedicaIIIRegenStatus],
            GlareIV => [GlareIVReadyStatus],
            DivineCaress => [DivineCaressReadyStatus, DivineCaressShieldStatus, DivineCaressRegenStatus],
            _ => Array.Empty<ushort>(),
        };

    public void Apply(in JobActionContext context)
    {
        switch (context.ActionId)
        {
            case PresenceOfMind:
                context.Grant(PresenceOfMindStatus, 0, 15f);
                if (context.Level >= 92)
                    context.Grant(GlareIVReadyStatus, 3, 30f);
                break;

            case ThinAir:
                context.Grant(ThinAirStatus, 65436, 12f);
                break;

            case GlareIV:
                context.Consume(GlareIVReadyStatus);
                break;

            case Temperance:
                context.Grant(TemperanceHealingStatus, 0, 20f);
                context.GrantParty(TemperanceMitigationStatus, 0, 20f, 50f);
                if (context.Level >= 100)
                    context.Grant(DivineCaressReadyStatus, 0, 30f);
                break;

            case Aquaveil:
                context.Grant(AquaveilStatus, 0, 8f, context.Target ?? context.Player);
                break;

            case Lilybell:
            case LilybellGroundVariant:
                context.Grant(LilybellStatus, 5, 20f);
                break;

            case MedicaIII:
                context.GrantParty(MedicaIIIRegenStatus, 0, 15f, 20f);
                break;
            case DivineCaress:
                context.Remove(DivineCaressReadyStatus);
                if (!caressTargets.TryGetValue(context.SourceRole, out var targets))
                    caressTargets[context.SourceRole] = targets = new HashSet<SimCharacter>();
                foreach (var previous in targets)
                    context.Remove(DivineCaressShieldStatus, previous);
                targets.Clear();
                foreach (var member in context.World.Party.ActiveMembers())
                {
                    if (!member.IsActive || member is ISimPartyMember { Dead: true } ||
                        context.Player.Placement().DistanceSq(member) > 30f * 30f)
                        continue;
                    context.Grant(DivineCaressShieldStatus, 0, 10f, member);
                    targets.Add(member);
                }
                break;
        }
    }

    public void OnMechanicHit(in JobActionContext context, SimCharacter target)
    {
        // Lilybell is a self-attached five-hit marker. No HP engine is involved;
        // only its observable stack transition is represented here.
        if (ReferenceEquals(target, context.Player) &&
            context.Find(LilybellStatus) is not null &&
            (!lilybellCooldowns.TryGetValue(context.SourceRole, out var cooldown) || cooldown <= 0f))
        {
            context.Consume(LilybellStatus);
            lilybellCooldowns[context.SourceRole] = 1f;
        }

        // Divine Caress converts a source-qualified barrier into its normal regen
        // marker when a mechanic actually consumes that barrier. Expiry is handled
        // by Tick; replacement clears the old shield without inventing a hit.
        if (context.Find(DivineCaressShieldStatus, target) is null) return;
        context.Remove(DivineCaressShieldStatus, target);
        context.Grant(DivineCaressRegenStatus, 0, 15f, target);
        if (caressTargets.TryGetValue(context.SourceRole, out var tracked))
            tracked.Remove(target);
    }

    public void ResetStatusState()
    {
        caressTargets.Clear();
        lilybellCooldowns.Clear();
    }

    public void Tick(in JobActionContext context, float deltaSeconds)
    {
        if (float.IsFinite(deltaSeconds) && deltaSeconds > 0f &&
            lilybellCooldowns.TryGetValue(context.SourceRole, out var cooldown))
        {
            cooldown -= deltaSeconds;
            if (cooldown <= 0f)
                lilybellCooldowns.Remove(context.SourceRole);
            else
                lilybellCooldowns[context.SourceRole] = cooldown;
        }

        if (!caressTargets.TryGetValue(context.SourceRole, out var targets) || targets.Count == 0)
            return;
        foreach (var target in new List<SimCharacter>(targets))
        {
            if (!target.IsActive)
            {
                targets.Remove(target);
                continue;
            }
            if (context.Find(DivineCaressShieldStatus, target) is not null)
                continue;
            if (context.Find(DivineCaressRegenStatus, target) is null)
                context.Grant(DivineCaressRegenStatus, 0, 15f, target);
            targets.Remove(target);
        }
    }

    private static WhiteMageGauge* Gauge()
    {
        var manager = JobGaugeManager.Instance();
        if (manager == null || manager->ClassJobId != JobId || manager->CurrentGauge == null) return null;
        return (WhiteMageGauge*)manager->CurrentGauge;
    }

    public void OnLocalFire(uint actionId, bool comboOk, byte level)
    {
        var gauge = Gauge();
        if (gauge == null) return;

        switch (actionId)
        {
            case AfflatusSolace:
            case AfflatusRapture:
                if (gauge->Lily == 0) return;
                gauge->Lily--;
                if (level >= 74 && gauge->BloodLily < BloodLilyCap)
                    gauge->BloodLily++;
                break;

            case AfflatusMisery:
                if (gauge->BloodLily >= BloodLilyCap)
                    gauge->BloodLily = 0;
                break;
        }
    }

    public void Reset(byte level)
    {
        var gauge = Gauge();
        if (gauge == null) return;
        gauge->LilyTimer = 0;
        gauge->Lily = 0;
        gauge->BloodLily = 0;
    }

    public void Tick(float deltaSeconds, byte level)
    {
        var gauge = Gauge();
        if (gauge == null || level < 52 || !float.IsFinite(deltaSeconds) || deltaSeconds <= 0f)
            return;
        if (gauge->Lily >= LilyCap)
            return;

        var timer = Math.Max(0, (int)gauge->LilyTimer + (int)MathF.Round(deltaSeconds * 1000f));
        while (timer >= TimerMilliseconds && gauge->Lily < LilyCap)
        {
            gauge->Lily++;
            timer -= TimerMilliseconds;
        }
        gauge->LilyTimer = gauge->Lily == LilyCap ? (short)0 : (short)timer;
    }

    public bool AllowsNaturalManaRecovery => true;
}
