using System;
using System.Collections.Generic;
using System.Numerics;

namespace AnoMech.Core.Game.Ai;

/// <summary>目前這一輪練習路線的來源。Original 是安全預設；Custom 必須由使用者明示選取。</summary>
public enum PracticePositionMode
{
    Original,
    Custom,
}

/// <summary>一個 scenario run 的識別資訊。Key 由 Game 組合完整型別、scenario、解法與錄影檔。</summary>
public readonly record struct PracticeScenarioInfo(string Key, string Name, string StratName);

/// <summary>AiManager 登記的單一步驟。Id 以本場的執行順序產生，不跨場重用。</summary>
public readonly record struct PracticeStepToken(string Id, int Index, float Time, float ArrivalTime);

/// <summary>執行後供編輯器觀察的實際移動步驟。</summary>
public sealed record PracticeObservedMove(
    PracticeStepToken Step,
    Vector2?[] Original,
    Vector2?[] Effective,
    string OriginalFingerprint,
    bool CustomRequested,
    bool CustomApplied,
    bool FingerprintMatched,
    IReadOnlyList<string> AppliedIntervals,
    string Status)
{
    public string Id => Step.Id;
    public float Time => Step.Time;
    public float ArrivalTime => Step.ArrivalTime;
}

/// <summary>
/// 錄製軌跡的時間區間覆寫。ExpectedFingerprints 是 fail-closed guard：
/// 只有本次步驟仍產生同一份原始目標時才套用，避免動態 branch 共用錯解法。
/// EndPosition 為 null 時是固定錨點；有值時在區間內線性移動。
/// </summary>
public sealed record PracticeIntervalOverlay(
    string Id,
    float StartTime,
    float EndTime,
    int Slot,
    Vector2 StartPosition,
    Vector2? EndPosition,
    IReadOnlyDictionary<string, string> ExpectedFingerprints);

/// <summary>單一步驟的角色目標覆寫；null slot 代表保留原始演算法目標。</summary>
public sealed record PracticeStepOverlay(
    string StepId,
    string OriginalFingerprint,
    Vector2?[] Positions);

/// <summary>
/// 純邏輯 seam，讓 runtime 與 Tests.csproj 可共用同一套分支指紋、區間邊界與錨點規則。
/// </summary>
public static class PracticePositionLogic
{
    public const int PartySlotCount = 8;

    /// <summary>把來源目標補齊／裁成 8 個 party slot，並保證回傳可獨立修改的複本。</summary>
    public static Vector2?[] CloneSlots(IReadOnlyList<Vector2?>? source)
    {
        var result = new Vector2?[PartySlotCount];
        if (source is null) return result;
        for (var i = 0; i < result.Length && i < source.Count; i++)
            result[i] = source[i];
        return result;
    }

    /// <summary>
    /// 以穩定的 64-bit FNV-1a 指紋識別一組「已完成角色分工後」的原始目標。
    /// null、slot 順序與 float bit pattern 都納入，且不使用 HashCode 的程序隨機種子。
    /// </summary>
    public static string Fingerprint(IReadOnlyList<Vector2?> positions)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        hash = Mix(hash, (uint)positions.Count, prime);
        for (var i = 0; i < positions.Count; i++)
        {
            hash = Mix(hash, (uint)i, prime);
            if (positions[i] is not { } p)
            {
                hash = Mix(hash, 0u, prime);
                continue;
            }

            hash = Mix(hash, 1u, prime);
            hash = Mix(hash, unchecked((uint)BitConverter.SingleToInt32Bits(p.X)), prime);
            hash = Mix(hash, unchecked((uint)BitConverter.SingleToInt32Bits(p.Y)), prime);
        }
        return hash.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>只在預期指紋相同時套用 step overlay；不符時原樣回傳。</summary>
    public static (Vector2?[] Positions, bool Applied, bool FingerprintMatched) ApplyStep(
        IReadOnlyList<Vector2?> original,
        string originalFingerprint,
        PracticeStepOverlay? overlay)
    {
        var result = CloneSlots(original);
        if (overlay is null)
            return (result, false, true);
        if (!string.Equals(overlay.OriginalFingerprint, originalFingerprint, StringComparison.Ordinal)
            || overlay.Positions.Length != PartySlotCount)
            return (result, false, false);

        for (var i = 0; i < PartySlotCount; i++)
            if (overlay.Positions[i] is { } position)
                result[i] = position;
        return (result, true, true);
    }

    /// <summary>區間邊界是閉區間 [start, end]；兩端同時成立時固定回傳 StartPosition。</summary>
    public static bool IsWithinInclusive(float time, float start, float end)
        => time >= start && time <= end;

    /// <summary>依時間求區間錨點；EndPosition null 代表整段固定在 StartPosition。</summary>
    public static Vector2 ResolveIntervalPosition(PracticeIntervalOverlay overlay, float time)
    {
        if (overlay.EndPosition is not { } end || overlay.EndTime <= overlay.StartTime)
            return overlay.StartPosition;
        var fraction = Math.Clamp(
            (time - overlay.StartTime) / (overlay.EndTime - overlay.StartTime), 0f, 1f);
        return Vector2.Lerp(overlay.StartPosition, end, fraction);
    }

    /// <summary>
    /// 套用一組帶指紋 guard 的區間覆寫。回傳套用過的 interval Id，供 UI 解釋結果。
    /// </summary>
    public static (Vector2?[] Positions, string[] AppliedIntervals) ApplyIntervals(
        IReadOnlyList<Vector2?> source,
        PracticeStepToken step,
        string originalFingerprint,
        IReadOnlyList<PracticeIntervalOverlay> overlays)
    {
        var result = CloneSlots(source);
        var applied = new List<string>();
        foreach (var overlay in overlays)
        {
            if (overlay.Slot is < 0 or >= PartySlotCount
                || !IsWithinInclusive(step.Time, overlay.StartTime, overlay.EndTime)
                || !overlay.ExpectedFingerprints.TryGetValue(step.Id, out var expected)
                || !string.Equals(expected, originalFingerprint, StringComparison.Ordinal))
                continue;

            result[overlay.Slot] = ResolveIntervalPosition(overlay, step.Time);
            applied.Add(overlay.Id);
        }
        return (result, applied.ToArray());
    }

    private static ulong Mix(ulong hash, uint value, ulong prime)
    {
        hash ^= value;
        return hash * prime;
    }
}
