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
/// 對策：自己開一支 FileStream，每寫一行就交給 OS（<c>Flush()</c>）。行程硬崩時
/// OS 已收下的資料不會丟——當初丟失的是 Serilog **行程內**的 buffer，不是 OS cache。
/// 2026-09-16 前這裡每行都開檔＋<c>Flush(flushToDisk: true)</c>（fsync）：實測尖峰
/// 36 行／100 ms，等於主執行緒每 100 ms 做 36 次 fsync，維護者看到整個畫面停頓。
/// 只有 <see cref="Exception"/> 與 <see cref="Session"/> 這種一次性高風險點才真正落盤。
///
/// 檔案位置：插件 config 目錄下的 anomech-trace.log（追加寫入）。插件載入後第一次寫入時，
/// 若已超過 <see cref="RotateBytes"/> 就改名成 anomech-trace.1.log（覆蓋更舊的那份）再開新檔——
/// 2026-09-23 前只追加不清，08-19 起累積到 44.5 MB。
///
/// dalamud.log 只同步關鍵行（<see cref="MirroredPrefixes"/>、<see cref="Session"/>、
/// <see cref="Exception"/>）；技能循環、狀態、場地、NPC 台詞等只寫本檔。
/// </summary>
internal static class CrashTrace
{
    private static readonly object Gate = new();
    private static string? path;
    private static FileStream? stream;

    private const long RotateBytes = 16L * 1024 * 1024;

    // 載入／開場標記、連線、顯示給玩家的錯誤。以前每一行都在 dalamud.log 再寫一份。
    private static readonly string[] MirroredPrefixes = ["===", "[多人]", "[relay]", "[chat!]"];

    private static FileStream? Stream
    {
        get
        {
            if (stream != null) return stream;
            try
            {
                RotateIfLarge(Path);
                stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 1, FileOptions.None);
            }
            catch
            {
                stream = null;
            }
            return stream;
        }
    }

    private static void RotateIfLarge(string current)
    {
        try
        {
            var info = new FileInfo(current);
            if (!info.Exists || info.Length <= RotateBytes) return;
            File.Move(current, System.IO.Path.ChangeExtension(current, ".1.log"), overwrite: true);
        }
        catch
        {
            // 改名失敗（例如舊檔正被編輯器鎖住）就照舊追加：診斷不能因此停擺。
        }
    }

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

    /// <summary>寫一行並交給 OS；關鍵行（<see cref="MirroredPrefixes"/>）同時進 Dalamud log。</summary>
    public static void Log(string message) => Write(message, toDisk: false, mirror: IsKeyLine(message));

    private static bool IsKeyLine(string message)
    {
        foreach (var prefix in MirroredPrefixes)
            if (message.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    private static void Write(string message, bool toDisk, bool mirror)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        if (mirror)
            try { Plugin.Log.Information($"[trace] {message}"); } catch { /* log 尚未就緒 */ }
        lock (Gate)
        {
            try
            {
                var fs = Stream;
                if (fs == null) return;
                var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: toDisk);
            }
            catch
            {
                // 追蹤檔寫不進去不該讓主流程失敗——它是診斷工具不是功能。
                // 串流壞了就丟掉，下一行重開。
                try { stream?.Dispose(); } catch { }
                stream = null;
            }
        }
    }

    /// <summary>插件卸載時關檔；沒關只是讓檔案句柄多活到行程結束，不影響內容。</summary>
    public static void Close()
    {
        lock (Gate)
        {
            try { stream?.Flush(flushToDisk: true); stream?.Dispose(); } catch { }
            stream = null;
        }
    }

    /// <summary>標一段 Start 的開頭，方便在追加式檔案裡分辨是第幾次嘗試。</summary>
    public static void Session(string what)
    {
        Log(new string('=', 60));
        Write($"=== {what} ===", toDisk: true, mirror: true);
    }

    /// <summary>把例外（含 inner 與堆疊）完整寫進追蹤檔，也完整進 Dalamud log。</summary>
    public static void Exception(string where, System.Exception ex)
    {
        Write($"!!! 例外於 {where}: {ex.GetType().FullName}: {ex.Message}", toDisk: false, mirror: true);
        Write(ex.StackTrace ?? "(無堆疊)", toDisk: false, mirror: true);
        var inner = ex.InnerException;
        var depth = 0;
        while (inner != null && depth++ < 5)
        {
            Write($"  --> inner[{depth}] {inner.GetType().FullName}: {inner.Message}", toDisk: false, mirror: true);
            Write(inner.StackTrace ?? "(無堆疊)", toDisk: false, mirror: true);
            inner = inner.InnerException;
        }
        Write("!!! 例外記錄完畢", toDisk: true, mirror: true);
    }
}
