namespace AnoMech.Core.Native.Interfaces;

// 上游 anomek/AnoMech 1646a70 Core/Native/Interfaces/StatusSlot.cs 原檔。
// One StatusManager slot as the server's status-list packet carries it. RemainingTime 0 never
// expires; StatusId 0 is an empty slot.
public readonly record struct StatusSlot(ushort StatusId, ushort Param, float RemainingTime, uint SourceEntityId);
