using System.Collections.Generic;
using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.SimObjects;
using static AnoMech.Scenarios.Top.TopConstants;

namespace AnoMech.Scenarios.Top;

public sealed class TopZone : IZone
{
    public static readonly TopZone Instance = new();
    public static readonly Phase P2 = new(Instance, "P2", 78, BgmId.TopP2);
    public static readonly Phase P3 = new(Instance, "P3", 79, 0);
    // Source SHA 09474e88... uses P4 weather 78/BGM 0; keep the existing P3
    // weather 79 unchanged because it is a separate phase contract.
    public static readonly Phase P4 = new(Instance, "P4", 78, 0);
    public static readonly Phase P5 = new(Instance, "P5", 174, BgmId.TopP5);
    public static readonly Phase P6 = new(Instance, "P6", 175, BgmId.TopP6);

    public string Name => "The Omega Protocol";
    public uint TerritoryId => 1122;
    public Vector3 Origin => new(100f, 0f, 100f);
    public byte Level => 90;
    public ushort ItemLevel => 365;

    public IReadOnlyList<WaymarkLayout> WaymarkPresets { get; } =
        [new WaymarkLayout("Ring", TopUtils.TopWaymarks)];

    public void Run(SimWorld world)
    {
        world.EnforceArenaBoundary(Geometry.ArenaRadius);
        // Server MapEffect arena setup; no-op outside TOP.
        world.Events.Add(1f, () => InitTopArena(world));
    }
    
    private void InitTopArena(SimWorld world)
    {
        for (byte i = 1; i <= 8; i++)
            world.Map.AddEffect(0x00040004, i); // hide eyes
        world.Map.AddEffect(0x00020002, 0x00); // show death wall
    }
}
