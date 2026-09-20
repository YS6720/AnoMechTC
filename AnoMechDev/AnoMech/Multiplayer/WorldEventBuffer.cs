using System;
using System.Collections.Generic;

namespace AnoMech.Multiplayer;

// Framework-owned, one run only. A cue may outlive its native actor within a
// stalled frame. Retain managed actor state until that cue crosses the wire,
// then publish the actual live set after the reliable-event barrier.
internal sealed class WorldEventBuffer
{
    private readonly Dictionary<int, EnemyState> eventEnemies = [];
    private readonly List<WorldEvent> events = [];
    private readonly HashSet<int> activeIds = [];
    private WorldSnapshotMessage? retirementSnapshot;
    private MpError? failure;

    public bool HasPendingEvents => events.Count != 0;

    public void ObserveEnemy(EnemyState enemy)
    {
        EnsureHealthy();
        if (!WorldValidation.ValidateEnemy(enemy)) Fail(MpError.InvalidMessage);
        if (!eventEnemies.ContainsKey(enemy.NetId) && eventEnemies.Count >= MpLimits.Enemies)
            Fail(MpError.Capacity);
        eventEnemies[enemy.NetId] = enemy;
    }

    public void Add(WorldEvent item)
    {
        EnsureHealthy();
        if (!WorldValidation.ValidateEvent(item)) Fail(MpError.InvalidMessage);
        if (events.Count >= MpLimits.SendQueue) Fail(MpError.Capacity);
        events.Add(item);
    }

    public WorldSnapshotMessage Capture(WorldSnapshotMessage current)
    {
        EnsureHealthy();
        if (!WorldValidation.ValidateWorld(current)) Fail(MpError.InvalidMessage);
        retirementSnapshot = null;
        if (eventEnemies.Count == 0) return current;
        activeIds.Clear();
        foreach (var enemy in current.Enemies) activeIds.Add(enemy.NetId);
        var retiredCount = 0;
        foreach (var id in eventEnemies.Keys)
            if (!activeIds.Contains(id)) retiredCount++;
        if (retiredCount == 0) return current;
        if (current.Enemies.Length + retiredCount > MpLimits.Enemies) Fail(MpError.Capacity);
        var retained = new EnemyState[current.Enemies.Length + retiredCount];
        current.Enemies.CopyTo(retained, 0);
        var index = current.Enemies.Length;
        foreach (var (id, enemy) in eventEnemies)
            if (!activeIds.Contains(id)) retained[index++] = enemy;
        retirementSnapshot = current;
        return current with { Enemies = retained };
    }

    public IReadOnlyList<WorldEvent> Drain()
    {
        EnsureHealthy();
        var result = events.Count == 0 ? Array.Empty<WorldEvent>() : events.ToArray();
        events.Clear();
        eventEnemies.Clear();
        return result;
    }

    public WorldSnapshotMessage? TakeRetirements()
    {
        EnsureHealthy();
        var result = retirementSnapshot;
        retirementSnapshot = null;
        return result;
    }

    public void Fail(MpError error)
    {
        failure ??= error;
        throw new MpProtocolException(failure.Value);
    }

    public void Clear()
    {
        events.Clear();
        eventEnemies.Clear();
        activeIds.Clear();
        retirementSnapshot = null;
    }

    private void EnsureHealthy()
    {
        if (failure is { } error) throw new MpProtocolException(error);
    }
}
