using System.Collections.Generic;
using AnoMech.Core.EnemyActions;
using AnoMech.Core.SimObjects;

namespace AnoMech.Core.Game;

// 移植自上游 anomek/AnoMech 1646a70 Core/Game/SimEvents.cs（B7 補上 ActionResolved／OnActionResolved
// 與 StatusAdded 的 Action／Caster 欄位）。

// One resolve of an action, at its snapshot. Hits are the party members its area caught at their
// center, nearest Origin first; NpcHits the NPCs it caught anywhere on their hitbox, its caster aside.
// ReleaseDelay runs from the snapshot to the ActionEffect: CastSpec.ActionEffectOffset for a cast,
// 0 for an instant action.
public sealed record ActionResolved(
    EnemyAction Action,
    SimCharacter Caster,
    Placement Origin,
    IReadOnlyList<SimCharacter> Hits,
    IReadOnlyList<SimEnemy> NpcHits,
    IReadOnlyList<SimCharacter> Killed,
    float ReleaseDelay);

// A status gained, stacked or refreshed. PreviousStacks is 0 for a new status and equals Stacks for
// a refresh at its cap. Action and Caster are set when an action's effect applied it.
public sealed record StatusAdded(SimCharacter Holder, ushort StatusId, ushort Stacks, ushort PreviousStacks, EnemyAction? Action, SimCharacter? Caster);

public enum StatusLossReason
{
    Expired,
    Removed,
    Died,
}

public sealed record StatusLost(SimCharacter Holder, ushort StatusId, ushort Stacks, StatusLossReason Reason);

public enum TetherEndReason
{
    Expired,
    // Its BreaksWhen condition held.
    Stretched,
    EndDied,
}

public sealed record TetherEnded(SimTether Tether, TetherEndReason Reason);

// What a scenario reacts to as retail does when it happens: a debuff that detonates as it runs
// out, a tether that snaps. Only for that: the fight's timeline stays in Events, and what an action
// does to its own targets in its EnemyAction effects. A scenario forwards to a shared helper
// (HelloWorldFailures) from its own hooks.
public interface IWorldEventListener
{
    void OnActionResolved(ActionResolved resolved) { }
    void OnStatusAdded(StatusAdded added) { }
    // A death godmode spares loses nothing.
    void OnStatusLost(StatusLost lost) { }
    void OnTetherEnded(TetherEnded ended) { }
}

// Queued where they happen and delivered at the end of SimWorld.Tick, so a hook can spawn, cast and
// kill without mutating a collection mid-iteration.
public sealed class SimEvents
{
    private readonly DeferredDispatch<IWorldEventListener> queue = new();

    // The running scenario, host side; none on a peer, which runs no sim.
    internal IWorldEventListener? Listener
    {
        get => queue.Listener;
        set => queue.Listener = value;
    }

    internal void Raise(ActionResolved e) => queue.Enqueue(l => l.OnActionResolved(e));
    internal void Raise(StatusAdded e) => queue.Enqueue(l => l.OnStatusAdded(e));
    internal void Raise(StatusLost e) => queue.Enqueue(l => l.OnStatusLost(e));
    internal void Raise(TetherEnded e) => queue.Enqueue(l => l.OnTetherEnded(e));

    // Events a hook raises are delivered in the same pass.
    internal void Dispatch()
        => queue.Dispatch(e => DiagnosticLog.Warn($"[SimEvents] A hook threw and was skipped: {e}"));

    internal void Clear() => queue.Clear();
}
