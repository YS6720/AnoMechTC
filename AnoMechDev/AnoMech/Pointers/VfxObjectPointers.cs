using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace AnoMech.Pointers;

// 台服 FFXIVClientStructs 0.0.6966 的 VfxObject 只宣告了少數欄位，
// Create / Update 這兩個 member function 完全沒有。以下 signature 取自國際服 CS 的
// [MemberFunction]，並已離線在台服 ffxiv_dx11.exe 的 .text 掃過，**兩者皆唯一命中**：
//   Create → RVA 0x6F1B87 ／ Update → RVA 0x7257AB
// （抽取與掃描方式見 docs/upstream/TC-CS-BITFIELDS.md）
//
// 兩個 sig 都以 E8 開頭＝比對的是 call 指令而非函式本體；Dalamud 的 ScanText 會自動
// 沿 rel32 解析到被呼叫的函式，所以這裡不需要自己算偏移。
internal unsafe class VfxObjectPointers
{
    [Signature("E8 ?? ?? ?? ?? F3 0F 10 35 ?? ?? ?? ?? 48 89 43 08", UseFlags = SignatureUseFlags.Pointer, ScanType = ScanType.Text)]
    public static CreateDelegate Create { get; private set; } = null!;

    [Signature("E8 ?? ?? ?? ?? ?? ?? ?? 8B 4A ?? 85 C9", UseFlags = SignatureUseFlags.Pointer, ScanType = ScanType.Text)]
    public static UpdateDelegate Update { get; private set; } = null!;

    public delegate VfxObject* CreateDelegate(byte* path, byte* pool);
    public delegate void UpdateDelegate(VfxObject* thisPtr, float deltaSeconds, int unk);

    public static void Initialize()
    {
        Plugin.GameInterop.InitializeFromAttributes(new VfxObjectPointers());
    }
}
