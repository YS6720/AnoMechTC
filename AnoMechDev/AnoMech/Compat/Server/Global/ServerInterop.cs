using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Network;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace AnoMech.Compat.Server;

/// <summary>
/// 國際服伺服器層：Dalamud API 15 所帶 FFXIVClientStructs／Lumina 與台服欄位型別或名稱不同之處。
/// 共用碼經由這裡存取，兩版各有一份同簽章的實作（台服版見 Compat/Server/Tc）。
/// </summary>
internal static unsafe class ServerInterop
{
    /// <summary>國際服 CS 的 CastInfo／ActionEffectHandler.Header.ActionType 是 byte。</summary>
    internal static byte ActionTypeField(ActionType value) => (byte)value;

    /// <summary>國際服 CS 的 GameObject.RenderFlags 是 VisibilityFlags enum。</summary>
    internal static VisibilityFlags RenderFlags(VisibilityFlags flags) => flags;

    internal static void SetEnemyCount(EnemyListNumberArray* array, int count) => array->EnemyCount = count;

    internal static ReadOnlySeString LockonIconName(in Lockon row) => row.IconName;

    internal static void HandleSpawnObjectPacket(SpawnObjectPacket* packet)
        => PacketDispatcher.HandleSpawnObjectPacket(0, packet);

    // 國際服 CS 把 0x2C 宣告為 private Unk2C；與 SimEventObject 寫入 TimelineState 的是同一位置。
    internal static ushort SpawnTimelineState(SpawnObjectPacket* packet) => *(ushort*)((byte*)packet + 0x2C);

    // 國際服 CS 把 0x30 宣告為 private Unk30（uint；0x34 起是 PositionX）＝上游 EventObjects 寫入的 Arg2。
    internal static void SetSpawnArg2(SpawnObjectPacket* packet, uint arg2) => *(uint*)((byte*)packet + 0x30) = arg2;
}
