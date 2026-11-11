using AnoMech.Multiplayer;

namespace AnoMech.Scenarios;

// PerRole.SeatsActive 的房主半邊：只在本機是房主、房間還開著時，八個座位才是真的
// （成員以 ClaimRole 認領座位，沒人認領的由 AI 補位）。成員端不建場景狀態，單人只有自己一席。
// 與 PerRoleSetting.cs 分檔，讓純邏輯那半邊能以 Compile Include 連進 Tests。
public static partial class PerRole
{
    static partial void ProbeHosting(ref bool hosting)
        => hosting = Plugin.GameInstance?.Multiplayer?.Session is { IsHost: true, Phase: not MultiplayerPhase.Closed };
}
