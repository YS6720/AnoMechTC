using AnoMech.Core.Native.Interfaces;
using Lumina.Excel;
using LuminaAction = Lumina.Excel.Sheets.Action;
using LuminaStatus = Lumina.Excel.Sheets.Status;

namespace AnoMech.Core;

// Resolves an Action-sheet row id to its display name for user-facing messages.
internal static class ActionLookup
{
    private static readonly ExcelSheet<LuminaAction> Sheet =
        Plugin.DataManager.GetExcelSheet<LuminaAction>();

    // Action name for `actionId`, or the raw id as a fallback when the row is
    // missing or unnamed — death messages must never go blank.
    public static string Name(uint actionId)
    {
        if (Sheet.TryGetRow(actionId, out var row))
        {
            var name = row.Name.ExtractText();
            if (!string.IsNullOrEmpty(name)) return name;
        }
        return actionId.ToString();
    }

    // 上游 GameData.Action（1646a70）：EnemyActions／AoeQuery 讀的欄位；查無此列回 null。
    public static ActionRow? Row(uint actionId)
    {
        if (!Sheet.TryGetRow(actionId, out var row)) return null;
        return new ActionRow(
            actionId,
            row.Name.ExtractText(),
            row.Cast100ms / 10f,
            (CastType)row.CastType,
            row.EffectRange,
            row.XAxisModifier,
            (sbyte)row.AttackType.RowId,
            row.TargetArea);
    }
}

// Status-sheet 名稱（EnemyActions 的死因說明用）；查無名稱時回 id，死因不留白。
internal static class StatusLookup
{
    private static readonly ExcelSheet<LuminaStatus> Sheet =
        Plugin.DataManager.GetExcelSheet<LuminaStatus>();

    public static string Name(ushort statusId)
    {
        if (Sheet.TryGetRow(statusId, out var row))
        {
            var name = row.Name.ExtractText();
            if (!string.IsNullOrEmpty(name)) return name;
        }
        return statusId.ToString();
    }
}
