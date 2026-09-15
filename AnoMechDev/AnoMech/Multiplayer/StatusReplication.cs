namespace AnoMech.Multiplayer;

// Peer-side status replication rule. Deliberately free of native/Dalamud types
// so the 0-param boundary below stays regression-testable offline.
//
// The owner's snapshot is the only authority on existence:
//   * a status listed in the snapshot must exist on the peer even when Param is
//     0 — Param carries the raw native ushort, and 0 is a live value (every
//     self-authored TOP P3 marker rides on param 0). Reading a 0 as "no stacks
//     left" is what silently dropped those markers on peers while the host
//     showed them.
//   * a status is removed only when the snapshot stops listing it, never
//     because of its Param.
//   * identity is the (status ID, nullable source role) pair, so another
//     source's copy neither refreshes nor removes this instance.
//   * a listed status that is already tracked is refreshed in place. Re-running
//     the status-init path instead would replay the native gain VFX at snapshot
//     rate (20Hz).
internal interface IStatusReplicationSink
{
    int TrackedCount { get; }
    StatusKey TrackedKeyAt(int index);
    void Remove(StatusKey key);

    /// <returns>true when the status was already present and got refreshed.</returns>
    bool TryRefresh(StatusState state);
    void Create(StatusState state);
}

internal static class StatusReplication
{
    // Generic + ref over the sink: the sim-side sink is a struct wrapper around
    // the character, so a 20Hz snapshot costs no allocation at all.
    internal static void Apply<TSink>(ref TSink sink, StatusState[] snapshot)
        where TSink : IStatusReplicationSink
    {
        // Reverse walk: Remove may drop the entry out of the tracked collection.
        for (int i = sink.TrackedCount - 1; i >= 0; i--)
        {
            var trackedKey = sink.TrackedKeyAt(i);
            if (!Contains(snapshot, trackedKey)) sink.Remove(trackedKey);
        }

        foreach (var status in snapshot)
            if (!sink.TryRefresh(status))
                sink.Create(status);
    }

    private static bool Contains(StatusState[] snapshot, StatusKey key)
    {
        foreach (var status in snapshot)
            if (status.Key == key) return true;
        return false;
    }
}

