// 國際服 FFXIVClientStructs 宣告了 VfxObject.SomeFlags（0x248）與 CleanupRender()，
// 直接用具名成員（上游 fork 點 v0.3.12.0 VfxFunctions.cs:63,72 同寫法）。
// 台服版直寫位移／走 vtable，見 Compat/Server/Tc。

using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace AnoMech.Compat;

internal static unsafe class VfxObjectCompat
{
    /// <summary>偶爾會讓 vfx 隱形的旗標。</summary>
    public static ref byte SomeFlags(VfxObject* vfx) => ref vfx->SomeFlags;

    public static void CleanupRender(VfxObject* vfx)
    {
        if (vfx == null) return;
        vfx->CleanupRender();
    }
}
