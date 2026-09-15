using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace AnoMech.Core.Game.Ai;

/// <summary>
/// One scenario-scoped registry for AiManager movement steps and user practice overlays.
/// It is deliberately independent of scenario implementations: every AiManager.Move call
/// registers itself, so handwritten, replay, TOP and UWU routes all use the same seam.
/// </summary>
public sealed class PracticePositions
{
    private const int StorageSchema = 1;

    private readonly IPracticePositionStore store;
    private readonly Dictionary<string, ScenarioDocument> scenarios = new(StringComparer.Ordinal);
    private readonly List<PracticeObservedMove> observed = new();
    private readonly Dictionary<string, int> observedIndex = new(StringComparer.Ordinal);

    private bool loaded;
    private ScenarioDocument? active;
    private string? activeKey;
    private string? activeName;
    private string? activeStratName;
    private int nextStepIndex;

    public PracticePositions(IPracticePositionStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>只有 Begin 與 End 之間的 AiManager.Move 才會進入編輯器 registry。</summary>
    public bool IsActive => active is not null;

    /// <summary>目前選擇的來源；每次 Begin 預設沿用該 scenario 上次明示保存的選擇。</summary>
    public PracticePositionMode Mode
    {
        get => UseCustom ? PracticePositionMode.Custom : PracticePositionMode.Original;
        set => SetMode(value);
    }

    public bool UseCustom { get; private set; }

    /// <summary>
    /// Counts every successfully persisted edit — mode switch, step override, interval.
    /// A run latches it, so editing positions retires that run's streak and any pending
    /// retry instead of quietly continuing under the same practice identity. Readable
    /// while no scenario is active, which is exactly when a queued retry is re-checked.
    /// </summary>
    public long ContentVersion { get; private set; }

    public string? LastError { get; private set; }
    public int LoadErrorCount { get; private set; }
    public int SaveErrorCount { get; private set; }

    public PracticeScenarioInfo? CurrentScenario
        => activeKey is null || activeName is null || activeStratName is null
            ? null
            : new PracticeScenarioInfo(activeKey, activeName, activeStratName);

    /// <summary>本場已實際 fire 的 movement steps；未 fire 的步驟不偽造座標。</summary>
    public IReadOnlyList<PracticeObservedMove> ObservedSteps => observed;
    public long ObservationVersion { get; private set; }

    public IReadOnlyList<PracticeIntervalOverlay> IntervalOverlays
        => active is null
            ? Array.Empty<PracticeIntervalOverlay>()
            : GetIntervals(active);

    /// <summary>
    /// 開始一個隔離的 scenario session。舊 session 的 registry 會清空，但檔案中的其他 scenario 不受影響。
    /// </summary>
    public void Begin(string scenarioKey, string scenarioName, string stratName)
    {
        EnsureLoaded();
        observed.Clear();
        observedIndex.Clear();
        ObservationVersion++;
        nextStepIndex = 0;
        active = null;
        activeKey = null;
        activeName = null;
        activeStratName = null;
        UseCustom = false;

        if (string.IsNullOrWhiteSpace(scenarioKey))
        {
            SetError("PracticePositions.Begin 缺少 scenario key；本場維持原始路線。");
            return;
        }

        var key = scenarioKey.Trim();
        activeKey = key;
        activeName = scenarioName ?? string.Empty;
        activeStratName = stratName ?? string.Empty;
        if (!scenarios.TryGetValue(key, out active))
        {
            active = new ScenarioDocument
            {
                Name = activeName,
                StratName = activeStratName,
            };
            scenarios[key] = active;
        }
        UseCustom = active.UseCustom;
    }

    /// <summary>結束 session；持久化內容保留，下一場不會誤用本場 steps。</summary>
    public void End()
    {
        active = null;
        activeKey = null;
        activeName = null;
        activeStratName = null;
        observed.Clear();
        observedIndex.Clear();
        ObservationVersion++;
        nextStepIndex = 0;
        UseCustom = false;
    }

    /// <summary>AiManager 在排程時呼叫；step index 在本場全域遞增，跨 manager 仍保持穩定順序。</summary>
    public PracticeStepToken Register(float time, float arrivalTime)
    {
        if (active is null)
            return new PracticeStepToken("inactive", -1, time, arrivalTime);

        var index = nextStepIndex++;
        return new PracticeStepToken($"s{index:D6}", index, time, arrivalTime);
    }

    /// <summary>
    /// 在 movement event fire 時解析原始目標。所有自訂資料都要通過原始指紋 guard；
    /// 未選 Custom、沒有 overlay 或 branch 不同時，回傳原始演算法目標。
    /// </summary>
    public Vector2?[] Resolve(PracticeStepToken step, IReadOnlyList<Vector2?> original,
        IReadOnlyList<Vector2?>? reference = null)
    {
        var originalSlots = PracticePositionLogic.CloneSlots(original);
        if (active is null || step.Index < 0) return originalSlots;

        // Recorded tracks supply their authored targets before proximity/no-op filtering.
        // Custom movement must not change its own branch fingerprint on the next sample.
        var referenceSlots = reference is null ? originalSlots : PracticePositionLogic.CloneSlots(reference);
        var fingerprint = PracticePositionLogic.Fingerprint(referenceSlots);
        var hasStepOverlay = active.Steps.TryGetValue(step.Id, out var stepDocument);
        var stepOverlay = UseCustom && hasStepOverlay
            && TryCreateStepOverlay(step.Id, stepDocument!, out var parsedStep) ? parsedStep : null;
        var stepResult = stepOverlay is not null
            ? PracticePositionLogic.ApplyStep(originalSlots, fingerprint, stepOverlay)
            : (Positions: originalSlots, Applied: false, FingerprintMatched: true);
        var intervals = UseCustom ? GetIntervals(active) : Array.Empty<PracticeIntervalOverlay>();
        var intervalSeen = false;
        var intervalMismatch = false;
        foreach (var interval in intervals)
        {
            if (!PracticePositionLogic.IsWithinInclusive(step.Time, interval.StartTime, interval.EndTime)) continue;
            intervalSeen = true;
            if (!interval.ExpectedFingerprints.TryGetValue(step.Id, out var expected)
                || !string.Equals(expected, fingerprint, StringComparison.Ordinal))
                intervalMismatch = true;
        }
        var intervalResult = intervals.Count > 0
            ? PracticePositionLogic.ApplyIntervals(stepResult.Positions, step, fingerprint, intervals)
            : (Positions: stepResult.Positions, AppliedIntervals: Array.Empty<string>());
        var hasOverlay = hasStepOverlay || intervalSeen;
        var fingerprintMatched = stepResult.FingerprintMatched && !intervalMismatch;
        var customApplied = UseCustom && (stepResult.Applied || intervalResult.AppliedIntervals.Length > 0);
        RecordObserved(new PracticeObservedMove(
            step, referenceSlots, intervalResult.Positions, fingerprint, UseCustom, customApplied,
            fingerprintMatched, intervalResult.AppliedIntervals,
            ResolveStatus(UseCustom, hasOverlay, customApplied, fingerprintMatched)));
        // The caller may alter its result, but not the editor's original/effective snapshots.
        return PracticePositionLogic.CloneSlots(intervalResult.Positions);
    }

    /// <summary>明示切換 Original / Custom；選擇本身也存入對應 scenario，不影響其他場。</summary>
    public bool SetMode(PracticePositionMode mode)
    {
        if (active is null)
            return Fail("尚未開始 scenario，無法選擇練習路線。");

        var oldUseCustom = UseCustom;
        var oldDocumentValue = active.UseCustom;
        UseCustom = mode == PracticePositionMode.Custom;
        active.UseCustom = UseCustom;
        if (TrySave()) return true;

        UseCustom = oldUseCustom;
        active.UseCustom = oldDocumentValue;
        return false;
    }

    /// <summary>取得指定步驟目前保存的 per-slot 覆寫；回傳複本。</summary>
    public PracticeStepOverlay? GetStepOverlay(string stepId)
    {
        if (active is null || !active.Steps.TryGetValue(stepId, out var document)
            || !TryCreateStepOverlay(stepId, document, out var overlay))
            return null;
        return new PracticeStepOverlay(
            overlay.StepId,
            overlay.OriginalFingerprint,
            PracticePositionLogic.CloneSlots(overlay.Positions));
    }

    public bool TrySetStepSlot(string stepId, int slot, Vector2 position)
    {
        if (slot is < 0 or >= PracticePositionLogic.PartySlotCount)
            return Fail("角色欄位超出 MT/ST/H1/H2/D1-D4 範圍。");
        if (FindObserved(stepId) is not { } observedStep)
            return Fail("這一步尚未實際執行，請先重跑 scenario 觀察後再編輯。");

        var positions = active!.Steps.TryGetValue(stepId, out var old)
            && string.Equals(old.Fingerprint, observedStep.OriginalFingerprint, StringComparison.Ordinal)
            && TryReadPositions(old.Positions, out var saved)
            ? saved
            : new Vector2?[PracticePositionLogic.PartySlotCount];
        positions[slot] = position;
        return TrySetStepPositions(stepId, observedStep.OriginalFingerprint, positions);
    }

    public bool TryClearStepSlot(string stepId, int slot)
    {
        if (slot is < 0 or >= PracticePositionLogic.PartySlotCount)
            return Fail("角色欄位超出 MT/ST/H1/H2/D1-D4 範圍。");
        if (active is null || !active.Steps.TryGetValue(stepId, out var old)
            || !TryReadPositions(old.Positions, out var positions))
            return true;

        positions[slot] = null;
        if (positions.All(position => position is null))
            return TryResetStep(stepId);
        return TrySetStepPositions(stepId, old.Fingerprint, positions);
    }

    public bool TrySetStepPositions(string stepId, string expectedFingerprint, IReadOnlyList<Vector2?> positions)
    {
        if (active is null)
            return Fail("尚未開始 scenario，無法保存步驟覆寫。");
        if (FindObserved(stepId) is null)
            return Fail("這一步尚未實際執行，請先觀察原始目標再保存覆寫。");
        if (positions.Count != PracticePositionLogic.PartySlotCount)
            return Fail("步驟覆寫必須包含 8 個 party slot。");
        if (string.IsNullOrWhiteSpace(expectedFingerprint))
            return Fail("缺少原始分支指紋；為避免誤套用，本次不保存。");
        return Commit(document => document.Steps[stepId] = new StepDocument
        {
            Fingerprint = expectedFingerprint,
            Positions = positions.Select(ToRawPosition).ToArray(),
        });
    }

    public bool TryResetStep(string stepId)
    {
        if (active is null) return Fail("尚未開始 scenario，無法還原步驟。");
        if (!active.Steps.ContainsKey(stepId)) return true;
        return Commit(document => document.Steps.Remove(stepId));
    }

    /// <summary>
    /// 建立閉區間 [start,end] 的固定錨點／線性錨點。ExpectedFingerprints 由已觀察步驟自動產生，
    /// 因此同一個時間區間遇到另一個動態 branch 時會 fail closed。
    /// </summary>
    public bool TryAddInterval(
        float startTime,
        float endTime,
        int slot,
        Vector2 startPosition,
        Vector2? endPosition = null)
    {
        if (active is null) return Fail("尚未開始 scenario，無法建立軌跡區間。");
        if (!float.IsFinite(startTime) || !float.IsFinite(endTime) || endTime < startTime)
            return Fail("軌跡區間必須是有效的起始／結束秒數，且結束不得早於起始。");
        if (slot is < 0 or >= PracticePositionLogic.PartySlotCount)
            return Fail("角色欄位超出 MT/ST/H1/H2/D1-D4 範圍。");
        if (!TryCollectFingerprints(startTime, endTime, out var fingerprints))
            return false;

        var id = NewIntervalId();
        return Commit(document => document.Intervals.Add(new IntervalDocument
        {
            Id = id,
            StartTime = startTime,
            EndTime = endTime,
            Slot = slot,
            Start = ToRaw(startPosition),
            End = endPosition is { } end ? ToRaw(end) : null,
            ExpectedFingerprints = fingerprints,
        }));
    }

    public bool TryUpdateInterval(
        string id,
        float startTime,
        float endTime,
        int slot,
        Vector2 startPosition,
        Vector2? endPosition = null)
    {
        if (active is null) return Fail("尚未開始 scenario，無法更新軌跡區間。");
        if (!float.IsFinite(startTime) || !float.IsFinite(endTime) || endTime < startTime)
            return Fail("軌跡區間必須是有效的起始／結束秒數，且結束不得早於起始。");
        if (slot is < 0 or >= PracticePositionLogic.PartySlotCount)
            return Fail("角色欄位超出 MT/ST/H1/H2/D1-D4 範圍。");
        if (!TryCollectFingerprints(startTime, endTime, out var fingerprints))
            return false;
        if (!active.Intervals.Any(interval => string.Equals(interval.Id, id, StringComparison.Ordinal)))
            return Fail("找不到要更新的軌跡區間。");

        return Commit(document =>
        {
            var interval = document.Intervals.First(item =>
                string.Equals(item.Id, id, StringComparison.Ordinal));
            interval.StartTime = startTime;
            interval.EndTime = endTime;
            interval.Slot = slot;
            interval.Start = ToRaw(startPosition);
            interval.End = endPosition is { } end ? ToRaw(end) : null;
            interval.ExpectedFingerprints = fingerprints;
        });
    }

    public bool TryDeleteInterval(string id)
    {
        if (active is null) return Fail("尚未開始 scenario，無法刪除軌跡區間。");
        return Commit(document =>
        {
            document.Intervals.RemoveAll(interval =>
                string.Equals(interval.Id, id, StringComparison.Ordinal));
        });
    }

    /// <summary>清除本場全部自訂步驟／軌跡區間並回到 Original。保存失敗時回滾記憶體狀態。</summary>
    public bool TryRestoreOriginal()
    {
        if (active is null) return Fail("尚未開始 scenario，無法還原原始路線。");
        return Commit(document =>
        {
            document.Steps.Clear();
            document.Intervals.Clear();
            document.UseCustom = false;
            UseCustom = false;
        });
    }

    private bool TryCollectFingerprints(float startTime, float endTime,
        out Dictionary<string, string> fingerprints)
    {
        fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var step in observed)
            if (PracticePositionLogic.IsWithinInclusive(step.Time, startTime, endTime))
                fingerprints[step.Id] = step.OriginalFingerprint;
        if (fingerprints.Count != 0) return true;
        return Fail("指定的軌跡區間尚未觀察到移動步驟；請先執行並調整時間範圍。");
    }

    private string NewIntervalId()
    {
        var number = 1;
        while (active!.Intervals.Any(interval =>
                   string.Equals(interval.Id, $"i{number:D4}", StringComparison.Ordinal)))
            number++;
        return $"i{number:D4}";
    }

    private bool Commit(Action<ScenarioDocument> mutation)
    {
        if (active is null || activeKey is null)
            return Fail("尚未開始 scenario，無法保存練習覆寫。");

        var before = Clone(active!);
        mutation(active!);
        active!.ParsedIntervals = null;
        if (TrySave()) return true;

        active = before;
        scenarios[activeKey!] = before;
        UseCustom = before.UseCustom;
        return false;
    }

    private bool TrySave()
    {
        if (LoadErrorCount > 0)
        {
            SaveErrorCount++;
            return Fail("站位檔未成功讀取，禁止覆寫原檔；修復後請重啟遊戲再編輯。");
        }
        try
        {
            var root = new StorageDocument
            {
                Schema = StorageSchema,
                Scenarios = scenarios.ToDictionary(pair => pair.Key, pair => ToStorage(pair.Value), StringComparer.Ordinal),
            };
            var json = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
            if (store.TrySave(json, out var error))
            {
                ContentVersion++;
                LastError = null;
                return true;
            }

            SaveErrorCount++;
            SetError($"練習站位保存失敗：{error ?? "未知錯誤"}");
            return false;
        }
        catch (Exception ex)
        {
            SaveErrorCount++;
            SetError($"練習站位保存失敗：{ex.Message}");
            return false;
        }
    }

    private void EnsureLoaded()
    {
        if (loaded) return;
        loaded = true;
        try
        {
            if (!store.TryLoad(out var contents, out var error))
            {
                LoadErrorCount++;
                SetError($"練習站位讀取失敗：{error ?? "未知錯誤"}");
                return;
            }
            if (string.IsNullOrWhiteSpace(contents)) return;

            var root = JsonSerializer.Deserialize<StorageDocument>(contents,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (root is null || root.Schema != StorageSchema)
            {
                LoadErrorCount++;
                SetError($"練習站位檔 schema 不支援（需要 {StorageSchema}）；本次維持原始路線。");
                return;
            }

            foreach (var (key, raw) in root.Scenarios ?? new Dictionary<string, StorageScenario>())
            {
                if (string.IsNullOrWhiteSpace(key) || raw is null)
                    throw new FormatException("站位檔含無效的場景條目");
                var normalized = Normalize(raw);
                if (normalized is not null) scenarios[key] = normalized;
            }
        }
        catch (Exception ex)
        {
            scenarios.Clear();
            LoadErrorCount++;
            SetError($"練習站位 JSON 讀取失敗：{ex.Message}");
        }
    }

    public PracticeObservedMove? FindObserved(string id)
        => observedIndex.TryGetValue(id, out var index) ? observed[index] : null;

    private void RecordObserved(PracticeObservedMove step)
    {
        if (observedIndex.TryGetValue(step.Id, out var index))
            observed[index] = step;
        else
        {
            observedIndex[step.Id] = observed.Count;
            observed.Add(step);
        }
        ObservationVersion++;
    }

    private bool TryCreateStepOverlay(string stepId, StepDocument document,
        out PracticeStepOverlay overlay)
    {
        overlay = null!;
        if (string.IsNullOrWhiteSpace(document.Fingerprint)
            || !TryReadPositions(document.Positions, out var positions))
            return false;
        overlay = new PracticeStepOverlay(
            StepId: stepId,
            OriginalFingerprint: document.Fingerprint,
            Positions: positions);
        return true;
    }

    private static string ResolveStatus(bool customRequested, bool hasOverlay,
        bool customApplied, bool fingerprintMatched)
    {
        if (!customRequested)
            return hasOverlay ? "原始模式（自訂未套用）" : "原始目標";
        if (hasOverlay && !fingerprintMatched)
            return customApplied ? "部分自訂套用；其餘未觀察或分支不符" : "自訂略過：未觀察或原始分支指紋不符";
        return customApplied ? "自訂目標" : "原始目標";
    }

    private static IReadOnlyList<PracticeIntervalOverlay> GetIntervals(ScenarioDocument document)
        => document.ParsedIntervals ??= document.Intervals.Select(ToPublicInterval).ToArray();

    private static PracticeIntervalOverlay ToPublicInterval(IntervalDocument document)
        => new(
            document.Id,
            document.StartTime,
            document.EndTime,
            document.Slot,
            FromRaw(document.Start) ?? Vector2.Zero,
            FromRaw(document.End),
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(document.ExpectedFingerprints ?? new(), StringComparer.Ordinal)));

    private static StorageScenario ToStorage(ScenarioDocument document)
        => new()
        {
            Name = document.Name,
            StratName = document.StratName,
            UseCustom = document.UseCustom,
            Steps = document.Steps.ToDictionary(pair => pair.Key, pair => new StorageStep
            {
                Fingerprint = pair.Value.Fingerprint,
                Positions = pair.Value.Positions,
            }, StringComparer.Ordinal),
            Intervals = document.Intervals.Select(interval => new StorageInterval
            {
                Id = interval.Id,
                StartTime = interval.StartTime,
                EndTime = interval.EndTime,
                Slot = interval.Slot,
                Start = interval.Start,
                End = interval.End,
                ExpectedFingerprints = interval.ExpectedFingerprints,
            }).ToList(),
        };

    private static ScenarioDocument? Normalize(StorageScenario raw)
    {
        var normalized = new ScenarioDocument
        {
            Name = raw.Name ?? string.Empty,
            StratName = raw.StratName ?? string.Empty,
            UseCustom = raw.UseCustom,
        };

        foreach (var (stepId, step) in raw.Steps ?? new Dictionary<string, StorageStep>())
        {
            if (string.IsNullOrWhiteSpace(stepId) || step is null
                || string.IsNullOrWhiteSpace(step.Fingerprint)
                || !TryReadPositions(step.Positions, out var positions))
                throw new FormatException($"站位檔含無效步驟：{stepId}");
            normalized.Steps[stepId] = new StepDocument
            {
                Fingerprint = step.Fingerprint,
                Positions = positions.Select(ToRawPosition).ToArray(),
            };
        }

        foreach (var interval in raw.Intervals ?? new List<StorageInterval>())
        {
            if (interval is null || string.IsNullOrWhiteSpace(interval.Id)
                || !float.IsFinite(interval.StartTime) || !float.IsFinite(interval.EndTime)
                || interval.EndTime < interval.StartTime
                || interval.Slot is < 0 or >= PracticePositionLogic.PartySlotCount
                || FromRaw(interval.Start) is not { } start)
                throw new FormatException("站位檔含無效時間區間");
            var end = FromRaw(interval.End);
            if (interval.End is not null && end is null)
                throw new FormatException("站位檔含無效區間終點");
            var expected = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in interval.ExpectedFingerprints ?? new())
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                    expected[pair.Key] = pair.Value;
            if (expected.Count == 0) throw new FormatException("站位區間缺少原始分支指紋");
            normalized.Intervals.Add(new IntervalDocument
            {
                Id = interval.Id,
                StartTime = interval.StartTime,
                EndTime = interval.EndTime,
                Slot = interval.Slot,
                Start = ToRaw(start),
                End = end is { } endPosition ? ToRaw(endPosition) : null,
                ExpectedFingerprints = expected,
            });
        }
        return normalized;
    }

    private static ScenarioDocument Clone(ScenarioDocument source)
        => new()
        {
            Name = source.Name,
            StratName = source.StratName,
            UseCustom = source.UseCustom,
            Steps = source.Steps.ToDictionary(pair => pair.Key, pair => new StepDocument
            {
                Fingerprint = pair.Value.Fingerprint,
                Positions = pair.Value.Positions.Select(raw => raw is null ? null : (float[])raw.Clone()).ToArray(),
            }, StringComparer.Ordinal),
            Intervals = source.Intervals.Select(interval => new IntervalDocument
            {
                Id = interval.Id,
                StartTime = interval.StartTime,
                EndTime = interval.EndTime,
                Slot = interval.Slot,
                Start = interval.Start is null ? null : (float[])interval.Start.Clone(),
                End = interval.End is null ? null : (float[])interval.End.Clone(),
                ExpectedFingerprints = new Dictionary<string, string>(
                    interval.ExpectedFingerprints ?? new(), StringComparer.Ordinal),
            }).ToList(),
        };

    private bool Fail(string message)
    {
        SetError(message);
        return false;
    }

    private void SetError(string message) => LastError = message;

    private static float[]? ToRawPosition(Vector2? position)
        => position is { } value ? ToRaw(value) : null;

    private static float[] ToRaw(Vector2 position) => [position.X, position.Y];

    private static Vector2? FromRaw(float[]? raw)
        => raw is { Length: 2 } && float.IsFinite(raw[0]) && float.IsFinite(raw[1])
            ? new Vector2(raw[0], raw[1])
            : null;

    private static bool TryReadPositions(float[]?[]? raw, out Vector2?[] positions)
    {
        positions = new Vector2?[PracticePositionLogic.PartySlotCount];
        if (raw is null || raw.Length != PracticePositionLogic.PartySlotCount)
            return false;
        for (var i = 0; i < positions.Length; i++)
        {
            if (raw[i] is null) continue;
            if (FromRaw(raw[i]) is not { } position) return false;
            positions[i] = position;
        }
        return true;
    }

    private sealed class StorageDocument
    {
        public int Schema { get; set; }
        public Dictionary<string, StorageScenario>? Scenarios { get; set; }
            = new(StringComparer.Ordinal);
    }

    private sealed class StorageScenario
    {
        public string? Name { get; set; }
        public string? StratName { get; set; }
        public bool UseCustom { get; set; }
        public Dictionary<string, StorageStep>? Steps { get; set; }
            = new(StringComparer.Ordinal);
        public List<StorageInterval>? Intervals { get; set; } = new();
    }

    private sealed class StorageStep
    {
        public string? Fingerprint { get; set; }
        public float[]?[]? Positions { get; set; }
    }

    private sealed class StorageInterval
    {
        public string? Id { get; set; }
        public float StartTime { get; set; }
        public float EndTime { get; set; }
        public int Slot { get; set; }
        public float[]? Start { get; set; }
        public float[]? End { get; set; }
        public Dictionary<string, string>? ExpectedFingerprints { get; set; }
    }

    private sealed class ScenarioDocument
    {
        public string Name { get; set; } = string.Empty;
        public string StratName { get; set; } = string.Empty;
        public bool UseCustom { get; set; }
        public Dictionary<string, StepDocument> Steps { get; set; }
            = new(StringComparer.Ordinal);
        public List<IntervalDocument> Intervals { get; set; } = new();
        public IReadOnlyList<PracticeIntervalOverlay>? ParsedIntervals { get; set; }
    }

    private sealed class StepDocument
    {
        public string Fingerprint { get; set; } = string.Empty;
        public float[]?[] Positions { get; set; }
            = new float[]?[PracticePositionLogic.PartySlotCount];
    }

    private sealed class IntervalDocument
    {
        public string Id { get; set; } = string.Empty;
        public float StartTime { get; set; }
        public float EndTime { get; set; }
        public int Slot { get; set; }
        public float[]? Start { get; set; }
        public float[]? End { get; set; }
        public Dictionary<string, string> ExpectedFingerprints { get; set; }
            = new(StringComparer.Ordinal);
    }
}
