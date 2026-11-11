using AnoMech.Compat.Server;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace AnoMech.Pointers;

internal unsafe class EventFrameworkPointers
{
    [Signature("E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? E8 ?? ?? ?? ?? 8B 54 24 70 48 8B C8 E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? E8 ?? ?? ?? ?? 0F", UseFlags = SignatureUseFlags.Pointer, ScanType = ScanType.Text)]
    public static InitDirectorDelegate InitDirector { get; private set; } = null!;

    [Signature("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 70 48 8D B1", UseFlags = SignatureUseFlags.Pointer, ScanType = ScanType.Text)]
    public static TerminateDirectorDelegate TerminateDirector { get; private set; } = null!;

    [Signature("89 54 24 10 48 89 4C 24 ?? 53 56 57 41 55 41 57 48 83 EC 30 48 8B 99", UseFlags = SignatureUseFlags.Pointer, ScanType = ScanType.Text)]
    public static SetDirectorDataDelegate SetDirectorData { get; private set; } = null!;

    // 台服是手刻 sig 的 7 參數函式，國際服走 CS 具名成員（9 參數）包成同一個 delegate；
    // 兩服的解析方式與定位來源見 Compat/Server/{Tc,Global}/ServerNative.cs。
    public static ProcessDirectorUpdateDelegate ProcessDirectorUpdate { get; private set; } = null!;

    // 共用 delegate 為台服的 7 參數——不要照國際服 / 上游補回 arg5/arg6（國際版在伺服器層補 0）。
    public delegate void ProcessDirectorUpdateDelegate(
        EventFramework* thisPtr, EventId eventId, uint category,
        uint arg1, uint arg2, uint arg3, uint arg4);
    public delegate void InitDirectorDelegate(EventFramework* thisPtr, EventId eventId, uint contentId, uint flags);
    public delegate void TerminateDirectorDelegate(EventFramework* thisPtr, EventId eventId);
    public delegate void SetDirectorDataDelegate(EventFramework* thisPtr, EventId eventId, byte sequence, byte unknown, byte* unionDataBuffer, ulong length);

    public static void Initialize()
    {
        Plugin.GameInterop.InitializeFromAttributes(new EventFrameworkPointers());
        ProcessDirectorUpdate = ServerNative.ResolveProcessDirectorUpdate();
    }
}
