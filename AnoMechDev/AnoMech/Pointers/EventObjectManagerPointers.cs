using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace AnoMech.Pointers;

internal unsafe class EventObjectManagerPointers
{
    [Signature("48 89 5C 24 ?? 48 89 6C 24 ?? 57 41 54 41 57 48 83 EC ?? 8B", UseFlags = SignatureUseFlags.Pointer, ScanType = ScanType.Text)]
    public static CreateEventObjectDelegate CreateEventObject { get; private set; } = null!;

    // 上游 sig 只有 4 bytes「83 FA 27 77」(cmp edx,0x27/ja)＝一個「index≤39」的 bounds
    // check，台服 binary 內 3 處命中（另 2 處是別的函式裡的 16-bit 66 83 FA 27）；
    // ScanText 取最低位址＝錯函式 ⇒ 塔/地面圈生成壞掉。延長到真函式獨特尾段
    // （movsxd rax,edx / mov rax,[rcx+rax*8+0x10]）後唯一命中（RVA 0x1634D80）。
    [Signature("83 FA 27 77 09 48 63 C2 48 8B 44 C1 10", UseFlags = SignatureUseFlags.Pointer, ScanType = ScanType.Text)]
    public static GetEventObjectByIndexDelegate GetEventObjectByIndex { get; private set; } = null!;

    public delegate int CreateEventObjectDelegate(EventObjectManager* thisPtr, uint entityId, uint eObjId, ulong a4, uint layoutId, uint gimmickId, int objectIndex, byte flag);
    public delegate GameObject* GetEventObjectByIndexDelegate(EventObjectManager* eventObjectManager, uint index);

    public static void Initialize()
    {
        Plugin.GameInterop.InitializeFromAttributes(new EventObjectManagerPointers());
    }
}
