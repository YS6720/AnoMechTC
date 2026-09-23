using System;
using System.Collections.Generic;

namespace AnoMech.Multiplayer;

// How a queued frame may be treated when its receiver (or the network) falls behind.
// 2026-09-23 Owner: lag must not kick a member or close the room. Normal traffic never reaches
// the shed point; only the situations that used to end the connection now degrade instead.
public enum DeliveryClass
{
    // Lifecycle, lobby, control, acknowledgements and every world event that leaves lasting
    // state on the receiver: map effect, director, weather, knockback, teleport, face, failure
    // notice, action timelines (they set BaseOverride/loops), timeline reset, persistent actor
    // VFX and their removal. Never dropped, order kept.
    Required,
    // Superseded by the next sample of the same key (poses, roles, world, run status, heartbeat).
    LatestState,
    // Self-expiring visual cue: cast bar, action effect, fire-and-forget actor VFX. Dropped only
    // when shedding; missing one leaves nothing behind once its own duration would have ended.
    Cosmetic,
}

public enum DeliveryResult { Accepted, Coalesced, Overflow }

public static class MpDelivery
{
    public static DeliveryClass Classify(MpMessage message) => message switch
    {
        ILatestState => DeliveryClass.LatestState,
        WorldEventMessage { Event: NativeCastEvent or NativeActionEffectEvent or ActorVfxEvent { Persistent: false } }
            => DeliveryClass.Cosmetic,
        _ => DeliveryClass.Required,
    };
}

/// <summary>
/// Bounded FIFO with latest-state coalescing, shared by the client send/receive queues and the
/// relay's per-receiver queue. Not thread-safe: callers keep their existing locks.
///
/// Below <c>shedAt</c> it behaves exactly like the queues it replaced: a latest-state item
/// replaces the queued sample of the same key and moves to the end, and every other item is a
/// barrier that stops later samples coalescing across it (a world snapshot that introduced an
/// actor must stay ahead of that actor's cues). At <c>shedAt</c> — where those queues refused
/// the item and the caller closed the connection — it sheds instead.
/// </summary>
public sealed class DeliveryQueue<T, TKey> where TKey : struct, IEquatable<TKey>
{
    private readonly record struct Entry(T Item, DeliveryClass Class, TKey? Key);

    private readonly LinkedList<Entry> items = new();
    private readonly Dictionary<TKey, LinkedListNode<Entry>> latest = new();
    private readonly int shedAt;
    private readonly int hardLimit;

    public DeliveryQueue(int shedAt, int hardLimit)
    {
        if (shedAt <= 0 || hardLimit < shedAt) throw new ArgumentOutOfRangeException(nameof(hardLimit));
        this.shedAt = shedAt;
        this.hardLimit = hardLimit;
    }

    public int Count => items.Count;
    /// <summary>Items dropped by shedding since creation (health readout only).</summary>
    public long Shed { get; private set; }

    /// <summary>
    /// Overflow only when Required items alone reach the hard limit; the caller then keeps its
    /// old last-resort behaviour. <paramref name="shed"/> reports items dropped by this call.
    /// </summary>
    public DeliveryResult Enqueue(T item, DeliveryClass deliveryClass, TKey? key, out int shed)
    {
        shed = 0;
        if (deliveryClass == DeliveryClass.LatestState && key is { } existingKey &&
            latest.TryGetValue(existingKey, out var existing))
        {
            existing.Value = new Entry(item, deliveryClass, key);
            items.Remove(existing);
            items.AddLast(existing);
            return DeliveryResult.Coalesced;
        }
        if (deliveryClass != DeliveryClass.LatestState)
            latest.Clear();
        if (items.Count >= shedAt)
            shed = ShedBacklog();
        if (items.Count >= hardLimit)
            return DeliveryResult.Overflow;
        var node = items.AddLast(new Entry(item, deliveryClass, key));
        if (deliveryClass == DeliveryClass.LatestState && key is { } stateKey)
            latest[stateKey] = node;
        return DeliveryResult.Accepted;
    }

    public bool TryDequeue(out T item)
    {
        if (items.First is not { } node)
        {
            item = default!;
            return false;
        }
        items.RemoveFirst();
        if (node.Value.Key is { } key && latest.TryGetValue(key, out var mapped) && ReferenceEquals(mapped, node))
            latest.Remove(key);
        item = node.Value.Item;
        return true;
    }

    public void Clear()
    {
        items.Clear();
        latest.Clear();
    }

    // Drop every cosmetic cue and every sample that a newer sample of the same key supersedes
    // with no Required item in between; keep all Required items in order. So each surviving
    // Required item is still preceded by exactly the latest sample of every key that preceded
    // it originally (the world snapshot that introduced an actor stays ahead of that actor's
    // timeline or VFX event), and a cue arriving later follows its own frame's world capture.
    private int ShedBacklog()
    {
        var dropped = 0;
        HashSet<TKey>? newer = null;
        for (var node = items.Last; node is not null;)
        {
            var previous = node.Previous;
            var entry = node.Value;
            if (entry.Class == DeliveryClass.Required)
            {
                newer?.Clear();
            }
            else if (entry.Class == DeliveryClass.Cosmetic ||
                     entry.Key is { } key && !(newer ??= new()).Add(key))
            {
                items.Remove(node);
                dropped++;
            }
            node = previous;
        }
        latest.Clear();
        Shed += dropped;
        return dropped;
    }
}
