using System.Collections.Generic;
using System.Linq;
using AnoMech.Core.EnemyActions;
using AnoMech.Core.Game;

namespace AnoMech.Core.SimObjects;

// 移植自上游 anomek/AnoMech 1646a70 Core/SimObjects/SimHelperPool.cs；挑 helper 的規則抽到 HelperPoolRules（純函式）。
// The zone's pooled invisible helpers (SimWorld.SpawnHelperPool), each cast by one taken for it. On
// the helper, in the cast's frame, retail sends: an unlink if it is still linked, the name the action
// is shown under, the link to the actor it acts for, the teleport, the cast. Ending a cast sends
// nothing; a linked helper is unlinked on its own once that actor dies or leaves, or its cast is
// cancelled. The helpers themselves are ordinary world children, so the world's teardown covers them.
internal sealed class SimHelperPool : ISimObject
{
    // In spawn order: retail takes the highest-index free one.
    private readonly List<SimEnemy> helpers;
    private readonly Dictionary<SimEnemy, SimEnemy> linkedTo = new();

    internal SimHelperPool(List<SimEnemy> helpers) => this.helpers = helpers;

    public bool IsActive => true;

    // False when every helper is still busy with an earlier cast; nothing is sent then.
    public bool Cast(SimEnemy parent, EnemyAction action, CastTarget target, Placement at, byte animationVariation)
    {
        var index = HelperPoolRules.PickFree(helpers, static h => h.IsActive, static h => h.BusyFor);
        if (index < 0) return false;
        var helper = helpers[index];
        Unlink(helper);
        helper.ActorControl.SetName(action.ShownAs ?? parent.HelperName ?? parent.NameId);
        var parentId = parent.GameObjectId.ObjectId;
        if (parent.IsAlive() && parentId != 0)
        {
            helper.ActorControl.SetOwner(parentId);
            linkedTo[helper] = parent;
        }
        helper.SetPosition(at);
        helper.Cast(action, target, animationVariation);
        return true;
    }

    public void Unlink(SimEnemy helper)
    {
        if (linkedTo.Remove(helper)) helper.ActorControl.ClearOwner();
    }

    public void Tick(float deltaSeconds)
    {
        foreach (var (helper, parent) in linkedTo.ToList())
            if (!parent.IsAlive())
                Unlink(helper);
    }

    public void Despawn()
    {
        linkedTo.Clear();
        helpers.Clear();
    }
}
