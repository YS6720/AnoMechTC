using System;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;

namespace AnoMech.Core.Combat.Jobs;

// Practice owns MP while the server is isolated. Native prediction may debit MP
// at cast start; this ledger commits only completed actions and writes it back.
internal static unsafe class LocalJobResources
{
    private static int mana;
    private static int originalMana;
    private static float recoveryTimer;
    private static nint owner;
    private static ulong ownerObjectId;
    private static long resourceRevision = -1;
    private static byte addersting;
    private static bool darkArts;
    private static int kenkiGained;
    private static bool retired;
    private static int gaugeSize;
    private static readonly byte[] originalGauge = new byte[MaximumGaugeSize()];
    private static readonly byte[] managedGauge = new byte[MaximumGaugeSize()];
    private static readonly (ushort Id, ushort Param, float Remaining)[] pressStatuses = new (ushort, ushort, float)[60];
    private static readonly (ushort Id, ushort Param, float Remaining)[] actionStatuses = new (ushort, ushort, float)[60];
    private static bool readingActionStatuses;
    internal static byte ClassJob { get; private set; }
    internal static byte Level { get; private set; }
    internal static bool OwnsPlayer
    {
        get
        {
            var local = Plugin.ObjectTable.LocalPlayer;
            if (owner == 0 || retired || local == null) return false;
            if (local.Address == owner && local.GameObjectId == ownerObjectId &&
                Plugin.PlayerState.ClassJob.RowId == ClassJob && Plugin.PlayerState.EffectiveLevel == Level)
                return true;
            retired = true; // Never bind a replacement actor during the same run.
            return false;
        }
    }
    internal static int MaxMana { get; private set; }
    internal static bool Active => owner != 0;
    internal static int Mana
    {
        get => mana;
        set => mana = Math.Clamp(value, 0, MaxMana);
    }

    private static BattleChara* Player()
        => Plugin.ObjectTable.LocalPlayer is { Address: not 0 } player ? (BattleChara*)player.Address : null;

    internal static void Begin(byte classJob, byte level)
    {
        var player = Player();
        var local = Plugin.ObjectTable.LocalPlayer;
        if (player == null || local == null || Active) return;
        owner = (nint)player;
        ownerObjectId = local.GameObjectId;
        ClassJob = classJob;
        Level = level;
        retired = false;
        originalMana = (int)player->Mana;
        MaxMana = (int)player->MaxMana;
        Mana = MaxMana;
        recoveryTimer = 0f;
        resourceRevision = -1;
        addersting = 0;
        darkArts = false;
        kenkiGained = 0;
        var manager = JobGaugeManager.Instance();
        gaugeSize = manager != null && manager->ClassJobId == classJob && manager->CurrentGauge != null
            ? GaugeSize(classJob) : 0;
        if (gaugeSize > 0)
            new ReadOnlySpan<byte>(manager->CurrentGauge, gaugeSize).CopyTo(originalGauge);
        JobRules.ResetLocalGauge(level);
        CaptureGauge();
        Flush();
    }

    internal static void End()
    {
        var player = Player();
        if (player != null && OwnsPlayer)
        {
            player->Mana = (uint)Math.Clamp(originalMana, 0, (int)player->MaxMana);
            var manager = JobGaugeManager.Instance();
            if (gaugeSize > 0 && manager != null && manager->ClassJobId == ClassJob && manager->CurrentGauge != null)
                originalGauge.AsSpan(0, gaugeSize).CopyTo(new Span<byte>(manager->CurrentGauge, gaugeSize));
        }
        owner = 0;
        recoveryTimer = 0f;
        gaugeSize = 0;
        readingActionStatuses = false;
    }

    internal static void RestoreMana(int amount) => Mana += amount;
    internal static void SpendMana(int amount) => Mana -= Math.Max(0, amount);

    internal static void Tick(float seconds, bool allowsNaturalRecovery)
    {
        if (!OwnsPlayer) return;
        recoveryTimer += seconds;
        while (recoveryTimer >= 3f)
        {
            recoveryTimer -= 3f;
            // Combat MP tick: 200; Lucid Dreaming adds 550. Astral Fire blocks both.
            if (allowsNaturalRecovery)
                RestoreMana(200 + (HasStatus(1204) ? 550 : 0));
        }
        Flush();
    }

    internal static void Flush()
    {
        var player = Player();
        if (player == null || !OwnsPlayer) return;
        player->Mana = (uint)Mana;
        var manager = JobGaugeManager.Instance();
        if (gaugeSize > 0 && manager != null && manager->ClassJobId == ClassJob && manager->CurrentGauge != null)
            managedGauge.AsSpan(0, gaugeSize).CopyTo(new Span<byte>(manager->CurrentGauge, gaugeSize));
    }

    internal static void CaptureGauge()
    {
        var manager = JobGaugeManager.Instance();
        if (OwnsPlayer && gaugeSize > 0 && manager != null && manager->ClassJobId == ClassJob && manager->CurrentGauge != null)
            new ReadOnlySpan<byte>(manager->CurrentGauge, gaugeSize).CopyTo(managedGauge);
    }

    internal static void CapturePressStatuses()
    {
        Array.Clear(pressStatuses);
        var player = Player();
        if (player == null || !OwnsPlayer) return;
        var i = 0;
        foreach (ref var status in player->StatusManager.Status)
        {
            if (i == pressStatuses.Length) break;
            pressStatuses[i++] = (status.StatusId, status.Param, status.RemainingTime);
        }
    }

    internal static void AcceptPressStatuses() => pressStatuses.CopyTo(actionStatuses, 0);

    internal static void CommitAction(uint actionId, bool comboOk, int manaCost)
    {
        if (!OwnsPlayer) return;
        Flush();
        readingActionStatuses = true;
        try
        {
            SpendMana(manaCost);
            JobRules.OnLocalFire(ClassJob, actionId, comboOk, Level);
        }
        finally { readingActionStatuses = false; }
        // Shield resources belong to the host, not to local job prediction.
        WriteHostResources();
        CaptureGauge();
        Flush();
    }

    internal static void ApplyResourceFeedback(byte classJob, long revision, byte nextAddersting, bool nextDarkArts,
        int nextKenkiGained)
    {
        if (!OwnsPlayer || classJob != ClassJob || revision <= resourceRevision) return;
        resourceRevision = revision;
        addersting = nextAddersting;
        darkArts = nextDarkArts;
        Flush();
        if (classJob == Samurai.JobId && nextKenkiGained > kenkiGained)
        {
            var manager = JobGaugeManager.Instance();
            if (manager != null && manager->ClassJobId == classJob && manager->CurrentGauge != null)
            {
                var gauge = (SamuraiGauge*)manager->CurrentGauge;
                // Cumulative host earnings survive coalesced snapshots without
                // replaying a hit or overwriting locally spent Kenki.
                gauge->Kenki = (byte)Math.Min(100L, gauge->Kenki + (long)nextKenkiGained - kenkiGained);
            }
            kenkiGained = nextKenkiGained;
        }
        WriteHostResources();
        CaptureGauge();
    }

    private static void WriteHostResources()
    {
        var manager = JobGaugeManager.Instance();
        if (!OwnsPlayer || manager == null || manager->ClassJobId != ClassJob || manager->CurrentGauge == null) return;
        if (ClassJob == Sage.JobId) ((SageGauge*)manager->CurrentGauge)->Addersting = addersting;
        else if (ClassJob == DarkKnight.JobId) ((DarkKnightGauge*)manager->CurrentGauge)->DarkArtsState = (byte)(darkArts ? 1 : 0);
    }

    private static int MaximumGaugeSize()
    {
        var maximum = 0;
        foreach (var job in new byte[] { 34, 39, 31, 25, 24, 40, 32, 19 })
            maximum = Math.Max(maximum, GaugeSize(job));
        return maximum;
    }

    private static int GaugeSize(byte job) => job switch
    {
        Samurai.JobId => sizeof(SamuraiGauge),
        Reaper.JobId => sizeof(ReaperGauge),
        Machinist.JobId => sizeof(MachinistGauge),
        BlackMage.JobId => sizeof(BlackMageGauge),
        WhiteMage.JobId => sizeof(WhiteMageGauge),
        Sage.JobId => sizeof(SageGauge),
        DarkKnight.JobId => sizeof(DarkKnightGauge),
        Paladin.JobId => sizeof(PaladinGauge),
        _ => 0,
    };

    internal static bool HasStatus(ushort statusId)
    {
        if (readingActionStatuses)
        {
            foreach (var status in actionStatuses)
                if (status.Id == statusId) return true;
            return false;
        }
        var player = Player();
        if (player == null) return false;
        foreach (ref var status in player->StatusManager.Status)
            if (status.StatusId == statusId) return true;
        return false;
    }

    internal static int StatusStacks(ushort statusId)
    {
        if (readingActionStatuses)
        {
            foreach (var status in actionStatuses)
                if (status.Id == statusId) return status.Param;
            return 0;
        }
        var player = Player();
        if (player == null) return 0;
        foreach (ref var status in player->StatusManager.Status)
            if (status.StatusId == statusId) return status.Param;
        return 0;
    }

    internal static float StatusRemaining(ushort statusId)
    {
        if (readingActionStatuses)
        {
            foreach (var status in actionStatuses)
                if (status.Id == statusId) return status.Remaining;
            return 0f;
        }
        var player = Player();
        if (player == null) return 0f;
        foreach (ref var status in player->StatusManager.Status)
            if (status.StatusId == statusId) return status.RemainingTime;
        return 0f;
    }

    internal static void ReduceCooldown(uint actionId, float seconds)
    {
        var manager = ActionManager.Instance();
        if (manager == null) return;
        var group = manager->GetRecastGroup((int)ActionType.Action, actionId);
        var cooldown = manager->GetRecastGroupDetail(group);
        if (cooldown != null && cooldown->IsActive)
            cooldown->Elapsed = MathF.Min(cooldown->Total, cooldown->Elapsed + seconds);
    }
}
