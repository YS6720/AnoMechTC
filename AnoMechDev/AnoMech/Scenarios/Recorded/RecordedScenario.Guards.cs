using System;
using System.Collections.Generic;
using System.Linq;

namespace AnoMech.Scenarios.Recorded;

public partial class RecordedScenario
{
    /// <summary>
    /// Prove the complete actor interval set fits the runtime character budget
    /// before scheduling any replay work.  The old schema-1 path intentionally
    /// keeps its historical base-id behavior and is not subject to this guard.
    /// </summary>
    private bool PreflightActorCapacity()
    {
        if (!IsV2) return true;

        var actorsById = new Dictionary<int, RecordedTimeline.Event>();
        foreach (var actor in timeline!.Of("actor"))
        {
            // Converter output guarantees positive instance ids.  Keep the
            // first row for a duplicate id because SpawnActorInstances does the
            // same and never schedules that instance twice.
            if (actor.Id > 0) actorsById.TryAdd(actor.Id, actor);
        }

        var timelineEnd = timeline.Events
            .Select(e => MathF.Max(e.T, e.Until ?? e.T))
            .DefaultIfEmpty(0f)
            .Max();
        var replayEnd = MathF.Max(0f, At(timelineEnd));
        var intervals = new List<(float Start, float End)>(actorsById.Count);
        foreach (var actor in actorsById.Values)
        {
            var start = MathF.Max(0f, At(actor.T));
            var end = actor.Until is { } until
                ? MathF.Max(start, At(until))
                : MathF.Max(start, replayEnd);
            // A zero-length interval still occupies a slot at its spawn event.
            if (end <= start) end = start + 0.001f;
            intervals.Add((start, end));
        }

        var boundaries = new List<(float Time, int Delta)>(intervals.Count * 2);
        foreach (var (start, end) in intervals)
        {
            boundaries.Add((start, +1));
            boundaries.Add((end, -1));
        }
        boundaries.Sort((a, b) =>
        {
            var time = a.Time.CompareTo(b.Time);
            return time != 0 ? time : a.Delta.CompareTo(b.Delta);
        });

        var live = 0;
        var peak = 0;
        foreach (var (_, delta) in boundaries)
        {
            live += delta;
            if (live > peak) peak = live;
        }
        if (peak <= MaxConcurrentActors) return true;

        Core.CrashTrace.Log($"[場景] 演員容量預檢失敗：peak={peak}, limit={MaxConcurrentActors}");
        Core.ChatOutput.Error($"[AnoMech] 場景資料需要同時 {peak} 隻演員，超過上限 "
                            + $"{MaxConcurrentActors}；拒絕啟動以避免重播途中靜默少怪。"
                            + "請用完整生滅資料重新轉換。");
        return false;
    }
}
