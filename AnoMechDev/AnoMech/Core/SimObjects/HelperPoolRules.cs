using System;
using System.Collections.Generic;

namespace AnoMech.Core.SimObjects;

// SimHelperPool 挑 helper 的規則（移植自上游 anomek/AnoMech 1646a70 SimHelperPool.Free），抽成純函式可單元測試。
internal static class HelperPoolRules
{
    // Retail reuses a helper this long before its animation lock ends: one helper repeats Akh Rhai
    // (0.6s lock) every ~0.55s and the exalines (2.1s lock) every 2.05s.
    public const float EarlyRelease = 0.1f;

    // helpers in spawn order: retail takes the highest-index free one, free once its last cast's bar
    // and ActionEffect are done and its animation lock nearly is. -1 when every helper is busy.
    public static int PickFree<T>(IReadOnlyList<T> helpers, Func<T, bool> isActive, Func<T, float> busyFor)
    {
        for (var i = helpers.Count - 1; i >= 0; i--)
            if (isActive(helpers[i]) && busyFor(helpers[i]) <= EarlyRelease)
                return i;
        return -1;
    }
}
