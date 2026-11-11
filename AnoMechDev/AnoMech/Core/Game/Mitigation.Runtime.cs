using AnoMech.Core.SimObjects;

namespace AnoMech.Core.UserActions;

// 上游 1646a70 Mitigation 中要讀設定與角色狀態的部分（純計算在 Mitigation.cs）。
public readonly partial record struct Mitigation
{
    // Off, no hit checks mitigation. 上游另要求它的 UserActions 職業規則表開著；我們沒有那套系統（不移植），只看設定。
    public static bool Required => Plugin.Config.EnableTankMitigation;

    // A shield is used up by the hit it was counted against. A status carries either a
    // shield or a reduction, never both, so dropping the whole status loses nothing else.
    // 職業技能給的狀態帶 SourceRole（RecordedAbilityRuntime），要以 (Id, SourceRole) 移除計入的那一份。
    public static void SpendShields(SimCharacter target)
    {
        foreach (var status in target.ActiveStatusSnapshot)
            if (ByStatusId.TryGetValue(status.Id, out var m) && m.IsShield)
                target.RemoveStatus(status.Id, status.SourceRole);
    }
}
