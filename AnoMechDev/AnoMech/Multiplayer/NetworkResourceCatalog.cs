using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnoMech.Scenarios;
using AnoMech.Scenarios.Recorded;
using LuminaAction = Lumina.Excel.Sheets.Action;
using LuminaBnpcBase = Lumina.Excel.Sheets.BNpcBase;
using LuminaEObj = Lumina.Excel.Sheets.EObj;
using LuminaModelChara = Lumina.Excel.Sheets.ModelChara;
using LuminaStatus = Lumina.Excel.Sheets.Status;

namespace AnoMech.Multiplayer;

// The relay exchanges only opaque resource keys. A key is backed by this
// process-local catalog, which is built from installed Lumina rows and local
// game files before a run is prepared. No remote path or row is ever trusted.
public sealed class NetworkResourceCatalog
{
    private const string ActorPrefix = "actor:";
    private const string StaticPrefix = "static:";
    private readonly Dictionary<string, string> staticKeysByPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> actorKeysByPath = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> staticResources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> actorResources = new(StringComparer.Ordinal);
    private readonly HashSet<uint> actionIds = [];
    private readonly HashSet<uint> bnpcIds = [];
    private readonly HashSet<uint> bnpcNameIds = [];
    private readonly HashSet<uint> modelCharaIds = [];
    private readonly HashSet<uint> eobjIds = [];
    private readonly HashSet<ushort> statusIds = [];
    private readonly HashSet<uint> timelineIds = [];
    private readonly HashSet<uint> tetherIds = [];
    private readonly HashSet<uint> weatherIds = [];

    private NetworkResourceCatalog(IScenario scenario, RecordedTimeline? timeline)
    {
        Scenario = scenario;
        BuildCodeVisualCatalog();
        BuildRecordedVisualCatalog(scenario, timeline);
        LoadRuntimeRows();
        Fingerprint = BuildFingerprint();
    }

    public IScenario Scenario { get; }
    public string Fingerprint { get; }
    public bool RuntimeDataAvailable { get; private set; }

    /// <summary>
    /// <paramref name="timeline"/> is the approved recording for <paramref name="scenario"/>.
    /// A recorded scene must supply it: the catalog never opens the Data file itself,
    /// so the advertised resources always belong to the bytes the run was approved for.
    /// </summary>
    public static NetworkResourceCatalog Create(IScenario scenario, RecordedTimeline? timeline)
    {
        if (scenario is null) throw new ArgumentNullException(nameof(scenario));
        if ((scenario is RecordedScenario) != (timeline is not null))
            throw new MpProtocolException(MpError.SceneMismatch);
        return new(scenario, timeline);
    }

    public bool TryGetStaticKey(string path, out string key)
    {
        if (path is not null && staticKeysByPath.TryGetValue(path, out var found))
        {
            key = found;
            return true;
        }
        key = string.Empty;
        return false;
    }

    public bool TryGetActorKey(string path, out string key)
    {
        if (path is not null && actorKeysByPath.TryGetValue(path, out var found))
        {
            key = found;
            return true;
        }
        key = string.Empty;
        return false;
    }

    public bool TryResolveStatic(string key, out string path)
        => staticResources.TryGetValue(key, out path!);

    public bool TryResolveActor(string key, out string path)
        => actorResources.TryGetValue(key, out path!);

    public bool HasAction(uint actionId) => actionId != 0 && actionIds.Contains(actionId);
    public bool HasStatus(ushort statusId) => statusId != 0 && statusIds.Contains(statusId);
    public bool HasBnpc(uint bnpcId) => bnpcId != 0 && bnpcIds.Contains(bnpcId);
    public bool HasModelChara(uint modelCharaId) => modelCharaId != 0 && modelCharaIds.Contains(modelCharaId);
    public bool HasEobj(uint eobjId) => eobjId != 0 && eobjIds.Contains(eobjId);

    public bool TryValidateEnemy(EnemyState value)
    {
        if (!WorldValidation.ValidateEnemy(value) || !HasBnpc(value.BnpcBaseId)) return false;
        if (value.ModelCharaId != 0 && !HasModelChara(value.ModelCharaId)) return false;
        if (value.NameId != 0 && !bnpcNameIds.Contains(value.NameId)) return false;
        foreach (var status in value.Statuses)
            if (!HasStatus(status.Id)) return false;
        return true;
    }

    public bool TryValidateEventObject(EventObjectState value)
        => WorldValidation.ValidateEventObject(value) && HasEobj(value.EobjId);

    public bool TryValidateRole(RoleState value)
    {
        if (!WorldValidation.ValidateRole(value)) return false;
        foreach (var status in value.Statuses)
            if (!HasStatus(status.Id)) return false;
        return true;
    }

    public bool TryValidateAction(uint actionId) => HasAction(actionId);

    private void BuildCodeVisualCatalog()
    {

        // Paths used by the current non-DSR helpers are still local resources and
        // therefore must be in the same allow-list when a scenario emits them.
        AddVisual("vfx/omen/eff/general02f.avfx", actor: false, statik: true);
        AddVisual("vfx/monster/m0114/eff/m0114cbbm_sp_pop_c0i.avfx", actor: true, statik: false);
    }

    private void AddStaticConstants(Type type)
    {
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            if (field.IsLiteral && field.FieldType == typeof(string) && field.GetRawConstantValue() is string path)
                AddVisual(path, actor: false, statik: true);
    }

    public bool HasTimeline(uint id) => id == 0 || timelineIds.Contains(id);
    public bool HasTether(uint id) => id != 0 && tetherIds.Contains(id);
    public bool HasWeather(uint id) => weatherIds.Contains(id);

    private void BuildRecordedVisualCatalog(IScenario scenario, RecordedTimeline? timeline)
    {
        if (scenario is not RecordedScenario || timeline is null) return;
        try
        {
            foreach (var evidence in timeline.VisualEvidence)
            {
                if (evidence.ValueKind != JsonValueKind.Object ||
                    !evidence.TryGetProperty("replay", out var replay) ||
                    replay.ValueKind != JsonValueKind.True ||
                    !evidence.TryGetProperty("kind", out var kind) ||
                    !string.Equals(kind.GetString(), "svfx", StringComparison.Ordinal) ||
                    !evidence.TryGetProperty("path", out var pathValue) ||
                    pathValue.ValueKind != JsonValueKind.String)
                    continue;
                var path = pathValue.GetString();
                if (!string.IsNullOrWhiteSpace(path)) AddVisual(path, actor: false, statik: true);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Multiplayer] recorded visual catalog skipped: {ex.Message}");
        }
    }

    private void LoadRuntimeRows()
    {
        try
        {
            foreach (var row in Plugin.DataManager.GetExcelSheet<LuminaAction>()) actionIds.Add(row.RowId);
            foreach (var row in Plugin.DataManager.GetExcelSheet<LuminaBnpcBase>())
            {
                bnpcIds.Add(row.RowId);
                if (row.ModelChara.RowId != 0) modelCharaIds.Add(row.ModelChara.RowId);
            }
            foreach (var row in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.BNpcName>())
                bnpcNameIds.Add(row.RowId);
            foreach (var row in Plugin.DataManager.GetExcelSheet<LuminaModelChara>()) modelCharaIds.Add(row.RowId);
            foreach (var row in Plugin.DataManager.GetExcelSheet<LuminaEObj>()) eobjIds.Add(row.RowId);
            foreach (var row in Plugin.DataManager.GetExcelSheet<LuminaStatus>())
                if (row.RowId <= ushort.MaxValue) statusIds.Add((ushort)row.RowId);
            foreach (var row in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>())
                timelineIds.Add(row.RowId);
            foreach (var row in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Channeling>())
                tetherIds.Add(row.RowId);
            foreach (var row in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Weather>())
                weatherIds.Add(row.RowId);
            foreach (var row in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Lockon>())
            {
                // Same TC field mapping as VfxFunctions.LockonVfxIconName:
                // Unknown0 is the local sheet's actor-VFX icon name.
                var iconName = row.Unknown0.ExtractText();
                if (!string.IsNullOrEmpty(iconName))
                    AddVisual($"vfx/lockon/eff/{iconName}.avfx", actor: true, statik: false);
            }
            RuntimeDataAvailable = actionIds.Count != 0 && bnpcIds.Count != 0 && eobjIds.Count != 0
                && bnpcNameIds.Count != 0 && timelineIds.Count != 0 && tetherIds.Count != 0 && weatherIds.Count != 0;
        }
        catch (Exception ex)
        {
            RuntimeDataAvailable = false;
            Plugin.Log.Warning($"[Multiplayer] runtime resource catalog unavailable: {ex.Message}");
        }
    }

    private void AddVisual(string? path, bool actor, bool statik)
    {
        if ((!actor && !statik) || string.IsNullOrWhiteSpace(path) || path.Length > 512 ||
            path.Any(char.IsWhiteSpace) || !path.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase))
            return;
        if (!FileExists(path)) return;

        if (actor)
        {
            var key = MakeKey(ActorPrefix, path);
            actorResources[key] = path;
            actorKeysByPath[path] = key;
        }
        if (statik)
        {
            var key = MakeKey(StaticPrefix, path);
            staticResources[key] = path;
            staticKeysByPath[path] = key;
        }
    }

    private static string MakeKey(string prefix, string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path));
        return prefix + Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static bool FileExists(string path)
    {
        try { return Plugin.DataManager.FileExists(path); }
        catch { return false; }
    }

    private string BuildFingerprint()
    {
        var lines = new List<string>(staticResources.Count + actorResources.Count +
                                     actionIds.Count + bnpcIds.Count + eobjIds.Count + statusIds.Count + 1)
        {
            $"runtime:{RuntimeDataAvailable}"
        };
        lines.AddRange(staticResources.Select(pair => $"{pair.Key}:{pair.Value}"));
        lines.AddRange(actorResources.Select(pair => $"{pair.Key}:{pair.Value}"));
        lines.AddRange(actionIds.Select(id => $"action:{id}"));
        lines.AddRange(bnpcIds.Select(id => $"bnpc:{id}"));
        lines.AddRange(bnpcNameIds.Select(id => $"bnpc-name:{id}"));
        lines.AddRange(modelCharaIds.Select(id => $"model:{id}"));
        lines.AddRange(eobjIds.Select(id => $"eobj:{id}"));
        lines.AddRange(statusIds.Select(id => $"status:{id}"));
        lines.AddRange(timelineIds.Select(id => $"timeline:{id}"));
        lines.AddRange(tetherIds.Select(id => $"tether:{id}"));
        lines.AddRange(weatherIds.Select(id => $"weather:{id}"));
        lines.Sort(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines)))).ToLowerInvariant();
    }
}
