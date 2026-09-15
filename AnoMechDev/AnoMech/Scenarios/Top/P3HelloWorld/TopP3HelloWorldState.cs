using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game.Party;

namespace AnoMech.Scenarios.Top.P3HelloWorld;

public sealed class TopP3HelloWorldMemberState
{
    internal TopP3HelloWorldMemberState(PartyRole role) => Role = role;

    public PartyRole Role { get; }
    public bool Alive { get; internal set; } = true;
    public TopP3HelloWorldColor Color { get; internal set; }
    public TopP3HelloWorldColor LastRotationColor { get; internal set; }
    public int ColorRound { get; internal set; }
    public float ColorExpiresAt { get; internal set; }
    public float BlueLatentExpiresAt { get; internal set; }
    public float RedLatentExpiresAt { get; internal set; }
    public bool BlueLatent { get; internal set; }
    public bool RedLatent { get; internal set; }
    public bool ActiveStack { get; internal set; }
    public bool ActiveDefamation { get; internal set; }
    public float ActiveStackExpiresAt { get; internal set; }
    public float ActiveDefamationExpiresAt { get; internal set; }
    public bool NeedStack { get; internal set; }
    public bool NeedDefamation { get; internal set; }
    public float NeedStackExpiresAt { get; internal set; }
    public float NeedDefamationExpiresAt { get; internal set; }
    public int StackVaccine { get; internal set; }
    public int DefamationVaccine { get; internal set; }
    public int BlueVaccine { get; internal set; }
    public int RedVaccine { get; internal set; }
    public Vector3 Position { get; internal set; }
    public Vector3? PreviousPosition { get; internal set; }
    public bool PositionInitialized { get; internal set; }

    public bool HasColorVaccine(TopP3HelloWorldColor color)
        => color == TopP3HelloWorldColor.Blue ? BlueVaccine > 0 : RedVaccine > 0;

    // Color vaccine is persistent immunity.  It is not the one-shot stack or
    // defamation vaccine consumed by the shared AOE mechanic.
    internal bool ConsumeColorVaccine(TopP3HelloWorldColor color)
        => HasColorVaccine(color);
}

/// <summary>
/// Production authority for the four-round Hello World rehearsal. Native
/// statuses are a projection of this state and never grant a predicted role.
/// </summary>
public sealed partial class TopP3HelloWorldState
{
    private readonly Dictionary<PartyRole, TopP3HelloWorldMemberState> members;
    private readonly HashSet<int> towersResolved = [];
    private readonly HashSet<int> aoesResolved = [];
    private readonly HashSet<(int Round, TopP3HelloWorldRole Role)> tethersResolved = [];
    private readonly HashSet<(int Round, PartyRole Receiver, PartyRole Source)> contactsResolved = [];
    private readonly HashSet<(int Round, PartyRole Receiver, PartyRole Source)> blockedContacts = [];

    public TopP3HelloWorldState(
        float contactRadius = TopP3HelloWorldRules.ContactRadiusDefault,
        PartyRole playerRole = PartyRole.MainTank,
        TopP3HelloWorldPattern? pattern = null)
    {
        PlayerRole = playerRole;
        Pattern = pattern ?? new TopP3HelloWorldPattern(playerRole);
        ContactRadius = TopP3HelloWorldRules.ClampContactRadius(contactRadius);
        members = TopP3HelloWorldRules.RecordingOrder
            .ToDictionary(role => role, role => new TopP3HelloWorldMemberState(role));
    }

    public PartyRole PlayerRole { get; }
    public TopP3HelloWorldPattern Pattern { get; }
    public float ContactRadius { get; }
    public IReadOnlyDictionary<PartyRole, TopP3HelloWorldMemberState> Members => members;
    public bool Failed { get; private set; }
    public bool Completed { get; private set; }
    public TopP3HelloWorldFailure Failure { get; private set; }
    public string? FailureMessage { get; private set; }
    internal PartyRole? FailureRole { get; private set; }
    public float LastProcessedAt { get; private set; }
    public IReadOnlyCollection<(int Round, PartyRole Receiver, PartyRole Source)> ResolvedContacts
        => contactsResolved;

    public void Reset()
    {
        towersResolved.Clear();
        aoesResolved.Clear();
        tethersResolved.Clear();
        ResetTethers();
        contactsResolved.Clear();
        blockedContacts.Clear();
        Failed = false;
        Completed = false;
        Failure = TopP3HelloWorldFailure.None;
        FailureMessage = null;
        FailureRole = null;
        LastProcessedAt = 0f;
        foreach (var member in members.Values)
        {
            member.Alive = true;
            member.Color = TopP3HelloWorldColor.None;
            member.LastRotationColor = TopP3HelloWorldColor.None;
            member.ColorRound = 0;
            member.ColorExpiresAt = 0f;
            member.BlueLatentExpiresAt = 0f;
            member.RedLatentExpiresAt = 0f;
            member.BlueLatent = false;
            member.RedLatent = false;
            member.ActiveStack = false;
            member.ActiveDefamation = false;
            member.ActiveStackExpiresAt = 0f;
            member.ActiveDefamationExpiresAt = 0f;
            member.NeedStack = false;
            member.NeedDefamation = false;
            member.NeedStackExpiresAt = 0f;
            member.NeedDefamationExpiresAt = 0f;
            member.StackVaccine = 0;
            member.DefamationVaccine = 0;
            member.BlueVaccine = 0;
            member.RedVaccine = 0;
            member.Position = default;
            member.PreviousPosition = null;
            member.PositionInitialized = false;
        }
    }

    public void SeedInitial(float now)
    {
        if (Failed) return;
        LastProcessedAt = MathF.Max(LastProcessedAt, now);
        foreach (var member in members.Values)
        {
            member.NeedStack = true;
            member.NeedStackExpiresAt = now + TopP3HelloWorldRules.InitialNeedStackDuration;
            member.NeedDefamation = Pattern.RoleGroup(1, TopP3HelloWorldRole.LocalTether).Contains(member.Role);
            member.NeedDefamationExpiresAt = member.NeedDefamation
                ? now + TopP3HelloWorldRules.InitialNeedDefamationDuration : 0f;
        }
    }

    public void ApplyInitialActiveStatuses(float now)
    {
        if (Failed) return;
        LastProcessedAt = MathF.Max(LastProcessedAt, now);
        foreach (var role in Pattern.RoleGroup(1, TopP3HelloWorldRole.Defamation))
        {
            SetColor(role, Pattern.DefamationColor,
                now + TopP3HelloWorldRules.InitialActiveDuration, 1);
            SetActive(role, TopP3HelloWorldRole.Defamation,
                TopP3HelloWorldRules.ActiveExpiryAt(1));
        }
        foreach (var role in Pattern.RoleGroup(1, TopP3HelloWorldRole.Stack))
        {
            SetColor(role, Pattern.StackColor,
                now + TopP3HelloWorldRules.InitialActiveDuration, 1);
            SetActive(role, TopP3HelloWorldRole.Stack,
                TopP3HelloWorldRules.ActiveExpiryAt(1));
        }
    }

    public bool ResolveTowers(
        int round,
        IReadOnlyDictionary<PartyRole, Vector3> snapshot,
        float now = 0f)
    {
        if (Failed) return false;
        if (towersResolved.Contains(round)) return true;
        var staged = new List<(PartyRole Role, TopP3HelloWorldColor Color)>();
        foreach (var tower in Pattern.TowersForRound(round))
        {
            var hits = TopP3HelloWorldRules.RecordingOrder
                .Where(role => members[role].Alive && snapshot.TryGetValue(role, out var position)
                    && TopP3HelloWorldRules.Inside(position, tower.Center,
                        TopP3HelloWorldRules.TowerRadius))
                .ToArray();
            if (hits.Length == 0)
                return Fail(TopP3HelloWorldFailure.EmptyTower, $"R{round} tower {tower.Index} empty");
            if (hits.Length != 1)
                return Fail(TopP3HelloWorldFailure.OverlappingTower,
                    $"R{round} tower {tower.Index} ×{hits.Length}");
            var role = hits[0];
            if (members[role].Color != tower.Color)
                return Fail(TopP3HelloWorldFailure.WrongColor,
                    $"R{round} tower {tower.Index} expected {tower.Color}", role);
            staged.Add((role, tower.Color));
        }
        var effectiveNow = now > 0f ? now : LastProcessedAt;
        foreach (var (role, color) in staged)
        {
            // Latent expiry is a source-status duration from the actual tower
            // effect. A late color contact must never extend this deadline.
            var latentExpiry = effectiveNow + TopP3HelloWorldRules.InitialLatentDuration;
            if (color == TopP3HelloWorldColor.Blue)
            {
                members[role].BlueLatent = true;
                members[role].BlueLatentExpiresAt = latentExpiry;
            }
            else
            {
                members[role].RedLatent = true;
                members[role].RedLatentExpiresAt = latentExpiry;
            }
        }
        towersResolved.Add(round);
        return true;
    }

    public bool ResolveAoes(
        int round,
        IReadOnlyDictionary<PartyRole, Vector3> snapshot,
        float now = 0f)
    {
        if (Failed) return false;
        if (aoesResolved.Contains(round)) return true;
        var defamationOwners = members.Values
            .Where(member => member.Alive && member.ActiveDefamation).ToArray();
        var stackOwners = members.Values
            .Where(member => member.Alive && member.ActiveStack).ToArray();
        if (defamationOwners.Length != 2 || stackOwners.Length != 2)
            return Fail(TopP3HelloWorldFailure.Incomplete,
                $"R{round} active mechanic ownership missing");

        // Snapshot every owner and every hit before changing a vaccine or
        // active flag. This prevents one AOE's commit order from changing the
        // result of another AOE in the same batch.
        var staged = new List<(TopP3HelloWorldRole Kind, PartyRole Owner, PartyRole[] Hits)>();
        foreach (var owner in defamationOwners)
        {
            if (!snapshot.TryGetValue(owner.Role, out var center))
                return Fail(TopP3HelloWorldFailure.WrongDefamation, $"R{round} owner missing",
                    owner.Role);
            var hits = HitsAt(snapshot, center, TopP3HelloWorldRules.DefamationRadius);
            var expectedCount = round == 4 ? 1 : 2;
            if (!hits.Contains(owner.Role) || hits.Length != expectedCount)
                return Fail(TopP3HelloWorldFailure.WrongDefamation,
                    $"R{round} defamation hit count {hits.Length}: {string.Join(',', hits)}",
                    owner.Role);
            staged.Add((TopP3HelloWorldRole.Defamation, owner.Role, hits));
        }
        foreach (var owner in stackOwners)
        {
            if (!snapshot.TryGetValue(owner.Role, out var center))
                return Fail(TopP3HelloWorldFailure.WrongStack, $"R{round} owner missing",
                    owner.Role);
            var hits = HitsAt(snapshot, center, TopP3HelloWorldRules.StackRadius);
            var expectedCount = round == 4 ? 3 : 2;
            if (!hits.Contains(owner.Role) || hits.Length != expectedCount)
                return Fail(TopP3HelloWorldFailure.WrongStack,
                    $"R{round} stack hit count {hits.Length}", owner.Role);
            staged.Add((TopP3HelloWorldRole.Stack, owner.Role, hits));
        }

        var effectiveNow = now > 0f ? now : LastProcessedAt;
        var activeExpiry = TopP3HelloWorldRules.NextActiveExpiryAt(round);
        var defamationOwnersSet = defamationOwners.Select(member => member.Role).ToHashSet();
        var stackOwnersSet = stackOwners.Select(member => member.Role).ToHashSet();
        var defamationHits = CountHits(staged, TopP3HelloWorldRole.Defamation);
        var stackHits = CountHits(staged, TopP3HelloWorldRole.Stack);
        var vaccines = members.Values.ToDictionary(member => member.Role,
            member => (Defamation: member.DefamationVaccine, Stack: member.StackVaccine));

        foreach (var owner in defamationOwners)
        {
            owner.ActiveDefamation = false;
            owner.ActiveDefamationExpiresAt = 0f;
        }
        foreach (var owner in stackOwners)
        {
            owner.ActiveStack = false;
            owner.ActiveStackExpiresAt = 0f;
        }

        foreach (var member in members.Values)
        {
            if (defamationHits.TryGetValue(member.Role, out var defamationCount))
            {
                member.NeedDefamation = false;
                var owner = defamationOwnersSet.Contains(member.Role);
                // Only the owner's own hit is excluded. The expiry vaccine
                // cannot block another source in this same transaction.
                var otherHits = defamationCount - (owner ? 1 : 0);
                var consumed = Math.Min(vaccines[member.Role].Defamation, otherHits);
                member.DefamationVaccine = owner ? 1 : vaccines[member.Role].Defamation - consumed;
                if (otherHits > consumed)
                    SetActive(member.Role, TopP3HelloWorldRole.Defamation, activeExpiry);
            }
            if (stackHits.TryGetValue(member.Role, out var stackCount))
            {
                member.NeedStack = false;
                var owner = stackOwnersSet.Contains(member.Role);
                var otherHits = stackCount - (owner ? 1 : 0);
                var consumed = Math.Min(vaccines[member.Role].Stack, otherHits);
                member.StackVaccine = owner ? 1 : vaccines[member.Role].Stack - consumed;
                if (otherHits > consumed)
                    SetActive(member.Role, TopP3HelloWorldRole.Stack, activeExpiry);
            }
        }
        LastProcessedAt = MathF.Max(LastProcessedAt, effectiveNow);
        aoesResolved.Add(round);
        return true;

        static Dictionary<PartyRole, int> CountHits(
            IReadOnlyList<(TopP3HelloWorldRole Kind, PartyRole Owner, PartyRole[] Hits)> all,
            TopP3HelloWorldRole kind)
            => all.Where(item => item.Kind == kind)
                .SelectMany(item => item.Hits)
                .GroupBy(role => role)
                .ToDictionary(group => group.Key, group => group.Count());
    }

    public bool CheckContactDeadline(int round, TopP3HelloWorldRole tetherRole)
    {
        if (Failed) return false;
        var receivers = Pattern.RoleGroup(round, tetherRole);
        foreach (var contact in Pattern.ContactsForRound(round))
        {
            if (!receivers.Contains(contact.Receiver)
                || contactsResolved.Contains((contact.Round, contact.Receiver, contact.Source)))
                continue;
            return Fail(TopP3HelloWorldFailure.MissingContact,
                $"R{round} {tetherRole} contact deadline", contact.Receiver);
        }
        return true;
    }

    public bool CheckContactDeadline(int round)
        => CheckContactDeadline(round, TopP3HelloWorldRole.RemoteTether)
           && CheckContactDeadline(round, TopP3HelloWorldRole.LocalTether);


    public bool MarkPlayerDead(PartyRole role)
    {
        members[role].Alive = false;
        return Fail(TopP3HelloWorldFailure.PlayerDied, $"{role} died", role);
    }

    public bool FinalizeRun(float now)
    {
        if (Failed || now > TopP3HelloWorldRules.RuntimeAt(TopP3HelloWorldRules.EndAt) + 0.001f)
        {
            Completed = false;
            if (!Failed) Fail(TopP3HelloWorldFailure.Incomplete, "run crossed ending boundary");
            return false;
        }
        var expectedContacts = Pattern.ExpectedContactExpectations.All(contact =>
            contactsResolved.Contains((contact.Round, contact.Receiver, contact.Source)));
        var complete = towersResolved.Count == 4 && aoesResolved.Count == 4
            && tethersResolved.Count == 8 && expectedContacts
            && members.Values.All(member => member.Alive
                && member.Color == TopP3HelloWorldColor.None
                && !member.ActiveStack && !member.ActiveDefamation
                && !member.BlueLatent && !member.RedLatent
                && !member.NeedStack && !member.NeedDefamation
                && member.DefamationVaccine > 0);
        if (!complete)
        {
            Fail(TopP3HelloWorldFailure.Incomplete, "four-round state incomplete at boundary");
            return false;
        }
        Completed = true;
        return true;
    }

    internal void SetColor(
        PartyRole role,
        TopP3HelloWorldColor color,
        float expiresAt,
        int round = 0)
    {
        var member = members[role];
        member.Color = color;
        member.ColorExpiresAt = expiresAt;
        member.ColorRound = round;
    }

    private PartyRole[] HitsAt(
        IReadOnlyDictionary<PartyRole, Vector3> snapshot,
        Vector3 center,
        float radius)
        => TopP3HelloWorldRules.RecordingOrder
            .Where(role => members[role].Alive && snapshot.TryGetValue(role, out var position)
                && TopP3HelloWorldRules.Inside(position, center, radius))
            .ToArray();

    private bool Fail(
        TopP3HelloWorldFailure failure,
        string message,
        PartyRole? role = null)
    {
        if (!Failed)
        {
            Failed = true;
            Failure = failure;
            FailureMessage = message;
            FailureRole = role;
        }
        return false;
    }
}
