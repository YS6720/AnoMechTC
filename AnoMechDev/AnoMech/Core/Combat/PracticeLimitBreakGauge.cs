using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace AnoMech.Core.Combat;

// Project the practice gauge and restore the native state on leave/unload.
// P6 supplies its shared, dynamis-driven availability; other practices retain one fill.
internal sealed unsafe class PracticeLimitBreakGauge
{
    private bool saved;
    private byte savedBars;
    private ushort savedUnits;
    private ushort savedBarUnits;
    private bool consumed;
    internal bool IsAvailable => saved && !consumed;
    private long generation = -1;

    internal void Update(bool active, long runGeneration, bool? available = null)
    {
        if (!active)
        {
            Restore();
            return;
        }
        var gauge = LimitBreakController.StaticAddressPointers.pInstance;
        // The client territory determines PvP, not the native gauge's cached mode.
        if (gauge == null || Plugin.ClientState.IsPvP || gauge->BarUnits > ushort.MaxValue / 3)
            return;
        if (!saved)
        {
            savedBars = gauge->BarCount;
            savedUnits = gauge->CurrentUnits;
            savedBarUnits = gauge->BarUnits;
            saved = true;
        }
        if (generation == runGeneration)
        {
            if (available is { } ready)
            {
                consumed = !ready;
                gauge->BarCount = 3;
                gauge->CurrentUnits = ready ? (ushort)(3 * gauge->BarUnits) : (ushort)0;
            }
            return;
        }
        generation = runGeneration;
        consumed = available == false;
        // Solo has no native party gauge. One local unit per bar is sufficient:
        // only the ratio is used in practice; restore the original scale on exit.
        if (gauge->BarUnits == 0) gauge->BarUnits = 1;
        gauge->BarCount = 3;
        gauge->CurrentUnits = consumed ? (ushort)0 : (ushort)(3 * gauge->BarUnits);
        Core.CrashTrace.Log("[LB] 練習量表已接管；P6 依潛能量回補，離場還原原值。");
    }

    internal void Consume()
    {
        consumed = true;
        var gauge = LimitBreakController.StaticAddressPointers.pInstance;
        if (gauge != null) gauge->CurrentUnits = 0;
    }

    internal void Restore()
    {
        if (!saved) return;
        var gauge = LimitBreakController.StaticAddressPointers.pInstance;
        if (gauge == null) return;
        gauge->BarCount = savedBars;
        gauge->CurrentUnits = savedUnits;
        gauge->BarUnits = savedBarUnits;
        saved = false;
        consumed = false;
        generation = -1;
    }
}
