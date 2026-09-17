using System;
using System.Collections.Generic;

namespace AnoMech.Core.Game;

// Time-based event queue. Owned by Game; scenarios schedule actions via the
// world.Events passthrough (world.Events.Add(offset, ...)) and SimObjects that
// need to schedule receive the instance via constructor injection. Game.Tick
// advances the scheduler (scaled by Game.EventTimeScale) before World.Tick;
// Game.ResetInternal clears it between scenarios.
public sealed class EventScheduler
{
    private readonly List<Entry> entries = new();
    private float elapsed;

    public float Time => elapsed;
    /// <summary>Scheduled callbacks not yet fired. Zero means the timeline has fully played out.</summary>
    public int Pending => entries.Count;

    public void Add(float offset, Action action)
        => AddAt(elapsed + MathF.Max(0f, offset), action);

    // Keep the original deadline when a callback schedules another due event.
    // Clamping to now would let a later damage event overtake its native release.
    public void AddAt(float time, Action action)
    {
        var index = entries.FindIndex(e => e.Time > time);
        var entry = new Entry(time, action);
        if (index < 0) entries.Add(entry);
        else entries.Insert(index, entry);
    }

    public void Tick(float deltaSeconds)
    {
        elapsed += deltaSeconds;
        while (entries.Count > 0 && entries[0].Time <= elapsed)
        {
            var action = entries[0].Action;
            entries.RemoveAt(0);
            action();
        }
    }

    public void Clear()
    {
        entries.Clear();
        elapsed = 0f;
    }

    private readonly record struct Entry(float Time, Action Action);
}
