using System;
using System.Collections.Generic;

namespace AnoMech.Core.Recording;

internal sealed unsafe partial class CombatRecorder
{
    private readonly HashSet<uint> knownActions = new();
    private readonly Queue<uint> pendingActionMetadata = new();

    // Called under recordingGate. Native hooks only enqueue IDs; Lumina access
    // happens on the framework-side snapshot/stop path, never inside a detour.
    private void QueueActionMetadataLocked(uint actionId)
    {
        if (actionId != 0 && knownActions.Add(actionId))
            pendingActionMetadata.Enqueue(actionId);
    }

    private void FlushActionMetadataLocked()
    {
        while (writer?.IsHealthy == true && pendingActionMetadata.TryDequeue(out var actionId))
        {
            try
            {
                var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
                if (!sheet.TryGetRow(actionId, out var action))
                {
                    Write("capture-gap", $"\"channel\":\"actionmeta\",\"action\":{actionId},\"reason\":\"missing-action-row\"");
                    continue;
                }

                var omenPath = action.Omen.ValueNullable is { } omen ? omen.Path.ToString() : "";
                var alternatePath = action.OmenAlt.ValueNullable is { } alternate ? alternate.Path.ToString() : "";
                // Sheet values are evidence, not a claim that every encounter uses
                // this nominal shape or that an empty native action has a name.
                Write("actionmeta", $"\"action\":{actionId},\"name\":\"{Escape(action.Name.ExtractText())}\","
                    + $"\"source\":\"lumina\",\"castType\":{action.CastType},"
                    + $"\"effectRange\":{action.EffectRange},\"xAxisModifier\":{action.XAxisModifier},"
                    + $"\"omen\":{{\"id\":{action.Omen.RowId},\"path\":\"{Escape(omenPath)}\"}},"
                    + $"\"omenAlt\":{{\"id\":{action.OmenAlt.RowId},\"path\":\"{Escape(alternatePath)}\"}}");
            }
            catch (Exception ex)
            {
                Write("capture-gap", $"\"channel\":\"actionmeta\",\"action\":{actionId},\"reason\":\"{Escape(ex.Message)}\"");
            }
        }
    }
}
