# AnoMech TC

FFXIV 台服（繁中）版的 **AnoMech** 移植。在旅館裡生出假隊友與假 BOSS，一個人就能練高難副本的機制。

> 非官方專案，與 SQUARE ENIX 無關。使用第三方插件的風險由使用者自行承擔。

## 功能

- **單人練機制**：生成其餘七名假隊友與 BOSS，重現招式時間軸、讀條、場地特效與 debuff，不必湊人也不必進真副本
- **收錄副本**：The Omega Protocol（TOP）各階段，可指定從哪一段開始
- **打法切換**：同一個機制可選不同站位打法，場標預設一鍵套用
- **多人連線**：最多八人同房共練，房主自架 relay，所有人看到同一份模擬
- **失誤即時顯示**：踩中機制會標示原因；可開無敵練習，不會被打斷但仍計失誤
- **重試與切換**：場內直接重開這一輪，或不回旅館直接換另一個場景
- **連勝與自動重試**：成功後可自動重開下一輪並累計連勝
- **實錄時間軸**：招式演出取自實際錄製，不靠猜測數值

## 安裝

1. `/xlsettings` → **體驗性功能** → **自訂插件庫**
2. 貼上來源網址後按 **+** 並儲存：

   ```
   https://raw.githubusercontent.com/YS6720/AnoMechTC/main/repo.json
   ```

3. `/xlplugins` → 搜尋 **AnoMech** → 安裝

更新由 Dalamud 自動提示。

## 使用

- 必須在**旅館、房屋內部或公寓**裡啟動（角色忙碌、過場、戰鬥中無法開始）
- `/anomech` 開啟面板；子指令：`config`／`start`／`reset`／`leave`
- 選好副本、階段與打法後按「開始」
- 場內可按「重試」重開這一輪，或直接選另一個場景切換；「離開場景」回旅館
- 多人房間中，開始／重試／切換場景只有房主能操作

## 建置

需要台服 Dalamud（API 13／net9）與 .NET 9 SDK：

```powershell
dotnet build AnoMechDev/AnoMech -c Release
```

## 授權

**AGPL-3.0-or-later**。上游為 [anomek/AnoMech](https://github.com/anomek/AnoMech)；衍生聲明見 [`NOTICE.md`](AnoMechDev/NOTICE.md)，條文見 [`LICENSE.md`](AnoMechDev/LICENSE.md)。
