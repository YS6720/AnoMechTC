using System.Collections.Generic;
using Dalamud.Configuration;
using System;
using AnoMech.Core;
using AnoMech.Core.Game.Party;

namespace AnoMech;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool OpenSimMenuOnInn { get; set; } = true;
    public bool OpenSimMenuOnSupportedInstanceSolo { get; set; } = false;
    public bool EnableEventLogging { get; set; } = false;

    // 模擬區預設使用精簡操作列；完整面板仍可隨時開啟。只新增偏好，不改既有值。
    public bool CompactSimulationControls { get; set; } = true;
    // Scenario auto-retry is opt-in and only follows an explicit successful completion.
    public bool AutoRetry { get; set; } = false;

    // 連戰：清單裡的場景完成後自動接下一個（維護者 2026-09-17：「P5 delta 完自動接 sigma 完自動接 omega」）。
    // 每一項記場景型別與當時選的打法／標點／進度；清單走到底就停，若同時開「自動重試」則從頭再來。
    public bool ChainEnabled { get; set; } = false;
    public List<ScenarioChainEntry> Chain { get; set; } = new();

    // Only the profile path is persisted. The machine-local profile contains
    // public endpoint/process paths, never room tokens or Cloudflare credentials.
    // Local hosting is explicitly configured by its operator; never prefill an account-specific path.
    public string MultiplayerHostingProfile { get; set; } = "";

    // 共用 Relay 的開房授權：只有使用者主動匯入時才寫入，內容是 Windows DPAPI（CurrentUser）密文。
    // 不存明文、不從既有欄位遷移或覆寫；解密失敗一律要求重新匯入，不回落任何明文來源。
    public string MultiplayerHostGrantProtected { get; set; } = "";

    // 只在練習模擬小隊期間搬動 _PartyList 每列的原生 row node；預設關閉。
    // 新欄位只由 property initializer 提供首次設定預設值，reload 不覆寫使用者選擇。
    public bool EnablePartyListDisplayOrder { get; set; } = false;
    public PartyRole[] PartyListDisplayOrder { get; set; } = PartyListDisplayOrderPlanner.CreateDefaultOrder();

    // 維護者 的副本顯示偏好：新設定預設只顯示 TOP（1122）與 UWU（777）。
    // 只在首次建立設定時套用；JSON reload 後保留使用者選擇（包括全隱藏）。
    public ScenarioVisibility ScenarioVisibility { get; set; } = new();

    // 進真副本自動錄製（重建機制所需的座標 / BNpcBase / 頭標 / MapEffect 只有 client 端錄得到）。
    // 預設開：漏錄一次真副本的代價遠大於多產生幾個檔案。
    public bool AutoRecordInDuty { get; set; } = true;

    // 插件訊息不進聊天視窗。預設 true：進真副本錄影時，插件的字會被一起錄進去。
    // 抑制的只是「顯示」——內容照樣寫進 CrashTrace 追蹤檔，診斷能力不受影響。
    public bool SuppressChatOutput { get; set; } = true;
    public bool SuppressBgm { get; set; } = true;

    // Pinned Hyperborea opcode cache: key is exact gameVersion@commit, and
    // ZoneDownOpcodes is enabled only after the complete table is validated and saved.
    public uint[] ZoneDownOpcodes { get; set; } = [];
    public string ZoneFirewallGameVersion { get; set; } = "";

    // Safe mode (incoming packet firewall):
    //   true  — only ZoneDownOpcodes pass; cuts you off from server traffic
    //           (no party join/leave updates, no ready checks, no duty pops).
    //   false — all incoming packets pass to the engine. You'll see popups
    //           and party updates, but it's easier to break the sim zone.
    // The send-side firewall stays on either way: nothing the client does in
    // the sim zone leaks back to the server.
    public bool SafeMode { get; set; } = true;

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}

[Serializable]
public sealed class ScenarioChainEntry
{
    public string ScenarioType { get; set; } = "";
    public string ScenarioName { get; set; } = "";
    public int? SelectedAi { get; set; }
    public int SelectedWaymark { get; set; }
    public string ProgressKey { get; set; } = "full";
}
