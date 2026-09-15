using System;
using System.Threading;

namespace AnoMech.Core.Map;

// Immutable-at-publication packet opcode snapshot. The receive detour only reads
// this array; it never consults Configuration or allocates per packet.
internal sealed class OpcodeAllowlist
{
    private ushort[] snapshot = Array.Empty<ushort>();

    internal OpcodeAllowlist()
    {
    }

    internal int Count => Volatile.Read(ref snapshot).Length;

    internal bool Contains(ushort opcode)
    {
        var values = Volatile.Read(ref snapshot);
        return Array.BinarySearch(values, opcode) >= 0;
    }

    // The caller may continue to mutate its input after this call. Sorting the
    // clone before the volatile publication keeps every observed snapshot valid.
    internal void Publish(ReadOnlySpan<ushort> opcodes)
    {
        var next = opcodes.ToArray();
        Array.Sort(next);
        Volatile.Write(ref snapshot, next);
    }
}
