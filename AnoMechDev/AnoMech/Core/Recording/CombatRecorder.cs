using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using AnoMech.Pointers;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;

namespace AnoMech.Core.Recording;

/// <summary>
/// 真副本錄影機：保存封包與客戶端狀態；擷取覆蓋以 start.coverage 為準。
///
/// 為什麼要它：FFLogs 給得到「什麼時候放了什麼招」，但**給不到座標**，也給不到
/// BNpcBase（決定模型）、頭標、連線、塔的位置、場地 MapEffect。這些只有在
/// client 端當場錄才有。兩者互補——FFLogs 對時間軸，這支對空間與 id。
///
/// id 有兩種、兩種都要記：`snap` 走 GameObjectId，封包（cast／control）走 EntityId。
/// 只記一種就無法把「誰放的招」對到「誰站在哪」——obj 事件同時帶兩者當作對照表。
///
/// 錄什麼（全部帶相對秒數）：
///   spawn / despawn  物件生滅（BNpcBase、BNpcName、座標、朝向、半徑、TimelineState）
///   cast             施法開始（actionId、詠唱長度、目標、施法者座標與朝向）
///   control          ActorControl（頭標 / 連線 / tether / director 類事件）
///   mapeffect        場地外觀變化（就是換階段場景的那個機制）
///   status           玩家與隊友身上的 buff/debuff 增減
///   snap             每 100ms 一次的全體座標快照（站位就是從這裡還原）
///
/// 安全性：所有 hook 一律呼叫 Original，detour 內全程 try/catch——
/// 錄影是診斷工具，**絕不能因為它讓遊戲出事**。
/// </summary>
internal sealed unsafe partial class CombatRecorder : IDisposable
{
    private const float SnapshotIntervalSeconds = 0.1f;
    private const int MaximumDirectorDataBytes = 64 * 1024;

    private readonly Hook<PacketDispatcherPointers.HandleActorCastPacketDelegate>? castHook;
    private readonly Hook<PacketDispatcherPointers.HandleActorControlPacketDelegate>? controlHook;
    private readonly Hook<PacketDispatcherPointers.HandleSpawnObjectPacketDelegate>? spawnHook;
    private readonly Hook<ReceiveActionEffectDelegate>? effectHook;
    private readonly Hook<Pointers.EventFrameworkPointers.SetDirectorDataDelegate>? dirDataHook;
    // 特效三通道（2026-08-21 補，維護者：「特效等等都必須要抓」）。三個 sig 都是
    // 模擬端自己在呼叫的 TC 已驗證函式（VfxFunctions），拿來掛 hook 零新增風險。
    private readonly Hook<Pointers.VfxDataPointers.CreateActorCharacterVfxDelegate>? actorVfxHook;
    private readonly Hook<Pointers.VfxObjectPointers.CreateDelegate>? staticVfxHook;
    private readonly Hook<Pointers.VfxDataPointers.DtorDelegate>? actorVfxDtorHook;
    private readonly Hook<Pointers.VfxObjectPointers.UpdateDelegate>? staticVfxUpdateHook;
    private Hook<CleanupRenderDelegate>? staticVfxCleanupHook;
    private readonly Hook<Pointers.VfxContainerPointers.SetTetherDelegate>? tetherHook;

    // ActionEffect＝招式**實際落下**。瞬發技（無詠唱條）永遠不會經過 HandleActorCastPacket，
    // 只有這條路徑才錄得到。2026-08-19 實證：牙尾連旋讀完後真正造成傷害的
    // 銳牙大迴旋(26389)／邪尾大迴旋(26390) 在整份錄影的 cast 事件裡**一次都沒有**，
    // 因為它們是瞬發 —— 少了這個 hook，所有瞬發機制在資料裡都不存在。
    // VfxObjectCompat.CleanupRender has the verified vtable[1] signature. The hook is
    // installed lazily from a live VfxObject rather than guessing a standalone native sig.
    private unsafe delegate void CleanupRenderDelegate(
        FFXIVClientStructs.FFXIV.Client.Graphics.Scene.VfxObject* thisPtr);

    private readonly VfxCaptureTracker vfxTracker = new();
    private bool cleanupRenderHookAttempted;
    private bool vfxTrackingCappedLogged;
    private int captureSeq;
    // Native VFX may arrive on another thread or after a new session started.
    // A cause is valid only on the originating thread and for the same writer.
    [ThreadStatic]
    private static (CombatRecorder? Owner, RecordingWriter? Writer, int Sequence) activeCauseSeq;
    private int? CurrentCauseSequence =>
        ReferenceEquals(activeCauseSeq.Owner, this) && ReferenceEquals(activeCauseSeq.Writer, writer)
            ? activeCauseSeq.Sequence : null;
    // VfxObjectCompat proves CleanupRender at vtable[1]; the partial hook code reads this
    // address only while a live object is in hand.
    private const int CleanupRenderVtblIndex = 1;
    private unsafe delegate void ReceiveActionEffectDelegate(
        uint casterEntityId, Character* caster, Vector3* targetPos,
        ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects,
        GameObjectId* targetIds);

    private RecordingWriter? writer;
    private readonly object recordingGate = new();
    private long startedAt;
    private float sinceSnapshot;
    private readonly Dictionary<ulong, string> knownObjects = new();
    private readonly Dictionary<ulong, RecordedObjectState> knownObjectStates = new();
    private readonly Dictionary<ulong, Dictionary<(uint StatusId, uint SourceId), RecordedStatus>> knownStatuses = new();
    private readonly Dictionary<(uint StatusId, uint SourceId), RecordedStatus> sampledStatuses = new();
    private readonly List<(uint StatusId, uint SourceId)> removedStatuses = new();

    private readonly record struct RecordedStatus(uint SourceId, ushort Param, float RemainingTime);

    public bool IsRecording
    {
        get
        {
            lock (recordingGate) return writer?.IsHealthy == true;
        }
    }

    public string? CurrentPath { get; private set; }
    public int EventCount { get; private set; }
    public string? LastError { get; private set; }
    public int WriteErrorCount { get; private set; }

    /// <summary>每類事件的計數。回傳快照，避免 UI 列舉時撞上 hook 執行緒的寫入。</summary>
    public IReadOnlyDictionary<string, int> Counts
    {
        get
        {
            lock (recordingGate) return new Dictionary<string, int>(counts);
        }
    }
    private readonly Dictionary<string, int> counts = new();

    /// <summary>三個 hook 各自掛上了沒。掛不上的那類事件會改由輪詢補（見 PollCasts）。</summary>
    public bool CastHookLive => castHook != null;
    public bool ControlHookLive => controlHook != null;
    public bool SpawnHookLive => spawnHook != null;

    /// <summary>
    /// 錄影資料的 schema 版本。轉換器靠它判斷這份錄影有沒有 eff／omenDelay／targetable
    /// 等後補欄位——**沒有版本號的錄影缺欄時零訊號**：轉換器照跑、場景照播、
    /// 畫面上看不出來（2026-09-02 稽核：FRU 8/28、8/30 與 M8S 8/30 三份全部缺欄且無人察覺）。
    /// </summary>
    public const int SchemaVersion = 3; // native header／VFX identity、transform、lifetime、cause

    /// <summary>每場錄影各 hook 的掛載／有效捕獲兩態。</summary>
    public IReadOnlyDictionary<string, (bool Live, bool Fired)> HookStates
    {
        get
        {
            lock (recordingGate)
            {
                return new Dictionary<string, (bool, bool)>
                {
                    ["cast"] = (castHook != null, hookFired.Contains("cast")),
                    ["control"] = (controlHook != null, hookFired.Contains("control")),
                    ["actorVfx"] = (actorVfxHook != null, hookFired.Contains("actorVfx")),
                    ["actorVfxDtor"] = (actorVfxDtorHook != null, hookFired.Contains("actorVfxDtor")),
                    ["staticVfx"] = (staticVfxHook != null, hookFired.Contains("staticVfx")),
                    ["staticVfxUpdate"] = (staticVfxUpdateHook != null, hookFired.Contains("staticVfxUpdate")),
                    ["staticVfxCleanup"] = (staticVfxCleanupHook != null, hookFired.Contains("staticVfxCleanup")),
                    ["spawn"] = (spawnHook != null, hookFired.Contains("spawn")),
                    ["effect"] = (effectHook != null, hookFired.Contains("effect")),
                    ["dirdata"] = (dirDataHook != null, hookFired.Contains("dirdata")),
                    ["tether"] = (tetherHook != null, hookFired.Contains("tether")),
                };
            }
        }
    }

    private readonly HashSet<string> hookFired = new();
    private int observedWriterErrorCount;

    private readonly record struct RecordedObjectState(
        bool Targetable, bool Visible, uint ModelChara, float Scale, float Hitbox,
        ulong WMain, ulong WOff, string Name, uint NameId);

    /// <summary>ActorControl 裡的接線類別（cat 35＝設定接線）計數。tether 自檢的對照組。</summary>
    private int controlCat35Count;

    public CombatRecorder()
    {
        castHook = TryHook(PacketDispatcherPointers.HandleActorCastPacket, CastDetour, nameof(castHook));
        controlHook = TryHook(PacketDispatcherPointers.HandleActorControlPacket, ControlDetour, nameof(controlHook));
        spawnHook = TryHook(PacketDispatcherPointers.HandleSpawnObjectPacket, SpawnDetour, nameof(spawnHook));
        try
        {
            var addr = (nint)ActionEffectHandler.MemberFunctionPointers.Receive;
            effectHook = Plugin.GameInterop.HookFromAddress<ReceiveActionEffectDelegate>(addr, EffectDetour);
            effectHook.Enable();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[CombatRecorder] ActionEffect hook 掛載失敗，瞬發技不會被錄到：{ex.Message}");
        }
        // SetDirectorData＝director 二進位資料流（2026-08-21 補）：階段／場景狀態的
        // 最後一個未錄通道——EnvControl 已實證只管眼球動畫，場地變換不在裡面。
        dirDataHook = TryHook(Pointers.EventFrameworkPointers.SetDirectorData, DirDataDetour, nameof(dirDataHook));
        actorVfxHook = TryHook(Pointers.VfxDataPointers.ActorVfxCreate, ActorVfxDetour, nameof(actorVfxHook));
        staticVfxHook = TryHook(Pointers.VfxObjectPointers.Create, StaticVfxDetour, nameof(staticVfxHook));
        actorVfxDtorHook = TryHook(Pointers.VfxDataPointers.Dtor, ActorVfxDtorDetour, nameof(actorVfxDtorHook));
        staticVfxUpdateHook = TryHook(Pointers.VfxObjectPointers.Update, StaticVfxUpdateDetour, nameof(staticVfxUpdateHook));
        tetherHook = TryHook(Pointers.VfxContainerPointers.SetTether, TetherDetour, nameof(tetherHook));
        // NPC 戰鬥台詞（2026-08-21 盤點補）：boss 喊話常是機制預告（「聚集吧——」），
        // 復刻演出要有。只收 NPC 對話類，玩家聊天一個字都不碰（隱私＋雜訊）。
        Plugin.ChatGui.ChatMessage += OnChatMessage;
    }

    private void OnChatMessage(Dalamud.Game.Text.XivChatType type, int timestamp,
                               ref Dalamud.Game.Text.SeStringHandling.SeString sender,
                               ref Dalamud.Game.Text.SeStringHandling.SeString message, ref bool isHandled)
    {
        try
        {
            if (writer == null) return;
            if (type is not (Dalamud.Game.Text.XivChatType.NPCDialogue
                          or Dalamud.Game.Text.XivChatType.NPCDialogueAnnouncements)) return;
            Write("chat", $"\"type\":{(ushort)type},\"who\":\"{Escape(sender.TextValue)}\","
                        + $"\"text\":\"{Escape(message.TextValue)}\"");
        }
        catch { }
    }


    private static string CStr(byte* p)
    {
        var n = 0;
        while (n < 128 && p[n] != 0) n++;
        return Encoding.UTF8.GetString(p, n);
    }

    private unsafe void DirDataDetour(
        FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework* thisPtr,
        FFXIVClientStructs.FFXIV.Client.Game.Event.EventId eventId,
        byte sequence, byte unknown, byte* buffer, ulong length)
    {
        try
        {
            CaptureDirectorData(eventId.Id, sequence, unknown, buffer, length);
        }
        catch (Exception ex)
        {
            Write("capture-gap", $"\"channel\":\"dirdata\",\"reason\":\"{Escape(ex.Message)}\"");
            CrashTrace.Log($"[錄影] dirdata 出錯（已忽略）：{ex.Message}");
        }
        finally
        {
            dirDataHook!.Original(thisPtr, eventId, sequence, unknown, buffer, length);
        }
    }

    private void CaptureDirectorData(uint eventId, byte sequence, byte unknown, byte* buffer, ulong length)
    {
        lock (recordingGate)
        {
            if (writer?.IsHealthy != true) return;
            var n = buffer == null ? 0 : (int)Math.Min(length, (ulong)MaximumDirectorDataBytes);
            var complete = (ulong)n == length;
            var hex = Convert.ToHexStringLower(new ReadOnlySpan<byte>(buffer, n));
            WriteHook("dirdata", "dirdata",
                $"\"eid\":{eventId},\"seq\":{sequence},\"u\":{unknown},\"len\":{length},"
              + $"\"capturedLength\":{n},\"complete\":{(complete ? "true" : "false")},"
              + $"\"truncated\":{(!complete ? "true" : "false")},\"hex\":\"{hex}\"");
            if (!complete)
                Write("capture-gap", $"\"channel\":\"dirdata\",\"reason\":\"{(buffer == null ? "null-buffer" : "payload-limit")}\","
                    + $"\"length\":{length},\"capturedLength\":{n},\"limit\":{MaximumDirectorDataBytes}");
        }
    }

    // 任何一個掛不上都只是少錄一類事件，不該讓整支插件失敗——所以個別 try/catch。
    private static Hook<T>? TryHook<T>(T target, T detour, string label) where T : Delegate
    {
        try
        {
            var addr = Marshal.GetFunctionPointerForDelegate(target);
            var hook = Plugin.GameInterop.HookFromAddress<T>(addr, detour);
            hook.Enable();
            return hook;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[CombatRecorder] {label} 掛載失敗，該類事件不會被錄到：{ex.Message}");
            return null;
        }
    }

    // ── 開始 / 結束 ───────────────────────────────────────────────────────

    private uint recordingTerritory;

    public void Start(string reason)
    {
        // 進副本會觸發兩次起錄（TerritoryChanged 一次、DutyStarted 再一次），
        // 同一個 territory 已經在錄就沿用，不重起。
        var territoryNow = Plugin.ClientState.TerritoryType;
        lock (recordingGate)
        {
            if (writer?.IsHealthy == true && recordingTerritory == territoryNow)
            {
                CrashTrace.Log($"[錄影] 已在錄同一個 territory（{territoryNow}），沿用（{reason}）");
                return;
            }

            StopLocked();
            ResetSessionLocked();

            RecordingWriter? opened = null;
            var territory = Plugin.ClientState.TerritoryType;
            var directory = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "recordings");
            var baseName = $"{DateTime.Now:yyyyMMdd-HHmmss}-t{territory}";
            var path = Path.Combine(directory, $"{baseName}.jsonl.inprogress");
            CurrentPath = path;
            try
            {
                Directory.CreateDirectory(directory);
                opened = OpenUniqueWriter(directory, baseName, out path);
                CurrentPath = path;
                writer = opened;
                startedAt = Stopwatch.GetTimestamp();
                recordingTerritory = territory;

                // start 本身也必須成功寫入，否則這場不應顯示成正在錄影。
                if (WriteCoreLocked(opened, "start",
                        $"\"reason\":\"{Escape(reason)}\",\"territory\":{territory},"
                      + $"\"ver\":{SchemaVersion},\"plugin\":\"{Escape(PluginVersion)}\",\"seal\":\"rename-v1\","
                      + $"\"timebase\":\"monotonic\",\"snapshotInterval\":{F(SnapshotIntervalSeconds)},"
                      + "\"coverage\":{\"actorAnimation\":\"sampled\",\"bgmState\":\"sampled\","
                      + "\"layoutIdentity\":\"sampled\",\"soundLifecycle\":\"unavailable\","
                      + "\"animationLifecycle\":\"unavailable\",\"layoutLifecycle\":\"unavailable\"}") == 0)
                {
                    CrashTrace.Log($"[錄影] 起錄標頭寫入失敗：{CurrentPath}");
                    return;
                }

                CrashTrace.Log($"[錄影] 開始：{CurrentPath}（{reason}）");
            }
            catch (Exception ex)
            {
                if (opened != null)
                {
                    opened.Close();
                    SyncWriterDiagnosticsLocked(opened);
                }
                writer = null;
                CurrentPath = path;
                RecordExternalErrorLocked(ex);
                ResetCaptureStateLocked();
                CrashTrace.Log($"[錄影] 開始失敗：{ex.Message}");
            }
        }
    }

    private static RecordingWriter OpenUniqueWriter(string directory, string baseName, out string path)
    {
        path = Path.Combine(directory, $"{baseName}.jsonl.inprogress");
        for (var suffix = 0; suffix < 10000; suffix++)
        {
            var finalizedPath = Path.Combine(directory, suffix == 0
                ? $"{baseName}.jsonl"
                : $"{baseName}-{suffix}.jsonl");
            path = finalizedPath + ".inprogress";
            if (File.Exists(finalizedPath) || Directory.Exists(finalizedPath)) continue;
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                                        4096, FileOptions.SequentialScan);
            }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path))
            {
                // Only an existing path is a collision. Permission, quota, and other I/O
                // errors are rethrown and become visible through LastError.
                continue;
            }

            try
            {
                var text = new StreamWriter(stream, new UTF8Encoding(false), 4096)
                {
                    // Keep each successful line immediately visible; Tick still calls Flush
                    // explicitly to surface stream errors at the normal 100ms boundary.
                    AutoFlush = true,
                };
                return new RecordingWriter(text);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        throw new IOException($"錄影檔名碰撞超過上限：{baseName}");
    }

    /// <summary>插件版號（AssemblyVersion）。寫進 start 事件當作「這份錄影錄得到什麼」的依據。</summary>
    private static string PluginVersion =>
        typeof(CombatRecorder).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

    public void Stop()
    {
        lock (recordingGate) StopLocked();
    }

    private void StopLocked()
    {
        var current = writer;
        if (current == null)
        {
            ResetCaptureStateLocked();
            return;
        }

        FlushActionMetadataLocked();
        if (!ReferenceEquals(writer, current)) return;

        // Flush before writing stop so a pending stream failure cannot be hidden by a
        // deceptively complete stop event. Stop.events is deliberately the pre-stop count.
        if (!current.TryFlush())
        {
            SyncWriterDiagnosticsLocked(current);
            FailWriterLocked(current);
            return;
        }

        var eventsBeforeStop = EventCount;
        var tetherCount = counts.GetValueOrDefault("tether");
        if (controlCat35Count > 0 && tetherCount == 0)
            CrashTrace.Log($"[錄影] ⚠ tether hook 掛上但整場沒觸發（cat35={controlCat35Count}）"
                         + "——接線資料只剩 ActorControl 一條證據，見 BACKLOG P-099");

        var stop = WriteCoreLocked(current, "stop",
            $"\"events\":{eventsBeforeStop},\"errors\":{WriteErrorCount},\"complete\":true");
        if (stop == 0 || !ReferenceEquals(writer, current)) return;

        var flushed = current.TryFlush();
        SyncWriterDiagnosticsLocked(current);
        var closed = current.Close();
        SyncWriterDiagnosticsLocked(current);
        writer = null;
        ResetCaptureStateLocked();

        if (!flushed || !closed)
        {
            CrashTrace.Log($"[錄影] ⚠ 封口失敗（{CurrentPath}）：{LastError}");
            return;
        }

        // Publish a sealed .jsonl only after every writer operation, including Close,
        // succeeded. A failed finalization remains .inprogress and converters reject it.
        try
        {
            var pendingPath = CurrentPath ?? throw new InvalidOperationException("錄影路徑遺失");
            var finalizedPath = pendingPath[..^".inprogress".Length];
            File.Move(pendingPath, finalizedPath); // Never overwrite a previous recording.
            CurrentPath = finalizedPath;
        }
        catch (Exception ex)
        {
            RecordExternalErrorLocked(ex);
            CrashTrace.Log($"[錄影] 封口發布失敗，保留未封口原始檔：{CurrentPath}");
            return;
        }

        CrashTrace.Log($"[錄影] 結束：{CurrentPath}，共 {eventsBeforeStop} 筆，完整=true");
    }

    private void ResetSessionLocked()
    {
        EventCount = 0;
        counts.Clear();
        LastError = null;
        WriteErrorCount = 0;
        observedWriterErrorCount = 0;
        hookFired.Clear();
        knownObjects.Clear();
        knownObjectStates.Clear();
        knownStatuses.Clear();
        knownActions.Clear();
        pendingActionMetadata.Clear();
        ResetStateCaptureLocked();
        lastPartySignature = "";
        lastWeather = 255;
        castingNow.Clear();
        lastWaymarkSig = "";
        targetNow.Clear();
        controlCat35Count = 0;
        vfxTracker.Reset();
        captureSeq = 0;
        activeCauseSeq = default;
        vfxTrackingCappedLogged = false;
        sinceSnapshot = 0f;
    }

    private void ResetCaptureStateLocked()
    {
        vfxTracker.Reset();
        knownActions.Clear();
        pendingActionMetadata.Clear();
        ResetStateCaptureLocked();
        captureSeq = 0;
        activeCauseSeq = default;
        sinceSnapshot = 0f;
    }

    private void RecordExternalErrorLocked(Exception ex)
    {
        WriteErrorCount++;
        LastError = $"{ex.GetType().Name}: {ex.Message}";
        CrashTrace.Log($"[錄影] I/O 錯誤：{LastError}");
    }

    private void SyncWriterDiagnosticsLocked(RecordingWriter current)
    {
        var errors = current.ErrorCount;
        if (errors > observedWriterErrorCount)
        {
            WriteErrorCount += errors - observedWriterErrorCount;
            observedWriterErrorCount = errors;
        }
        if (current.LastError != null) LastError = current.LastError;
    }

    private void FailWriterLocked(RecordingWriter failed)
    {
        SyncWriterDiagnosticsLocked(failed);
        if (ReferenceEquals(writer, failed)) writer = null;
        failed.Close();
        SyncWriterDiagnosticsLocked(failed);
        CrashTrace.Log($"[錄影] 寫入失敗，已停止錄影（{CurrentPath}）：{LastError}");
        ResetCaptureStateLocked();
    }

    // ── 每幀 ──────────────────────────────────────────────────────────────

    public void Tick(float deltaSeconds)
    {
        lock (recordingGate)
        {
            if (writer?.IsHealthy != true) return;
            try
            {
                if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0f) return;
                sinceSnapshot += deltaSeconds;
                if (sinceSnapshot < SnapshotIntervalSeconds) return;
                sinceSnapshot %= SnapshotIntervalSeconds;
                PollWeather();
                PollWaymarks();
                PollBgmState();
                PollLayoutState();
                PollTargets();
                PollCombo();
                TrackParty();
                PollCasts();
                Snapshot();
                FlushActionMetadataLocked();
                FlushRecording();
            }
            catch (Exception ex)
            {
                Write("capture-gap", $"\"channel\":\"snapshot\",\"reason\":\"{Escape(ex.Message)}\"");
                Plugin.Log.Warning($"[錄影] 快照失敗，已標記資料缺口：{ex.Message}");
            }
        }
    }

    private bool FlushRecording()
    {
        lock (recordingGate)
        {
            var current = writer;
            if (current?.IsHealthy != true) return false;
            if (current.TryFlush()) return true;
            SyncWriterDiagnosticsLocked(current);
            FailWriterLocked(current);
            return false;
        }
    }


    // ── 天氣（2026-08-21 補）：副本天氣由 director 下發、不在 WeatherRate 表，
    //    P3 場景外觀的天氣層只有執行期抓得到——變化時記一筆 id。
    private byte lastWeather = 255;

    private unsafe void PollWeather()
    {
        var wm = FFXIVClientStructs.FFXIV.Client.Game.WeatherManager.Instance();
        if (wm == null) return;
        var w = wm->GetCurrentWeather();
        if (w == lastWeather) return;
        lastWeather = w;
        Write("weather", $"\"id\":{w}");
        CrashTrace.Log($"[錄影] 天氣 → {w}");
    }

    private readonly HashSet<ulong> seenThisSnap = new();

    // ── 場地標點（2026-08-21 盤點補）：攻略站位以 A/B/C/D/1-4 為座標系，
    //    錄影裡的走位要對回攻略敘述（「A 塔」「1 標分攤」）就得知道標點在哪。
    //    只在變化時寫一筆（開場擺好後整場不動是常態）。
    private string lastWaymarkSig = "";

    private void PollWaymarks()
    {
        var mc = FFXIVClientStructs.FFXIV.Client.Game.UI.MarkingController.Instance();
        if (mc == null) return;
        var sb = new StringBuilder("\"w\":[");
        for (int i = 0; i < 8; i++)
        {
            ref var m = ref mc->FieldMarkers[i];
            if (i > 0) sb.Append(',');
            if (m.Active)
                sb.Append($"[{i},{F(m.Position.X)},{F(m.Position.Y)},{F(m.Position.Z)}]");
            else
                sb.Append($"[{i}]");
        }
        sb.Append(']');
        var sig = sb.ToString();
        if (sig == lastWaymarkSig) return;
        lastWaymarkSig = sig;
        Write("waymark", sig);
    }

    // ── 敵方目標（2026-08-21 盤點補）：boss／小怪「正在瞄誰」＝順劈方向、
    //    引導判定、坦克互換的直接證據——朝向快照只給臉朝哪，不給鎖定誰。
    private readonly Dictionary<ulong, ulong> targetNow = new();

    private void PollTargets()
    {
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IBattleNpc npc) continue;
            var tid = npc.TargetObjectId;
            if (targetNow.GetValueOrDefault(npc.GameObjectId) == tid) continue;
            targetNow[npc.GameObjectId] = tid;
            Write("target", $"\"id\":{npc.GameObjectId},\"target\":{tid}");
        }
    }

    // ── 連段欄位觀測（2026-08-22）：模擬區連段燈修不亮，需要「真伺服器環境下
    //    ActionManager.Combo 讀值」當對照組——若真戰鬥中這裡也讀不到連段變化，
    //    ＝TC CS 該欄位位移錯，模擬端就別再往那個位置想。
    private uint lastComboAction = uint.MaxValue;

    private unsafe void PollCombo()
    {
        var am = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
        if (am == null) return;
        var a = am->Combo.Action;
        if (a == lastComboAction) return;
        lastComboAction = a;
        Write("combo", $"\"action\":{a},\"timer\":{F(am->Combo.Timer)}");
    }

    private void Snapshot()
    {
        var sb = new StringBuilder();
        sb.Append("\"o\":[");
        var first = true;
        seenThisSnap.Clear();
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj.Address == IntPtr.Zero) continue;
            seenThisSnap.Add(obj.GameObjectId);
            var p = obj.Position;
            var nameId = obj is IBattleNpc bn ? bn.NameId : 0u;
            var owner = obj is IBattleNpc bn2 ? bn2.OwnerId : 0u;
            var lvl = obj is IBattleChara bc ? bc.Level : (byte)0;
            var job = obj is IPlayerCharacter pc ? pc.ClassJob.RowId : 0u;
            var isMe = obj.GameObjectId == (Plugin.ObjectTable.LocalPlayer?.GameObjectId ?? 0);

            // Appearance and draw state are sampled every snapshot so an NPC
            // actorstate row can represent targetability/visibility transitions.
            // 外觀／武器使用 TC 提供的完整 ModelId.Value；手動拼接會漏雙染色。
            var go = (GameObject*)obj.Address;
            var scale = go != null ? go->Scale : 0f;
            var hitbox = go != null ? go->HitboxRadius : 0f;
            var visible = go != null && go->DrawObject != null && go->DrawObject->IsVisible;
            uint modelChara = 0;
            ulong wMain = 0, wOff = 0;
            byte targetable = 0;
            if (obj is IBattleChara && obj.Address != nint.Zero)
            {
                var nbc = (BattleChara*)obj.Address;
                modelChara = (uint)nbc->ModelContainer.ModelCharaId;
                wMain = nbc->DrawData.Weapon(DrawDataContainer.WeaponSlot.MainHand).ModelId.Value;
                wOff = nbc->DrawData.Weapon(DrawDataContainer.WeaponSlot.OffHand).ModelId.Value;
                // 只有 IsTargetable 位元代表「真的可以選取」。整個 TargetableStatus != 0
                // 會把「退場中／暫時不可選取但還沒 despawn」的演員錄成可選取，而重播端的
                // SetTargetable(true) 又會把那個位元加回去 ⇒ 不可選取的轉換整段消失，
                // 而畫面上只是「這隻本來就能點」，看不出是錄影失真（BLIND-ASTRA-06）。
                targetable = (byte)((nbc->Character.GameObject.TargetableStatus
                                     & ObjectTargetableFlags.IsTargetable) != 0 ? 1 : 0);
            }

            // 新物件先補一筆 meta，之後快照只帶座標，檔案才不會爆。
            if (!knownObjects.ContainsKey(obj.GameObjectId))
            {
                knownObjects[obj.GameObjectId] = obj.Name.TextValue;
                Write("obj", $"\"id\":{obj.GameObjectId},\"entityId\":{obj.EntityId},\"kind\":{(int)obj.ObjectKind},"
                           + $"\"baseId\":{obj.BaseId},\"nameId\":{nameId},\"lv\":{lvl},\"owner\":{owner},"
                           + $"\"job\":{job},\"me\":{(isMe ? "true" : "false")},"
                           + $"\"scale\":{F(scale)},\"hitbox\":{F(hitbox)},\"modelChara\":{modelChara},"
                           + $"\"wMain\":{wMain},\"wOff\":{wOff},\"targetable\":{targetable},"
                           + $"\"visible\":{(visible ? "true" : "false")},"
                           + $"\"name\":\"{Escape(obj.Name.TextValue)}\","
                           + $"\"x\":{F(p.X)},\"y\":{F(p.Y)},\"z\":{F(p.Z)},\"r\":{F(obj.Rotation)}");
            }

            // NPC targetability/visibility and appearance can change without a
            // new object row. Emit only transitions for deterministic replay.
            if (obj is IBattleNpc)
            {
                var state = new RecordedObjectState(
                    targetable != 0, visible, modelChara, scale, hitbox, wMain, wOff,
                    obj.Name.TextValue, nameId);
                if (!knownObjectStates.TryGetValue(obj.GameObjectId, out var old) || old != state)
                {
                    knownObjectStates[obj.GameObjectId] = state;
                    Write("actorstate", $"\"id\":{obj.GameObjectId},\"nameId\":{state.NameId},"
                               + $"\"targetable\":{(state.Targetable ? "true" : "false")},"
                               + $"\"visible\":{(state.Visible ? "true" : "false")},"
                               + $"\"modelChara\":{state.ModelChara},\"scale\":{F(state.Scale)},"
                               + $"\"hitbox\":{F(state.Hitbox)},\"wMain\":{state.WMain},"
                               + $"\"wOff\":{state.WOff},\"name\":\"{Escape(state.Name)}\"");
                }
            }
            if (!first) sb.Append(',');
            first = false;
            sb.Append($"[{obj.GameObjectId},{F(p.X)},{F(p.Y)},{F(p.Z)},{F(obj.Rotation)}]");

            if (obj is IBattleChara chara)
            {
                CaptureActorAnimation(obj.GameObjectId, obj.EntityId, (Character*)obj.Address);
                TrackStatuses(chara);
            }
        }
        sb.Append(']');
        Write("snap", sb.ToString());

        // 消失偵測（2026-08-21 補，維護者：「小怪…都必須要抓」）：上一格還在、這一格
        // 不在 ObjectTable ＝despawn。記下來重播才知道小怪「何時該收」；同時把 meta
        // 與所有以 GameObjectId 為 key 的增量快取清掉——同 gid 若再出現（重生）必須
        // 重發 obj、target、castpoll、animation、status+，否則新生命週期會沿用舊狀態。
        // 精度 ±100ms 夠用。
        knownObjects.Keys.Where(id => !seenThisSnap.Contains(id)).ToList().ForEach(id =>
        {
            Write("despawn", $"\"id\":{id}");
            knownObjects.Remove(id);
            knownObjectStates.Remove(id);
            knownAnimations.Remove(id);
            knownStatuses.Remove(id);
            targetNow.Remove(id);
            castingNow.Remove(id);
        });
    }

    private string lastPartySignature = "";

    // 小隊名冊：座標要還原成「哪個角色槽位站哪」，就得知道小隊順序與各人職業。
    // 只在名冊變動時寫一筆（換人／換職業／進出小隊），不必每格重複。
    private void TrackParty()
    {
        var sb = new StringBuilder("\"m\":[");
        for (int i = 0; i < Plugin.PartyList.Length; i++)
        {
            var m = Plugin.PartyList[i];
            if (m == null) continue;
            if (i > 0) sb.Append(',');
            sb.Append($"[{i},{m.EntityId},{m.ClassJob.RowId},\"{Escape(m.Name.TextValue)}\"]");
        }
        sb.Append(']');
        var sig = sb.ToString();
        if (sig == lastPartySignature) return;
        lastPartySignature = sig;
        Write("party", sig);
    }

    private readonly Dictionary<ulong, uint> castingNow = new();

    // 不依賴 hook 的施法偵測：直接讀 ObjectTable 的詠唱狀態。
    // 為什麼要有第二條路：三個 hook 的 signature 雖然離線驗過是正確函式，
    // 但**在台服從未實際執行過**。萬一其中一個在 runtime 掛不上或行為不同，
    // 靠 hook 的那類事件就會整場空白——而那是進副本打完才會發現的。
    // 輪詢的精度是 ±100ms（比封包差），但它「一定拿得到」。
    private void PollCasts()
    {
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IBattleChara c) continue;
            var was = castingNow.GetValueOrDefault(c.GameObjectId);
            var now = c.IsCasting ? c.CastActionId : 0u;
            if (now == was) continue;
            castingNow[c.GameObjectId] = now;
            if (now == 0) continue;
            QueueActionMetadataLocked(now);
            var p = c.Position;
            Write("castpoll", $"\"id\":{c.GameObjectId},\"action\":{now},"
                            + $"\"total\":{F(c.TotalCastTime)},\"cur\":{F(c.CurrentCastTime)},"
                            + $"\"target\":{c.CastTargetObjectId},"
                            + $"\"x\":{F(p.X)},\"y\":{F(p.Y)},\"z\":{F(p.Z)},\"r\":{F(c.Rotation)}");
        }
    }

    private const float StatusRenewalEpsilon = 0.25f;

    private static bool StatusDurationRenewed(float oldDuration, float currentDuration)
    {
        if (oldDuration == currentDuration) return false;
        // Zero is the boundary between an active countdown and expiration /
        // a permanent-style slot.  Only entering a non-zero duration is a
        // renewal; reaching zero is not.
        if (oldDuration == 0f || currentDuration == 0f)
            return oldDuration == 0f && currentDuration != 0f;
        // Proc timers are negative and count toward zero.  A refresh therefore
        // increases the magnitude, while ordinary countdown progress reduces it.
        // A sign change is itself meaningful and must not be erased by Abs.
        if ((oldDuration < 0f) != (currentDuration < 0f))
            return true;
        return MathF.Abs(currentDuration) > MathF.Abs(oldDuration) + StatusRenewalEpsilon;
    }

    private void TrackStatuses(IBattleChara chara)
    {
        if (!knownStatuses.TryGetValue(chara.GameObjectId, out var previous))
            knownStatuses[chara.GameObjectId] = previous = new Dictionary<(uint StatusId, uint SourceId), RecordedStatus>();

        sampledStatuses.Clear();
        foreach (var st in chara.StatusList)
        {
            if (st.StatusId != 0)
                sampledStatuses[(st.StatusId, st.SourceId)] = new RecordedStatus(st.SourceId, st.Param, st.RemainingTime);
        }

        // Remove the old source before adding a new one at the same timestamp.
        removedStatuses.Clear();
        foreach (var key in previous.Keys)
            if (!sampledStatuses.ContainsKey(key)) removedStatuses.Add(key);
        foreach (var key in removedStatuses)
        {
            Write("status-", $"\"id\":{chara.GameObjectId},\"status\":{key.StatusId},\"src\":{key.SourceId}");
            previous.Remove(key);
        }

        foreach (var (key, current) in sampledStatuses)
        {
            if (!previous.TryGetValue(key, out var old))
            {
                previous[key] = current;
                Write("status+", StatusBody(chara.GameObjectId, key.StatusId, current));
                continue;
            }

            var renewed = StatusDurationRenewed(old.RemainingTime, current.RemainingTime);
            previous[key] = current;
            if (old.Param != current.Param || renewed)
                Write("status~", StatusBody(chara.GameObjectId, key.StatusId, current));
        }
    }


    private static string StatusBody(ulong objectId, uint statusId, RecordedStatus status)
        => $"\"id\":{objectId},\"status\":{statusId},\"dur\":{F(status.RemainingTime)},"
         + $"\"stacks\":{status.Param},\"param\":{status.Param},\"src\":{status.SourceId}";

    // ── Hook detours（一律先錄再放行；出錯不得影響遊戲）────────────────────

    private void CastDetour(uint entityId, ActorCastPacket* packet)
    {
        var previousCauseSeq = activeCauseSeq;
        try
        {
            if (writer != null && packet != null)
            {
                try
                {
                    // omenDelay＝**地上的範圍提示什麼時候才出現**（封包欄位，單位 0.1s）。
                    // 判定時刻另有 effect 事件；兩者合起來才完整回答「什麼時候出現、什麼時候判定」。
                    WriteCause("cast", "cast",
                        $"\"src\":{entityId},\"action\":{packet->ActionId_2},"
                      + $"\"actionType\":{packet->ActionType},\"actionId16\":{packet->ActionId},\"ballista\":{packet->BallistaEntityId},"
                      + $"\"castTime\":{F(packet->CastTime)},\"omenDelay\":{F(packet->OmenDelay / 10f)},"
                      + $"\"interruptible\":{(packet->Interruptible ? 1 : 0)},"
                      + $"\"target\":{packet->TargetEntityId},"
                      + $"\"rot\":{packet->RotationInt},\"px\":{packet->PositionX},"
                      + $"\"py\":{packet->PositionY},\"pz\":{packet->PositionZ}",
                        packet->ActionType == (byte)FFXIVClientStructs.FFXIV.Client.Game.ActionType.Action ? packet->ActionId_2 : 0);
                }
                catch { /* 錄影失敗不得阻斷封包處理 */ }
            }
            castHook!.Original(entityId, packet);
        }
        finally
        {
            activeCauseSeq = previousCauseSeq;
        }
    }

    private void ControlDetour(uint entityId, uint category, uint p1, uint p2, uint p3, uint p4,
                               uint p5, uint p6, uint p7, uint p8, uint targetId, bool p10)
    {
        try
        {
            if (writer != null)
            {
                var sequence = WriteHook("control", "control",
                    $"\"src\":{entityId},\"cat\":{category},\"p\":[{p1},{p2},{p3},{p4},{p5},{p6},{p7},{p8}],"
                  + $"\"target\":{targetId},\"p10\":{(p10 ? "true" : "false")}");
                if (sequence > 0 && category == 35)
                {
                    lock (recordingGate) controlCat35Count++;
                }
            }
        }
        catch { }
        controlHook!.Original(entityId, category, p1, p2, p3, p4, p5, p6, p7, p8, targetId, p10);
    }

    private void SpawnDetour(uint unused, SpawnObjectPacket* packet)
    {
        try
        {
            if (writer != null && packet != null)
                // owner＝召喚主（0＝非寵物——把玩家寵物從機制演員裡分出去就靠它）；
                // vis／targetable／layoutId／eventId／gimmickId＝重播還原物件狀態用，
                // 封包裡本來就有、不多錄是浪費（2026-08-21 盤點補齊）。
                WriteHook("spawn", "spawnobj",
                    $"\"baseId\":{packet->BaseId},\"entity\":{packet->EntityId},"
                  + $"\"index\":{packet->ObjectIndex},\"fateId\":{packet->FateId},"
                  + $"\"kind\":{packet->ObjectKind},\"state\":{packet->TimelineState},"
                  + $"\"eventState\":{packet->EventState},\"radius\":{F(packet->Radius)},"
                  + $"\"owner\":{packet->OwnerId},\"vis\":{packet->Visibility},"
                  + $"\"targetable\":{packet->TargetableStatus},\"layoutId\":{packet->LayoutId},"
                  + $"\"eventId\":{packet->EventId},\"gimmickId\":{packet->GimmickId},"
                  + $"\"x\":{F(packet->PositionX)},\"y\":{F(packet->PositionY)},"
                  + $"\"z\":{F(packet->PositionZ)},\"rot\":{packet->Rotation}");
        }
        catch { }
        spawnHook!.Original(unused, packet);
    }

    private void EffectDetour(uint casterEntityId, Character* caster, Vector3* targetPos,
                              ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects,
                              GameObjectId* targetIds)
    {
        var previousCauseSeq = activeCauseSeq;
        try
        {
            if (writer != null && header != null)
            {
                try
                {
                    // Header.NumTargets is the native live count. Do not impose a party-size
                    // cap here: both native arrays contain exactly that many entries.
                    var targetCount = (int)header->NumTargets;
                    var ids = new StringBuilder();
                    if (targetIds != null)
                    {
                        for (var i = 0; i < targetCount; i++)
                        {
                            if (i > 0) ids.Append(',');
                            ids.Append(((ulong)targetIds[i]).ToString(CultureInfo.InvariantCulture));
                        }
                    }

                    // Keep each non-empty raw effect entry aligned to its native target index.
                    // The converter can map these entries without reinterpreting the payload.
                    var eff = new StringBuilder();
                    if (effects != null)
                    {
                        for (var i = 0; i < targetCount; i++)
                        {
                            var te = effects[i];
                            for (var j = 0; j < 8; j++)
                            {
                                var a = te.Effects[j];
                                if (a.Type == 0) continue;
                                if (eff.Length > 0) eff.Append(',');
                                eff.Append($"[{i},{(byte)a.Type},{a.Value},{a.Param0},{a.Param1},{a.Param2},{a.Param3},{a.Param4}]");
                            }
                        }
                    }

                    var hasPosition = targetPos != null;
                    WriteCause("effect", "effect",
                        $"\"src\":{casterEntityId},\"action\":{header->ActionId},"
                      + $"\"targets\":{header->NumTargets},\"rot\":{header->RotationInt},"
                      + $"\"animationTarget\":{(ulong)header->AnimationTargetId},"
                      + $"\"animationLock\":{R(header->AnimationLock)},\"spellId\":{header->SpellId},"
                      + $"\"animationVariation\":{header->AnimationVariation},"
                      + $"\"actionType\":{(byte)header->ActionType},\"flags\":{header->Flags},"
                      + $"\"ballista\":{header->BallistaEntityId},"
                      + $"\"sourceSequence\":{header->SourceSequence},"
                      + $"\"globalSequence\":{header->GlobalSequence},"
                      + $"\"x\":{F(targetPos != null ? targetPos->X : 0f)},"
                      + $"\"y\":{R(targetPos != null ? targetPos->Y : 0f)},"
                      + $"\"z\":{F(targetPos != null ? targetPos->Z : 0f)},"
                      + $"\"hasPosition\":{(hasPosition ? "true" : "false")},"
                      + $"\"tid\":[{ids}],\"eff\":[{eff}],"
                      + $"\"tidComplete\":{(targetIds != null || targetCount == 0 ? "true" : "false")},"
                      + $"\"effComplete\":{(effects != null || targetCount == 0 ? "true" : "false")}",
                        header->ActionType == FFXIVClientStructs.FFXIV.Client.Game.ActionType.Action ? header->ActionId : 0);
                }
                catch { }
            }
            effectHook!.Original(casterEntityId, caster, targetPos, header, effects, targetIds);
        }
        finally
        {
            activeCauseSeq = previousCauseSeq;
        }
    }

    /// <summary>由 MapEffects 的既有 hook 轉送過來（那支本來就在攔，不重複掛）。</summary>
    public void LogMapEffect(uint index, ushort state, ushort flags)
    {
        if (writer == null) return;
        try { Write("mapeffect", $"\"index\":{index},\"state\":{state},\"flags\":{flags}"); }
        catch { }
    }

    // ── 輸出 ──────────────────────────────────────────────────────────────

    private int Write(string type, string body)
    {
        lock (recordingGate)
        {
            var current = writer;
            if (current?.IsHealthy != true) return 0;
            return WriteCoreLocked(current, type, body);
        }
    }

    private int WriteHook(string hookName, string type, string body)
    {
        lock (recordingGate)
        {
            var current = writer;
            if (current?.IsHealthy != true) return 0;
            return WriteCoreLocked(current, type, body, hookName);
        }
    }

    private void WriteCause(string hookName, string type, string body, uint actionId = 0)
    {
        lock (recordingGate)
        {
            var current = writer;
            if (current?.IsHealthy != true) return;
            var sequence = WriteCoreLocked(current, type, body, hookName);
            if (sequence > 0)
            {
                activeCauseSeq = (this, current, sequence);
                QueueActionMetadataLocked(actionId);
            }
        }
    }

    private int WriteCoreLocked(RecordingWriter current, string type, string body, string? hookName = null)
    {
        if (!ReferenceEquals(writer, current) || !current.IsHealthy) return 0;
        var sequence = captureSeq + 1;
        var t = Stopwatch.GetElapsedTime(startedAt).TotalSeconds;
        var line = $"{{\"t\":{t.ToString("F3", CultureInfo.InvariantCulture)},"
                 + $"\"captureSeq\":{sequence},\"e\":\"{type}\",{body}}}";
        if (!current.TryWriteLine(line))
        {
            SyncWriterDiagnosticsLocked(current);
            FailWriterLocked(current);
            return 0;
        }

        captureSeq = sequence;
        EventCount++;
        counts[type] = counts.GetValueOrDefault(type) + 1;
        if (hookName != null) hookFired.Add(hookName);
        return sequence;
    }

    private static string CauseSuffix(int? causeSeq)
        => causeSeq is > 0 ? $",\"causeSeq\":{causeSeq.Value}" : ",\"causeSeq\":null";

    private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);
    private static string R(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static string Escape(string s) => RecordingJson.Escape(s);

    public void Dispose()
    {
        Stop();
        Plugin.ChatGui.ChatMessage -= OnChatMessage;
        castHook?.Dispose();
        controlHook?.Dispose();
        actorVfxDtorHook?.Dispose();
        staticVfxUpdateHook?.Dispose();
        staticVfxCleanupHook?.Dispose();
        spawnHook?.Dispose();
        effectHook?.Dispose();
        dirDataHook?.Dispose();
        actorVfxHook?.Dispose();
        staticVfxHook?.Dispose();
        tetherHook?.Dispose();
    }
}
