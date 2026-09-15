using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnoMech.Scenarios.Recorded;

/// <summary>
/// 從真副本錄影轉出來的場景資料。**這是機制的唯一事實源**。
///
/// 由來（2026-08-19）：先前是人工把錄影裡的數字抄進 C# 常數。抄一次錯一次——
/// 詠唱 7.3 秒抄成 3 秒、debuff 忘了給持續時間變成永久沒倒數、時間軸取錯零點。
/// 而且**這類錯誤在畫面上看不出來**，只有真的打過那個副本的人才知道不對，
/// 所以每次都得等使用者實機打完才發現，一輪一輪燒。
///
/// 改成：`tools/build-scenario.py` 把錄影轉成 JSON，C# 只負責播放。
/// 數字不對＝重跑轉換器換一份錄影，不是回來改常數。
///
/// Schema v2（2026-09-02，P-098）：加欄位、**不改既有欄位**。所有新欄位都是 optional
/// 且預設值＝v1 行為，舊檔（`schema` 缺席＝1）照播且路徑與現況完全相同——
/// 那些 v1 檔（dsr-p3 等）是 P3 反覆調校過的，行為變了沒有任何測試會紅。
/// </summary>
public sealed class RecordedTimeline
{
    /// <summary>轉換器有完整可用證據的能力；缺席可能是舊錄製、未觸發或資料不完整。</summary>
    public static readonly string[] KnownCaps =
        ["eff", "omen", "targetable", "weapon", "weaponComplete", "appearance",
         "actorName", "actorState", "position3d", "nativeEffect",
         "vfxIdentity", "vfxTransform", "vfxLifetime", "vfxCause",
         "captureGap", "completion", "actionMetadata"];

    public sealed class Event
    {
        [JsonPropertyName("t")] public float T { get; set; }
        [JsonPropertyName("e")] public string Kind { get; set; } = "";

        // cast
        [JsonPropertyName("action")] public uint Action { get; set; }
        /// <summary>逐點 effect，不把同刻 helper 的不同目標合併成一份。</summary>
        [JsonPropertyName("effects")] public List<NativeEffect?> Effects { get; set; } = [];
        [JsonPropertyName("cast")] public float CastSeconds { get; set; }
        [JsonPropertyName("base")] public uint BaseId { get; set; }

        // status
        [JsonPropertyName("status")] public ushort Status { get; set; }
        [JsonPropertyName("dur")] public float Duration { get; set; }
        /// <summary>錄影時的 status SourceId；來源切換由 status-／status+ 順序表達。</summary>
        [JsonPropertyName("statusSource")] public ulong? StatusSource { get; set; }

        // headmarker
        [JsonPropertyName("marker")] public uint Marker { get; set; }
        // actor（視窗內出現過的敵人，含不施法的遠景物）
        [JsonPropertyName("nameId")] public uint NameId { get; set; }
        [JsonPropertyName("lv")] public byte Level { get; set; } = 90;
        [JsonPropertyName("name")] public string? ActorName { get; set; }
        [JsonPropertyName("visible")] public bool? Visible { get; set; }
        [JsonPropertyName("modelChara")] public uint? ModelCharaId { get; set; }
        [JsonPropertyName("scale")] public float? Scale { get; set; }
        [JsonPropertyName("hitbox")] public float? HitboxRadius { get; set; }
        [JsonPropertyName("wMain")] public ulong? WeaponMain { get; set; }
        [JsonPropertyName("wOff")] public ulong? WeaponOff { get; set; }

        // eobj（塔等）
        [JsonPropertyName("baseId")] public uint EObjId { get; set; }
        [JsonPropertyName("x")] public float X { get; set; }
        [JsonPropertyName("y")] public float Y { get; set; }
        [JsonPropertyName("z")] public float Z { get; set; }

        /// <summary>同一刻同時發生幾份（塔 ×3、點名 3 人…）。</summary>
        [JsonPropertyName("n")] public int Count { get; set; } = 1;

        /// <summary>
        /// 這筆點名／頭標在錄影中落在哪些 role（0=MT 1=ST 2=H1 3=H2 4=D1 5=D2 6=D3 7=D4）。
        /// 有它就照發、**不隨機**——跑位是跟著被點的 debuff 走的，分配一亂全亂
        /// （維護者 2026-08-20）。空＝舊資料，退回程式分配。
        /// </summary>
        [JsonPropertyName("roles")] public List<int> Roles { get; set; } = new();

        /// <summary>施法者當下的場景本地座標（每個同時發動的份各一組）。塔／直線 AOE 靠它定位。</summary>
        [JsonPropertyName("pos")] public List<float[]> Positions { get; set; } = new();

        // ── v2 ────────────────────────────────────────────────────────────
        // 以下全部 optional，預設值＝v1 行為（缺席時走的路徑與 schema 1 完全一樣）。

        /// <summary>
        /// 實例序號。actor／npcmove／eobj＝這一隻（同一個 BNpcBase 可能同時有好幾隻）；
        /// status 在 NPC 身上時＝那隻演員；**weather 事件借用同一個欄位當天氣 id**
        /// ——錄影機的 weather 事件本來就寫 `"id"`，JSON 名稱只能有一個屬性接，
        /// 硬拆兩個屬性會讓其中一個永遠讀不到（見 <see cref="WeatherId"/>）。
        ///
        /// **0＝缺席；轉換器保證實例 id ≥ 1**——重播端拿 `Id != 0` 當「這筆有沒有指定實例」
        /// 的判準（例：status 掛在 NPC 身上還是隊伍槽位）。id 從 0 起算的話，
        /// 第一隻演員會被整個系統當成「沒有指定」，而那**在畫面上看起來只是少一隻**。
        /// </summary>
        [JsonPropertyName("id")] public int Id { get; set; }

        /// <summary>天氣事件的 Weather row。與 <see cref="Id"/> 是同一個 JSON 欄位，見該處註解。</summary>
        [JsonIgnore] public byte WeatherId => (byte)Id;

        /// <summary>施法者／接線起點的演員實例序號（對 actor 事件的 id）。＝<see cref="Srcs"/>[0]。</summary>
        [JsonPropertyName("src")] public int Src { get; set; }

        /// <summary>
        /// 同刻多份施法的**逐點施法者**，與 <see cref="Positions"/> 一一對齊；
        /// null＝那一份找不到實例（生臨時 helper 代打）。
        ///
        /// 由來（2026-09-02 驗收）：先前只讓 `src` 那一隻出手，於是「三座塔同刻讀條」
        /// 只有第一座出現——實測 FRU P2 就有 17 筆多點事件（塔／多方向直線）。
        /// 而少掉的那兩座在畫面上**只是「這次沒有那個機制」**，看不出是資料掉了還是本來就沒有。
        /// </summary>
        [JsonPropertyName("srcs")] public List<int?> Srcs { get; set; } = new();

        /// <summary>施法瞬間的絕對朝向（弧度，0＝南）。null＝錄影沒有這欄，維持 v1 的「不轉向」。</summary>
        [JsonPropertyName("rot")] public float? Rot { get; set; }

        /// <summary>範圍提示提前出現的秒數（封包 OmenDelay）。0＝跟著讀條走。</summary>
        [JsonPropertyName("omen")] public float Omen { get; set; }

        /// <summary>
        /// 目標：`"self"`／`{"role":k}`／`{"actor":id}`。用 JsonElement 直接收原樣——
        /// 三種形狀各給一個 C# 型別只會多兩個轉換器，解析在
        /// <see cref="RecordedScenario"/> 一處收斂。缺席＝Undefined＝沿用 v1 的自我目標。
        /// </summary>
        [JsonPropertyName("target")] public JsonElement Target { get; set; }

        /// <summary>招式真正落下的相對秒數（來自 effect 通道）。null＝用 t+cast 推。</summary>
        [JsonPropertyName("hit")] public float? Hit { get; set; }

        /// <summary>這一擊在錄影中打到哪些 role。空＝沒錄到，改用幾何結算。</summary>
        [JsonPropertyName("hits")] public List<int> Hits { get; set; } = new();

        /// <summary>Knockback sheet 的 row id（effect type 32 的 value）。0＝不擊退。</summary>
        [JsonPropertyName("kb")] public uint Kb { get; set; }

        /// <summary>舊錄製格式的狀態層數；未提供時沿用單層。</summary>
        [JsonPropertyName("stacks")] public int Stacks { get; set; } = 1;

        /// <summary>
        /// 錄影原始 Status.Param；與語意化的 <see cref="Stacks"/> 分開保存。
        /// 缺席（舊錄影／舊轉換器）時重播端退回 Stacks。
        /// </summary>
        [JsonPropertyName("param")] public ushort? StatusParam { get; set; }

        /// <summary>這個演員／EObj／接線消失的相對秒數。null＝到視窗結束都在。</summary>
        [JsonPropertyName("until")] public float? Until { get; set; }

        /// <summary>
        /// **actor 專用**：可選取（＝真身，不是隱形 helper）。null＝錄影沒這欄，
        /// 退回「只有 anchor 可選」。EObj 的可選取狀態是封包原始 byte（實測值 5），
        /// 型別對不上 ⇒ 走 <see cref="TargetableStatus"/> 另一個鍵，
        /// 同鍵不同型會讓**整份檔案反序列化就拋例外**（2026-09-02 驗收實踩）。
        /// </summary>
        [JsonPropertyName("targetable")] public bool? Targetable { get; set; }

        /// <summary>**eobj 專用**：封包的 TargetableStatus 原始 byte（1＝不可選取）。原樣轉交引擎。</summary>
        [JsonPropertyName("targetableStatus")] public byte TargetableStatus { get; set; } = 1;

        // eobj 狀態欄（封包原樣；SpawnObjectPacket 的同名欄位）
        [JsonPropertyName("state")] public ushort State { get; set; }
        [JsonPropertyName("eventState")] public byte EventState { get; set; }
        [JsonPropertyName("vis")] public byte Vis { get; set; }
        [JsonPropertyName("radius")] public float Radius { get; set; } = 1f;
        [JsonPropertyName("layoutId")] public uint LayoutId { get; set; }
        [JsonPropertyName("eventId")] public uint EventId { get; set; }
        [JsonPropertyName("gimmickId")] public uint GimmickId { get; set; }

        // mapeffect
        [JsonPropertyName("index")] public byte Index { get; set; }
        [JsonPropertyName("flags")] public ushort Flags { get; set; }

        // tether
        [JsonPropertyName("tetherId")] public ushort TetherId { get; set; }

        /// <summary>接線起點與 target 同型：role 或 actor。舊資料缺席時沿用 Src。</summary>
        [JsonPropertyName("source")] public JsonElement Source { get; set; }

        // chat（NPC 台詞）
        [JsonPropertyName("who")] public string Who { get; set; } = "";
        [JsonPropertyName("text")] public string Text { get; set; } = "";
    }

    public sealed class NativeEffect
    {
        [JsonPropertyName("hit")] public float? Hit { get; set; }
        [JsonPropertyName("animationLock")] public float? AnimationLock { get; set; }
        [JsonPropertyName("spellId")] public ushort? SpellId { get; set; }
        [JsonPropertyName("animationVariation")] public byte? AnimationVariation { get; set; }
        [JsonPropertyName("actionType")] public byte? ActionType { get; set; }
        [JsonPropertyName("flags")] public byte? Flags { get; set; }
        [JsonPropertyName("animationTarget")] public JsonElement AnimationTarget { get; set; }
        [JsonPropertyName("castTarget")] public JsonElement CastTarget { get; set; }
        [JsonPropertyName("ballistaTarget")] public JsonElement BallistaTarget { get; set; }
        [JsonPropertyName("targets")] public List<JsonElement> Targets { get; set; } = [];
        [JsonPropertyName("targetCount")] public int? TargetCount { get; set; }
        [JsonPropertyName("hasPosition")] public bool? HasPosition { get; set; }
        [JsonPropertyName("x")] public float? X { get; set; }
        [JsonPropertyName("y")] public float? Y { get; set; }
        [JsonPropertyName("z")] public float? Z { get; set; }
        [JsonPropertyName("rot")] public float? Rotation { get; set; }
        [JsonPropertyName("tidComplete")] public bool? TargetsComplete { get; set; }
        [JsonPropertyName("effComplete")] public bool? EffectsComplete { get; set; }
        /// <summary>保留原始 sequence／效果等診斷欄位；不向 client 重播真實傷害。</summary>
        [JsonExtensionData] public Dictionary<string, JsonElement>? Evidence { get; set; }

        // Nullable fields distinguish absent old metadata from a recorded zero.
        [JsonIgnore] public bool IsComplete =>
            Hit.HasValue && AnimationLock.HasValue && SpellId.HasValue
            && AnimationVariation.HasValue && ActionType.HasValue && Flags.HasValue
            && AnimationTarget.ValueKind != JsonValueKind.Undefined
            && TargetCount is >= 0 and <= byte.MaxValue && Targets.Count == TargetCount
            && TargetsComplete != false && EffectsComplete != false
            && Rotation.HasValue && HasPosition.HasValue
            && (HasPosition == false || (X.HasValue && Y.HasValue && Z.HasValue));
    }

    /// <summary>範圍提示（Omen）的 sheet id 與**原始** path；path 不做任何改寫。</summary>
    public sealed class OmenReference
    {
        [JsonPropertyName("id")] public uint? Id { get; set; }
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonIgnore] public bool IsComplete => Id.HasValue && Path != null && (Id == 0 || Path.Length != 0);
    }

    /// <summary>
    /// 一個 action 的技術形狀 metadata。**來源是錄影當下 runtime Lumina 查表**
    /// （<see cref="Source"/>＝<c>"lumina"</c>），不是封包原始形狀的真值——
    /// 它用來說明「這招是什麼形狀／提示長什麼樣」，不參與傷害或狀態結算。
    ///
    /// 缺席不能當成已觀察到的空值；空名稱與 Omen id=0 則是合法的原生資料。
    /// 轉換器只會寫入它實際觀察到的 action。
    /// </summary>
    public sealed class ActionMetadataEntry
    {
        [JsonPropertyName("action")] public uint Action { get; set; }
        /// <summary>Lumina 的 Action 名稱。**空字串合法**——原生無名 helper 的 row 就是空的。</summary>
        [JsonPropertyName("name")] public string? Name { get; set; }
        /// <summary>metadata 來源標記；目前只有 <c>"lumina"</c>。</summary>
        [JsonPropertyName("source")] public string Source { get; set; } = "";
        [JsonPropertyName("castType")] public byte? CastType { get; set; }
        [JsonPropertyName("effectRange")] public float? EffectRange { get; set; }
        [JsonPropertyName("xAxisModifier")] public float? XAxisModifier { get; set; }
        [JsonPropertyName("omen")] public OmenReference? Omen { get; set; }
        [JsonPropertyName("omenAlt")] public OmenReference? OmenAlt { get; set; }

        [JsonIgnore] public bool IsComplete =>
            Action != 0 && Name != null
            && string.Equals(Source, "lumina", StringComparison.Ordinal)
            && CastType.HasValue && EffectRange.HasValue && float.IsFinite(EffectRange.Value)
            && XAxisModifier.HasValue && float.IsFinite(XAxisModifier.Value)
            && Omen is { IsComplete: true } && OmenAlt is { IsComplete: true };
    }

    /// <summary>
    /// 錄影當下的擷取條件。缺席＝那份錄影沒寫（舊檔），**不是**預設值：
    /// 轉換器不回填，所以這裡也不給 fallback。
    /// </summary>
    public sealed class CaptureInfo
    {
        /// <summary>時間軸時基；<c>"monotonic"</c>＝單調 Stopwatch。</summary>
        [JsonPropertyName("timebase")] public string? Timebase { get; set; }
        /// <summary>快照取樣間隔（秒）。</summary>
        [JsonPropertyName("snapshotInterval")] public float? SnapshotInterval { get; set; }
        /// <summary>擷取方式或缺口；sampled 不等於完整事件生命週期。</summary>
        [JsonPropertyName("coverage")] public Dictionary<string, string>? Coverage { get; set; }
    }

    public sealed class CaptureEvidenceInfo
    {
        [JsonPropertyName("timebase")] public string Timebase { get; set; } = "";
        [JsonPropertyName("replay")] public bool Replay { get; set; }
        [JsonPropertyName("initial")] public List<JsonElement> Initial { get; set; } = [];
        [JsonPropertyName("events")] public List<JsonElement> Events { get; set; } = [];
    }

    /// <summary>場地識別。錄影要能當場景跑就必須有這個欄位（territory／原點／半徑）。</summary>
    public sealed class ZoneInfo
    {
        [JsonPropertyName("territory")] public uint Territory { get; set; }
        /// <summary>場景原點的**世界**座標 [x,y,z]。場景本地座標全部相對於它。</summary>
        [JsonPropertyName("center")] public float[] Center { get; set; } = [];
        [JsonPropertyName("radius")] public float Radius { get; set; } = 20f;
        [JsonPropertyName("level")] public byte Level { get; set; } = 100;
        [JsonPropertyName("phase")] public string Phase { get; set; } = "";
    }

    public sealed class CompletionInfo
    {
        [JsonPropertyName("complete")] public bool Complete { get; set; }
        [JsonPropertyName("t")] public float Time { get; set; }
        [JsonPropertyName("evidence")] public List<string> Evidence { get; set; } = [];
    }

    public sealed class IntegrityInfo
    {
        [JsonPropertyName("status")] public string Status { get; set; } = "";
        [JsonPropertyName("complete")] public bool? Complete { get; set; }
    }

    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("anchorBase")] public uint AnchorBase { get; set; }
    [JsonPropertyName("events")] public List<Event> Events { get; set; } = new();
    /// <summary>保留原始時間／ID／payload 的擷取證據；不得排入原生重播。</summary>
    [JsonPropertyName("captureEvidence")] public CaptureEvidenceInfo? CaptureEvidence { get; set; }
    /// <summary>只供還原與缺口診斷；action已生成的VFX不得再無條件重播一次。</summary>
    [JsonPropertyName("visualEvidence")] public List<JsonElement> VisualEvidence { get; set; } = [];

    /// <summary>資料格式版本。**缺席＝1**，走與現況逐行相同的舊路徑（AC6）。</summary>
    [JsonPropertyName("schema")] public int Schema { get; set; } = 1;
    /// <summary>
    /// Converter integrity envelope.  Explicitly incomplete/diagnostic files
    /// are never playable; old v1/v2 files omit the fields and retain legacy behavior.
    [JsonPropertyName("completion")] public CompletionInfo? Completion { get; set; }
    [JsonPropertyName("integrity")] public IntegrityInfo? Integrity { get; set; }
    [JsonPropertyName("complete")] public bool? Complete { get; set; }
    [JsonPropertyName("partial")] public bool Partial { get; set; }
    [JsonPropertyName("diagnosticOnly")] public bool DiagnosticOnly { get; set; }

    /// <summary>錄這份資料的錄影機版本（start 事件的 plugin 欄）。缺欄時用來分辨「沒發生」與「當時沒錄」。</summary>
    // ⚠️ 型別必須是 int：`tools/build-scenario.py` 寫的是數字（`tests/test_build_scenario.py` 鎖著），
    // 這裡曾宣告 string ⇒ System.Text.Json 對每一份 v2 檔拋 JsonException、Load 回 null、
    // 註冊端靜默跳過——P-098 的全部場景在遊戲裡一份都沒出現過（健檢 2026-09-05 M1）。
    [JsonPropertyName("recorderVer")] public int RecorderVer { get; set; } = 1;

    /// <summary>該份錄影具備哪些能力。缺項＝資料天生就少那一塊，重錄才補得回來。</summary>
    [JsonPropertyName("caps")] public Dictionary<string, bool> Caps { get; set; } = new();

    [JsonPropertyName("zone")] public ZoneInfo? Zone { get; set; }

    /// <summary>錄影當下的場地標點，每筆 [slot, x, z]（場景本地座標）。</summary>
    [JsonPropertyName("waymarks")] public List<float[]> Waymarks { get; set; } = new();

    /// <summary>
    /// 轉換器實際觀察到的 action 的技術 metadata，鍵＝action id 的十進位字串
    /// （JSON 物件的鍵只能是字串）。**只有轉換器真的看到那一招才會在裡面**——
    /// 空的或缺這一招＝該份錄影沒有這塊資料（`caps["actionMetadata"]` 會是 false），
    /// 不可當成「這招沒有形狀」。查表一律走 <see cref="MetadataFor"/>。
    /// </summary>
    [JsonPropertyName("actionMetadata")]
    public Dictionary<string, ActionMetadataEntry> ActionMetadata { get; set; } = new();

    /// <summary>錄影當下的擷取條件（單調時基／快照間隔）；缺席＝舊錄影沒寫。</summary>
    [JsonPropertyName("capture")] public CaptureInfo? Capture { get; set; }

    /// <summary>
    /// 這一招的 metadata；沒有（或鍵與 <see cref="ActionMetadataEntry.Action"/> 不一致）
    /// 就回 null。鍵與內容不一致時**不猜**——查不到才是誠實的答案。
    /// </summary>
    public ActionMetadataEntry? MetadataFor(uint action) =>
        ActionMetadata.TryGetValue(action.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                   out var meta)
        && meta != null && meta.Action == action
            ? meta
            : null;

    /// <summary>載入時算出來的缺項能力，供場景在 Run 時提示使用者「重錄可補」。</summary>
    [JsonIgnore] public IReadOnlyList<string> MissingCaps { get; private set; } = [];

    public IEnumerable<Event> Of(string kind) => Events.Where(e => e.Kind == kind);

    /// <summary>資料檔目錄：跟著 DLL 走（csproj 有設 CopyToOutputDirectory）。</summary>
    internal static string DataDir =>
        Path.Combine(Plugin.PluginInterface.AssemblyLocation.Directory?.FullName ?? ".", "Data");

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static bool ValidPoint(float[] point) =>
        point is { Length: >= 2 and <= 5 } && point.All(Finite);

    private static bool ValidateEvent(Event e)
    {
        if (string.IsNullOrWhiteSpace(e.Kind) || !Finite(e.T))
            return false;
        if (e.Until is { } until && !Finite(until))
            return false;
        if (e.CastSeconds < 0f || !Finite(e.CastSeconds)
            || e.Duration < 0f || !Finite(e.Duration)
            || e.Count < 0 || e.Stacks < 0)
            return false;
        if (e.Hit is { } hit && !Finite(hit))
            return false;
        if (!Finite(e.X) || !Finite(e.Y) || !Finite(e.Z))
            return false;
        if (e.Positions is null || e.Positions.Any(p => !ValidPoint(p)))
            return false;
        if (e.ActorName is { Length: > 256 })
            return false;
        if (e.Scale is { } scale && (!Finite(scale) || scale < 0f))
            return false;
        if (e.HitboxRadius is { } hitbox && (!Finite(hitbox) || hitbox < 0f))
            return false;
        if (e.Kind is "actorstate" or "npcmove" && e.Id <= 0)
            return false;
        return true;
    }

    private static bool ValidateTimeline(RecordedTimeline t)
    {
        if (t.Events.Any(e => !ValidateEvent(e)))
            return false;
        if (t.Zone?.Center is { Length: > 0 } center
            && (center.Length != 3 || center.Any(v => !Finite(v))))
            return false;
        if (t.Completion is { } completion)
        {
            if (!Finite(completion.Time) || completion.Time < 0f)
                return false;
            if (completion.Complete && completion.Evidence.Count == 0)
                return false;
        }
        if (t.Caps.GetValueOrDefault("actionMetadata"))
        {
            var hasAction = false;
            foreach (var e in t.Events)
            {
                if (e.Kind != "cast") continue;
                hasAction = true;
                if (t.MetadataFor(e.Action) is not { IsComplete: true }) return false;
            }
            if (!hasAction) return false;
        }
        // 數值欄與事件同一把尺：NaN／Inf 代表這份檔壞了，不是「未知」。
        if (t.ActionMetadata.Values.Any(
                m => m != null
                     && ((m.EffectRange is { } range && !Finite(range))
                         || (m.XAxisModifier is { } modifier && !Finite(modifier)))))
            return false;
        if (t.Capture?.SnapshotInterval is { } interval && (!Finite(interval) || interval <= 0f))
            return false;
        return true;
    }

    /// <summary>
    /// 載入一份場景資料。**載入失敗回 null，呼叫端必須擋下啟動**——
    /// 沒有資料就沒有機制，讓它安靜地跑一個空場景比直接報錯更糟。
    /// </summary>
    public static RecordedTimeline? Load(string fileName)
    {
        var bytes = ReadBytes(fileName);
        return bytes == null ? null : Parse(fileName, bytes);
    }

    /// <summary>
    /// 載入並同時封存這一次讀到的 bytes 身分（大寫 SHA256）。
    ///
    /// 由來（BLIND-ASTRA-05）：prepare 時核准一份 hash、commit 時再 <see cref="Load"/> 一次，
    /// 中間檔案被轉換器重新輸出就會執行**沒被核准過的資料**，而資源集合不變、
    /// ResourceMismatch 攔不到。所以解析、驗證與 hash 一律出自**同一次讀取的同一份 bytes**，
    /// 呼叫端把回傳的 snapshot 固定在該次 run 上消費（<see cref="RecordedScenario.RunPrepared"/>）。
    ///
    /// 失敗（找不到、IO、JSON、封口未完成、數值不合法）一律回 null＝fail-closed。
    /// 普通 <see cref="Load"/> 不算 hash——單人入口沒有這個需求。
    /// </summary>
    public static RecordedTimelineSnapshot? LoadSnapshot(string fileName)
    {
        var bytes = ReadBytes(fileName);
        if (bytes == null) return null;
        var timeline = Parse(fileName, bytes);
        return timeline == null
            ? null
            : new RecordedTimelineSnapshot(fileName, Convert.ToHexString(SHA256.HashData(bytes)), timeline);
    }

    private static byte[]? ReadBytes(string fileName)
    {
        var path = Path.Combine(DataDir, fileName);
        try
        {
            if (File.Exists(path))
                return File.ReadAllBytes(path);
            Core.CrashTrace.Log($"[資料] 找不到場景資料 {path}");
            return null;
        }
        catch (Exception ex)
        {
            Core.CrashTrace.Log($"[資料] 讀取 {fileName} 失敗：{ex.Message}");
            return null;
        }
    }

    /// <summary>解析＋驗證既有規則（來源資料不變時逐條與原本相同）。</summary>
    private static RecordedTimeline? Parse(string fileName, byte[] bytes)
    {
        try
        {
            // UTF-8 BOM 在 byte 解析路徑上是「無效的值起頭」；ReadAllText 以前會吃掉它。
            var body = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
                ? new ReadOnlySpan<byte>(bytes, 3, bytes.Length - 3)
                : new ReadOnlySpan<byte>(bytes);
            var t = JsonSerializer.Deserialize<RecordedTimeline>(body);
            if (t == null || t.Events.Count == 0)
            {
                Core.CrashTrace.Log($"[資料] {fileName} 解析後沒有事件");
                return null;
            }
            if (t.Partial || t.DiagnosticOnly || t.Complete == false
                || (t.Integrity != null
                    && (t.Integrity.Complete == false
                        || (!string.Equals(t.Integrity.Status, "complete", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(t.Integrity.Status, "unknown", StringComparison.OrdinalIgnoreCase)))))
            {
                Core.CrashTrace.Log($"[資料] 拒絕載入未完成／診斷場景 {fileName}："
                                  + "converter integrity 未封口");
                return null;
            }
            if (!ValidateTimeline(t))
            {
                Core.CrashTrace.Log($"[資料] 拒絕載入含無效數值／座標的場景 {fileName}");
                return null;
            }
            t.MissingCaps = t.Schema < 2
                ? []      // v1 檔沒有 caps 概念，列一整排「缺」只是噪音
                : KnownCaps.Where(c => !t.Caps.GetValueOrDefault(c)).ToList();
            Core.CrashTrace.Log($"[資料] 載入 {fileName}：{t.Events.Count} 筆事件（來源錄影 {t.Source}）"
                              + $"，schema {t.Schema}"
                              + (t.Schema < 2 ? "：沿用舊行為" : $"，錄影機 {t.RecorderVer}")
                              + (t.MissingCaps.Count > 0 ? $"，缺能力 {string.Join("／", t.MissingCaps)}" : ""));
            return t;
        }
        catch (Exception ex)
        {
            Core.CrashTrace.Log($"[資料] 載入 {fileName} 失敗：{ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// 一份**已核准**的錄影：<c>Sha256</c>（大寫 hex）與 <c>Timeline</c> 出自同一次讀取的
/// 同一份 bytes，之後 Data 檔怎麼改都不影響已交出的這份。<c>DataFile</c> 是這份 snapshot
/// 對應的檔名，消費端（<see cref="RecordedScenario.RunPrepared"/>）必須核對。
/// </summary>
public sealed record RecordedTimelineSnapshot(string DataFile, string Sha256, RecordedTimeline Timeline);
