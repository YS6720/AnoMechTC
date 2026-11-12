using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AnoMech.Core.Native.Interfaces;

namespace AnoMech.Compat.Server;

// 國際服 B8 原生補件的封包結構與純組包函式（不依賴 Dalamud，Tests 以 Compile Include 連結）。
// 結構取上游 anomek/AnoMech master 1646a70
// Core/Native/Implementations/Pointers/PacketDispatcherPointers.cs，欄位／Size／FieldOffset 照原檔。

// The server's teleport of one actor. Type 0 is a plain placement; retail warps its own player with
// 25 or 5, which also start a client load.
[StructLayout(LayoutKind.Explicit, Size = 0x18)]
public struct ActorSetPosPacket
{
    [FieldOffset(0x00)] public ushort RotationInt; // Quantized Rotation
    [FieldOffset(0x02)] public byte Type;
    [FieldOffset(0x03)] public byte Flags;
    [FieldOffset(0x08)] public float X;
    [FieldOffset(0x0C)] public float Y;
    [FieldOffset(0x10)] public float Z;
}

// Range in yalms, whole numbers only.
[StructLayout(LayoutKind.Explicit, Size = 0x8)]
public struct ForcedApproachPacket
{
    [FieldOffset(0x00)] public byte Range;
    [FieldOffset(0x04)] public uint TargetEntityId;
}

[StructLayout(LayoutKind.Explicit, Size = 0xC)]
public struct StatusEffectListStatus
{
    [FieldOffset(0x0)] public ushort StatusId;
    [FieldOffset(0x2)] public ushort Param;
    [FieldOffset(0x4)] public float RemainingTime;
    [FieldOffset(0x8)] public uint SourceEntityId;
}

// The server's full HP/MP and status state for one character: two 30-entry status lists around
// the HP/MP block, Statuses for StatusManager slots 0-29 and ExtraStatuses for 30-59.
[StructLayout(LayoutKind.Explicit, Size = 0x2E8)]
public struct StatusEffectListDoublePacket
{
    public const int Capacity = 30;
    public const int ExtraStatusesOffset = 0x000;
    public const int StatusesOffset = 0x17C;

    [FieldOffset(0x168)] public byte ClassJob;
    [FieldOffset(0x169)] public byte Level;
    [FieldOffset(0x16B)] public byte Level_2;
    [FieldOffset(0x16C)] public uint CurrentHP;
    [FieldOffset(0x170)] public uint MaxHP;
    [FieldOffset(0x174)] public ushort CurrentMP;
    [FieldOffset(0x176)] public ushort MaxMP;
    [FieldOffset(0x178)] public byte DamageShield;
}

// One target's confirmation of one ActionEffect; the client applies that target's queued effects
// when it arrives, then sets its HP.
[StructLayout(LayoutKind.Explicit, Size = 0x18)]
public struct EffectResultBasicPacket
{
    [FieldOffset(0x00)] public byte Count;
    [FieldOffset(0x04)] public uint GlobalSequence;
    [FieldOffset(0x08)] public uint EntityId;
    [FieldOffset(0x0C)] public uint CurrentHP;
    // The target's index in the ActionEffect's target list.
    [FieldOffset(0x10)] public byte TargetIndex;
}

// Every slot's resting state at once; plays no timeline. Retail sends 0x88 bytes, but the handler
// reads one state per slot up to MapEffectList.ItemCount, so this holds all 128.
[StructLayout(LayoutKind.Explicit, Size = 0x106)]
public unsafe struct MapEffectSnapshotPacket
{
    public const int Capacity = 128;
    // Bit 0 applies the states on the director's next update (retail always sets it); bit 2 also
    // writes 5 to MapEffectList+0x604 (retail: zone-in only); bit 1 skips slots whose item+4 bit 0 is set.
    public const ushort HeaderReset = 0x1;
    public const ushort HeaderZoneIn = 0x5;

    [FieldOffset(0x0)] public uint DirectorId;
    [FieldOffset(0x4)] public ushort Header;
    [FieldOffset(0x6)] public fixed ushort States[Capacity];
}

internal static unsafe class ServerPackets
{
    // 上游 BattleCharaProxy.ReceiveEffectResult：Count 固定 1，HP 由呼叫端給（上游傳當下原生 HP）。
    internal static EffectResultBasicPacket EffectResult(uint globalSequence, uint entityId, uint currentHp, byte targetIndex)
        => new()
        {
            Count = 1,
            GlobalSequence = globalSequence,
            EntityId = entityId,
            CurrentHP = currentHp,
            TargetIndex = targetIndex,
        };

    // 上游 BattleCharaProxy.ReceiveStatusList：前 30 筆進 Statuses（0x17C 起）、31–60 筆進
    // ExtraStatuses（0x000 起），其餘捨棄；未用的槽為 0。
    internal static StatusEffectListDoublePacket StatusList(IReadOnlyList<StatusSlot> statuses,
        byte classJob, byte level, uint health, uint maxHealth, uint mana, uint maxMana, byte shield)
    {
        var packet = new StatusEffectListDoublePacket
        {
            ClassJob = classJob,
            Level = level,
            Level_2 = level,
            CurrentHP = health,
            MaxHP = maxHealth,
            CurrentMP = (ushort)mana,
            MaxMP = (ushort)maxMana,
            DamageShield = shield,
        };
        var first = (StatusEffectListStatus*)((byte*)&packet + StatusEffectListDoublePacket.StatusesOffset);
        var second = (StatusEffectListStatus*)((byte*)&packet + StatusEffectListDoublePacket.ExtraStatusesOffset);
        for (var i = 0; i < statuses.Count && i < 2 * StatusEffectListDoublePacket.Capacity; i++)
        {
            var entry = i < StatusEffectListDoublePacket.Capacity ? &first[i] : &second[i - StatusEffectListDoublePacket.Capacity];
            entry->StatusId = statuses[i].StatusId;
            entry->Param = statuses[i].Param;
            entry->RemainingTime = statuses[i].RemainingTime;
            entry->SourceEntityId = statuses[i].SourceEntityId;
        }
        return packet;
    }

    // 上游 MapEffects.ApplySnapshot 的組包：Header 依 zoneIn 選 0x5／0x1，超出 states 長度的槽為 0。
    // 超過 128 槽是呼叫端錯誤（ServerNative 先擋下並記警告）。
    internal static MapEffectSnapshotPacket MapEffectSnapshot(ReadOnlySpan<ushort> states, bool zoneIn, uint directorId)
    {
        if (states.Length > MapEffectSnapshotPacket.Capacity)
            throw new ArgumentOutOfRangeException(nameof(states), states.Length, "MapEffect 快照最多 128 槽");
        var packet = new MapEffectSnapshotPacket
        {
            DirectorId = directorId,
            Header = zoneIn ? MapEffectSnapshotPacket.HeaderZoneIn : MapEffectSnapshotPacket.HeaderReset,
        };
        for (var i = 0; i < states.Length; i++) packet.States[i] = states[i];
        return packet;
    }

    // D788：上游 PlayerConditions.Send 的組包。The packet carries every networked condition: bit n is
    // the n-th IsNetworked Condition row by RowId. 我方只換 overrideRow 一列，其餘照 current（呼叫端
    // 傳 Conditions 記憶體本身，handler 比對的就是同一塊，所以其他列寫入值與現值相同、不會被改）。
    // 超過 bits 容量（16 bytes＝128 列）的列不寫，同上游。
    internal const int PlayerStateFlagsLength = 16;

    internal static void PlayerStateFlags(ReadOnlySpan<uint> networkedRows, ReadOnlySpan<byte> current,
        uint overrideRow, bool overrideValue, Span<byte> bits)
    {
        bits.Clear();
        for (var bit = 0; bit < networkedRows.Length && bit < bits.Length * 8; bit++)
        {
            var row = networkedRows[bit];
            var on = row == overrideRow ? overrideValue : row < (uint)current.Length && current[(int)row] != 0;
            if (on) bits[bit >> 3] |= (byte)(1 << (bit & 7));
        }
    }

    // D788：混亂旗標（63）要送的值，null＝不送。舉起只在我們要、而 client 還沒亮時；放下只在是我們
    // 舉的（raised）且仍亮著時——不是我們舉的 63（真伺服器）永遠不清。
    internal static bool? ControlAfflictionSend(bool wanted, bool current, bool raised) =>
        wanted ? (current ? null : true) : (raised && current ? false : null);

    // 送完回讀後的持有狀態：回讀等於送出值才算數；不符（handler 跳過該列）就維持原狀，下一次同步再送。
    // 沒送時：要鎖就維持；不要鎖且 63 已被別人放下，就不再算我們持有。
    internal static bool ControlAfflictionRaised(bool wanted, bool raised, bool? sent, bool currentAfter) =>
        sent is { } value
            ? (currentAfter == value ? value : raised)
            : (wanted ? raised : raised && currentAfter);
}
