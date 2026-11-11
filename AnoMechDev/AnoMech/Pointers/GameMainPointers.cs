using AnoMech.Compat.Server;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AnoMech.Pointers;

internal unsafe class GameMainPointers
{
    // LoadZone 不在 FFXIVClientStructs，兩服各用手刻 sig；字串與定位來源見
    // Compat/Server/{Tc,Global}/ServerNative.cs。delegate 兩服同為 5 參數。
    [Signature(ServerNative.LoadZoneSignature, UseFlags = SignatureUseFlags.Pointer, ScanType = ScanType.Text)]
    public static LoadZoneDelegate LoadZone = null!;

    public delegate void LoadZoneDelegate(GameMain* thisPtr, uint territoryTypeId, uint transitionTerritoryFilterKey, byte a4, byte a5);

    public static void Initialize()
    {
        Plugin.GameInterop.InitializeFromAttributes(new GameMainPointers());
        // 把解析到的位址留在追蹤檔：LoadZone 是手刻 sig（CS 無權威可對），
        // 萬一 native 端崩潰（try/catch 攔不到）時，這是唯一能事後回推「掃到哪」的線索。
        try
        {
            var addr = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(LoadZone);
            var baseAddr = System.Diagnostics.Process.GetCurrentProcess().MainModule!.BaseAddress;
            AnoMech.Core.CrashTrace.Log($"[sig] LoadZone = {addr:X}（module+0x{(long)addr - (long)baseAddr:X}）");
        }
        catch (System.Exception ex)
        {
            AnoMech.Core.CrashTrace.Log($"[sig] LoadZone 位址取得失敗：{ex.Message}");
        }
    }
}
