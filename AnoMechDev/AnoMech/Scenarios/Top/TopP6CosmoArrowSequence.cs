using System;
using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.SimObjects;
using static AnoMech.Scenarios.Top.TopConstants;
using static AnoMech.Scenarios.Top.TopP6CosmoArrow;

namespace AnoMech.Scenarios.Top;

// Both P6 scenarios use the original Exasquares/WC2 actors, casts and timing.
internal static class TopP6CosmoArrowSequence
{
    internal static void Run(SimWorld world, DamageSolver damage, bool inFirst, float delay = 0f)
    {
        var (early, initial) = Pattern(inFirst);
        for (var i = 0; i < 12; i++)
        {
            SimEnemy? helper = null;
            world.Events.Add(0f, () => helper = world.SpawnEnemy(new EnemySpawnConfig(
                BNpcBaseId: BNpcBaseId.OmegaHelper, NameId: BNpcNameId.AlphaOmega, Level: 1,
                Targetable: false, EnemyList: EnemyListMode.Never, IsVisible: false,
                Placement: new Placement(new Vector3(0.200f, 0f, 19.320f), 3.140f))));

            var offset = initial[i / 4];
            var isEarly = early[i / 4];
            var placement = i % 2 == 0
                ? new Placement(new(-20f, 0f, offset), MathF.PI / 2)
                : new Placement(new(offset, 0f, -20f), 0f);
            var action = i % 4 > 1 ? ActionId.Inhale : ActionId.CosmoArrowOmen;
            world.Events.Add(delay + (isEarly ? 1.82f : 3.82f), () => helper?.SetPosition(placement));
            world.Events.Add(delay + (isEarly ? 1.90f : 3.91f), () => helper?.Cast(action, targetId: helper?.GameObjectId));
            if (action == ActionId.CosmoArrowOmen)
                world.Events.Add(delay + (isEarly ? 9.90f : 11.91f), () =>
                    damage.Resolve(helper, ActionId.CosmoArrowOmen, [DamageType.Lethal], []));

            var waves = Init(early, initial, []);
            for (var pulse = 0; pulse < 7; pulse++)
            {
                var index = 11 - i;
                if (index < waves.Count * 2)
                {
                    var line = waves[index / 2].Line;
                    var next = index % 2 == 0
                        ? new Placement(new(-20f, 0f, line), MathF.PI / 2)
                        : new Placement(new(line, 0f, -20f), 0f);
                    var delta = delay + pulse * 2f;
                    world.Events.Add(11.82f + delta, () => helper?.SetPosition(next));
                    world.Events.Add(11.91f + delta, () => helper?.Cast(ActionId.CosmoArrowDamage, castSeconds: 0f));
                    world.Events.Add(11.91f + delta, () =>
                        damage.Resolve(helper, ActionId.CosmoArrowDamage, [DamageType.Lethal], []));
                }
                waves = pulse == 0 ? Init(early, initial, waves) : Progress(waves);
            }
        }
    }
}
