using System;
using AnoMech.Core.Combat;
using AnoMech.Core.SimObjects;

namespace AnoMech.Core.Game.Party;

// 上游 anomek/AnoMech 1646a70 Core/Game/Party/LimitBreakGauge.cs 的 Set 入口（名稱與簽章同上游）。
// 上游是假造原生量表；我方轉接場景的 PracticeLimitBreakRuntime：練習只模擬 LB3，
// 滿 3 格＝可用、不足＝不可用，量表顯示沿用 RotationSim 依 HasGaugeCharge 的做法。
// 只動「自己建立且仍掛在 world 上」的 runtime：場景自建的（TOP P6）與連線 replica 不碰；
// 連線成員端 no-op，把 null 留給 MultiplayerManager.ApplyRoles 建 replica。
// runtime 的生命週期照舊由 SimWorld.Despawn 處置。
public sealed class LimitBreakGauge
{
    private const float Bars = 3f;

    private readonly SimWorld? world;
    private PracticeLimitBreakRuntime? owned;

    // world 為 null＝SimParty.Empty，Set 為 no-op。
    internal LimitBreakGauge(SimWorld? world) => this.world = world;

    public void Set(float bars)
    {
        if (world is null || Plugin.GameInstance is { IsNetworkPeer: true }) return;
        if (world.LimitBreaks is null)
        {
            owned = new PracticeLimitBreakRuntime(world, static (_, _, _, _) => { });
            world.LimitBreaks = owned;
        }
        else if (!ReferenceEquals(world.LimitBreaks, owned))
        {
            return;
        }
        owned.SetCharge(Math.Clamp(bars, 0f, Bars) >= Bars);
    }
}
