using AnoMech.Core.Game;
using AnoMech.Helpers;
using AnoMech.Pointers;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using System.Numerics;

namespace AnoMech.Core.SimObjects;

public class EventObjectSpawnConfig
{
    // References Lumina's EObj sheet,
    // the row's SgbPath/PopType drives the model picked by the engine's internal SharedGroup attach.
    // ModelChara substitution is not part of the EObj pipeline; pick the right EObj row.
    public uint EObjId { get; init; }

    // Placement.Position is scenario-local (offset from SimWorld.ScenarioOrigin),
    // same coordinate space as SimEventObject.Position / SetPosition once spawned.
    public Placement Placement { get; init; }

    public sbyte ObjectIndex { get; init; } = -1;
    public byte TargetableStatus { get; init; } = 1; // 1 - untargettable
    public byte VisibilityFlag { get; init; } = 0;
    public uint EntityId { get; init; } = 0;
    // Legacy callers keep their packet-provided EntityId; P6 opts into a
    // per-pool-slot synthetic identity for EventState dispatch.
    public bool UseSyntheticEntityId { get; init; } = false;
    public uint LayoutId { get; init; } = 0;
    public EventId EventId { get; init; } = 0;
    public uint OwnerId { get; init; } = 0xE0000000;
    public uint GimmickId { get; init; } = 0;
    public float Radius { get; init; } = 1;
    public ushort FateId { get; init; } = 0;
    public byte EventState { get; init; } = 0;

    // This is the SG state index that means "visible" for this EObj.
    // The orb (1EB83C) is already visible at the engine default state=0, so leave it at 0.
    // The Sigma ground circles (1EB83D / 1EB83E) need state=16 to render fully
    // state=1..6 partial-renders are the engine's player-proximity animation frames. SetVisible toggles between this value and 0.
    public ushort TimelineState { get; init; } = 0;

    public bool SpawnVisible { get; init; } = true;
    public float Lifetime { get; init; } = 0;

    public unsafe SpawnObjectPacket ToPacket(Coordinates coordinates)
    {
        var objectIndex = sbyte.Max(-1, ObjectIndex);
        var worldPos = coordinates.ToGlobal(Placement.Position);

        var packet = new SpawnObjectPacket
        {
            ObjectIndex = (byte)objectIndex,
            ObjectKind = 7, // EventObject
            TargetableStatus = TargetableStatus,
            Visibility = VisibilityFlag,
            BaseId = EObjId,
            EntityId = EntityId,
            LayoutId = LayoutId,
            EventId = EventId,
            OwnerId = OwnerId,
            GimmickId = GimmickId,
            Radius = Radius,
            Rotation = MathUtil.QuantizeRotation(Placement.Rotation),
            FateId = FateId,
            EventState = EventState,
            PositionX = worldPos.X,
            PositionY = worldPos.Y,
            PositionZ = worldPos.Z
        };

        // private in CS, so we have to set it manually
        var packetBytePtr = (byte*)&packet;
        var timelineStatePtr = (ushort*)(packetBytePtr + 0x2C);
        *timelineStatePtr = TimelineState;

        return packet;
    }
}

// Handle around an EventObject GameObject allocated via the manager's
// CreateEventObject (the 40-slot pool exposed in GameObjectManager indices
// 449-488). Mirror of SimEnemy / SimNpc for the EObj actor pool: we own the
// slot, write position/rotation/state directly on the GameObject, and release
// the slot on Despawn via GameObject.Terminate (vfunc 60).
//
// Rendering note: EObjs render via the LayoutEngine scene graph using their
// attached SharedGroup, NOT via GameObject.DrawObject. Visibility is therefore
// driven by the state field at actor+0x1B2 (which gates which SG sub-instances
// are visible), not by EnableDraw/DisableDraw — those are character-only.
//
// Spawn uses HandleSpawnObjectPacket to allocate the EObj pool slot; after
// creation this wrapper applies direct position/rotation/state updates and
// releases that owned slot through HandleDespawnObjectPacket.
public unsafe class SimEventObject : ISimObject, IPositioned
{
    private int slot = -1;
    private GameObject* obj;
    private readonly Coordinates coordinates;
    private readonly ushort visibleState;
    private readonly float lifetime;
    // Only non-zero for wrappers spawned with UseSyntheticEntityId.
    private readonly uint ownedEntityId;
    // SetState can run before the asynchronous SharedGroup attachment. The
    // native call stores the state but cannot notify the layout until it is ready.
    private bool stateApplyPending;

    public uint EObjRowId { get; }
    public string DisplayName => $"EObj 0x{EObjRowId:X}";
    public int Slot => slot;
    public nint Address => (nint)obj;

    public uint LayoutId { get; }
    public ushort TimelineState => visibleState;
    public ushort CurrentState { get; private set; }

    public bool IsAlive => slot >= 0 && obj != null;
    // No death-vs-presence distinction for event objects: kept while the slot is live.
    public virtual bool IsActive => IsAlive;

    // Stored Position/Rotation mirror the native GameObject — mutators write
    // both, and Tick re-syncs from native to catch any direct-struct writes.
    public Vector3 Position { get; private set; }
    public float Rotation { get; private set; }

    private float lifetimeElapsed { get; set; } = 0;

    protected SimEventObject(int slot, GameObject* obj, Coordinates coordinates, uint eObjRowId,
        ushort visibleState, float lifetime, uint ownedEntityId = 0, uint layoutId = 0,
        ushort currentState = 0)
    {
        this.slot = slot;
        this.obj = obj;
        this.coordinates = coordinates;
        this.visibleState = visibleState;
        this.lifetime = lifetime;
        this.ownedEntityId = ownedEntityId;
        EObjRowId = eObjRowId;
        LayoutId = layoutId;
        CurrentState = currentState;
    }

    internal static SimEventObject? Spawn(EventObjectSpawnConfig config, Coordinates coordinates, EventScheduler events)
    {
        var packet = config.ToPacket(coordinates);

        if (!EventObjectHelper.Create(&packet, out var slot, out var eObjPtr, config.UseSyntheticEntityId))
        {
            return null;
        }

        var eObj = new SimEventObject(slot, eObjPtr, coordinates, config.EObjId, config.TimelineState,
            config.Lifetime, config.UseSyntheticEntityId ? packet.EntityId : 0u,
            config.LayoutId, config.TimelineState);

        if (!config.SpawnVisible && config.TimelineState != 0)
        {
            eObj.SetVisible(false);
        }

        Plugin.Log.Info($"[SimEventObject.Create] Spawned EObj with EObjId 0x{config.EObjId:X} at Slot: {slot} ({packet.PositionX:F2}, {packet.PositionY:F2}, {packet.PositionZ:F2})");
        return eObj;
    }

    public void SetPosition(Vector3 position)
    {
        Position = position;
        if (obj == null) return;
        var w = coordinates.ToGlobal(position);
        obj->SetPosition(w.X, w.Y, w.Z);
    }

    public void SetPosition(Placement placement)
    {
        Position = placement.Position;
        Rotation = MathUtil.NormalizeRotation(placement.Rotation);
        if (obj == null) return;
        var w = coordinates.ToGlobal(placement.Position);
        obj->SetPosition(w.X, w.Y, w.Z);
        obj->SetRotation(Rotation);
    }

    // Writes the EObj state field at actor+0x1B2 and (when the SharedGroup
    // layout instance is attached at actor+0x108) notifies the SG to flip
    // sub-instance visibility. Per-EObj state values are SG-specific —
    // experiment empirically to find what activates a given visual. Safe to
    // call before the SG instance is attached: the field write is retained,
    // and Tick retries the notify once the layout becomes ready.
    public void SetState(ushort state)
    {
        CurrentState = state;
        if (obj == null) return;
        EventObjectHelper.SetState(obj, state);
        stateApplyPending = obj->SharedGroupLayoutInstance == null;
    }

    // Stable snapshots must not retrigger SG state; pending attachment still needs replay.
    internal void ApplyNetworkState(ushort state)
    {
        if (CurrentState != state || stateApplyPending)
            SetState(state);
    }

    // Dispatches EventState through ActorControl category 106 for this
    // wrapper's own synthetic entity only; this is separate from SetState,
    // which switches the attached SharedGroup timeline state.
    public void SetEventState(byte state)
    {
        if (!IsAlive ||
            ownedEntityId == 0 ||
            ownedEntityId == 0xE0000000 ||
            ownedEntityId != 0xE0000000u + 449u + (uint)slot)
        {
            return;
        }

        PacketDispatcherPointers.HandleActorControlPacket(
            ownedEntityId, 106, state, 0, 0, 0, 0, 0, 0xE0000000, 0, 0, false);
    }

    // Convenience for parser-driven scenarios that emit SetVisible from
    // ACT 261|Change ModelStatus events. Flips between the configured
    // VisibleState and 0 (the engine default / "hidden" for gated SGs).
    public void SetVisible(bool visible) => SetState(visible ? visibleState : (ushort)0);

    public virtual void Tick(float deltaSeconds)
    {
        // Re-sync stored Position/Rotation from native — catches any
        // direct-struct writes between Ticks (engine doesn't move EObjs on
        // its own, but the parallel pattern with SimNpc keeps the contract
        // uniform across IPositioned implementers).
        if (obj == null)
        {
            return;
        }

        Position = coordinates.ToLocal(obj->Position);
        Rotation = obj->Rotation;

        if (lifetime > 0)
        {
            lifetimeElapsed += deltaSeconds;

            if (lifetimeElapsed >= lifetime)
            {
                Despawn();
                return;
            }
        }

        if (stateApplyPending && obj->SharedGroupLayoutInstance != null)
        {
            EventObjectHelper.SetState(obj, CurrentState);
            stateApplyPending = false;
        }
    }

    public void Despawn()
    {
        if (slot < 0) return;
        var releasedSlot = slot;

        slot = -1;
        obj = null;

        byte[] packet = [(byte)releasedSlot];

        fixed (byte* packetPtr = packet)
        {
            PacketDispatcherPointers.HandleDespawnObjectPacket(0, packetPtr);
        }

        Plugin.Log.Info($"SimEventObject: despawned slot {releasedSlot}");
    }
}
