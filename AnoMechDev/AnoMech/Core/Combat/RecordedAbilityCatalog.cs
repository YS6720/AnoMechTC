using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnoMech.Core.Combat;

public enum RecordedAbilityTargetKind
{
    Self,
    Target,
}

public enum RecordedAbilityTargetCategory
{
    Self,
    Player,
    Npc,
}

public readonly record struct RecordedAbilityIdentity(byte JobId, uint ActionId);

public sealed record RecordedAbilitySource(string Name, string Sha256);

public sealed record RecordedAbilitySourceRef(string Source, long? CaptureSeq, long? SourceLine, string Sha256);

public sealed record RecordedAbilityObservedAction(byte JobId, uint ActionId, IReadOnlyList<byte> ObservedLevels);

public sealed record RecordedAbilityRule(
    byte JobId,
    uint ActionId,
    ushort StatusId,
    RecordedAbilityTargetKind TargetKind,
    RecordedAbilityTargetCategory TargetCategory,
    float DurationSeconds,
    float DelaySeconds,
    int? Param,
    IReadOnlyList<byte> ObservedLevels,
    string ApplicationCondition,
    string DurationKind,
    IReadOnlyList<RecordedAbilitySourceRef> SourceRefs);

public sealed record RecordedAbilityRemoval(
    byte JobId,
    uint ActionId,
    ushort StatusId,
    RecordedAbilityTargetKind TargetKind,
    RecordedAbilityTargetCategory TargetCategory,
    float DelaySeconds,
    IReadOnlyList<byte> ObservedLevels,
    string RemovalCondition,
    IReadOnlyList<RecordedAbilitySourceRef> SourceRefs);

/// <summary>
/// The immutable, validated projection of the local recorded-ability evidence.
/// Only observed-only rules with a settled positive sampled timer are executable;
/// conditional and unusable observations remain in <see cref="Rules"/> for
/// diagnostics but are never exposed through the executable lookup methods.
/// </summary>
public sealed class RecordedAbilityCatalog
{
    public const int SupportedSchemaVersion = 1;
    public const string EmbeddedResourceName = "AnoMech.Data.recorded_abilities.json";

    private static readonly IReadOnlyList<RecordedAbilityRule> EmptyRules = Array.Empty<RecordedAbilityRule>();
    private static readonly IReadOnlyList<RecordedAbilityRemoval> EmptyRemovals = Array.Empty<RecordedAbilityRemoval>();

    private readonly Dictionary<RecordedAbilityIdentity, IReadOnlyList<byte>> observedLevels;
    private readonly Dictionary<(byte JobId, uint ActionId, byte Level), IReadOnlyList<RecordedAbilityRule>> executableRules;
    private readonly Dictionary<(byte JobId, uint ActionId, byte Level), IReadOnlyList<RecordedAbilityRemoval>> executableRemovals;

    private RecordedAbilityCatalog(
        IReadOnlyList<RecordedAbilitySource> sources,
        IReadOnlyList<RecordedAbilityObservedAction> observedActions,
        IReadOnlyList<RecordedAbilityRule> rules,
        IReadOnlyList<RecordedAbilityRemoval> removals,
        IReadOnlyList<JsonElement> unresolved)
    {
        Sources = sources;
        ObservedActions = observedActions;
        Rules = rules;
        Removals = removals;
        Unresolved = unresolved;

        observedLevels = new Dictionary<RecordedAbilityIdentity, IReadOnlyList<byte>>();
        foreach (var action in observedActions)
            observedLevels.Add(new(action.JobId, action.ActionId), action.ObservedLevels);

        var ruleMap = new Dictionary<(byte, uint, byte), List<RecordedAbilityRule>>();
        foreach (var rule in rules)
        {
            if (!IsExecutable(rule)) continue;
            foreach (var level in rule.ObservedLevels)
            {
                var key = (rule.JobId, rule.ActionId, level);
                if (!ruleMap.TryGetValue(key, out var list))
                    ruleMap[key] = list = new List<RecordedAbilityRule>();
                list.Add(rule);
            }
        }
        executableRules = ruleMap.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<RecordedAbilityRule>)pair.Value.ToArray());

        var removalMap = new Dictionary<(byte, uint, byte), List<RecordedAbilityRemoval>>();
        foreach (var removal in removals)
        {
            if (!IsExecutable(removal)) continue;
            foreach (var level in removal.ObservedLevels)
            {
                var key = (removal.JobId, removal.ActionId, level);
                if (!removalMap.TryGetValue(key, out var list))
                    removalMap[key] = list = new List<RecordedAbilityRemoval>();
                list.Add(removal);
            }
        }
        executableRemovals = removalMap.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<RecordedAbilityRemoval>)pair.Value.ToArray());
    }

    public IReadOnlyList<RecordedAbilitySource> Sources { get; }
    public IReadOnlyList<RecordedAbilityObservedAction> ObservedActions { get; }
    public IReadOnlyList<RecordedAbilityRule> Rules { get; }
    public IReadOnlyList<RecordedAbilityRemoval> Removals { get; }
    public IReadOnlyList<JsonElement> Unresolved { get; }
    public int ExecutableRuleCount => Rules.Count(rule => IsExecutable(rule));
    public int ExecutableRemovalCount => Removals.Count(removal => IsExecutable(removal));

    public bool IsObservedAction(byte jobId, uint actionId, byte level)
        => observedLevels.TryGetValue(new(jobId, actionId), out var levels) && levels.Contains(level);

    public IReadOnlyList<RecordedAbilityRule> FindExecutableRules(byte jobId, uint actionId, byte level)
        => executableRules.TryGetValue((jobId, actionId, level), out var rules) ? rules : EmptyRules;

    public IReadOnlyList<RecordedAbilityRemoval> FindExecutableRemovals(byte jobId, uint actionId, byte level)
        => executableRemovals.TryGetValue((jobId, actionId, level), out var removals) ? removals : EmptyRemovals;

    public static RecordedAbilityCatalog LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(RecordedAbilityCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName);
        if (stream is null)
            throw new InvalidOperationException(
                $"Recorded ability catalog resource '{EmbeddedResourceName}' is missing from {assembly.GetName().Name}; " +
                "the plugin cannot expose recorded ability support without the embedded allowlist.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static RecordedAbilityCatalog Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("Recorded ability catalog is empty.");

        RawCatalog raw;
        try
        {
            raw = JsonSerializer.Deserialize<RawCatalog>(json, SerializerOptions) ??
                throw new InvalidDataException("Recorded ability catalog JSON is null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Recorded ability catalog JSON is malformed.", ex);
        }

        return Validate(raw);
    }

    private static RecordedAbilityCatalog Validate(RawCatalog raw)
    {
        if (raw.SchemaVersion != SupportedSchemaVersion)
            throw new InvalidDataException($"Unsupported recorded ability catalog schemaVersion {raw.SchemaVersion}; expected {SupportedSchemaVersion}.");
        if (raw.Sources is null || raw.Sources.Count == 0)
            throw new InvalidDataException("Recorded ability catalog must contain at least one source.");
        if (raw.ObservedActions is null)
            throw new InvalidDataException("Recorded ability catalog is missing observedActions.");
        if (raw.Rules is null)
            throw new InvalidDataException("Recorded ability catalog is missing rules.");
        if (raw.Removals is null)
            throw new InvalidDataException("Recorded ability catalog is missing removals.");
        if (raw.Unresolved is null)
            throw new InvalidDataException("Recorded ability catalog is missing unresolved.");

        var sources = new List<RecordedAbilitySource>(raw.Sources.Count);
        var sourceHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in raw.Sources)
        {
            RequireText(source.Name, "source.name");
            RequireSha256(source.Sha256, "source.sha256");
            if (!sourceHashes.TryAdd(source.Name!, source.Sha256!))
                throw new InvalidDataException($"Duplicate catalog source '{source.Name}'.");
            sources.Add(new(source.Name!, source.Sha256!));
        }

        var observedActions = new List<RecordedAbilityObservedAction>(raw.ObservedActions.Count);
        var observedKeys = new HashSet<RecordedAbilityIdentity>();
        foreach (var action in raw.ObservedActions)
        {
            ValidateJob(action.JobId, "observedActions.jobId");
            ValidateAction(action.ActionId, "observedActions.actionId");
            var levels = ValidateLevels(action.ObservedLevels, "observedActions.observedLevels");
            if (!observedKeys.Add(new((byte)action.JobId, action.ActionId)))
                throw new InvalidDataException($"Duplicate observed action {action.JobId}/{action.ActionId}.");
            observedActions.Add(new((byte)action.JobId, action.ActionId, levels));
        }
        var observedLevelMap = observedActions.ToDictionary(
            action => new RecordedAbilityIdentity(action.JobId, action.ActionId),
            action => action.ObservedLevels);
        var rules = new List<RecordedAbilityRule>(raw.Rules.Count);
        var ruleKeys = new HashSet<(byte, uint, ushort, string, string)>();
        foreach (var rule in raw.Rules)
        {
            var normalized = ValidateRule(rule, sourceHashes);
            if (!ruleKeys.Add((normalized.JobId, normalized.ActionId, normalized.StatusId,
                    normalized.TargetKind.ToString(), normalized.TargetCategory.ToString())))
                throw new InvalidDataException(
                    $"Duplicate recorded ability rule {normalized.JobId}/{normalized.ActionId}/{normalized.StatusId}/{normalized.TargetKind}/{normalized.TargetCategory}.");
            rules.Add(normalized);
        }

        var removals = new List<RecordedAbilityRemoval>(raw.Removals.Count);
        var removalKeys = new HashSet<(byte, uint, ushort, string, string)>();
        foreach (var removal in raw.Removals)
        {
            var normalized = ValidateRemoval(removal, sourceHashes);
            if (!removalKeys.Add((normalized.JobId, normalized.ActionId, normalized.StatusId,
                    normalized.TargetKind.ToString(), normalized.TargetCategory.ToString())))
                throw new InvalidDataException(
                    $"Duplicate recorded ability removal {normalized.JobId}/{normalized.ActionId}/{normalized.StatusId}/{normalized.TargetKind}/{normalized.TargetCategory}.");
            removals.Add(normalized);
        }

        // An observed-only executable rule must refer to an observed action with the
        // same exact level. This prevents a malformed allowlist from creating a new
        // action identity through the rules section alone.
        foreach (var rule in rules.Where(IsExecutable))
            foreach (var level in rule.ObservedLevels)
            {
                var key = new RecordedAbilityIdentity(rule.JobId, rule.ActionId);
                if (!observedLevelMap.TryGetValue(key, out var observedLevels) ||
                    !observedLevels.Contains(level))
                    throw new InvalidDataException(
                        $"Executable rule {rule.JobId}/{rule.ActionId}/{rule.StatusId} has no exact observed action level {level}.");
            }
        foreach (var removal in removals.Where(IsExecutable))
            foreach (var level in removal.ObservedLevels)
            {
                var key = new RecordedAbilityIdentity(removal.JobId, removal.ActionId);
                if (!observedLevelMap.TryGetValue(key, out var observedLevels) ||
                    !observedLevels.Contains(level))
                    throw new InvalidDataException(
                        $"Executable removal {removal.JobId}/{removal.ActionId}/{removal.StatusId} has no exact observed action level {level}.");
            }

        return new(sources, observedActions, rules, removals, raw.Unresolved);
    }

    private static RecordedAbilityRule ValidateRule(RawRule raw, IReadOnlyDictionary<string, string> sourceHashes)
    {
        ValidateJob(raw.JobId, "rule.jobId");
        ValidateAction(raw.ActionId, "rule.actionId");
        ValidateStatus(raw.StatusId, "rule.statusId");
        var targetKind = ParseTargetKind(raw.TargetKind, "rule.targetKind");
        var targetCategory = ParseTargetCategory(raw.TargetCategory, "rule.targetCategory");
        ValidateTargetPair(targetKind, targetCategory, "rule");
        RequireText(raw.Operation, "rule.operation");
        if (!string.Equals(raw.Operation, "apply", StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported recorded rule operation '{raw.Operation}'.");
        RequireText(raw.ApplicationCondition, "rule.applicationCondition");
        if (raw.ApplicationCondition is not ("observedOnly" or "conditionalObserved"))
            throw new InvalidDataException($"Unsupported recorded rule applicationCondition '{raw.ApplicationCondition}'.");
        RequireText(raw.DurationKind, "rule.durationKind");
        if (raw.DurationKind is not ("sampledRemainingAtDelay" or "unusableSampledRemaining"))
            throw new InvalidDataException($"Unsupported recorded rule durationKind '{raw.DurationKind}'.");
        RequireText(raw.IntrinsicDuration, "rule.intrinsicDuration");
        if (!string.Equals(raw.IntrinsicDuration, "unknown", StringComparison.Ordinal))
            throw new InvalidDataException($"Recorded rule intrinsicDuration must be 'unknown', got '{raw.IntrinsicDuration}'.");
        var levels = ValidateLevels(raw.ObservedLevels, "rule.observedLevels");
        ValidateLevelRange(raw.LevelMin, raw.LevelMax, levels, "rule");
        ValidateSourceRefs(raw.SourceRefs, sourceHashes, "rule.sourceRefs");
        ValidateOptionalFiniteNonnegative(raw.DelaySeconds, "rule.delaySeconds");
        ValidateOptionalPositive(raw.DurationSeconds, "rule.durationSeconds");
        if (raw.DurationKind == "sampledRemainingAtDelay" && raw.DurationSeconds is null)
            throw new InvalidDataException("Sampled recorded rule must carry durationSeconds.");
        if (raw.DurationKind == "sampledRemainingAtDelay" && raw.DelaySeconds is null)
            throw new InvalidDataException("Sampled recorded rule must carry delaySeconds.");
        if (raw.Param is < 0 or > ushort.MaxValue)
            throw new InvalidDataException($"Recorded rule param {raw.Param} is outside ushort range.");

        return new((byte)raw.JobId, raw.ActionId, (ushort)raw.StatusId, targetKind, targetCategory,
            raw.DurationSeconds ?? 0f, raw.DelaySeconds ?? 0f, raw.Param, levels,
            raw.ApplicationCondition, raw.DurationKind, raw.SourceRefs!.Select(ToSourceRef).ToArray());
    }

    private static RecordedAbilityRemoval ValidateRemoval(RawRemoval raw, IReadOnlyDictionary<string, string> sourceHashes)
    {
        ValidateJob(raw.JobId, "removal.jobId");
        ValidateAction(raw.ActionId, "removal.actionId");
        ValidateStatus(raw.StatusId, "removal.statusId");
        var targetKind = ParseTargetKind(raw.TargetKind, "removal.targetKind");
        var targetCategory = ParseTargetCategory(raw.TargetCategory, "removal.targetCategory");
        ValidateTargetPair(targetKind, targetCategory, "removal");
        RequireText(raw.Operation, "removal.operation");
        if (!string.Equals(raw.Operation, "remove", StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported recorded removal operation '{raw.Operation}'.");
        RequireText(raw.RemovalCondition, "removal.removalCondition");
        if (raw.RemovalCondition is not ("observedOnly" or "conditionalObserved"))
            throw new InvalidDataException($"Unsupported recorded removal removalCondition '{raw.RemovalCondition}'.");
        var levels = ValidateLevels(raw.ObservedLevels, "removal.observedLevels");
        ValidateLevelRange(raw.LevelMin, raw.LevelMax, levels, "removal");
        ValidateSourceRefs(raw.SourceRefs, sourceHashes, "removal.sourceRefs");
        if (raw.DelaySeconds is null)
            throw new InvalidDataException("Recorded removal must carry delaySeconds.");
        ValidateOptionalFiniteNonnegative(raw.DelaySeconds, "removal.delaySeconds");

        return new((byte)raw.JobId, raw.ActionId, (ushort)raw.StatusId, targetKind, targetCategory,
            raw.DelaySeconds.Value, levels, raw.RemovalCondition, raw.SourceRefs!.Select(ToSourceRef).ToArray());
    }

    private static bool IsExecutable(RecordedAbilityRule rule)
        => rule.ApplicationCondition == "observedOnly" &&
           rule.DurationKind == "sampledRemainingAtDelay" && rule.Param.HasValue &&
           rule.DurationSeconds > 0f && float.IsFinite(rule.DurationSeconds) &&
           rule.DelaySeconds >= 0f && float.IsFinite(rule.DelaySeconds);

    private static bool IsExecutable(RecordedAbilityRemoval removal)
        => removal.RemovalCondition == "observedOnly" &&
           removal.DelaySeconds >= 0f && float.IsFinite(removal.DelaySeconds);

    private static byte[] ValidateLevels(IReadOnlyList<int>? rawLevels, string field)
    {
        if (rawLevels is null || rawLevels.Count == 0)
            throw new InvalidDataException($"{field} must be a non-empty array.");
        var result = new byte[rawLevels.Count];
        var seen = new HashSet<byte>();
        for (var i = 0; i < rawLevels.Count; i++)
        {
            if (rawLevels[i] is < 1 or > byte.MaxValue)
                throw new InvalidDataException($"{field}[{i}]={rawLevels[i]} is outside byte level range.");
            var level = (byte)rawLevels[i];
            if (!seen.Add(level)) throw new InvalidDataException($"{field} contains duplicate level {level}.");
            result[i] = level;
        }
        return result;
    }

    private static void ValidateLevelRange(int? min, int? max, IReadOnlyList<byte> levels, string field)
    {
        if (min is null || max is null || min < 1 || max < min || max > byte.MaxValue)
            throw new InvalidDataException($"{field} levelMin/levelMax is invalid.");
        if (levels.Any(level => level < min || level > max))
            throw new InvalidDataException($"{field} observed level falls outside levelMin/levelMax.");
    }

    private static void ValidateSourceRefs(IReadOnlyList<RawSourceRef>? refs,
        IReadOnlyDictionary<string, string> sourceHashes, string field)
    {
        if (refs is null || refs.Count == 0)
            throw new InvalidDataException($"{field} must be non-empty.");
        foreach (var source in refs)
        {
            RequireText(source.Source, $"{field}.source");
            if (!sourceHashes.TryGetValue(source.Source!, out var expectedHash))
                throw new InvalidDataException($"{field} references unknown source '{source.Source}'.");
            if (source.CaptureSeq is <= 0 || source.SourceLine is <= 0 ||
                (source.CaptureSeq is null && source.SourceLine is null))
                throw new InvalidDataException($"{field} requires a positive captureSeq or sourceLine.");
            RequireSha256(source.Sha256, $"{field}.sha256");
            if (!string.Equals(expectedHash, source.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{field} hash does not match source '{source.Source}'.");
    }

    }
    private static void ValidateTargetPair(RecordedAbilityTargetKind kind, RecordedAbilityTargetCategory category, string field)
    {
        if ((kind == RecordedAbilityTargetKind.Self) != (category == RecordedAbilityTargetCategory.Self))
            throw new InvalidDataException($"{field} targetKind/targetCategory pair is inconsistent.");
    }

    private static RecordedAbilityTargetKind ParseTargetKind(string? value, string field)
        => value switch
        {
            "self" => RecordedAbilityTargetKind.Self,
            "target" => RecordedAbilityTargetKind.Target,
            _ => throw new InvalidDataException($"{field} must be 'self' or 'target'."),
        };

    private static RecordedAbilityTargetCategory ParseTargetCategory(string? value, string field)
        => value switch
        {
            "self" => RecordedAbilityTargetCategory.Self,
            "player" => RecordedAbilityTargetCategory.Player,
            "npc" => RecordedAbilityTargetCategory.Npc,
            _ => throw new InvalidDataException($"{field} must be 'self', 'player', or 'npc'."),
        };

    private static void ValidateJob(int value, string field)
    {
        if (value is < 1 or > byte.MaxValue) throw new InvalidDataException($"{field}={value} is outside job range.");
    }

    private static void ValidateAction(uint value, string field)
    {
        if (value == 0) throw new InvalidDataException($"{field} must be non-zero.");
    }

    private static void ValidateStatus(int value, string field)
    {
        if (value is < 1 or > ushort.MaxValue) throw new InvalidDataException($"{field}={value} is outside status range.");
    }

    private static void ValidateOptionalPositive(float? value, string field)
    {
        if (value is { } number && (!float.IsFinite(number) || number <= 0f))
            throw new InvalidDataException($"{field} must be finite and positive when present.");
    }

    private static void ValidateOptionalFiniteNonnegative(float? value, string field)
    {
        if (value is { } number && (!float.IsFinite(number) || number < 0f))
            throw new InvalidDataException($"{field} must be finite and non-negative when present.");
    }

    private static void RequireText(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException($"{field} is required.");
    }

    private static void RequireSha256(string? value, string field)
    {
        RequireText(value, field);
        if (value!.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException($"{field} must be a 64-character SHA-256 hex string.");
    }

    private static RecordedAbilitySourceRef ToSourceRef(RawSourceRef source)
        => new(source.Source!, source.CaptureSeq, source.SourceLine, source.Sha256!);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private sealed class RawCatalog
    {
        public int SchemaVersion { get; set; }
        public List<RawSource>? Sources { get; set; }
        public List<RawObservedAction>? ObservedActions { get; set; }
        public List<RawRule>? Rules { get; set; }
        public List<RawRemoval>? Removals { get; set; }
        public List<JsonElement>? Unresolved { get; set; }
    }

    private sealed class RawSource
    {
        public string? Name { get; set; }
        public string? Sha256 { get; set; }
    }

    private sealed class RawObservedAction
    {
        public int JobId { get; set; }
        public uint ActionId { get; set; }
        public List<int>? ObservedLevels { get; set; }
    }

    private sealed class RawRule
    {
        public int JobId { get; set; }
        public uint ActionId { get; set; }
        public int StatusId { get; set; }
        public string? TargetKind { get; set; }
        public string? TargetCategory { get; set; }
        public string? Operation { get; set; }
        public float? DurationSeconds { get; set; }
        public float? DelaySeconds { get; set; }
        public int? Param { get; set; }
        public List<int>? ObservedLevels { get; set; }
        public string? ApplicationCondition { get; set; }
        public string? DurationKind { get; set; }
        public string? IntrinsicDuration { get; set; }
        public int? LevelMin { get; set; }
        public int? LevelMax { get; set; }
        public int? EvidenceCount { get; set; }
        public string? SourceDerivation { get; set; }
        public List<RawSourceRef>? SourceRefs { get; set; }
        public int? ObservedDelayCount { get; set; }
        public float? ObservedDelayMinSeconds { get; set; }
        public float? ObservedDelayMaxSeconds { get; set; }
        public int? ObservedRemainingCount { get; set; }
        public float? ObservedRemainingMin { get; set; }
        public float? ObservedRemainingMax { get; set; }
        public int? PendingNegativeCount { get; set; }
        public float? PendingNegativeMin { get; set; }
        public float? PendingNegativeMax { get; set; }
        public int? SettledDelayCount { get; set; }
        public float? SettledDelayMinSeconds { get; set; }
        public float? SettledDelayMaxSeconds { get; set; }
        public int? SettledPositiveCount { get; set; }
        public float? SettledPositiveMin { get; set; }
        public float? SettledPositiveMax { get; set; }
    }

    private sealed class RawRemoval
    {
        public int JobId { get; set; }
        public uint ActionId { get; set; }
        public int StatusId { get; set; }
        public string? TargetKind { get; set; }
        public string? TargetCategory { get; set; }
        public string? Operation { get; set; }
        public string? RemovalCondition { get; set; }
        public float? DelaySeconds { get; set; }
        public List<int>? ObservedLevels { get; set; }
        public int? LevelMin { get; set; }
        public int? LevelMax { get; set; }
        public int? EvidenceCount { get; set; }
        public List<RawSourceRef>? SourceRefs { get; set; }
        public int? ObservedDelayCount { get; set; }
        public float? ObservedDelayMinSeconds { get; set; }
        public string? SourceDerivation { get; set; }
        public float? ObservedDelayMaxSeconds { get; set; }
    }

    private sealed class RawSourceRef
    {
        public string? Source { get; set; }
        public long? CaptureSeq { get; set; }
        public long? SourceLine { get; set; }
        public string? Sha256 { get; set; }
    }
}

/// <summary>Pure validation shared by the runtime and its dependency-free tests.</summary>
public static class RecordedAbilityEligibility
{
    public static bool IsTargetAllowed(RecordedAbilityTargetKind kind,
        RecordedAbilityTargetCategory category, bool hasTarget, bool targetIsPlayer)
    {
        if (kind == RecordedAbilityTargetKind.Self)
            return category == RecordedAbilityTargetCategory.Self;
        if (!hasTarget) return false;
        return category switch
        {
            RecordedAbilityTargetCategory.Player => targetIsPlayer,
            RecordedAbilityTargetCategory.Npc => !targetIsPlayer,
            _ => false,
        };
    }

    public static bool IsComboReady(uint prerequisite, uint current, float ageSeconds)
        => prerequisite == 0 || (current == prerequisite && ageSeconds >= 0f && ageSeconds < 30f);
}

/// <summary>
/// A tiny real-time queue used by the ability resolver. It intentionally does not
/// execute callbacks: the runtime drains values and performs actor/generation
/// checks immediately before mutating managed/native status state.
/// </summary>
public readonly record struct RecordedAbilityQueueEntry<T>(long Generation, long Version, float Due, T Value);

public sealed class RecordedAbilityQueue<T>
{
    private readonly List<RecordedAbilityQueueEntry<T>> entries = new();
    private float elapsed;

    public float Time => elapsed;
    public int Count => entries.Count;

    public void Add(float delaySeconds, long generation, long version, T value)
    {
        if (!float.IsFinite(delaySeconds) || delaySeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(delaySeconds));
        var entry = new RecordedAbilityQueueEntry<T>(generation, version, elapsed + delaySeconds, value);
        var index = entries.FindIndex(existing => existing.Due > entry.Due);
        if (index < 0) entries.Add(entry);
        else entries.Insert(index, entry);
    }

    public int Drain(float deltaSeconds, long generation, List<RecordedAbilityQueueEntry<T>> destination)
    {
        if (deltaSeconds < 0f || !float.IsFinite(deltaSeconds))
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        if (destination is null) throw new ArgumentNullException(nameof(destination));
        elapsed += deltaSeconds;
        destination.Clear();
        while (entries.Count > 0 && entries[0].Due <= elapsed)
        {
            var entry = entries[0];
            entries.RemoveAt(0);
            if (entry.Generation == generation) destination.Add(entry);
        }
        return destination.Count;
    }

    public void Clear()
    {
        entries.Clear();
        elapsed = 0f;
    }
}
