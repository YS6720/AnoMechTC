using System;

namespace AnoMech.Core.Game;

// EventScheduler 的插件端記錄（Tests 不連結本檔：DiagnosticLog 依賴 Plugin.Log）。
public sealed partial class EventScheduler
{
    // 上游 1646a70 EventScheduler.Tick 同一行 WRN（上游另帶排程呼叫端 file:line，我方 Add 沒有 Caller 參數，不帶）。
    static partial void ReportSkipped(float time, Exception e)
        => DiagnosticLog.Warn($"[EventScheduler] Scheduled action at t={time:F2} threw and was skipped: {e}");
}
