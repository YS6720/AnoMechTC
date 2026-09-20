using System;
using System.Collections.Generic;
using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;

namespace AnoMech.Core.Combat.Jobs;

// Black Mage keeps its elemental/proc transitions in host-owned statuses while
// the native gauge remains local.  No damage or cast-time simulation belongs here.
internal sealed unsafe class BlackMage : IJobStatusRules, IJobGaugeRules
{
    internal const byte JobId = 25;
    internal static readonly BlackMage Instance = new();

    // Actions (Taiwan client action rows, verified in tmp/eight-job-data/BLM.json).
    private const uint Blizzard = 142, Fire = 141, Thunder = 144;
    private const uint FireII = 147, Transpose = 149, FireIII = 152, ThunderIII = 153;
    private const uint BlizzardIII = 154, Manafont = 158, Freeze = 159, Flare = 162;
    private const uint LeyLines = 3573, BlizzardIV = 3576, FireIV = 3577;
    private const uint BetweenTheLines = 7419, ThunderIV = 7420, Triplecast = 7421;
    private const uint Foul = 7422, ThunderII = 7447;
    private const uint Despair = 16505, UmbralSoul = 16506, Xenoglossy = 16507;
    private const uint BlizzardII = 25793, HighFireII = 25794, HighBlizzardII = 25795;
    private const uint Amplifier = 25796, Paradox = 25797;
    private const uint HighThunder = 36986, HighThunderII = 36987;
    private const uint Retrace = 36988, FlareStar = 36989;

    // Status rows from the current client.  Astral/Umbral have separate rows for
    // each stack; Paradox and the ready procs are source-qualified by the ctx.
    private const ushort AstralFire = 173, AstralFireII = 174, AstralFireIII = 175;
    private const ushort UmbralIce = 176, UmbralIceII = 177, UmbralIceIII = 178;
    private const ushort Firestarter = 165, Thundercloud = 3870;
    private const ushort LeyLinesStatus = 737, TriplecastStatus = 1211;
    private const ushort EnochianStatus = 868, PolyglotStatus = 3169;
    private const ushort ParadoxStatus = 3223;
    // Native ActionTimeline 4011's continuous ground-circle component.  The
    // embedded c1m model is authored at the 3-yalm Ley Lines radius, so it is
    // deliberately spawned at unit scale rather than using a generic omen.
    internal const string LeyLinesGroundVfxPath =
        "vfx/action/ab_thm_abl012/eff/abi_thm_abl012c1m.avfx";

    private const float ElementStatusSeconds = 3_600f;
    private const float EnochianSeconds = 30f;
    private const float ProcSeconds = 30f;
    private const float LeyLinesSeconds = 20f;
    private const float LeyLinesRadius = 3f;
    private const short PolyglotMilliseconds = 30_000;
    private const float PolyglotSeconds = 30f;
    // Aspect Mastery III (trait 459, level 35, verified via the local client dump):
    // Fire II / Blizzard II grant the maximum elemental stack, not a single one.
    private const byte AspectMasteryIIILevel = 35;

    private static readonly ushort[] ElementTouched =
    [AstralFire, AstralFireII, AstralFireIII, UmbralIce, UmbralIceII, UmbralIceIII,
     EnochianStatus, ParadoxStatus, TriplecastStatus, Thundercloud];
    private static readonly ushort[] FireTouched =
    [AstralFire, AstralFireII, AstralFireIII, UmbralIce, UmbralIceII, UmbralIceIII,
     EnochianStatus, ParadoxStatus, Firestarter, TriplecastStatus, Thundercloud];
    private static readonly ushort[] ThunderTouched = [Thundercloud, TriplecastStatus];
    private static readonly ushort[] ManafontTouched =
    [AstralFire, AstralFireII, AstralFireIII, UmbralIce, UmbralIceII, UmbralIceIII,
     EnochianStatus, Thundercloud];
    private static readonly ushort[] TriplecastTouched = [TriplecastStatus];
    private static readonly ushort[] LeyLinesTouched = [LeyLinesStatus];
    private static readonly ushort[] ParadoxTouched = [ParadoxStatus, Firestarter, TriplecastStatus];
    private static readonly ushort[] PolyglotTouched = [PolyglotStatus];

    private readonly Dictionary<PartyRole, LeyLinesState> leyLines = new();
    private readonly Dictionary<PartyRole, float> polyglotTimers = new();
    private readonly Dictionary<PartyRole, byte> umbralHearts = new();
    private sealed class LeyLinesState(Placement center)
    {
        internal Placement Center { get; set; } = center;
        internal float Remaining { get; set; } = LeyLinesSeconds;
        internal SimOmen? Visual { get; set; }

        internal void DespawnVisual()
        {
            Visual?.Despawn();
            Visual = null;
        }
    }

    // ---- IJobStatusRules -----------------------------------------------------

    public bool IsKnownAction(uint actionId) => actionId is
        Blizzard or Fire or Thunder or FireII or Transpose or FireIII or ThunderIII or
        BlizzardIII or Manafont or Freeze or Flare or LeyLines or BlizzardIV or FireIV or
        BetweenTheLines or ThunderIV or Triplecast or Foul or ThunderII or
        Despair or UmbralSoul or Xenoglossy or BlizzardII or HighFireII or HighBlizzardII or
        Amplifier or Paradox or HighThunder or HighThunderII or Retrace or FlareStar;

    public IReadOnlyList<ushort> TouchedStatuses(uint actionId, bool comboOk) => actionId switch
    {
        Fire => FireTouched,
        FireII or HighFireII => ElementTouched,
        FireIII => FireTouched,
        FireIV or Flare or Despair or FlareStar => ElementTouched,
        Blizzard or BlizzardII or HighBlizzardII or BlizzardIII or BlizzardIV or Freeze or
            UmbralSoul or Transpose => ElementTouched,
        Thunder or ThunderII or ThunderIII or ThunderIV or HighThunder or HighThunderII => ThunderTouched,
        Foul or Xenoglossy => PolyglotTouched,
        Amplifier => [PolyglotStatus],
        Paradox => ParadoxTouched,
        Manafont => ManafontTouched,
        Triplecast => TriplecastTouched,
        LeyLines or Retrace => LeyLinesTouched,
        _ => Array.Empty<ushort>(),
    };

    public void Apply(in JobActionContext context)
    {
        var actionId = context.ActionId;
        if (!IsKnownAction(actionId)) return;
        var hearts = umbralHearts.GetValueOrDefault(context.SourceRole);
        if (CurrentAstral(context) > 0 && hearts > 0 &&
            actionId is Fire or FireII or HighFireII or FireIII or FireIV &&
            !(actionId == FireIII && context.Find(Firestarter) is not null))
            umbralHearts[context.SourceRole] = (byte)(hearts - 1);
        if (actionId is BlizzardIV or Freeze or Manafont) umbralHearts[context.SourceRole] = 3;
        if (actionId == UmbralSoul) umbralHearts[context.SourceRole] = (byte)Math.Min(3, hearts + 1);
        if (actionId == Flare) umbralHearts[context.SourceRole] = 0;

        switch (actionId)
        {
            case Fire:
                ConsumeTriplecast(context);
                ApplyBasicElement(context, fire: true);
                // Firestarter keeps its current 40% roll; no client row describes it.
                if (Random.Shared.Next(100) < 40) GrantFirestarter(context);
                return;

            case FireII:
                ConsumeTriplecast(context);
                ApplyElement(context, fire: true, forceMax: context.Level >= AspectMasteryIIILevel);
                return;

            case HighFireII:
                ConsumeTriplecast(context);
                ApplyElement(context, fire: true, forceMax: true);
                return;

            case FireIII:
                ConsumeTriplecast(context);
                context.Consume(Firestarter);
                ApplyElement(context, fire: true, forceMax: true);
                return;

            case FireIV:
                ConsumeTriplecast(context);
                return;

            case Flare or Despair:
                ConsumeTriplecast(context);
                ApplyElement(context, fire: true, forceMax: true);
                return;

            case Blizzard:
                ConsumeTriplecast(context);
                ApplyBasicElement(context, fire: false);
                return;

            case BlizzardII:
                ConsumeTriplecast(context);
                ApplyElement(context, fire: false, forceMax: context.Level >= AspectMasteryIIILevel);
                return;

            case HighBlizzardII:
                ConsumeTriplecast(context);
                ApplyElement(context, fire: false, forceMax: true);
                return;

            case BlizzardIII:
                ConsumeTriplecast(context);
                ApplyElement(context, fire: false, forceMax: true);
                return;

            case BlizzardIV or Freeze:
                ConsumeTriplecast(context);
                return;

            case UmbralSoul:
                // Umbral Soul is inherently instant and does not spend Triplecast.
                ApplyElement(context, fire: false, forceMax: false);
                return;

            case Transpose:
                ApplyTranspose(context);
                return;

            case Thunder or ThunderII or ThunderIII or ThunderIV or HighThunder or HighThunderII:
                context.Consume(Thundercloud);
                return;

            case Paradox:
                context.Consume(ParadoxStatus);
                if (CurrentAstral(context) > 0) GrantFirestarter(context);
                return;

            case Manafont:
                ApplyElement(context, fire: true, forceMax: true);
                context.Grant(Thundercloud, 0, ElementStatusSeconds);
                return;
            case Triplecast:
                context.Grant(TriplecastStatus, 3, 15f);
                return;
            case Amplifier:
                GrantPolyglot(context);
                return;
            case Foul or Xenoglossy:
                ConsumeTriplecast(context);
                context.Consume(PolyglotStatus);
                return;


            case LeyLines:
                PlaceLeyLines(context, context.Player.Placement(), LeyLinesSeconds);
                context.Grant(LeyLinesStatus, 0, LeyLinesSeconds);
                return;

            case Retrace:
                RefreshLeyLines(context);
                return;

            case FlareStar:
                ConsumeTriplecast(context);
                return;

            case BetweenTheLines:
                return;
        }
    }

    private static void ConsumeTriplecast(in JobActionContext context)
    {
        var row = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRow(context.ActionId);
        if (row.Cast100ms == 0 || context.ActionId == FireIII && context.Find(Firestarter) is not null ||
            context.ActionId == Foul && context.Level >= 80 ||
            context.ActionId == Despair && context.Level >= 100 ||
            context.Find(167) is not null) return;
        context.Consume(TriplecastStatus);
    }

    private static void GrantFirestarter(in JobActionContext context)
    {
        context.Grant(Firestarter, 0, ElementStatusSeconds);
    }

    private static void GrantPolyglot(in JobActionContext context)
    {
        var current = context.Find(PolyglotStatus)?.Stacks ?? 0;
        var cap = context.Level >= 98 ? 3 : 2;
        if (current < cap)
            context.Grant(PolyglotStatus, current + 1, ElementStatusSeconds);
    }

    private static int CurrentAstral(in JobActionContext context)
    {
        if (context.Find(AstralFireIII) is not null) return 3;
        if (context.Find(AstralFireII) is not null) return 2;
        return context.Find(AstralFire) is not null ? 1 : 0;
    }

    private static int CurrentUmbral(in JobActionContext context)
    {
        if (context.Find(UmbralIceIII) is not null) return 3;
        if (context.Find(UmbralIceII) is not null) return 2;
        return context.Find(UmbralIce) is not null ? 1 : 0;
    }

    private static void RemoveElements(in JobActionContext context)
    {
        context.Remove(AstralFire);
        context.Remove(AstralFireII);
        context.Remove(AstralFireIII);
        context.Remove(UmbralIce);
        context.Remove(UmbralIceII);
        context.Remove(UmbralIceIII);
    }

    // Fire (141) / Blizzard (142) only strip the opposing element ("在處於靈極冰狀態時
    // 只會解除該狀態"); every other elemental spell performs the full switch.
    private void ApplyBasicElement(in JobActionContext context, bool fire)
    {
        if ((fire ? CurrentUmbral(context) : CurrentAstral(context)) > 0)
        {
            DropElement(context);
            return;
        }
        ApplyElement(context, fire, forceMax: false);
    }

    // Losing the element also ends Enochian and dims the Paradox crystal (trait 465).
    private static void DropElement(in JobActionContext context)
    {
        RemoveElements(context);
        context.Remove(EnochianStatus);
        context.Remove(ParadoxStatus);
    }

    private void ApplyElement(in JobActionContext context, bool fire, bool forceMax)
    {
        var previousFire = CurrentAstral(context);
        var previousIce = CurrentUmbral(context);
        var previousFull = previousFire == 3 || previousIce == 3 &&
            umbralHearts.GetValueOrDefault(context.SourceRole) == 3;
        var stacks = forceMax ? 3 : fire
            ? Math.Min(previousFire + 1, 3)
            : Math.Min(previousIce + 1, 3);

        RemoveElements(context);
        var statusId = fire
            ? stacks switch
            {
                3 => AstralFireIII,
                2 => AstralFireII,
                _ => AstralFire,
            }
            : stacks switch
            {
                3 => UmbralIceIII,
                2 => UmbralIceII,
                _ => UmbralIce,
            };
        context.Grant(statusId, 0, ElementStatusSeconds);
        context.Grant(EnochianStatus, 0, ElementStatusSeconds);
        if (fire ? previousFire == 0 : previousIce == 0)
            context.Grant(Thundercloud, 0, ElementStatusSeconds);

        if (context.Level >= 90 && previousFull && ((previousFire > 0) != fire))
            context.Grant(ParadoxStatus, 0, ElementStatusSeconds);
    }

    private void ApplyTranspose(in JobActionContext context)
    {
        var fire = CurrentAstral(context);
        var ice = CurrentUmbral(context);
        // Transpose switches to the first stack of the opposite element and requires
        // an element to begin with (action 149); it is never a plain removal.
        if (fire == 0 && ice == 0) return;
        ApplyElement(context, fire: ice > 0, forceMax: false);
    }

    private void RefreshLeyLines(in JobActionContext context)
    {
        // Keep the existing status requirement; moving the native circle does
        // not renew the original deployment's lifetime.
        if (context.Find(LeyLinesStatus) is null ||
            !leyLines.TryGetValue(context.SourceRole, out var state)) return;
        var remaining = state.Remaining;
        if (remaining <= 0f) return;

        PlaceLeyLines(context, context.Player.Placement(), remaining);
        context.Grant(LeyLinesStatus, 0, remaining);
    }

    private void PlaceLeyLines(in JobActionContext context, Placement center, float remaining)
    {
        // Spawn first so a failed native/network resource resolution cannot erase
        // an already-visible deployment. The exact c1m model is authored as the
        // 3-yalm floor circle and is therefore kept at unit scale.
        var visual = context.World.SpawnOmen(
            LeyLinesGroundVfxPath, center, Vector3.One, remaining);
        if (leyLines.TryGetValue(context.SourceRole, out var previous))
            previous.DespawnVisual();

        leyLines[context.SourceRole] = new(center)
        {
            Remaining = remaining,
            Visual = visual,
        };
    }

    public void Tick(in JobActionContext context, float deltaSeconds)
    {
        if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0f) return;
        TickPolyglot(context, deltaSeconds);

        if (!leyLines.TryGetValue(context.SourceRole, out var state)) return;
        state.Remaining -= deltaSeconds;
        if (state.Remaining <= 0f)
        {
            context.Remove(LeyLinesStatus);
            state.DespawnVisual();
            leyLines.Remove(context.SourceRole);
            return;
        }

        // Stepping outside only suspends the buff: the circle keeps its center and its
        // remaining duration, so walking back in restores it until the duration ends.
        var inside = context.Player.Placement().DistanceSq(state.Center.Position) <=
            LeyLinesRadius * LeyLinesRadius;
        var active = context.Find(LeyLinesStatus) is not null;
        if (inside && !active) context.Grant(LeyLinesStatus, 0, state.Remaining);
        else if (!inside && active) context.Remove(LeyLinesStatus);
    }

    private void TickPolyglot(in JobActionContext context, float deltaSeconds)
    {
        if (context.Level < 80 || CurrentAstral(context) == 0 && CurrentUmbral(context) == 0 ||
            context.Find(EnochianStatus) is null)
        {
            polyglotTimers.Remove(context.SourceRole);
            return;
        }

        var timer = polyglotTimers.GetValueOrDefault(context.SourceRole) + deltaSeconds;
        var cap = context.Level >= 98 ? 3 : 2;
        var stacks = context.Find(PolyglotStatus)?.Stacks ?? 0;
        // The 30s Enochian cadence keeps running while capped; only the stack it would
        // have granted is wasted, so spending one never refunds a full timer.
        while (timer >= PolyglotSeconds)
        {
            timer -= PolyglotSeconds;
            if (stacks >= cap) continue;
            stacks++;
            context.Grant(PolyglotStatus, stacks, ElementStatusSeconds);
        }
        polyglotTimers[context.SourceRole] = timer;
    }

    // Main's default interface hook calls this on a new practice generation.
    public void ResetStatusState()
    {
        foreach (var state in leyLines.Values)
            state.DespawnVisual();
        leyLines.Clear();
        polyglotTimers.Clear();
        umbralHearts.Clear();
    }

    public void ForgetRole(PartyRole role)
    {
        if (leyLines.Remove(role, out var state))
            state.DespawnVisual();
        polyglotTimers.Remove(role);
        umbralHearts.Remove(role);
    }

    // ---- local native gauge -------------------------------------------------

    private static BlackMageGauge* Gauge()
    {
        var manager = JobGaugeManager.Instance();
        if (manager == null || manager->ClassJobId != JobId || manager->CurrentGauge == null)
            return null;
        return (BlackMageGauge*)manager->CurrentGauge;
    }

    public void OnLocalFire(uint actionId, bool comboOk, byte level)
    {
        var gauge = Gauge();
        if (gauge == null) return;
        // Ice recovery uses the stance before the spell, not its resulting stance.
        if (actionId is Blizzard or BlizzardII or HighBlizzardII or BlizzardIII or BlizzardIV or Freeze)
            RestoreUmbralMana(gauge);
        if (gauge->ElementStance > 0 && gauge->UmbralHearts > 0 &&
            actionId is Fire or FireII or HighFireII or FireIII or FireIV &&
            !(actionId == FireIII && LocalJobResources.HasStatus(Firestarter)))
            gauge->UmbralHearts--;

        switch (actionId)
        {
            case Fire:
                SetBasicElement(gauge, fire: true, level);
                break;

            case FireII:
                SetElement(gauge, fire: true, forceMax: level >= AspectMasteryIIILevel, level);
                break;

            case HighFireII:
                SetElement(gauge, fire: true, forceMax: true, level);
                break;

            case FireIII:
            case Despair:
                SetElement(gauge, fire: true, forceMax: true, level);
                if (actionId == Despair)
                    LocalJobResources.Mana = 0;
                break;
            case FireIV:
                if (level >= 100) AddAstralSoul(gauge, 1);
                break;

            case Flare:
                SetElement(gauge, fire: true, forceMax: true, level);
                // GetActionCost already accounted for the 2/3 cost with Umbral Hearts.
                gauge->UmbralHearts = 0;
                if (level >= 100) AddAstralSoul(gauge, 3);
                break;

            case Blizzard:
                SetBasicElement(gauge, fire: false, level);
                break;

            case BlizzardII:
                SetElement(gauge, fire: false, forceMax: level >= AspectMasteryIIILevel, level);
                break;

            case HighBlizzardII:
                SetElement(gauge, fire: false, forceMax: true, level);
                break;

            case BlizzardIII:
                SetElement(gauge, fire: false, forceMax: true, level);
                break;

            case BlizzardIV:
            case Freeze:
                gauge->UmbralHearts = 3;
                break;

            case UmbralSoul:
                RestoreUmbralMana(gauge);
                SetElement(gauge, fire: false, forceMax: false, level);
                if (level >= 58) gauge->UmbralHearts = (byte)Math.Min(3, gauge->UmbralHearts + 1);
                break;

            case Transpose:
                // Full switch to the opposite element; never a removal, and it needs
                // an element to switch from (action 149).
                if (gauge->ElementStance == 0) break;
                SetElement(gauge, fire: gauge->ElementStance < 0, forceMax: false, level);
                break;

            case FlareStar:
                gauge->EnochianFlags = (EnochianFlags)((byte)gauge->EnochianFlags & ~0x1c);
                break;

            case Paradox:
                gauge->EnochianFlags = (EnochianFlags)((byte)gauge->EnochianFlags & ~2);
                break;

            case Amplifier:
                gauge->PolyglotStacks = (byte)Math.Clamp(
                    gauge->PolyglotStacks + 1, 0, level >= 98 ? 3 : 2);
                break;

            case Foul:
            case Xenoglossy:
                // The Enochian cycle is never stalled by a full gauge, so spending a
                // stack must not restart it with a fresh 30s.
                gauge->PolyglotStacks = (byte)Math.Max(0, gauge->PolyglotStacks - 1);
                break;

            case Manafont:
                LocalJobResources.Mana = LocalJobResources.MaxMana;
                SetElement(gauge, fire: true, forceMax: true, level);
                if (level >= 58) gauge->UmbralHearts = 3;
                break;
        }
    }

    private static void SetElement(BlackMageGauge* gauge, bool fire, bool forceMax, byte level)
    {
        var previous = gauge->ElementStance;
        var previousFire = previous > 0;
        var stacks = forceMax ? 3 : fire
            ? previous > 0 ? Math.Min(previous + 1, 3) : 1
            : previous < 0 ? Math.Min(-previous + 1, 3) : 1;

        var flags = (byte)gauge->EnochianFlags;
        if (level >= 90 && (previous == 3 || previous == -3 && gauge->UmbralHearts == 3) && previousFire != fire)
            flags |= 2;
        flags |= 1;
        gauge->EnochianFlags = (EnochianFlags)flags;
        gauge->ElementStance = (sbyte)(fire ? stacks : -stacks);
        if (!fire) gauge->EnochianFlags = (EnochianFlags)((byte)gauge->EnochianFlags & ~0x1c);
        if (level >= 80 && gauge->EnochianTimer <= 0)
            gauge->EnochianTimer = PolyglotMilliseconds;
    }

    // Fire (141) / Blizzard (142) cast under the opposite element only strip it.
    private static void SetBasicElement(BlackMageGauge* gauge, bool fire, byte level)
    {
        if (fire ? gauge->ElementStance < 0 : gauge->ElementStance > 0)
        {
            // Enochian, the Paradox crystal and Astral Soul all die with the element.
            gauge->ElementStance = 0;
            gauge->EnochianTimer = 0;
            gauge->EnochianFlags = EnochianFlags.None;
            return;
        }
        SetElement(gauge, fire, forceMax: false, level);
    }

    private static void RestoreUmbralMana(BlackMageGauge* gauge)
    {
        switch (Math.Clamp(-gauge->ElementStance, 0, 3))
        {
            case 1: LocalJobResources.RestoreMana(2_500); break;
            case 2: LocalJobResources.RestoreMana(5_000); break;
            case 3: LocalJobResources.RestoreMana(10_000); break;
        }
    }

    private static void AddAstralSoul(BlackMageGauge* gauge, int amount)
    {
        var flags = (byte)gauge->EnochianFlags;
        var souls = (flags & 0x1c) >> 2;
        souls = Math.Clamp(souls + amount, 0, 6);
        flags = (byte)((flags & ~0x1c) | (souls << 2));
        gauge->EnochianFlags = (EnochianFlags)flags;
    }
    public void Tick(float deltaSeconds, byte level)
    {
        var gauge = Gauge();
        if (gauge == null || !float.IsFinite(deltaSeconds) || deltaSeconds <= 0f) return;

        var milliseconds = Math.Max(0, (int)MathF.Round(deltaSeconds * 1000f));
        if (gauge->EnochianTimer <= 0 || level < 80 || gauge->ElementStance == 0)
        {
            gauge->EnochianTimer = (short)Math.Max(0, gauge->EnochianTimer - milliseconds);
            return;
        }

        // The 30s cycle keeps running while Polyglot is capped: the stack it would
        // have granted is wasted, and the leftover time carries into the next cycle.
        var cap = level >= 98 ? 3 : 2;
        var remaining = gauge->EnochianTimer - milliseconds;
        while (remaining <= 0)
        {
            if (gauge->PolyglotStacks < cap) gauge->PolyglotStacks++;
            remaining += PolyglotMilliseconds;
        }
        gauge->EnochianTimer = (short)remaining;
    }

    public void Reset(byte level)
    {
        var gauge = Gauge();
        if (gauge == null) return;
        gauge->EnochianTimer = 0;
        gauge->ElementStance = 0;
        gauge->UmbralHearts = 0;
        gauge->PolyglotStacks = 0;
        gauge->EnochianFlags = EnochianFlags.None;
    }

    public bool AllowsNaturalManaRecovery
    {
        get
        {
            var gauge = Gauge();
            return gauge == null || gauge->ElementStance <= 0;
        }
    }
}
