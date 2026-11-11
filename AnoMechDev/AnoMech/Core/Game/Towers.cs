using AnoMech.Core.EnemyActions;
using AnoMech.Core.SimObjects;

namespace AnoMech.Core.Game;

// 上游 anomek/AnoMech 1646a70 Core/Game/Towers.cs 原檔。
public static class Towers
{
    // For an OnActionResolved handler. A soak taken by fewer than `minSoakers` is punished by the
    // helper that cast it, once its animation lock ends. UNVERIFIED beyond two UMAD towers, both
    // within 0.1s of their 1.1s lock. `delay` counts from the snapshot, for a tower that waits otherwise.
    public static void ResolveTower(SimWorld world, ActionResolved resolved, uint soakId, EnemyAction punishment, int minSoakers, float? delay = null)
    {
        if (resolved.Action.ActionId != soakId || resolved.Hits.Count >= minSoakers || resolved.Caster is not SimEnemy tower) return;
        world.Events.Add(delay ?? resolved.ReleaseDelay + resolved.Action.Cast.AnimationLock, () =>
        {
            if (tower.IsActive) tower.Cast(punishment);
        });
    }
}
