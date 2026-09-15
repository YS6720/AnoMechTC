using FFXIVClientStructs.FFXIV.Client.Game.Object;
using System;
using System.Text;

namespace AnoMech.Helpers;

public static unsafe class GameObjectHelper
{
    public static void WriteName(GameObject* obj, string name)
    {
        Span<byte> encoded = stackalloc byte[64];
        var written = 0;
        foreach (var rune in name.EnumerateRunes())
        {
            if (!rune.TryEncodeToUtf8(encoded.Slice(written, 63 - written), out var count))
                break;
            written += count;
        }
        encoded[written..].Clear();
        for (var i = 0; i < encoded.Length; i++)
            obj->Name[i] = encoded[i];
    }
}
