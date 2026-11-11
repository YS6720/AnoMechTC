using System;

namespace AnoMech.Core.SimObjects;

// 上游 1646a70 同名列舉；值＝上游生成封包的 DisplayFlags。我方不走封包生成（CharacterManagerHelper），
// 只用它決定生成後隱不隱形、何時顯形（SimEnemy 自己開關 DrawObject，見 SpawnVisibilityRules）。
// Visible          — drawn at spawn.
// HiddenUntilShown — hidden until a warp_end/show timeline reveals it (bosses, clones, arm units).
// HiddenUntilPopIn — hidden until ActorControl.PopIn (summoned adds, pets).
// InvisibleHelper  — the meshless helper that casts a fight's AoEs; never drawn.
public enum SpawnVisibility : uint
{
    Visible = 0x4000B,
    HiddenUntilShown = 0x6000B,
    HiddenUntilPopIn = 0x4002B,
    InvisibleHelper = 0x40008,
}

internal static class SpawnVisibilityRules
{
    // 生成後是否先藏起。只認三個隱形值：未定義值（例如物件初始化器留下的 0）視同 Visible，
    // 讓沒設 Visibility 的既有呼叫端與舊版「IsVisible:false 才藏」完全相同。
    public static bool HiddenAtSpawn(bool isVisible, SpawnVisibility visibility)
        => !isVisible || visibility is SpawnVisibility.HiddenUntilShown
            or SpawnVisibility.HiddenUntilPopIn or SpawnVisibility.InvisibleHelper;

    // HiddenUntilShown 的角色播到出場類時間軸（ActionTimeline Key：warp/warp_end*，或 mon_sp 的
    // show／hide 組）時顯形；客戶端對封包生成的角色會自己做這件事，我方生成的要自己開。
    // 不 latch：每次播到都顯形，場景中途手動藏起後再出場也成立。
    public static bool RevealsOnTimeline(SpawnVisibility visibility, string? key)
    {
        if (visibility != SpawnVisibility.HiddenUntilShown || string.IsNullOrEmpty(key)) return false;
        return key.StartsWith("warp/warp_end", StringComparison.Ordinal)
            || key.Contains("/show/", StringComparison.Ordinal)
            || key.Contains("/hide/", StringComparison.Ordinal);
    }
}
