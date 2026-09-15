using Dalamud.Game.Text.SeStringHandling;

namespace AnoMech.Core;

/// <summary>
/// 插件對聊天視窗的唯一出口。
///
/// 由來（2026-08-19）：使用者要進真副本錄影，插件的訊息（開始錄製／機制提示／
/// 錯誤回報）會出現在聊天視窗被一起錄進去。
///
/// 作法是**關掉顯示、不關掉內容**：訊息一律照樣寫進 CrashTrace 追蹤檔，
/// 所以事後仍查得到「當時插件說了什麼」——直接刪掉 Print 會連診斷能力一起丟掉，
/// 那才是真正的損失。
///
/// 開發者版開關＝Configuration.SuppressChatOutput，預設 true（不顯示）；使用者版一律顯示。
/// </summary>
internal static class ChatOutput
{
    private static bool Suppressed => BuildEdition.IsDeveloper
        && (Plugin.Config?.SuppressChatOutput ?? true);

    public static void Print(string message)
    {
        CrashTrace.Log($"[chat] {message}");
        if (Suppressed) return;
        try { Plugin.ChatGui.Print(message); } catch { /* chat 尚未就緒 */ }
    }

    // Recovery instructions are required while simulation isolation holds the
    // player; ordinary errors continue to respect the build's chat suppression.
    public static void Error(string message, bool required = false)
    {
        CrashTrace.Log($"[chat!] {message}");
        if (Suppressed && !required) return;
        try { Plugin.ChatGui.PrintError(message); } catch { }
    }

    /// <summary>
    /// 訓練回饋（Coach）。**不受靜音開關影響**——靜音是為了錄真副本時畫面乾淨，
    /// 而 Coach 訊息只在模擬區出現，且它就是練習的重點：
    /// 「哪一座塔沒人踩」看不到的話，這個工具等於沒有訓練價值。
    /// </summary>
    public static void Coach(string message)
    {
        CrashTrace.Log($"[coach] {message}");
        try { Plugin.ChatGui.Print(message); } catch { }
    }

    /// <summary>
    /// NPC 台詞（重播錄影裡的 boss 喊話）。走 <see cref="Coach"/> 同一條路——
    /// 它是**練習內容的一部分**（機制預告台詞），不是插件在說話，所以不受靜音開關影響。
    /// 前綴用 boss 名而不是 [AnoMech]：靜音開關存在的理由是「錄真副本時畫面乾淨」，
    /// 而模擬區的台詞本來就該看起來像副本裡的台詞。
    /// </summary>
    public static void Npc(string who, string text)
    {
        var line = string.IsNullOrWhiteSpace(who) ? text : $"{who}：{text}";
        CrashTrace.Log($"[npc] {line}");
        try { Plugin.ChatGui.Print(line); } catch { }
    }

    /// <summary>帶格式的訊息（scenario 開場那種）。抑制時只留純文字進追蹤檔。</summary>
    public static void Print(Dalamud.Game.Text.XivChatEntry entry)
    {
        CrashTrace.Log($"[chat] {entry.Message?.TextValue}");
        if (Suppressed) return;
        try { Plugin.ChatGui.Print(entry); } catch { }
    }
}
