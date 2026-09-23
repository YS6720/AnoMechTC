using System;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;

namespace AnoMech.Core.Combat.Jobs;

// Black Mage's local native gauge: only the player's own BlackMageGauge is written here;
// the host-owned elemental/proc statuses live in BlackMage.cs.
internal sealed unsafe partial class BlackMage : IJobGaugeRules
{
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
