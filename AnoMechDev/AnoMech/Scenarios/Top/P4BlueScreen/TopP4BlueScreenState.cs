using System.Linq;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;

namespace AnoMech.Scenarios.Top.P4BlueScreen;

public sealed class TopP4BlueScreenState(SimParty party, bool? playerStackTarget)
{
    public PartyRole[][] StackTargets { get; } = Enumerable.Range(0, 3)
        .Select(_ => new RoleListBuilder { Size = 2, IncludePlayer = playerStackTarget }.Build(party).List).ToArray();

    public bool[] Sides(int round) => TopP4BlueScreenRules.StackSides(StackTargets[round][0], StackTargets[round][1]);
}
