using System;
using System.Collections.Generic;

namespace AnoMech.Core.Game;

// Time-based event queue. Owned by Game; scenarios schedule actions via the
// world.Events passthrough (world.Events.Add(offset, ...)) and SimObjects that
// need to schedule receive the instance via constructor injection. Game.Tick
// advances the scheduler (scaled by Game.EventTimeScale) before World.Tick;
// Game.ResetInternal clears it between scenarios.
public sealed partial class EventScheduler
{
    private readonly List<Entry> entries = new();
    private float elapsed;

    public float Time => elapsed;
    // 上游 1646a70 同名；與 Time 同值。
    public float Elapsed => elapsed;
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
            var due = entries[0];
            entries.RemoveAt(0);
            // 上游 1646a70 同：一項出錯只丟掉它自己（記一行 WRN），不讓例外拋出 Tick、把同一幀
            // 後面的排程與 World.Tick 一起擋掉（D801 a）。
            try
            {
                due.Action();
            }
            catch (Exception e)
            {
                ReportSkipped(due.Time, e);
            }
        }
    }

    // 實作在 EventScheduler.Diagnostics.cs（寫 DiagnosticLog.Warn）；Tests 只連結本檔，呼叫由編譯器省略。
    static partial void ReportSkipped(float time, Exception e);

    public void Clear()
    {
        entries.Clear();
        elapsed = 0f;
    }

    private readonly record struct Entry(float Time, Action Action);
}
