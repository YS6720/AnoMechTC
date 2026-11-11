using System.Runtime.InteropServices;

// 刻意放在國際服 CS 同名型別的命名空間：共用碼兩版都寫同一個
// `using FFXIVClientStructs.FFXIV.Client.Game.Network;` 就能接上，不用 #if。
namespace FFXIVClientStructs.FFXIV.Client.Game.Network;

// TC 伺服器層：台服 FFXIVClientStructs 0.0.6966 沒有 SpawnObjectPacket（整份 DLL 的
// "Spawn" 字樣 0 次），故在此自行宣告；國際版用 CS 自帶的同名型別，本檔由 shared
// targets 排除。位移不是猜的——從國際服 CS 的 metadata 逐欄解出型別後依
// sequential layout 算得，並用該版自帶的 Unk20 / Unk27 / Unk28 / Unk2C / Unk30
// 這幾個「以位移命名」的欄位交叉驗證：五個名稱與算出的位移全部吻合。
// 抽取方式與完整對照見 docs/upstream/TC-CS-BITFIELDS.md。
//
// 0x2C（CS 稱 Unk2C）就是 SimEventObject 原本手寫的 TimelineState 位置。
[StructLayout(LayoutKind.Explicit, Size = 0x40)]
public partial struct SpawnObjectPacket
{
    [FieldOffset(0x00)] public byte ObjectIndex;
    [FieldOffset(0x01)] public byte ObjectKind;
    [FieldOffset(0x02)] public byte TargetableStatus;
    [FieldOffset(0x03)] public byte Visibility;
    [FieldOffset(0x04)] public uint BaseId;
    [FieldOffset(0x08)] public uint EntityId;
    [FieldOffset(0x0C)] public uint LayoutId;
    [FieldOffset(0x10)] public uint EventId;
    [FieldOffset(0x14)] public uint OwnerId;
    [FieldOffset(0x18)] public uint GimmickId;
    [FieldOffset(0x1C)] public float Radius;
    [FieldOffset(0x22)] public ushort Rotation;   // quantized
    [FieldOffset(0x24)] public ushort FateId;
    [FieldOffset(0x26)] public byte EventState;
    [FieldOffset(0x2C)] public ushort TimelineState;
    // 國際服 CS 的 Unk30；上游稱 Arg2（實際 EObj 生成封包低位元組 3＋高字組物件序號）。
    [FieldOffset(0x30)] public uint Arg2;
    [FieldOffset(0x34)] public float PositionX;
    [FieldOffset(0x38)] public float PositionY;
    [FieldOffset(0x3C)] public float PositionZ;
}
