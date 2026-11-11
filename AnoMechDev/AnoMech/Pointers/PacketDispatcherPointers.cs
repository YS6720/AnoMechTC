using AnoMech.Compat.Server;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using System.Runtime.InteropServices;

namespace AnoMech.Pointers;

[StructLayout(LayoutKind.Explicit, Size = 0x20)]
public partial struct ActorCastPacket
{
    [FieldOffset(0x00)] public ushort ActionId;
    [FieldOffset(0x02)] public byte ActionType;
    [FieldOffset(0x03)] public byte OmenDelay; // The value gets divided by 10.0f
    [FieldOffset(0x04)] public uint ActionId_2;
    [FieldOffset(0x08)] public float CastTime;
    [FieldOffset(0x0C)] public uint TargetEntityId;
    [FieldOffset(0x10)] public ushort RotationInt; // Quantized Rotation
    [FieldOffset(0x12)] public bool Interruptible;
    [FieldOffset(0x14)] public uint BallistaEntityId;
    [FieldOffset(0x18)] public ushort PositionX; // Quantized Position
    [FieldOffset(0x1A)] public ushort PositionY; // Quantized Position
    [FieldOffset(0x1C)] public ushort PositionZ; // Quantized Position
}

[StructLayout(LayoutKind.Explicit, Size = 0x1)]
public partial struct DespawnCharacterPacket
{
    [FieldOffset(0x0)] public byte Index;
}

[StructLayout(LayoutKind.Explicit, Size = 0x10)]
public partial struct UpdateClassInfoPacket
{
    [FieldOffset(0x0)] public byte ClassJobId;
    [FieldOffset(0x2)] public ushort CurrentLevel;
    [FieldOffset(0x4)] public ushort ClassJobLevel;
    [FieldOffset(0x6)] public ushort SyncedLevel;
    [FieldOffset(0x8)] public ushort ClassJobExp;
    [FieldOffset(0xC)] public uint BaseRestedExperience;
}

internal unsafe class PacketDispatcherPointers
{
    [Signature("40 53 57 48 81 EC ?? ?? ?? ?? 48 8B FA 8B", UseFlags = SignatureUseFlags.Pointer, ScanType = ScanType.Text)]
    public static HandleActorCastPacketDelegate HandleActorCastPacket { get; private set; } = null!;

    // 台服 CS 未提供此函式。signature 取自國際服 CS 的 [MemberFunction]，
    // 並已離線在台服 ffxiv_dx11.exe 的 .text 掃過：**唯一命中**（RVA 0xA83ED0）。
    // 國際服（2026.10.16）：與 CS 15.0.3.9 sig 逐字相同，唯一命中 RVA 0xB4B310。
    [Signature("40 53 57 48 83 EC ?? F6 42", UseFlags = SignatureUseFlags.Pointer, ScanType = ScanType.Text)]
    public static HandleSpawnObjectPacketDelegate HandleSpawnObjectPacket { get; private set; } = null!;

    // 同上：台服 CS 未提供，sig 取自國際服 CS 並已離線在台服 .text 驗證唯一命中
    //（RVA 0xA73750）。只有 UWU 的 UltimatePredation 用到。
    // 國際服（2026.10.16）：與 CS 15.0.3.9 sig 逐字相同，唯一命中 RVA 0xB3A8C0。
    [Signature("40 55 53 57 41 54 41 56 48 8D AC 24 ?? ?? ?? ?? B8 ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 2B E0 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 85 ?? ?? ?? ?? 8B 85", UseFlags = SignatureUseFlags.Pointer, ScanType = ScanType.Text)]
    public static HandleActorControlPacketDelegate HandleActorControlPacket { get; private set; } = null!;

    [Signature("40 53 48 83 EC 20 48 8B DA 48 8D 0D ?? ?? ?? ?? 0F", UseFlags = SignatureUseFlags.Pointer, ScanType = ScanType.Text)]
    public static HandleDespawnObjectPacketDelegate HandleDespawnObjectPacket { get; private set; } = null!;

    [Signature("48 89 5C 24 ?? 57 48 83 EC 40 0F B6 1A", UseFlags = SignatureUseFlags.Pointer, ScanType = ScanType.Text)]
    public static HandleDespawnCharacterPacketDelegate HandleDespawnCharacterPacket { get; private set; } = null!;

    // Technically the real HandleUpdateClassInfoPacket is a wrapper to this sig... but this is still close to other HandleX methods, so it fits here
    [Signature("48 89 5C 24 ?? 57 48 83 EC 20 48 8B DA 48 8D 0D ?? ?? ?? ?? 33", UseFlags = SignatureUseFlags.Pointer, ScanType = ScanType.Text)]
    public static HandleUpdateClassInfoPacketDelegate HandleUpdateClassInfoPacket { get; private set; } = null!;

    public delegate void HandleActorCastPacketDelegate(uint entityId, ActorCastPacket* packet);
    public delegate void HandleSpawnObjectPacketDelegate(uint unused, SpawnObjectPacket* packet);
    public delegate void HandleActorControlPacketDelegate(
        uint entityId, uint category, uint p1, uint p2, uint p3, uint p4,
        uint p5, uint p6, uint p7, uint p8, uint targetId, bool p10);
    public delegate void HandleDespawnObjectPacketDelegate(uint unused, byte* packet);
    public delegate void HandleDespawnCharacterPacketDelegate(ulong unused, DespawnCharacterPacket* packet);
    public delegate void HandleUpdateClassInfoPacketDelegate(ulong unused, UpdateClassInfoPacket* packet);

    public static void Initialize()
    {
        Plugin.GameInterop.InitializeFromAttributes(new PacketDispatcherPointers());
        // 國際服專屬的封包 handler（B8）在伺服器層解析；台服為空實作，不新增台服 sig。
        ServerNative.InitializePacketHandlers();
    }
}
