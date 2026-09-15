using System.Numerics;

namespace AnoMech.Core.Map;

// Declares that a scenario wants to load a specific FFXIV territory client-side.
// Origin is used as ScenarioOrigin (Y taken from the live player). PlayerPosition
// is where the player is teleported on entry; Y may need tuning after the first
// in-game test. WeatherId, if set, is applied 1 second after zone load.
// LayerFilterKey 決定「同一個 territory 顯示哪一套場地」——它就是 LoadZone 的第 3 個參數
// （transitionTerritoryFilterKey），對應 LayoutManager.LayerFilterKey。
// 0＝該區域的預設。DSR 的 territory 968 一張圖裡裝了 P1–P7 好幾套，各有不同 key；
// 傳 0 只會拿到 P1（2026-08-19 實測：機制是 P3、場景是 P1 即此）。
public sealed record TargetInstance(
    uint TerritoryId,
    Vector3 Origin,
    Vector3 PlayerPosition,
    byte? WeatherId = null,
    uint LayerFilterKey = 0);
