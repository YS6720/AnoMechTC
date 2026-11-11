using AnoMech.Pointers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace AnoMech.Compat.Server;

/// <summary>
/// TC 伺服器層：台服 FFXIVClientStructs 0.0.6966／Lumina 與國際服欄位型別或名稱不同之處。
/// 共用碼經由這裡存取，兩版各有一份同簽章的實作（國際版見 Compat/Server/Global）。
/// </summary>
internal static unsafe class ServerInterop
{
    /// <summary>台服 CS 的 CastInfo／ActionEffectHandler.Header.ActionType 是 ActionType enum。</summary>
    internal static ActionType ActionTypeField(ActionType value) => value;

    /// <summary>台服 CS 的 GameObject.RenderFlags 是 int。</summary>
    internal static int RenderFlags(VisibilityFlags flags) => (int)flags;

    // 台服 CS 尚未替此欄命名：Unk1 ＝ 國際服的 EnemyCount。
    // 依據：兩版 EnemyListNumberArray 欄位順序完全相同
    // （_data, Unk0, [此欄], TargetEntityId, UnkEntityId, Unk4, _enemies, StructSize），
    // 國際服該位置名為 EnemyCount。
    internal static void SetEnemyCount(EnemyListNumberArray* array, int count) => array->Unk1 = count;

    // 台服 Lumina 尚未替 Lockon 的欄位命名：Unknown0 ＝ 國際服的 IconName。
    // 依據：兩版欄位順序相同（RowId, [此欄], Unknown1），國際服該位置名為 IconName。
    internal static ReadOnlySeString LockonIconName(in Lockon row) => row.Unknown0;

    // 台服 CS 無 PacketDispatcher.HandleSpawnObjectPacket ⇒ 走自行 signature-scan 的
    // 函式指標（已離線驗證唯一命中，見 Pointers/PacketDispatcherPointers.cs）。
    internal static void HandleSpawnObjectPacket(SpawnObjectPacket* packet)
        => PacketDispatcherPointers.HandleSpawnObjectPacket(0, packet);

    internal static ushort SpawnTimelineState(SpawnObjectPacket* packet) => packet->TimelineState;

    internal static void SetSpawnArg2(SpawnObjectPacket* packet, uint arg2) => packet->Arg2 = arg2;
}
