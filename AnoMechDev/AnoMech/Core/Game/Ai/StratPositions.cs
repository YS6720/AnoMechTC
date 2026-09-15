using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace AnoMech.Core.Game.Ai;

public enum StratPositionTableKind
{
    PartyPositions,
    MechanicPoints,
}

/// <summary>
/// 具名站位表：把「解法」從 C# 硬編碼裡拉出來，存成 JSON。
///
/// 由來：上游的站位是寫死在 AI 檔裡的座標字面值（TopP5OmegaAi 就有六組），
/// 改一個數字要重編譯 + reload。對「調流派」這種需要反覆微調的事來說成本太高。
///
/// 這裡只負責 **存取與持久化**；擷取／預覽的 UI 在 PositionEditorWindow。
/// 座標一律是**場景本地 XZ**（與 AiMove／MoveTo 同一個空間），不是世界座標。
/// </summary>
public static class StratPositions
{
    /// <summary>一張站位表的定義。預設值由 scenario 提供，使用者覆寫存 JSON。</summary>
    public sealed record Table(
        string Key,
        string Label,
        Vector2[] Defaults,
        string[] SlotLabels,
        StratPositionTableKind Kind);
    private static readonly Dictionary<string, Table> tables = new();
    private static readonly Dictionary<string, Vector2[]> overrides = new();
    private static bool loaded;
    private static bool loadFailed;
    private static readonly IPracticePositionStore Store = new FilePracticePositionStore(static () => FilePath);
    public static string? LastError { get; private set; }

    private static string FilePath =>
        Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "strat-positions.json");

    /// <summary>
    /// 註冊一張表。scenario 建構時呼叫；重複註冊同 key 會覆寫定義（熱重載友善）。
    /// </summary>
    public static void Register(
        string key,
        string label,
        Vector2[] defaults,
        string[] slotLabels,
        StratPositionTableKind kind = StratPositionTableKind.PartyPositions)
    {
        if (defaults.Length != slotLabels.Length)
        {
            Plugin.Log.Warning($"StratPositions.Register({key}): defaults {defaults.Length} 與 slotLabels {slotLabels.Length} 長度不符，已忽略。");
            LastError = $"站位表 {key} 的 defaults 與 slotLabels 長度不符。";
            return;
        }
        tables[key] = new Table(key, label, (Vector2[])defaults.Clone(), (string[])slotLabels.Clone(), kind);
        EnsureLoaded();
    }

    public static IEnumerable<Table> All => tables.Values.OrderBy(t => t.Key, StringComparer.Ordinal);

    public static Table? Find(string key) => tables.TryGetValue(key, out var t) ? t : null;

    /// <summary>目前生效的座標：有覆寫用覆寫，否則用預設。回傳的是複本，改它不會影響儲存值。</summary>
    public static Vector2[] Get(string key)
    {
        EnsureLoaded();
        if (overrides.TryGetValue(key, out var v) && tables.TryGetValue(key, out var t) && v.Length == t.Defaults.Length)
            return (Vector2[])v.Clone();
        return tables.TryGetValue(key, out var tt) ? (Vector2[])tt.Defaults.Clone() : Array.Empty<Vector2>();
    }

    public static bool IsOverridden(string key)
    {
        EnsureLoaded();
        return overrides.ContainsKey(key);
    }


    public static bool TrySet(string key, Vector2[] values)
    {
        if (!tables.TryGetValue(key, out var t) || values.Length != t.Defaults.Length)
        {
            Plugin.Log.Warning($"StratPositions.Set({key}): 長度不符或未註冊，已忽略。");
            LastError = $"站位表 {key} 長度不符或尚未註冊。";
            return false;
        }
        var hadPrevious = overrides.TryGetValue(key, out var previous);
        overrides[key] = (Vector2[])values.Clone();
        if (Save()) return true;
        if (hadPrevious) overrides[key] = previous!;
        else overrides.Remove(key);
        return false;
    }



    public static bool TryReset(string key)
    {
        if (!overrides.Remove(key, out var previous)) return true;
        if (Save()) return true;
        overrides[key] = previous;
        return false;
    }

    private static void EnsureLoaded()
    {
        if (loaded) return;
        loaded = true;
        try
        {
            if (!Store.TryLoad(out var contents, out var error)) throw new IOException(error);
            if (string.IsNullOrWhiteSpace(contents)) return;
            var raw = JsonSerializer.Deserialize<Dictionary<string, float[]>>(contents);
            if (raw is null) return;
            foreach (var (key, flat) in raw)
            {
                if (flat is null || flat.Length % 2 != 0 || flat.Any(value => !float.IsFinite(value)))
                    throw new FormatException($"站位表 {key} 的座標格式無效");
                var points = new Vector2[flat.Length / 2];
                for (var i = 0; i < points.Length; i++) points[i] = new Vector2(flat[i * 2], flat[i * 2 + 1]);
                overrides[key] = points;
            }
            Plugin.Log.Info($"StratPositions: 載入 {overrides.Count} 組覆寫（{FilePath}）");
        }
        catch (Exception ex)
        {
            overrides.Clear();
            loadFailed = true;
            LastError = $"站位表讀取失敗：{ex.Message}；禁止覆寫原檔，修復後請重啟遊戲。";
            Plugin.Log.Warning(LastError);
        }
    }

    private static bool Save()
    {
        if (loadFailed) return false;
        try
        {
            // 存成扁平 float 陣列而不是 Vector2 物件陣列：手改 JSON 時一行一組看得懂。
            var raw = overrides.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.SelectMany(p => new[] { p.X, p.Y }).ToArray());
            var contents = JsonSerializer.Serialize(raw, new JsonSerializerOptions { WriteIndented = true });
            if (!Store.TrySave(contents, out var error)) throw new IOException(error);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"站位表保存失敗：{ex.Message}";
            Plugin.Log.Warning($"StratPositions: 存檔失敗：{ex.Message}");
            return false;
        }
    }
}
