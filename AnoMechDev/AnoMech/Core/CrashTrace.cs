using System;
using System.IO;
using System.Text;

namespace AnoMech.Core;

/// <summary>
/// 「硬崩也留得下來」的追蹤檔。
///
/// 由來（2026-08-19）：按 Start 後 client 崩潰，dalamud.log 裡連
/// ZoneSession 既有的 Step 1–7 與防火牆自檢輸出都**一行都沒有**——
/// 不是沒執行到，是 Serilog 的 buffer 在 fatal 時整段丟失。
/// 因此再多的 Plugin.Log 埋點都看不到。
///
/// 對策：自己開一支 FileStream，每寫一行就 <c>Flush(flushToDisk: true)</c>
/// ——強制寫穿 OS cache 落到磁碟。代價是慢（每行一次 fsync），
/// 所以**只用在 Start／LoadZone 這種一次性的高風險路徑**，不得放進逐幀迴圈。
///
/// 檔案位置：插件 config 目錄下的 anomech-trace.log（每次 Start 追加，不清空）。
/// </summary>
internal static class CrashTrace
{
    private static readonly object Gate = new();
    private static string? path;

    private static string Path
    {
        get
        {
            if (path != null) return path;
            string dir;
            try
            {
                dir = Plugin.PluginInterface.ConfigDirectory.FullName;
                Directory.CreateDirectory(dir);
            }
            catch
            {
                dir = System.IO.Path.GetTempPath();
            }
            path = System.IO.Path.Combine(dir, "anomech-trace.log");
            return path;
        }
    }

    /// <summary>使用者要去哪裡撈這支檔。</summary>
    public static string FilePath => Path;

    /// <summary>寫一行並**立刻落盤**；同時進 Dalamud log（正常結束時方便一起看）。</summary>
    public static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        try { Plugin.Log.Information($"[trace] {message}"); } catch { /* log 尚未就緒 */ }
        lock (Gate)
        {
            try
            {
                using var fs = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            catch
            {
                // 追蹤檔寫不進去不該讓主流程失敗——它是診斷工具不是功能。
            }
        }
    }

    /// <summary>標一段 Start 的開頭，方便在追加式檔案裡分辨是第幾次嘗試。</summary>
    public static void Session(string what)
    {
        Log(new string('=', 60));
        Log($"=== {what} ===");
    }

    /// <summary>把例外（含 inner 與堆疊）完整寫進追蹤檔。</summary>
    public static void Exception(string where, System.Exception ex)
    {
        Log($"!!! 例外於 {where}: {ex.GetType().FullName}: {ex.Message}");
        Log(ex.StackTrace ?? "(無堆疊)");
        var inner = ex.InnerException;
        var depth = 0;
        while (inner != null && depth++ < 5)
        {
            Log($"  --> inner[{depth}] {inner.GetType().FullName}: {inner.Message}");
            Log(inner.StackTrace ?? "(無堆疊)");
            inner = inner.InnerException;
        }
    }
}
