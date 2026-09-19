using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace AnoMech.Core.Combat;

// A full tank LB for each practice attempt. Never fills the real-world gauge:
// save it on entry and restore it on leave/unload, like RotationSim's recasts.
internal sealed unsafe class PracticeLimitBreakGauge
{
    private bool saved;
    private byte savedBars;
    private ushort savedUnits;
    private long generation = -1;

    internal void Update(bool active, long runGeneration)
    {
        if (!active)
        {
            Restore();
            return;
        }
        var gauge = LimitBreakController.StaticAddressPointers.pInstance;
        if (gauge == null || gauge->IsPvP || gauge->BarUnits == 0 || gauge->BarUnits > ushort.MaxValue / 3)
            return;
        if (!saved)
        {
            savedBars = gauge->BarCount;
            savedUnits = gauge->CurrentUnits;
            saved = true;
        }
        if (generation == runGeneration) return;
        generation = runGeneration;
        gauge->BarCount = 3;
        gauge->CurrentUnits = (ushort)(3 * gauge->BarUnits);
        Core.CrashTrace.Log("[LB] 本輪坦克練習補滿三格；只顯示狀態，不判定減傷。");
    }

    internal void Restore()
    {
        if (!saved) return;
        var gauge = LimitBreakController.StaticAddressPointers.pInstance;
        if (gauge == null) return;
        gauge->BarCount = savedBars;
        gauge->CurrentUnits = savedUnits;
        saved = false;
        generation = -1;
    }
}
