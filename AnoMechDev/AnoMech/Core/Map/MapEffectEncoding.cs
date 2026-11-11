using System;

namespace AnoMech.Core.Map;

// MapController.AddEffect 的 packetFlags 是我方沿用的舊編碼（上游 b32fbb8 以前）：high16＝state
// （ProcessMapEffect 第 3 參數）、low8＝flags（第 4 參數）。上游 b32fbb8（2026-09-23）起改用 MapEffect
// 封包本身的值：low16＝state、high16＝timeline；UMAD 原檔（B10a 起照上游 1646a70）都是這種值，直接
// 交給 AddEffect 會把兩個參數對調（P2 Forsaken 的塔出現 3 秒就被收掉）。FromPacket 換成我方編碼，
// 送進原生函式的 (state, timeline) 與上游相同。
internal static class MapEffectEncoding
{
    public static (ushort State, ushort Flags) Decode(uint packetFlags)
        => ((ushort)(packetFlags >> 16), (ushort)(packetFlags & 0xFF));

    public static uint FromPacket(uint packet)
    {
        var timeline = packet >> 16;
        if (timeline > 0xFF)
            throw new ArgumentOutOfRangeException(nameof(packet),
                $"MapEffect 0x{packet:X8}：timeline 0x{timeline:X} 超出我方編碼的 8-bit flags，無法照上游送出。");
        return ((packet & 0xFFFF) << 16) | timeline;
    }
}
