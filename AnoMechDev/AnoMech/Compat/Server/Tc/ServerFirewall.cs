namespace AnoMech.Compat.Server;

/// <summary>TC 伺服器層：封包防火牆的伺服器差異（純常數；國際版見 Compat/Server/Global）。</summary>
internal static class ServerFirewall
{
    /// <summary>
    /// Hyperborea opcode 表的釘選 commit。Hyperborea 只發布國際服版本檔，台服版本號
    /// 永遠下載不到 ⇒ allowlist 為空（接收全擋＝fail-safe），見 FirewallSelfCheck 檔頭。
    /// </summary>
    internal const string HyperboreaCommit = "2538a9053b7ca04ad33880d8cf50d2b3d67c5cff";

    /// <summary>台服抓不到 opcode 表，空 allowlist 只算功能警告，不擋進場。</summary>
    internal const bool ZoneDownAllowlistRequired = false;
}
