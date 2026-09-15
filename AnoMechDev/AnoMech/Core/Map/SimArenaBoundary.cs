using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.Native;
using AnoMech.Core.SimObjects;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace AnoMech.Core.Map;

// Per-frame XZ arena fence, owned by SimWorld and cleared on reset.
// Circular callers retain the floor ring and one-yalm grace; P6 opts into a
// square fence without an artificial circular VFX.
internal sealed unsafe class SimArenaBoundary : ISimObject
{
    // Donut omen has a fixed inner/outer ratio of 0.82. Scale by radius/0.82 so the
    // inner edge aligns with the kill boundary (outer edge extends ~4.4y beyond it).
    private const string RingVfxPath = "vfx/omen/eff/gl_sircle_1109w.avfx";

    private readonly SimParty party;
    private readonly ArenaBoundaryBounds bounds;
    private readonly string cause;
    private readonly VfxObject* ringVfx;
    private int outsideRoles;

    public bool IsAlive => true;
    public bool IsActive => true;

    internal SimArenaBoundary(SimParty party, SimWorld world, float radius, string cause, bool showVfx = true)
        : this(party, ArenaBoundaryBounds.Circle(radius), cause)
    {

        if (showVfx && Plugin.DataManager.FileExists(RingVfxPath))
            ringVfx = VfxFunctions.SpawnStaticVfx(RingVfxPath, new Placement(world.ScenarioOrigin, 0f), new Vector3(radius / 0.82f, 1f, radius / 0.82f));
    }

    internal SimArenaBoundary(SimParty party, ArenaBoundaryBounds bounds, string cause)
    {
        this.party = party;
        this.bounds = bounds;
        this.cause = cause;
    }

    // Same geometry for per-frame deaths and external reset/teleport callers.
    internal bool IsOutside(Vector3 local) => bounds.IsOutside(local);

    public void Tick(float deltaSeconds)
    {
        // Keep checking every frame so protection expiry still causes a KO.
        // Only a new boundary entry reports a protected failure.
        var nextOutsideRoles = 0;
        foreach (var member in party.ActiveMembers())
        {
            if (!IsOutside(member.Position)) continue;
            var bit = 1 << (int)((ISimPartyMember)member).Role;
            member.Die(cause, reportProtectedFailure: (outsideRoles & bit) == 0);
            if (member.IsAlive()) nextOutsideRoles |= bit;
        }
        outsideRoles = nextOutsideRoles;
    }

    public void Despawn()
    {
        VfxFunctions.RemoveStaticVfx(ringVfx);
    }
}
