# NOTICE — 衍生作品聲明

本專案 **anomech-tc** 是 [AnoMech](https://github.com/anomek/AnoMech) 的衍生作品（fork）。

## 上游

- **專案**：AnoMech — Another FFXIV mechanics simulator
- **作者**：Anomek，及貢獻者 WorstAquaPlayer、Wydox
- **授權**：GNU Affero General Public License v3.0 或更新版本（AGPL-3.0-or-later）
- **來源**：https://github.com/anomek/AnoMech
- **fork 基準版本**：v0.3.12.0（commit `3559dc7`，2026-07-12）

上游本身亦倚賴其他 Dalamud 專案的成果，其致謝一併保留於 `docs/upstream/UPSTREAM-README.md`：
Hyperborea（solo duty arena 載入）、FFXIV-RaidsRewritten（死亡定身與 raid VFX）、bossmod（機制時間與座標）。

### TOP 台服 fork 移植來源

- **來源**：[Knucklesssss/AnoMech-TW](https://github.com/Knucklesssss/AnoMech-TW)，`api13-tw` 固定 commit `09474e88aabead0a6dadda9085fc0ce2873702e2`。
- **採用範圍**：TOP P4 Blue Screen 與 P5 Delta／Sigma／Omega 的指定打法；沿用上游 AGPL-3.0-or-later 授權。
- **打法歸屬**：保留來源的 `tuuufless`／`B站莫古力` 名稱與歸屬；本版 UI 依 Owner 指定顯示 `tuufless`／`莫古力`，不代表本衍生版本原創。
- **本版調整**：台服 API／資料相容、既有 Standard 與 TW 打法保留、Host 權威多人同步；未移植來源的遊戲全隊標記與房屋物件刪除副作用。

## 本衍生版本的修改（AGPL-3.0 §5 要求聲明）

**修改起始日：2026-08-18**

| 日期 | 修改 |
|---|---|
| 2026-08-18 | 自國際服環境（Dalamud SDK 15 / net10 / patch 7.3+）移植至**台服**環境（Dalamud fork API 13 / net9 / patch 7.2）：SDK 降版、Dalamud API 命名空間與事件簽章調整、補 .NET 9 相容層。 |
| 2026-08-18 | 移除 UMAD（Dancing Mad）相關 scenario 出編譯範圍——台服 7.2 client 無該副本資料（實測：TerritoryType 無 territory 1363、其 action id 超出台服 Action 表上限、BNpcBase 多數不存在）。 |
| 2026-08-18 | 新增台服資料層驗證工具（Gate 0 哨兵）。 |

> 後續修改一律同步記入 `CHANGELOG.md`；本表只記**與上游分歧的結構性變更**。

## 授權承繼

本衍生作品依 AGPL-3.0 copyleft 條款，**同樣以 AGPL-3.0-or-later 授權**。
完整授權條文見 `LICENSE.md`。

## 使用風險聲明

本工具會修改遊戲 client 記憶體、hook 封包收送、並向 client 注入偽造的伺服器封包。
這**違反 SQUARE ENIX 使用者條款**，使用者需自行承擔帳號風險。
本專案不提供任何擔保，作者與衍生作者對任何後果不負責任。
