using System.Collections.Generic;
using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.SimObjects;

namespace AnoMech.Scenarios.Recorded;

/// <summary>
/// 由場景資料的 <c>zone</c> 欄直接組出來的場地。
///
/// 為什麼要它：在此之前，每個階段都得手寫一個 IZone（territory／中心／半徑／品級）
/// 加一個 IScenario 再註冊進 Game.cs，錄完才「能練」的迴圈就斷在這三步。
/// zone 的四個數字全部量得出來（轉換器由錄影算），沒有一項需要人決定。
///
/// 品級同步查 <c>ContentFinderCondition.ItemLevelSync</c>——手寫時抄錯過（FRU 735 是對的，
/// 但零式是 0＝不同步，兩者長得一樣卻意義相反）。查表就不會抄錯。
/// </summary>
public sealed class RecordedZone : IZone
{
    private readonly RecordedTimeline.ZoneInfo info;

    public RecordedZone(string name, RecordedTimeline.ZoneInfo info, IReadOnlyList<float[]> waymarks)
    {
        Name = name;
        this.info = info;
        Origin = info.Center.Length >= 3
            ? new Vector3(info.Center[0], info.Center[1], info.Center[2])
            : new Vector3(100f, 0f, 100f);
        ItemLevel = ResolveItemLevelSync(info.Territory);
        var recorded = new List<Waymark>();
        foreach (var w in waymarks)
            if (w.Length >= 3 && w[0] is >= 0 and < 8)
                recorded.Add(new Waymark((WaymarkSlot)(int)w[0], new Vector3(w[1], 0f, w[2])));
        // 錄影標點排前面＝預設：那是實戰當下真的擺的位置，比幾何環更貼近攻略敘述
        //（「A 塔」「1 標分攤」對得上）。錄影沒擺標點時才只剩環。
        WaymarkPresets = recorded.Count > 0
            ?
            [
                new WaymarkLayout("錄影標點", recorded),
                new WaymarkLayout("環（半徑 14）", Core.Game.WaymarkPresets.Ring(14f)),
            ]
            : [new WaymarkLayout("環（半徑 14）", Core.Game.WaymarkPresets.Ring(14f))];
    }

    public string Name { get; }
    public uint TerritoryId => info.Territory;
    public Vector3 Origin { get; }
    public byte Level => info.Level;
    public ushort ItemLevel { get; }
    public float Radius => info.Radius;
    public string PhaseName => string.IsNullOrWhiteSpace(info.Phase) ? "實戰資料" : info.Phase;

    public IReadOnlyList<WaymarkLayout> WaymarkPresets { get; }

    public void Run(SimWorld world)
    {
        // +1＝走出去前的最後保險，不釘在畫出來的半徑——貼邊站是合法的
        //（DSR 2026-08-26 實證：釘在半徑會誤殺站對的人）。
        world.EnforceArenaBoundary(Radius + 1f);
    }

    /// <summary>該 territory 的品級同步（0＝不同步）。查不到就 0，不猜。</summary>
    private static ushort ResolveItemLevelSync(uint territory)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ContentFinderCondition>();
        foreach (var row in sheet)
            if (row.TerritoryType.RowId == territory)
                return row.ItemLevelSync;
        return 0;
    }
}
