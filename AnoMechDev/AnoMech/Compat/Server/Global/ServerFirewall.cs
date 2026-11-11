namespace AnoMech.Compat.Server;

/// <summary>國際服伺服器層：封包防火牆的伺服器差異（純常數；台服版見 Compat/Server/Tc）。</summary>
internal static class ServerFirewall
{
    /// <summary>
    /// Hyperborea opcode 表的釘選 commit：d059aa3（2026-11-10）新增
    /// opcodes/2026.11.03.0000.0000.txt（ZoneUp=872／ZoneDown=169,146），對應國際服當前版本。
    /// 國際服改版後要改釘含新版本檔的 commit，否則 allowlist 為空、自檢拒絕進場。
    /// </summary>
    internal const string HyperboreaCommit = "d059aa30fb037939a90b7b7f0bf3a2ea6ab46e3a";

    /// <summary>
    /// 國際服必須有 opcode 表：Hyperborea 有國際服當前版本檔，清單為空只可能是下載失敗、
    /// 版本檔未發布或釘選過舊，一律視為安全項未過、拒絕進場。
    /// </summary>
    internal const bool ZoneDownAllowlistRequired = true;
}
