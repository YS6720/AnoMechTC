using AnoMech.Core.Game;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;

namespace AnoMech.Core.Combat.Jobs;

internal enum JobResource { Addersting, DarkArts, Kenki }

// Host-owned, source-qualified transitions. All job grants/removals pass through
// the runtime so an older delayed catalog outcome cannot resurrect a consumed proc.
internal readonly struct JobActionContext(
    RecordedAbilityRuntime runtime, uint actionId, bool comboOk,
    SimCharacter player, PartyRole sourceRole, byte level,
    SimCharacter? target, SimWorld world)
{
    internal uint ActionId { get; } = actionId;
    internal bool ComboOk { get; } = comboOk;
    internal SimCharacter Player { get; } = player;
    internal PartyRole SourceRole { get; } = sourceRole;
    internal byte Level { get; } = level;
    internal SimCharacter? Target { get; } = target;
    internal SimWorld World { get; } = world;

    internal SimStatus? Find(ushort statusId, SimCharacter? recipient = null)
        => (recipient ?? Player).FindStatus(statusId, SourceRole);

    internal void Grant(ushort statusId, int param, float duration, SimCharacter? recipient = null)
        => runtime.ApplyJobStatus(Player, SourceRole, recipient ?? Player, statusId, param, duration);

    internal void Remove(ushort statusId, SimCharacter? recipient = null)
        => runtime.RemoveJobStatus(SourceRole, recipient ?? Player, statusId);

    internal void AddResource(JobResource resource, int amount)
        => runtime.ChangeJobResource(SourceRole, resource, amount);

    internal void Consume(ushort statusId, int count = 1, SimCharacter? recipient = null)
    {
        var status = Find(statusId, recipient);
        if (status is null) return;
        if (status.Stacks <= count) Remove(statusId, recipient);
        else Grant(statusId, status.Stacks - count, status.RemainingTime, recipient);
    }

    internal void GrantParty(ushort statusId, int param, float duration, float radius)
    {
        foreach (var member in World.Party.ActiveMembers())
        {
            if (!member.IsActive || member is ISimPartyMember { Dead: true }) continue;
            if (Player.Placement().DistanceSq(member) <= radius * radius)
                Grant(statusId, param, duration, member);
        }
    }
}
