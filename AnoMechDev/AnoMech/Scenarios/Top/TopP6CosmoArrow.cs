using System.Collections.Generic;
using System.Linq;

namespace AnoMech.Scenarios.Top;

// Shared by the opening and Exasquares/WC2. Keep the original P6 propagation:
// 10y initial strips split into 5y strips at ±7.5y, then advance 5y every pulse.
internal static class TopP6CosmoArrow
{
    internal readonly record struct Wave(float Line, float Dir);

    internal static (bool[] Early, float[] Initial) Pattern(bool inFirst) =>
        ([false, !inFirst, true], [inFirst ? -15 : 0, 15, inFirst ? 0 : -15]);

    internal static List<Wave> Init(bool[] early, float[] initial, List<Wave> current)
    {
        var isEarly = current.Count == 0;
        var waves = Progress(current);
        for (var i = 0; i < initial.Length; i++)
        {
            if (early[i] != isEarly) continue;
            waves.Add(new(initial[i] + 7.5f, 1));
            waves.Add(new(initial[i] - 7.5f, -1));
        }
        waves.RemoveAll(wave => wave.Line is > 20f or < -20f);
        return waves;
    }

    internal static List<Wave> Progress(List<Wave> current) => current
        .Select(wave => wave with { Line = wave.Line + 5 * wave.Dir })
        .Where(wave => wave.Line is <= 20f and >= -20f)
        .ToList();
}
