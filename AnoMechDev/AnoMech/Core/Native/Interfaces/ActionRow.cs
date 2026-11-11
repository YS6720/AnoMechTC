namespace AnoMech.Core.Native.Interfaces;

// 上游 anomek/AnoMech 1646a70 Core/Native/Interfaces/ActionRow.cs 的子集：只留 EnemyActions 與
// AoeQuery 讀的欄位（由 ActionLookup.Row 從客戶端資料表讀出）。
public sealed record ActionRow(
    uint Id,
    string Name,
    float CastSeconds,
    CastType CastType,
    byte EffectRange,
    byte XAxisModifier,
    sbyte AttackType,
    // Aimed at a ground point: the server's ActionEffect then carries no animation target.
    bool TargetArea);
