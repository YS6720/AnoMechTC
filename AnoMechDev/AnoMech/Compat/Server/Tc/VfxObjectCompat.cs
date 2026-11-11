// 台服 FFXIVClientStructs 0.0.6966 的 VfxObject 沒有宣告 SomeFlags，也沒有
// CleanupRender 這個 virtual function。兩者都能安全補回來，理由是**佈局經過比對**：
//
// 兩版 VfxObject 所有「都有宣告」的欄位位移完全一致
//   DrawObject 0x0／ParentObject 0x18／Position 0x50／Rotation 0x60／Scale 0x70／
//   Flags 0x88／VfxResourceInstance 0x2A0
// ⇒ struct 佈局相同，台服 CS 只是沒替 0x248 這格命名。
//
// vtable 同理：台服宣告的 Dtor=0、GetObjectType=2 與國際服索引一致
// ⇒ 國際服的 CleanupRender=1 在台服也是 1。
//
// 完整對照表見 docs/upstream/TC-CS-BITFIELDS.md。台服升版後要重跑探針重驗。

using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace AnoMech.Compat;

internal static unsafe class VfxObjectCompat
{
    private const int SomeFlagsOffset = 0x248;
    private const int CleanupRenderVtblIndex = 1;

    /// <summary>國際服 CS 稱為 <c>SomeFlags</c> 的那一格（偶爾會讓 vfx 隱形的旗標）。</summary>
    public static ref byte SomeFlags(VfxObject* vfx)
        => ref *(byte*)((nint)vfx + SomeFlagsOffset);

    /// <summary>Graphics.Scene.Object 的 vtable[1]；台服 CS 未宣告，直接走 vtable 呼叫。</summary>
    public static void CleanupRender(VfxObject* vfx)
    {
        if (vfx == null) return;
        var vtbl = *(void***)vfx;
        if (vtbl == null) return;
        ((delegate* unmanaged<VfxObject*, void>)vtbl[CleanupRenderVtblIndex])(vfx);
    }
}
