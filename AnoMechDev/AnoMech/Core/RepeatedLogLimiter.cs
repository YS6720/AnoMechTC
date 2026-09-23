using System.Collections.Generic;

namespace AnoMech.Core;

/// <summary>
/// Collapses a trace line that repeats in bursts (a key the client keeps rejecting while it is
/// held or spammed). The first occurrence of a key is written; repeats within the window are
/// only counted; the next written line for that key, or <see cref="Drain"/>, carries the count.
/// Nothing is lost, but the file grows by at most one line per key per window.
/// Not thread-safe: the caller's thread owns it.
/// </summary>
internal sealed class RepeatedLogLimiter<TKey>(long windowMilliseconds) where TKey : notnull
{
    private readonly Dictionary<TKey, (long WrittenAt, int Repeats)> keys = new();

    /// <summary>True when a line should be written now. <paramref name="repeats"/> counts the
    /// occurrences since the previous written line for this key that were not written.</summary>
    public bool ShouldWrite(TKey key, long nowMilliseconds, out int repeats)
    {
        if (keys.TryGetValue(key, out var state) && nowMilliseconds - state.WrittenAt < windowMilliseconds)
        {
            keys[key] = (state.WrittenAt, state.Repeats + 1);
            repeats = 0;
            return false;
        }
        repeats = state.Repeats;
        keys[key] = (nowMilliseconds, 0);
        return true;
    }

    /// <summary>Counts not yet written, per key; forgets every key.</summary>
    public List<(TKey Key, int Repeats)> Drain()
    {
        var pending = new List<(TKey Key, int Repeats)>();
        foreach (var (key, state) in keys)
            if (state.Repeats > 0)
                pending.Add((key, state.Repeats));
        keys.Clear();
        return pending;
    }
}
