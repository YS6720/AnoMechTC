using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using AnoMech.Core;
using AnoMech.Core.Native.Interfaces;
using AnoMech.Core.SimObjects;
using AnoMech.Pointers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler;

namespace AnoMech.Compat.Server;

/// <summary>
/// 國際服伺服器層：與台服 binary 不同的原生簽章與位移（台服版見 Compat/Server/Tc）。
/// 核對對象：國際服 ffxivgame.ver 2026.10.16.0000.0000；CS＝Dalamud 15.0.3.9 所帶 FFXIVClientStructs。
/// </summary>
internal static unsafe class ServerNative
{
    // 國際服 LoadZone：取上游 anomek/AnoMech master 1646a70
    // Core/Native/Implementations/Pointers/GameMainPointers.cs:8 的 sig（五參數 ABI 的權威）。
    // 國際服 exe 唯一命中 RVA 0x603E20；反組譯參數搬運
    //   mov rsi,rcx / movzx r12d,r9b / mov ecx,edx / mov r15d,r8d / mov ebp,edx
    // 只讀第 5 參數 [rsp+0E0h]、不讀第 6 個 ⇒ 與 LoadZoneDelegate 的 5 參數相符。
    // Hyperborea 898243b Memory.cs:19 是同一前綴加尾端位元組、命中同一位址（佐證，非權威）。
    internal const string LoadZoneSignature = "40 55 56 41 54 41 56 41 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44";

    /// <summary>
    /// 國際服走 CS 具名成員 <c>EventFramework.ProcessDirectorUpdate</c>（9 參數；上游 fork 點
    /// v0.3.12.0 Helpers/InstanceContentDirectorHelper.cs:32 同寫法），包成共用的 7 參數 delegate，
    /// arg5／arg6 傳 0。CS 15.0.3.9 sig 在國際服唯一命中 RVA 0xB83850，反組譯讀
    /// [rsp+90h..B0h] 五個堆疊參數、call [rax+1E8h]。CS 解析不到位址即拋（載入失敗）。
    /// </summary>
    internal static EventFrameworkPointers.ProcessDirectorUpdateDelegate ResolveProcessDirectorUpdate()
    {
        var address = (nint)EventFramework.Addresses.ProcessDirectorUpdate.Value;
        if (address == 0)
            throw new System.InvalidOperationException("ClientStructs 未解析 EventFramework.ProcessDirectorUpdate");
        try
        {
            var baseAddr = System.Diagnostics.Process.GetCurrentProcess().MainModule!.BaseAddress;
            AnoMech.Core.CrashTrace.Log($"[sig] ProcessDirectorUpdate（CS）= module+0x{(long)address - (long)baseAddr:X}（預期 0xB83850）");
        }
        catch (System.Exception ex)
        {
            AnoMech.Core.CrashTrace.Log($"[sig] ProcessDirectorUpdate 位址取得失敗：{ex.Message}");
        }
        return (eventFramework, eventId, category, arg1, arg2, arg3, arg4)
            => eventFramework->ProcessDirectorUpdate(eventId, category, arg1, arg2, arg3, arg4, 0, 0);
    }

    // ── B8 國際服原生補件（B7 留空的 EffectResult／ForcedFollow／MapEffect 快照＋StatusList／
    //    TimelinePreload／ControlAffliction）。sig 取上游 anomek/AnoMech master 1646a70
    //    Core/Native/Implementations/Pointers/PacketDispatcherPointers.cs；行為取同版
    //    BattleCharaProxy.cs／MapEffects.cs／ActionTimelinePreload.cs／PlayerConditions.cs。
    //    國際服 2026.10.16 exe 離線掃描皆唯一命中（RVA 見各 sig 註解）。台服版為同簽章空實作。

    /// <summary>由 <see cref="PacketDispatcherPointers.Initialize"/> 呼叫；任一 sig 解析不到即拋（載入失敗）。</summary>
    internal static void InitializePacketHandlers()
    {
        Plugin.GameInterop.InitializeFromAttributes(new PacketHandlers());
        var anchor = Plugin.SigScanner.ScanText(MapEffectSnapshotAnchorSignature);
        var address = anchor + MapEffectSnapshotAnchorOffset;
        handleMapEffectSnapshotPacket = Marshal.GetDelegateForFunctionPointer<HandleMapEffectSnapshotPacketDelegate>(address);
        try
        {
            var baseAddr = System.Diagnostics.Process.GetCurrentProcess().MainModule!.BaseAddress;
            CrashTrace.Log($"[sig] HandleMapEffectSnapshotPacket = module+0x{(long)address - (long)baseAddr:X}（預期 0xB848D0）");
        }
        catch (System.Exception ex)
        {
            CrashTrace.Log($"[sig] HandleMapEffectSnapshotPacket 位址取得失敗：{ex.Message}");
        }
        ResolvePlayerStateFlagsHandler();
    }

    // ── D788 a：UMAD P1 混亂時以玩家狀態旗標（PlayerStateFlags）鎖住本機玩家操作 ──────────
    // sig 取上游 anomek/AnoMech master 1646a70 Core/Native/Implementations/Pointers/PacketDispatcherPointers.cs
    // HandlePlayerStateFlagsPacket；行為取同版 PlayerConditions.cs／SimPlayer.SyncStatusAffliction，只寫 63
    // （ControlAffliction），不寫 73。國際服 2026.10.16 exe 唯一命中 RVA 0xE16740；handler 逐 RowId 0..0x6F
    // 取 Condition 表列，IsNetworked 的列各吃一個 bit，值與現值不同才寫（設計與反組譯見 D788 審查的
    // design-v2＋v3／v4 delta）。
    // 解析失敗只停用這一項（P1 混亂時不鎖，其他場景照常），不拋、不猜位址。
    internal const string PlayerStateFlagsSignature = "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 48 89 7C 24 20 41 54 41 56 41 57 48 83 EC 20 4C 8B FA 4C 8B E1 E8 ?? ?? ?? ?? 48 8B C8 48 8B D8 E8";
    // handler 迴圈上限（cmp ebx,70h）＝ Conditions 的 bool 數。
    private const int ConditionCount = 0x70;
    private const uint ControlAfflictionRow = (uint)ConditionFlag.SufferingStatusAffliction63;
    private static delegate* unmanaged<void*, byte*, void> playerStateFlagsHandler;
    private static uint[]? networkedConditionRows;
    // 63 是不是我們舉的（回讀確認後才算）；不是我們舉的永遠不清。
    private static bool controlAfflictionRaised;
    private static bool controlAfflictionMismatchLogged;

    private static void ResolvePlayerStateFlagsHandler()
    {
        if (!Plugin.SigScanner.TryScanText(PlayerStateFlagsSignature, out var address))
        {
            Plugin.Log.Error("[D788] PlayerStateFlags sig 解析失敗：UMAD P1 混亂時不會鎖住玩家操作（其他功能不受影響）。");
            CrashTrace.Log("[sig] HandlePlayerStateFlagsPacket 解析失敗，混亂鎖定停用");
            return;
        }
        playerStateFlagsHandler = (delegate* unmanaged<void*, byte*, void>)address;
        try
        {
            var baseAddr = System.Diagnostics.Process.GetCurrentProcess().MainModule!.BaseAddress;
            CrashTrace.Log($"[sig] HandlePlayerStateFlagsPacket = module+0x{(long)address - (long)baseAddr:X}（預期 0xE16740）");
        }
        catch (System.Exception ex)
        {
            CrashTrace.Log($"[sig] HandlePlayerStateFlagsPacket 位址取得失敗：{ex.Message}");
        }
    }

    // 上游 PlayerConditions.networked：Condition 表 IsNetworked 的列，依 RowId 排序。
    private static uint[] NetworkedConditionRows
    {
        get
        {
            if (networkedConditionRows is { } rows) return rows;
            rows = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Condition>()
                .Where(row => row.IsNetworked)
                .Select(row => row.RowId)
                .OrderBy(id => id)
                .ToArray();
            DiagnosticLog.Info($"[D788] networked Condition {rows.Length} 列：{string.Join(",", rows)}");
            return networkedConditionRows = rows;
        }
    }

    // 每個 SimPlayer Tick 呼叫一次。不要鎖且不是我們舉的 → 零成本返回（不讀、不送）。
    internal static void SyncControlAffliction(bool wanted)
    {
        if (!wanted && !controlAfflictionRaised) return;
        if (playerStateFlagsHandler == null) return;
        var conditions = (byte*)FFXIVClientStructs.FFXIV.Client.Game.Conditions.Instance();
        if (conditions == null) return;
        var current = conditions[ControlAfflictionRow] != 0;
        var send = ServerPackets.ControlAfflictionSend(wanted, current, controlAfflictionRaised);
        if (send is { } value)
        {
            SendConditionFlag(conditions, ControlAfflictionRow, value);
        }
        var after = conditions[ControlAfflictionRow] != 0;
        if (send is { } sent)
        {
            if (after == sent)
            {
                DiagnosticLog.Info($"[D788] 混亂旗標 63 {(sent ? "舉起" : "放下")}。");
                controlAfflictionMismatchLogged = false;
            }
            else if (!controlAfflictionMismatchLogged)
            {
                DiagnosticLog.Warn($"[D788] 送出混亂旗標 63={sent} 後回讀仍為 {after}，下一次同步再送。");
                controlAfflictionMismatchLogged = true;
            }
        }
        controlAfflictionRaised = ServerPackets.ControlAfflictionRaised(wanted, controlAfflictionRaised, send, after);
    }

    // 放下本插件舉起的 63；沒舉過不送（不清真伺服器的 63）。冪等。
    internal static void ReleaseControlAffliction() => SyncControlAffliction(false);

    // 墜落接住（MapController.CatchFall）：國際服送上游 1646a70 的 ActorControl 0xED／0x107 與下方收尾。
    internal static bool HandlesArenaFalls => true;

    private static bool endClientLoadUnavailableLogged;

    // 上游 PlayerConditions.EndClientLoad：墜落收尾時放下 client 自己舉起的 BetweenAreas 與
    // OccupiedInQuestEvent，其他列照現值。組包一次只換一列，所以送兩次。
    internal static void EndClientLoad()
    {
        if (playerStateFlagsHandler == null)
        {
            if (!endClientLoadUnavailableLogged)
                DiagnosticLog.Warn("[墜落] PlayerStateFlags sig 未解析：墜落收尾無法放下 BetweenAreas／OccupiedInQuestEvent。");
            endClientLoadUnavailableLogged = true;
            return;
        }
        var conditions = (byte*)FFXIVClientStructs.FFXIV.Client.Game.Conditions.Instance();
        if (conditions == null) return;
        SendConditionFlag(conditions, (uint)ConditionFlag.BetweenAreas, false);
        SendConditionFlag(conditions, (uint)ConditionFlag.OccupiedInQuestEvent, false);
    }

    private static void SendConditionFlag(byte* conditions, uint row, bool value)
    {
        var bits = stackalloc byte[ServerPackets.PlayerStateFlagsLength];
        ServerPackets.PlayerStateFlags(NetworkedConditionRows, new System.ReadOnlySpan<byte>(conditions, ConditionCount),
            row, value, new System.Span<byte>(bits, ServerPackets.PlayerStateFlagsLength));
        playerStateFlagsHandler(conditions, bits);
    }

    // 墜落接回場中：上游 1646a70 BattleCharaProxy.SetPos（ActorSetPos type 0，經 client 自己處理伺服器
    // 傳送的路徑）。只寫 GameObject 位置時，client 仍照原本的墜落速度往下掉：11-12 22:15:10 接回場中，
    // 2 秒後已在 Y=-49（trace）。這條路徑能否停住墜落，以 MapController.CloseFall 的「收尾」Y 驗證。
    internal static void PlaceLocalPlayer(System.Numerics.Vector3 position)
    {
        var lp = Plugin.ObjectTable.LocalPlayer;
        if (lp == null) return;
        var packet = new ActorSetPosPacket
        {
            RotationInt = MathUtil.QuantizeRotation(MathUtil.NormalizeRotation(lp.Rotation)),
            X = position.X,
            Y = position.Y,
            Z = position.Z,
        };
        PacketHandlers.HandleActorSetPosPacket(lp.EntityId, &packet);
    }

    private sealed class PacketHandlers
    {
        // 唯一命中 RVA 0xB39600。replay 0 is what the live packet dispatcher passes.
        [Signature("40 53 41 54 41 55 48 83 EC 40 83 3D ?? ?? ?? ?? 03 45 0F B6 E0 48 8B DA", UseFlags = SignatureUseFlags.Pointer, ScanType = ScanType.Text)]
        public static HandleEffectResultBasicPacketDelegate HandleEffectResultBasicPacket { get; private set; } = null!;

        // 唯一命中 RVA 0xB38F90。replay 0 is what the live packet dispatcher passes.
        [Signature("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 83 3D ?? ?? ?? ?? 03 41 0F B6 F0 48 8B DA 8B F9 74 ?? 48 81 C2 68 01 00 00", UseFlags = SignatureUseFlags.Pointer, ScanType = ScanType.Text)]
        public static HandleStatusEffectListDoublePacketDelegate HandleStatusEffectListDoublePacket { get; private set; } = null!;

        // 唯一命中 RVA 0xB4E300。Unique only together with the next function, which the compiler places after it.
        [Signature("48 8D 0D ?? ?? ?? ?? E9 ?? ?? ?? ?? CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC 48 8B 15 ?? ?? ?? ?? 48 85 D2 74 0C 48 8D 0D", UseFlags = SignatureUseFlags.Pointer, ScanType = ScanType.Text)]
        public static HandleForcedFollowEndPacketDelegate HandleForcedFollowEndPacket { get; private set; } = null!;

        // 唯一命中 RVA 0xB4E260。The same controller's approach mode: the client walks its own player
        // to the entity until within Range. UNVERIFIED that retail uses it for Confused.
        [Signature("0F B6 01 44 8B 41 04 48 8D 0D ?? ?? ?? ?? 66 0F 6E C8", UseFlags = SignatureUseFlags.Pointer, ScanType = ScanType.Text)]
        public static HandleForcedApproachPacketDelegate HandleForcedApproachPacket { get; private set; } = null!;

        // 唯一命中 RVA 0xB4E320。Hard stop: ends the controller's mode and halts the local player where
        // they stand. Takes no payload; the handler loads the player itself.
        [Signature("48 8B 15 ?? ?? ?? ?? 48 85 D2 74 0C 48 8D 0D ?? ?? ?? ?? E9", UseFlags = SignatureUseFlags.Pointer, ScanType = ScanType.Text)]
        public static HandleForcedStopPacketDelegate HandleForcedStopPacket { get; private set; } = null!;

        // 唯一命中 RVA 0xB39C40（國際服 2026.11.03 exe 離線掃描）。12-byte thunk shared in shape by many
        // handlers; the pattern runs on into HandleActorMovePacket, which follows it, to stay unique.
        [Signature("4C 8B C2 8B D1 48 8D 0D ?? ?? ?? ?? E9 ?? ?? ?? ?? CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC 48 89 5C 24 08 57 48 83 EC 20 4C 63 05", UseFlags = SignatureUseFlags.Pointer, ScanType = ScanType.Text)]
        public static HandleActorSetPosPacketDelegate HandleActorSetPosPacket { get; private set; } = null!;
    }

    // 上游 sig 在國際服命中兩個逐位元組相同的本體（0xB847C0、0xB848D0，各 0x10A bytes，
    // 一個快照 opcode 一個，行為相同）。Dalamud 的 Signature Offset 不適用 Pointer，所以手動錨定：
    // 「第一個本體的結尾＋6 個 CC padding＋第二個本體開頭」唯一命中 0xB848B7，+25＝0xB848D0。
    // padding 數一變 sig 就掃不到並拋出，不會把 +25 指進指令中間。
    internal const string MapEffectSnapshotAnchorSignature = "41 80 8B ?? ?? 00 00 08 48 8B 5C 24 ?? 48 83 C4 20 5F C3 CC CC CC CC CC CC 48 89 5C 24 ?? 57 48 83 EC 20 48 8B D9 48 8B FA 48 8B 89 58 01 00 00 48 85 C9 0F 84";
    private const int MapEffectSnapshotAnchorOffset = 25;
    private static HandleMapEffectSnapshotPacketDelegate handleMapEffectSnapshotPacket = null!;

    private delegate void HandleEffectResultBasicPacketDelegate(uint entityId, EffectResultBasicPacket* packet, byte replay);
    private delegate void HandleStatusEffectListDoublePacketDelegate(uint entityId, StatusEffectListDoublePacket* packet, byte replay);
    // Takes no payload.
    private delegate void HandleForcedFollowEndPacketDelegate(nint unused);
    private delegate void HandleForcedApproachPacketDelegate(ForcedApproachPacket* packet);
    private delegate void HandleForcedStopPacketDelegate(nint unused);
    private delegate void HandleMapEffectSnapshotPacketDelegate(EventFramework* framework, MapEffectSnapshotPacket* packet);
    private delegate void HandleActorSetPosPacketDelegate(uint entityId, ActorSetPosPacket* packet);

    // EffectResult：讓 client 套用 ActionEffect 中該目標的傷害條目（含飛字）。HP 帶當下原生值
    // （上游同），handler 先套用排隊效果再寫回這個 HP，所以不改血量。
    internal static void ReceiveEffectResult(SimCharacter target, uint globalSequence, byte targetIndex)
    {
        var chara = target.BattleCharaPtr;
        if (chara == null) return;
        var entityId = chara->EntityId;
        var packet = ServerPackets.EffectResult(globalSequence, entityId, chara->Health, targetIndex);
        PacketHandlers.HandleEffectResultBasicPacket(entityId, &packet, 0);
    }

    // Within this many yalms of the target the approach counts as arrived. UNVERIFIED: retail's value.
    private const byte ForcedApproachRange = 1;
    // 是否由我們開啟了強制追隨：解除（null）時沒開過就不送封包。
    private static bool forcedFollowActive;

    // 本機玩家的原生 forced follow（混亂類），上游 BattleCharaProxy.ForcedFollow：
    // The controller holds one mode at a time: the face-only follow would replace the walk, so
    // the approach, which turns the player as it walks, goes out alone. Retail always ends the
    // previous one before starting the next. null＝解除。
    internal static void ForcedFollow(uint? targetEntityId)
    {
        if (targetEntityId is null && !forcedFollowActive) return;
        PacketHandlers.HandleForcedFollowEndPacket(0);
        forcedFollowActive = false;
        if (targetEntityId is not { } target) return;
        var packet = new ForcedApproachPacket { Range = ForcedApproachRange, TargetEntityId = target };
        PacketHandlers.HandleForcedApproachPacket(&packet);
        forcedFollowActive = true;
    }

    internal static void StopForcedMove()
    {
        PacketHandlers.HandleForcedStopPacket(0);
        forcedFollowActive = false;
    }

    // 副本 MapEffect 全槽快照（DutyInitData.MapEffectSnapshot），上游 MapEffects.ApplySnapshot。
    // 回 false＝director 尚未就緒，MapController 逐幀重試；超過 128 槽記警告後回 true（重試也送不出）。
    internal static bool ApplyMapEffectSnapshot(ushort[] states, bool zoneIn)
    {
        if (states.Length > MapEffectSnapshotPacket.Capacity)
        {
            DiagnosticLog.Warn($"[MapEffect] 快照有 {states.Length} 槽，超過 {MapEffectSnapshotPacket.Capacity}，不送出。");
            return true;
        }
        var framework = EventFramework.Instance();
        if (framework == null) return false;
        var module = framework->DirectorModule.ActiveContentDirector;
        // Both handlers drop the packet silently while the director's vfunc 3 returns false; checking
        // it first turns that into a retry.
        if (module == null || ((delegate* unmanaged<ContentDirector*, byte>)(*(nint**)module)[3])(module) == 0
            || module->MapEffects == null)
            return false;
        var packet = ServerPackets.MapEffectSnapshot(states, zoneIn, module->GetEventId());
        handleMapEffectSnapshotPacket(framework, &packet);
        return true;
    }

    // 上游 BattleCharaProxy.ReceiveStatusList：Party members and enemies alike get the Double list
    // retail sends to players; the handler doesn't care which kind of character it is.
    internal static void ReceiveStatusList(SimCharacter target, IReadOnlyList<StatusSlot> statuses, uint health, uint mana, byte shield)
    {
        var chara = target.BattleCharaPtr;
        if (chara == null) return;
        var packet = ServerPackets.StatusList(statuses, chara->ClassJob, chara->Level, health, chara->MaxHealth, mana, chara->MaxMana, shield);
        PacketHandlers.HandleStatusEffectListDoublePacket(chara->EntityId, &packet, 0);
    }

    // 上游 ActionTimelinePreload.Preload：Preloads "mon_sp/gimmick" ActionTimeline rows through the
    // client's own entry point, so a helper's action VFX timeline has its resources up before the
    // action fires. Both spellings the engine might want are tried, the sheet key and the full .tmb path.
    internal static void PreloadActionTimelines(IEnumerable<(ushort Id, string Key)> rows, string tag)
    {
        var manager = ActionTimelineManager.Instance();
        if (manager == null)
        {
            DiagnosticLog.Warn($"[{tag}] ActionTimelineManager.Instance() is null -- no timeline preload.");
            return;
        }
        foreach (var (id, key) in rows)
        {
            foreach (var candidate in new[] { key, $"chara/action/{key}.tmb" })
            {
                var bytes = Encoding.UTF8.GetBytes(candidate + "\0");
                fixed (byte* keyPtr = bytes)
                {
                    var info = new ActionTimelineManager.PreloadActionTmbInfo { Key = keyPtr, Index = id };
                    var accepted = manager->PreloadActionTmb(&info);
                    DiagnosticLog.Info($"[{tag}] PreloadActionTmb({id}, \"{candidate}\") -> {accepted}.");
                }
            }
        }
    }

    // 上游 PlayerConditions.ControlAffliction：For a status that locks control (Confused): the
    // player's own input is ignored. Confused raises 63. 只讀；寫入（SetAfflictions）不在 B8。
    internal static bool ControlAffliction => Plugin.Condition[ConditionFlag.SufferingStatusAffliction63];

}
