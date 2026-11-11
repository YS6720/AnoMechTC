namespace AnoMech.Core;

// 上游 anomek/AnoMech 1646a70 Core/DiagnosticLog.cs 的同名 facade：移植過來的引擎與場景碼
// 原樣呼叫 DiagnosticLog.Info／Warn，實際寫進我方 CrashTrace（追蹤檔）。上游的輪替、
// 「這一輪」記憶體檢視與 Fatal 由 CrashTrace 與既有 Dalamud log 負責，不另搬。
internal static class DiagnosticLog
{
    public static void Info(string message) => CrashTrace.Log(message);

    // 警告同時進 dalamud.log：CrashTrace 只把特定前綴的關鍵行同步過去。
    public static void Warn(string message)
    {
        Plugin.Log.Warning(message);
        CrashTrace.Log($"[warn] {message}");
    }
}
