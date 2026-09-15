using System.IO;
using System.Numerics;
using AnoMech.Core.Recording;
using Dalamud.Bindings.ImGui;

namespace AnoMech.Windows;

internal static class RecorderPanel
{
    private static readonly string[] Channels =
    [
        "obj", "party", "snap", "despawn", "cast", "castpoll", "effect", "control",
        "status+", "status~", "status-", "tether", "spawnobj", "mapeffect", "weather", "waymark",
        "vfx", "vfx-end", "svfx", "svfx-transform", "svfx-end", "vfx-gap", "capture-gap", "dirdata", "chat",
    ];
    private static readonly Vector4 Observed = new(0.6f, 0.85f, 0.6f, 1f);
    private static readonly Vector4 Unknown = new(0.7f, 0.7f, 0.7f, 1f);
    private static readonly Vector4 Warning = new(0.95f, 0.75f, 0.35f, 1f);
    private static readonly Vector4 Error = new(1f, 0.4f, 0.4f, 1f);

    public static void Draw(CombatRecorder? recorder)
    {
        if (recorder is null)
        {
            ImGui.TextDisabled("錄製器尚未就緒");
            return;
        }
        ImGui.TextColored(recorder.LastError is not null ? Error : recorder.IsRecording ? Observed : Unknown,
            recorder.IsRecording ? $"錄製中　{recorder.EventCount} 筆" : "未錄製");
        ImGui.SameLine();
        if (ImGui.SmallButton(recorder.IsRecording ? "停止錄製" : "手動開始錄製"))
        {
            if (recorder.IsRecording) recorder.Stop();
            else recorder.Start("手動");
        }
        if (recorder.CurrentPath is { } path)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(Path.GetFileName(path));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(path);
        }
        if (recorder.LastError is { } error)
        {
            ImGui.TextColored(Error, $"錄製錯誤 {recorder.WriteErrorCount} 次：{error}");
            ImGui.TextDisabled("本份資料不可當作完整錄製；原檔保留供診斷。");
        }
        if (!ImGui.CollapsingHeader("錄製通道與本場狀態")) return;
        ImGui.TextDisabled("筆數與已觸發不代表完整；未觀測不等於故障。停止後須由分析器核對。");
        ImGui.TextDisabled("錄製中為 .inprogress；停止並成功封口後才成為 .jsonl。");
        var counts = recorder.Counts;
        for (var i = 0; i < Channels.Length; i++)
        {
            var key = Channels[i];
            counts.TryGetValue(key, out var count);
            ImGui.TextColored((key is "vfx-gap" or "capture-gap") && count > 0 ? Error : count > 0 ? Observed : Unknown,
                $"{key}:{count}");
            if (i % 4 != 3 && i + 1 < Channels.Length) ImGui.SameLine();
        }
        foreach (var (name, state) in recorder.HookStates)
        {
            var label = !state.Live ? "未掛載" : state.Fired ? "本場已觸發" : "本場未觀測";
            ImGui.TextColored(!state.Live ? Error : state.Fired ? Observed : Warning, $"{name}：{label}");
        }
        ImGui.TextDisabled("castpoll 是輪詢備援，不具備原生 cast 的全部欄位。");
    }
}
