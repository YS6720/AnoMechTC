using System;
using System.Collections.Generic;
using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.Native.Interfaces;
using AnoMech.Core.SimObjects;
using AnoMech.Scenarios;

namespace AnoMech.Core.EnemyActions;

// One resolve of one cast, shared by its effects in list order. What it decides for each target
// becomes that target's entries in the ActionEffect.
public sealed class EnemyActionContext
{
    // Never removed from, so it enumerates in insertion order.
    private readonly Dictionary<SimCharacter, TargetOutcome> outcomes = [];
    private readonly List<(SimStatus Status, float RevealAt)> hiddenStatuses = [];
    private readonly List<(SimCharacter, ushort)> impactRemovals = [];

    internal EnemyActionContext(EnemyAction action, SimCharacter caster, SimCharacter? target, Placement origin, SimParty party, float releaseDelay,
        Rng knockbackStream)
    {
        this.releaseDelay = releaseDelay;
        this.knockbackStream = knockbackStream;
        Action = action;
        Caster = caster;
        Target = target;
        Origin = origin;
        Party = party;
    }

    // Direction 2（隨機）擊退抽方向用，SimWorld.Stream("knockback")。
    private readonly Rng knockbackStream;

    // From this resolve to the ActionEffect: a cast's CastSpec.ActionEffectOffset, 0 for an instant action.
    private readonly float releaseDelay;

    public EnemyAction Action { get; }
    // An enemy, or a party member using an action through SimParty.Act.
    public SimCharacter Caster { get; }
    public SimCharacter? Target { get; }
    public Placement Origin { get; }
    public SimParty Party { get; }
    // Everyone the area caught, nearest to Origin first.
    public IReadOnlyList<SimCharacter> Hits { get; internal set; } = [];

    // The NPCs the area caught, the caster aside; effects reach them only through OnNpcs.
    public IReadOnlyList<SimEnemy> NpcHits { get; internal set; } = [];

    // Decided now; the character dies to this action after TimingSpec.DamageDelay. The first kill wins.
    public void Kill(SimCharacter who, string? explanation = null)
    {
        var outcome = Outcome(who);
        if (outcome.Killed) return;
        outcome.Killed = true;
        outcome.Explanation = explanation;
    }

    public bool IsKilled(SimCharacter who) => outcomes.TryGetValue(who, out var outcome) && outcome.Killed;

    // The largest hit is the one shown.
    public void ShowDamage(SimCharacter who, uint amount, DamageKind kind)
    {
        var outcome = Outcome(who);
        if (outcome.Damage is not { } shown || amount > shown.Amount) outcome.Damage = (amount, kind);
    }

    // Decided now; moves the character after `delay` (null = TimingSpec.DamageDelay), unless it died
    // meanwhile. knockbackRow goes into the ActionEffect; null for a push with no Knockback sheet
    // row. The first push wins.
    public void Knockback(SimCharacter who, float distance, float speed, float? delay = null, uint? knockbackRow = null)
    {
        var outcome = Outcome(who);
        if (outcome.Push is not null) return;
        var row = knockbackRow is { } id ? KnockbackLookup.Row(id) : null;
        var (from, heading) = row is null ? (Caster.Position, (Vector3?)null) : KnockbackSource(row);
        outcome.Push = new Push(from, heading, distance, speed, delay, knockbackRow);
    }

    // 我方不送 ActionEffect 的擊退條目（0x1F），client 不會依 row 自滑，位移一律走 SimCharacter.Knockback
    // 的「遠離 source」。Direction：0 遠離施放者、1 DirectionArg 角度、2 隨機、3／4／5 施放者的前／右／左、
    // 6 遠離招式目標。0／6 是實體來源；2–5 是方向，在位移當下由目標位置換成等效 source（Push.SourceFor），
    // resolve 到落地之間走位不會把方向扭反。
    private (Vector3 From, Vector3? Heading) KnockbackSource(KnockbackRow row)
    {
        var caster = Caster.Placement();
        switch (row.Direction)
        {
            case 0:
                return (caster.Position, null);
            case 2:
                var angle = knockbackStream.NextSingle() * MathF.Tau;
                return (caster.Position, new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle)));
            case 3:
                return (caster.Position, caster.MoveForward(1f).Position - caster.Position);
            case 4:
                return (caster.Position, caster.MoveRight(1f).Position - caster.Position);
            case 5:
                return (caster.Position, caster.Position - caster.MoveRight(1f).Position);
            case 6 when Target is not null:
                return (Target.Position, null);
            default:
                DiagnosticLog.Info($"[EnemyAction] Knockback row {row.Id} direction {row.Direction} not simulated; pushing away from the caster.");
                return (caster.Position, null);
        }
    }

    // Shown as a dodge, for one who escaped the action, e.g. by facing a gaze correctly.
    public void Dodge(SimCharacter who) => Outcome(who).Dodged = true;

    // Applied now, so effects after this one see it, and shown to the client, from the caster, with
    // the ActionEffect. delayToEffectResult: shown only with the EffectResult at TimingSpec.DamageDelay.
    // Either way the full duration runs from when it shows. entryParam goes in the ActionEffect's gain entry.
    public void AddStatus(SimCharacter who, ushort statusId, float duration = 0f, int stacks = 1, bool delayToEffectResult = false,
        byte entryParam = 0)
    {
        var revealAt = delayToEffectResult ? MathF.Max(Action.Timing.DamageDelay, releaseDelay) : releaseDelay;
        var delay = duration > 0f ? revealAt : 0f;
        // 上游另有 ShownAsFrom(Caster)，不搬：我方 sourceObject 是狀態身分鍵（Statuses.Has／Remove 以它比對），
        // 沒有獨立的顯示來源欄位；寫進去會讓房主與成員的原生槽對不上。
        if (who.AddStatus(statusId, duration + delay, stacks, overrideStacks: false, hidden: revealAt > 0f,
                appliedBy: (Action, Caster)) is not { } status) return;
        Outcome(who).StatusGains.Add((statusId, entryParam));
        if (status.Hidden) hiddenStatuses.Add((status, revealAt));
    }

    // Taken off at TimingSpec.DamageDelay, unless the character died meanwhile.
    public void RemoveStatusAtImpact(SimCharacter who, ushort statusId) => impactRemovals.Add((who, statusId));

    internal IReadOnlyCollection<TargetOutcome> Outcomes => outcomes.Values;
    internal IReadOnlyList<(SimStatus Status, float RevealAt)> HiddenStatuses => hiddenStatuses;
    internal IReadOnlyList<(SimCharacter Who, ushort StatusId)> ImpactRemovals => impactRemovals;

    private TargetOutcome Outcome(SimCharacter who)
    {
        if (outcomes.TryGetValue(who, out var outcome)) return outcome;
        outcome = new TargetOutcome(who);
        outcomes.Add(who, outcome);
        return outcome;
    }

    internal sealed class TargetOutcome(SimCharacter target)
    {
        public SimCharacter Target { get; } = target;
        public (uint Amount, DamageKind Kind)? Damage { get; set; }
        public bool Killed { get; set; }
        public bool Dodged { get; set; }
        public string? Explanation { get; set; }
        public Push? Push { get; set; }
        public List<(ushort StatusId, byte EntryParam)> StatusGains { get; } = [];
    }

    // Heading：定向擊退（Direction 2–5）的 XZ 單位方向；null＝遠離 From。
    internal sealed record Push(Vector3 From, Vector3? Heading, float Distance, float Speed, float? Delay, uint? KnockbackRow)
    {
        // 位移當下的等效 source：定向擊退取目標身後一米，其餘就是 From。
        public Vector3 SourceFor(Vector3 current) => Heading is { } heading ? current - heading : From;
    }
}
