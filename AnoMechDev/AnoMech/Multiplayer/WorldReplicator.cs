using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core;
using AnoMech.Compat;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace AnoMech.Multiplayer;

// Managed observations emitted by SimCharacter/MapController. These stay
// internal: only WorldReplicator turns them into wire events, after resolving
// local object identities to relay-neutral IDs.
internal abstract record SimNetworkEvent;
internal sealed record SimNetworkWorldEvent(SimCharacter? Actor, WorldEvent Event) : SimNetworkEvent;
internal sealed record SimNetworkCastEvent(
    SimCharacter Actor, uint ActionId, byte ActionType, float CastSeconds, float OmenDelay,
    bool Interruptible, float? Rotation, Vector3? Position, GameObjectId? Target) : SimNetworkEvent;
internal sealed record SimNetworkActionEffectEvent(
    SimCharacter Actor, uint ActionId, float AnimationLock, ushort SpellId, byte AnimationVariation,
    byte ActionType, byte Flags, GameObjectId[] Targets, float? Rotation, Vector3? Position,
    GameObjectId? AnimationTarget) : SimNetworkEvent;
internal sealed record SimNetworkFailureEvent(MpError Error) : SimNetworkEvent;
internal sealed record SimNetworkUnsupportedEvent(SimCharacter? Actor, string Description) : SimNetworkEvent;

// Host captures the managed simulation and emits authoritative state/events.
// Peers apply only validated host state/events. The adapter intentionally has no
// reconnect or takeover behavior; its lifetime is one run generation.
public sealed unsafe class WorldReplicator : IDisposable
{
    private readonly Game game;
    private readonly SimWorld world;
    private readonly NetworkResourceCatalog resources;
    private readonly bool host;
    private readonly PartyRole localRole;
    private readonly Func<bool> isCurrent;
    private readonly Func<PartyRole, GameObjectId> statusSourceResolver;

    private readonly Dictionary<SimEnemy, int> hostEnemies = [];
    private readonly Dictionary<SimEventObject, int> hostEventObjects = [];
    private readonly Dictionary<SimTether, int> hostTethers = [];
    private readonly Dictionary<(SimOmen Omen, int Part), int> hostVisuals = [];
    private readonly Dictionary<int, SimEnemy> peerEnemies = [];
    private readonly Dictionary<Sign, ulong> peerMarkers = [];
    private readonly PartyMarkerSync? peerMarkerSync;
    private readonly Dictionary<int, SimEventObject> peerEventObjects = [];
    private readonly Dictionary<int, SimTether> peerTethers = [];
    private readonly Dictionary<int, SimOmen> peerVisuals = [];
    private readonly WorldEventBuffer eventBuffer = new();
    private bool capturingEvent;
    private int nextNetId = 1;
    private bool disposed;

    public WorldReplicator(Game game, NetworkResourceCatalog resources, bool host,
        PartyRole localRole, Func<bool> isCurrent)
    {
        this.game = game ?? throw new ArgumentNullException(nameof(game));
        world = game.World;
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
        this.host = host;
        peerMarkerSync = host ? null : new PartyMarkerSync();
        this.localRole = localRole;
        this.isCurrent = isCurrent ?? throw new ArgumentNullException(nameof(isCurrent));
        statusSourceResolver = ResolveStatusSource;

        if (host)
        {
            world.SetNetworkEventSink(OnNetworkEvent);
            foreach (var member in world.Party.AllMembers())
                if (member is SimNetworkPuppet puppet)
                    puppet.NetworkControl += OnNetworkControl;
        }
    }

    public WorldSnapshotMessage CaptureWorld()
    {
        EnsureHost();
        EnsureCurrent();

        var enemies = new List<EnemyState>();
        foreach (var enemy in world.Children.OfType<SimEnemy>())
        {
            EnsureCurrent();
            if (!enemy.IsActive) continue;
            enemies.Add(CaptureEnemy(enemy));
        }

        var eventObjects = new List<EventObjectState>();
        foreach (var eventObject in world.Children.OfType<SimEventObject>())
        {
            EnsureCurrent();
            if (!eventObject.IsAlive) continue;
            eventObjects.Add(CaptureEventObject(eventObject));
        }

        var tethers = new List<TetherState>();
        foreach (var tether in world.Children.OfType<SimTether>())
        {
            EnsureCurrent();
            if (!tether.IsActive || tether.A is not { } source || tether.B is not { } target)
                continue;
            tethers.Add(new TetherState(GetTetherId(tether), tether.TetherId,
                ToEntity(source), ToEntity(target)));
        }

        var visuals = new List<StaticVisualState>();
        foreach (var omen in world.Children.OfType<SimOmen>())
        {
            EnsureCurrent();
            if (!omen.HasNetworkVisual || !omen.IsActive) continue;
            if (!omen.IsNetworkVisualReady)
                throw Failure(MpError.NativeFailure);
            if (omen.NetworkPart is { } part)
            {
                if (!resources.TryGetStaticKey(part.ResourcePath, out var key))
                    throw Failure(MpError.UnsupportedResource);
                visuals.Add(new StaticVisualState(GetVisualId(omen, part.Part), key,
                    MpVector.From(part.Position), MpQuaternion.From(part.Rotation), MpVector.From(part.Scale)));
            }
        }

        var snapshot = eventBuffer.Capture(new WorldSnapshotMessage(enemies.ToArray(), eventObjects.ToArray(),
            tethers.ToArray(), visuals.ToArray(), CapturePartyMarkers()));
        if (!WorldValidation.Validate(snapshot)) throw Failure(MpError.InvalidMessage);
        foreach (var enemy in snapshot.Enemies)
            if (!resources.TryValidateEnemy(enemy)) throw Failure(MpError.UnsupportedResource);
        foreach (var eventObject in snapshot.EventObjects)
            if (!resources.TryValidateEventObject(eventObject)) throw Failure(MpError.UnsupportedResource);
        return snapshot;
    }

    public RolesSnapshotMessage CaptureRoles()
    {
        EnsureHost();
        EnsureCurrent();
        var roles = new RoleState[MpLimits.Members];
        for (var i = 0; i < roles.Length; i++)
        {
            EnsureCurrent();
            var role = (PartyRole)i;
            var member = world.Party.Get(role);
            if (member is not ISimPartyMember partyMember)
                throw Failure(MpError.PrepareFailed);
            var pose = member is SimPlayer player
                ? player.SampleNetworkPose().Pose
                : new MpPose(MpVector.From(member.Position), member.Rotation);
            var (health, maxHealth) = ReadHealth(member);
            var hpFraction = maxHealth == 0 ? 0f : Math.Clamp((float)health / maxHealth, 0f, 1f);
            roles[i] = new RoleState(role, partyMember.Dead, pose,
                CaptureStatuses(member), hpFraction);
        }
        var snapshot = new RolesSnapshotMessage(roles);
        if (!WorldValidation.Validate(snapshot)) throw Failure(MpError.InvalidMessage);
        return snapshot;
    }

    public IReadOnlyList<WorldEvent> DrainEvents()
    {
        EnsureHost();
        EnsureCurrent();
        return eventBuffer.Drain();
    }

    public WorldSnapshotMessage? TakeWorldAfterEvents()
    {
        EnsureHost();
        EnsureCurrent();
        var snapshot = eventBuffer.TakeRetirements();
        // IDs are never reused, but dead wrappers need not stay rooted for a run.
        // Dictionary removal during enumeration is supported by the target runtime.
        foreach (var entry in hostEnemies)
            if (!entry.Key.IsActive) hostEnemies.Remove(entry.Key);
        foreach (var entry in hostEventObjects)
            if (!entry.Key.IsAlive) hostEventObjects.Remove(entry.Key);
        foreach (var entry in hostTethers)
            if (!entry.Key.IsActive) hostTethers.Remove(entry.Key);
        foreach (var entry in hostVisuals)
            if (!entry.Key.Omen.IsActive) hostVisuals.Remove(entry.Key);
        return snapshot;
    }

    public void ApplyWorld(WorldSnapshotMessage snapshot)
    {
        EnsurePeer();
        EnsureCurrent();
        if (!WorldValidation.Validate(snapshot)) throw Failure(MpError.InvalidMessage);
        foreach (var enemy in snapshot.Enemies)
            if (!resources.TryValidateEnemy(enemy)) throw Failure(MpError.UnsupportedResource);
        foreach (var eventObject in snapshot.EventObjects)
            if (!resources.TryValidateEventObject(eventObject)) throw Failure(MpError.UnsupportedResource);
        foreach (var visual in snapshot.StaticVisuals)
            if (!resources.TryResolveStatic(visual.ResourceKey, out _))
                throw Failure(MpError.UnsupportedResource);
        foreach (var tether in snapshot.Tethers)
            if (!resources.HasTether(tether.TetherId)) throw Failure(MpError.UnsupportedResource);
        ApplyPartyMarkers(snapshot.PartyMarkers);

        var seenEnemies = new HashSet<int>();
        foreach (var state in snapshot.Enemies)
        {
            EnsureCurrent();
            var enemy = GetOrSpawnEnemy(state);
            seenEnemies.Add(state.NetId);
            ApplyEnemy(enemy, state);
        }
        RemoveStale(peerEnemies, seenEnemies, enemy => NativeCall(enemy.Despawn));

        var seenEventObjects = new HashSet<int>();
        foreach (var state in snapshot.EventObjects)
        {
            EnsureCurrent();
            var eventObject = GetOrSpawnEventObject(state);
            seenEventObjects.Add(state.NetId);
            ApplyEventObject(eventObject, state);
        }
        RemoveStale(peerEventObjects, seenEventObjects, eventObject => NativeCall(eventObject.Despawn));

        var seenVisuals = new HashSet<int>();
        foreach (var state in snapshot.StaticVisuals)
        {
            EnsureCurrent();
            var visual = GetOrSpawnVisual(state);
            seenVisuals.Add(state.NetId);
            ApplyVisual(visual, state);
        }
        RemoveStale(peerVisuals, seenVisuals, visual => NativeCall(visual.Despawn));

        var seenTethers = new HashSet<int>();
        foreach (var state in snapshot.Tethers)
        {
            EnsureCurrent();
            ApplyTether(state);
            seenTethers.Add(state.NetId);
        }
        RemoveStale(peerTethers, seenTethers, tether => NativeCall(tether.Despawn));
    }

    public void ApplyRoles(RolesSnapshotMessage snapshot)
    {
        EnsurePeer();
        EnsureCurrent();
        if (!WorldValidation.Validate(snapshot) || snapshot.Roles.Length != MpLimits.Members)
            throw Failure(MpError.InvalidMessage);
        foreach (var role in snapshot.Roles)
            if (!resources.TryValidateRole(role)) throw Failure(MpError.UnsupportedResource);

        foreach (var state in snapshot.Roles)
        {
            EnsureCurrent();
            var member = world.Party.Get(state.Role);
            if (member is not ISimPartyMember partyMember)
                throw Failure(MpError.RoleRequired);

            NativeCall(() =>
            {
                if (state.Role != localRole)
                {
                if (member is SimNetworkPuppet puppet)
                    puppet.ApplyNetworkPose(state.Pose, moving: false, acting: false);
                else
                {
                    member.SetPosition(state.Pose.Position.ToVector());
                    member.SetRotation(state.Pose.Rotation);
                }
                }

                if (member is SimPlayer player)
                {
                    player.ApplyNetworkState(state.Dead, state.HpFraction);
                }
                else
                {
                    if (state.Dead)
                    {
                        if (!partyMember.Dead) partyMember.OnKilled();
                    }
                    else if (partyMember.Dead)
                    {
                        if (member is SimNetworkPuppet revivedPuppet) revivedPuppet.RestoreNetworkAlive();
                        else if (member is SimPartyNpc npc) npc.Revive();
                        else throw Failure(MpError.NativeFailure);
                    }
                    if (!state.Dead) member.ApplyNetworkHealthFraction(state.HpFraction);
                }
                member.ApplyNetworkStatuses(state.Statuses, statusSourceResolver);
            });
        }
    }

    public void ApplyEvent(WorldEvent value)
    {
        EnsurePeer();
        EnsureCurrent();
        var message = new WorldEventMessage(value);
        if (!WorldValidation.Validate(message)) throw Failure(MpError.InvalidMessage);

        switch (value)
        {
            case FailureNoticeEvent e:
                world.PresentFailure(e);
                break;
            case ActionTimelineEvent e:
                if (!resources.HasTimeline(e.TimelineId) || !resources.HasTimeline(e.LoopId) ||
                    !resources.HasTimeline(e.BaseOverride)) throw Failure(MpError.UnsupportedResource);
                NativeCall(() => ResolveActor(e.Actor).PlayActionTimeline(e.TimelineId, e.LoopId, e.BaseOverride));
                break;
            case ResetTimelineEvent e:
                NativeCall(() => ResolveActor(e.Actor).ResetActionTimeline());
                break;
            case NativeCastEvent e:
            {
                if (e.ActionType != (byte)ActionType.Action || !resources.TryValidateAction(e.ActionId))
                    throw Failure(MpError.UnsupportedResource);
                var actor = ResolveActor(e.Actor) as SimEnemy ?? throw Failure(MpError.InvalidMessage);
                GameObjectId? target = e.Target is { } targetEntity ? ResolveEntity(targetEntity).GameObjectId : null;
                NativeCall(() => actor.NativeCast(e.ActionId, (ActionType)e.ActionType, e.OmenDelay,
                    e.CastSeconds, e.Interruptible, e.Rotation, e.Position?.ToVector(), target));
                break;
            }
            case NativeActionEffectEvent e:
            {
                if (e.ActionType != (byte)ActionType.Action || !resources.TryValidateAction(e.ActionId) ||
                    (e.SpellId != 0 && !resources.TryValidateAction(e.SpellId)))
                    throw Failure(MpError.UnsupportedResource);
                var actor = ResolveActor(e.Actor) as SimEnemy ?? throw Failure(MpError.InvalidMessage);
                var targets = e.Targets.Select(entity => ResolveEntity(entity).GameObjectId).ToArray();
                GameObjectId? animationTarget = e.AnimationTarget is { } animationEntity
                    ? ResolveEntity(animationEntity).GameObjectId : null;
                NativeCall(() => actor.NativeActionEffect(e.ActionId, e.AnimationLock, e.SpellId,
                    e.AnimationVariation, (ActionType)e.ActionType, e.Flags, targets,
                    e.Rotation, e.Position?.ToVector(), animationTarget, null));
                break;
            }
            case ActorVfxEvent e:
            {
                var actor = ResolveActor(e.Actor);
                if (!resources.TryResolveActor(e.ResourceKey, out var path))
                    throw Failure(MpError.UnsupportedResource);
                NativeCall(() =>
                {
                    if (!actor.TryAddNetworkVfx(path, e.Duration, e.Persistent))
                        throw Failure(MpError.NativeFailure);
                });
                break;
            }
            case RemoveActorVfxEvent e:
                NativeCall(() => ResolveActor(e.Actor).RemoveVfx(ResolveActorVfx(e.ResourceKey)));
                break;
            case MapEffectEvent e:
                NativeCall(() => world.Map.AddEffect(e.Flags, e.Index));
                break;
            case DirectorEvent e:
                NativeCall(() => world.Map.DirectorUpdate(e.Category, e.Arg1, e.Arg2, e.Arg3, e.Arg4));
                break;
            case WeatherEvent e:
                if (!resources.HasWeather(e.WeatherId)) throw Failure(MpError.UnsupportedResource);
                NativeCall(() => world.Map.SetWeather(e.WeatherId, e.Transition));
                break;
            case KnockbackEvent e:
                NativeCall(() => ResolvePartyMember(e.Role).Knockback(e.Source.ToVector(), e.Distance, e.Speed));
                break;
            case TeleportEvent e:
                NativeCall(() => ResolveActor(MpEntity.Party(e.Role)).SetPosition(e.Position.ToVector()));
                break;
            case FaceEvent e:
                NativeCall(() => ResolveActor(MpEntity.Party(e.Role)).SetRotation(e.Rotation));
                break;
            default:
                throw Failure(MpError.InvalidMessage);
        }
        EnsureCurrent();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (host)
        {
            foreach (var member in world.Party.AllMembers())
                if (member is SimNetworkPuppet puppet)
                    puppet.NetworkControl -= OnNetworkControl;
            world.SetNetworkEventSink(null);
            eventBuffer.Clear();
        }
        foreach (var (sign, target) in peerMarkers)
            if (Markings.Get(sign).ObjectId == (uint)target)
                Markings.Clear(sign);
        peerMarkers.Clear();
        peerTethers.Clear();
        peerEnemies.Clear();
        peerEventObjects.Clear();
        peerVisuals.Clear();
        hostEnemies.Clear();
        hostEventObjects.Clear();
        hostTethers.Clear();
        hostVisuals.Clear();
    }

    private EnemyState CaptureEnemy(SimEnemy enemy)
    {
        var native = enemy.BattleCharaPtr;
        if (native == null || !ModelContainerCompat.TryGetModeAttributeFlags(native, out var modeAttributes))
            throw Failure(MpError.NativeFailure);
        return new EnemyState(GetEnemyId(enemy), enemy.BNpcBaseId, enemy.ModelCharaId,
            enemy.MainHandModel, enemy.OffHandModel, native->NameId, native->Level,
            enemy.Scale, enemy.ConfiguredHitboxRadius,
            (byte)enemy.EnemyListMode, enemy.InEnemyList, enemy.Visible, enemy.Targetable,
            enemy.ModelState, modeAttributes, new MpPose(MpVector.From(enemy.Position), enemy.Rotation),
            CaptureStatuses(enemy), native->Health, native->MaxHealth);
    }

    private EventObjectState CaptureEventObject(SimEventObject value)
        => new(GetEventObjectId(value), value.EObjRowId, value.LayoutId, value.TimelineState,
            value.CurrentState, new MpPose(MpVector.From(value.Position), value.Rotation));

    private StatusState[] CaptureStatuses(SimCharacter character)
        => character.ActiveStatusSnapshot;

    private GameObjectId ResolveStatusSource(PartyRole role)
        => world.Party.Get(role) is { IsActive: true } source
            ? source.GameObjectId
            : throw Failure(MpError.InvalidMessage);

    internal bool TryMapAbilityTarget(SimCharacter? actor, out MpEntity? entity)
    {
        entity = null;
        if (disposed || !isCurrent()) return false;
        if (actor is null) return true;
        if (!actor.IsActive) return false;
        foreach (var (role, member) in world.Party.FilledSlots())
            if (ReferenceEquals(member, actor))
            {
                entity = MpEntity.Party(role);
                return true;
            }
        if (actor is not SimEnemy enemy) return false;
        if (host)
        {
            if (!world.Children.Contains(enemy)) return false;
            entity = MpEntity.Enemy(GetEnemyId(enemy));
            return true;
        }
        // At most 64 actors: invert the authoritative map at input time,
        // without a second mutable ID cache or any peer-side ID allocation.
        foreach (var entry in peerEnemies)
            if (ReferenceEquals(entry.Value, enemy))
            {
                entity = MpEntity.Enemy(entry.Key);
                return true;
            }
        return false;
    }

    internal bool TryResolveAbilityTarget(MpEntity entity, out SimCharacter? actor)
    {
        actor = null;
        if (disposed || !host || !isCurrent() || !WorldValidation.ValidateEntity(entity)) return false;
        if (entity.Kind == MpEntityKind.Party)
        {
            actor = world.Party.Get((PartyRole)entity.Id);
            return actor is { IsActive: true };
        }
        foreach (var entry in hostEnemies)
            if (entry.Value == entity.Id && entry.Key.IsActive)
            {
                actor = entry.Key;
                return true;
            }
        return false;
    }
    public IReadOnlyList<PartyMarkerRequestMessage> CaptureLocalPartyMarkers()
    {
        EnsurePeer();
        EnsureCurrent();
        Span<ulong> party = stackalloc ulong[MpLimits.Members];
        GetPartyMarkerTargets(party);
        var changed = peerMarkerSync!.Capture(GetPartyMarkerSlots(), party);
        // Include local inputs in run cleanup even before their host echo.
        foreach (var marker in changed)
            if (marker.Role is { } role) peerMarkers[marker.Sign] = party[(int)role];
        return changed;
    }

    public void ApplyPartyMarker(PartyMarkerRequestMessage marker)
    {
        EnsureHost();
        EnsureCurrent();
        Span<ulong> party = stackalloc ulong[MpLimits.Members];
        GetPartyMarkerTargets(party);
        PartyMarkerSync.Apply(GetPartyMarkerSlots(), party, marker);
    }

    private void GetPartyMarkerTargets(Span<ulong> targets)
    {
        for (var index = 0; index < MpLimits.Members; index++)
            targets[index] = (ulong)(world.Party.Get((PartyRole)index)?.GameObjectId ?? default);
    }

    private static Span<ulong> GetPartyMarkerSlots()
    {
        var slots = Markings.GetSlots();
        if (slots.Length != MpLimits.Markers) throw Failure(MpError.NativeFailure);
        return slots;
    }

    private PartyMarkerState[] CapturePartyMarkers()
    {
        Span<ulong> party = stackalloc ulong[MpLimits.Members];
        GetPartyMarkerTargets(party);
        return PartyMarkerSync.Snapshot(GetPartyMarkerSlots(), party);
    }

    private WorldEvent TranslateWorldEvent(SimNetworkWorldEvent value)
    {
        return value.Event switch
        {
            ActionTimelineEvent e => e with { Actor = ToEntity(value.Actor) },
            ResetTimelineEvent e => e with { Actor = ToEntity(value.Actor) },
            ActorVfxEvent e => e with
            {
                Actor = ToEntity(value.Actor),
                ResourceKey = ResolveActorKey(e.ResourceKey),
            },
            RemoveActorVfxEvent e => e with
            {
                Actor = ToEntity(value.Actor),
                ResourceKey = ResolveActorKey(e.ResourceKey),
            },
            NativeCastEvent e => e with
            {
                Actor = ToEntity(value.Actor),
                Target = e.Target is { } target ? target : null,
            },
            NativeActionEffectEvent e => e with
            {
                Actor = ToEntity(value.Actor),
                Targets = e.Targets.ToArray(),
            },
            _ => value.Event,
        };
    }

    private NativeCastEvent TranslateCast(SimNetworkCastEvent value)
    {
        if (!resources.TryValidateAction(value.ActionId)) throw Failure(MpError.UnsupportedResource);
        return new(ToEntity(value.Actor), value.ActionId, value.ActionType, value.CastSeconds,
            value.OmenDelay, value.Interruptible, value.Rotation,
            value.Position is { } p ? MpVector.From(p) : null,
            value.Target is { } target && !IsNullObject(target) ? ToEntity(target) : null);
    }

    private NativeActionEffectEvent TranslateActionEffect(SimNetworkActionEffectEvent value)
    {
        if (!resources.TryValidateAction(value.ActionId)) throw Failure(MpError.UnsupportedResource);
        return new(ToEntity(value.Actor), value.ActionId, value.AnimationLock, value.SpellId,
            value.AnimationVariation, value.ActionType, value.Flags,
            value.Targets.Where(target => !IsNullObject(target)).Select(ToEntity).ToArray(),
            value.Rotation, value.Position is { } p ? MpVector.From(p) : null,
            value.AnimationTarget is { } target && !IsNullObject(target) ? ToEntity(target) : null);
    }

    private SimEnemy GetOrSpawnEnemy(EnemyState state)
    {
        if (peerEnemies.TryGetValue(state.NetId, out var existing))
        {
            if (existing.BNpcBaseId != state.BnpcBaseId || existing.ModelCharaId != state.ModelCharaId ||
                existing.MainHandModel != state.MainHandModel || existing.OffHandModel != state.OffHandModel ||
                MathF.Abs(existing.Scale - state.Scale) > 0.001f ||
                MathF.Abs(existing.ConfiguredHitboxRadius - state.HitboxRadius) > 0.001f)
                throw Failure(MpError.InvalidMessage);
            return existing;
        }

        SimEnemy? spawned = null;
        NativeCall(() => spawned = world.SpawnEnemy(new EnemySpawnConfig
        {
            BNpcBaseId = state.BnpcBaseId,
            NameId = state.NameId,
            Level = state.Level,
            Targetable = state.Targetable,
            EnemyList = (EnemyListMode)state.EnemyListMode,
            IsVisible = state.Visible,
            Placement = new Placement(state.Pose.Position.ToVector(), state.Pose.Rotation),
            ModelCharaId = state.ModelCharaId,
            MainHandModel = state.MainHandModel,
            OffHandModel = state.OffHandModel,
            Scale = state.Scale,
            HitboxRadius = state.HitboxRadius,
            InitialModeAttributeFlags = state.ModeAttributeFlags,
        }));
        if (spawned is null) throw Failure(MpError.NativeFailure);
        peerEnemies.Add(state.NetId, spawned);
        return spawned;
    }

    private void ApplyEnemy(SimEnemy enemy, EnemyState state)
    {
        NativeCall(() =>
        {
            enemy.SetPosition(state.Pose.Position.ToVector());
            enemy.SetRotation(state.Pose.Rotation);
            enemy.SetVisible(state.Visible);
            enemy.SetTargetable(state.Targetable);
            var native = enemy.BattleCharaPtr;
            if (native == null || !ModelContainerCompat.TryGetModeAttributeFlags(native, out var modeAttributes))
                throw Failure(MpError.NativeFailure);
            native->NameId = state.NameId;
            native->Level = state.Level;
            if (enemy.EnemyListMode == EnemyListMode.Manual) enemy.SetVisibleInEnemyList(state.InEnemyList);
            if (modeAttributes != state.ModeAttributeFlags) enemy.SetModeAttributeFlags(state.ModeAttributeFlags);
            if (enemy.ModelState != state.ModelState) enemy.SetModelState(state.ModelState);
            enemy.ApplyNetworkHealth(state.CurrentHp, state.MaxHp);
            enemy.ApplyNetworkStatuses(state.Statuses, statusSourceResolver);
        });
    }
    private void ApplyPartyMarkers(PartyMarkerState[] markers)
    {
        EnsureCurrent();
        Span<ulong> party = stackalloc ulong[MpLimits.Members];
        GetPartyMarkerTargets(party);
        peerMarkerSync!.Reconcile(GetPartyMarkerSlots(), party, markers);
        peerMarkers.Clear();
        foreach (var marker in markers)
            peerMarkers[marker.Sign] = party[(int)marker.Role];
    }

    private SimEventObject GetOrSpawnEventObject(EventObjectState state)
    {
        if (peerEventObjects.TryGetValue(state.NetId, out var existing))
        {
            if (existing.EObjRowId != state.EobjId || existing.LayoutId != state.LayoutId)
                throw Failure(MpError.InvalidMessage);
            return existing;
        }

        SimEventObject? spawned = null;
        NativeCall(() => spawned = world.SpawnEventObject(new EventObjectSpawnConfig
        {
            EObjId = state.EobjId,
            LayoutId = state.LayoutId,
            Placement = new Placement(state.Pose.Position.ToVector(), state.Pose.Rotation),
            TimelineState = state.TimelineState,
            // CurrentState belongs to the SharedGroup timeline, not packet EventState.
            SpawnVisible = state.CurrentState != 0,
        }));
        if (spawned is null) throw Failure(MpError.NativeFailure);
        NativeCall(() => spawned.SetState(state.CurrentState));
        peerEventObjects.Add(state.NetId, spawned);
        return spawned;
    }

    private void ApplyEventObject(SimEventObject value, EventObjectState state)
    {
        NativeCall(() =>
        {
            value.SetPosition(new Placement(state.Pose.Position.ToVector(), state.Pose.Rotation));
            value.ApplyNetworkState(state.CurrentState);
        });
    }

    private SimOmen GetOrSpawnVisual(StaticVisualState state)
    {
        if (peerVisuals.TryGetValue(state.NetId, out var existing)) return existing;
        if (!resources.TryResolveStatic(state.ResourceKey, out var path))
            throw Failure(MpError.UnsupportedResource);
        SimOmen? spawned = null;
        NativeCall(() => spawned = world.SpawnOmen(path,
            new Placement(state.Position.ToVector(), 0f), state.Scale.ToVector(), null,
            state.Rotation.ToQuaternion()));
        if (spawned is null || !spawned.IsNetworkVisualReady)
            throw Failure(MpError.NativeFailure);
        peerVisuals.Add(state.NetId, spawned);
        return spawned;
    }

    private void ApplyVisual(SimOmen value, StaticVisualState state)
        => NativeCall(() => value.UpdateRecordedTransform(state.Position.ToVector(),
            state.Rotation.ToQuaternion(), state.Scale.ToVector()));

    private void ApplyTether(TetherState state)
    {
        var source = ResolveEntity(state.Source);
        var target = ResolveEntity(state.Target);
        if (peerTethers.TryGetValue(state.NetId, out var current))
        {
            if (current.TetherId == state.TetherId && current.Progress == resources.Scenario.TetherProgress &&
                ReferenceEquals(current.A, source) && ReferenceEquals(current.B, target)) return;
            NativeCall(current.Despawn);
            peerTethers.Remove(state.NetId);
        }

        SimTether? tether = null;
        NativeCall(() => tether = world.Tether(source, target, state.TetherId, progress: resources.Scenario.TetherProgress));
        if (tether is null) throw Failure(MpError.NativeFailure);
        peerTethers.Add(state.NetId, tether);
    }

    private SimCharacter ResolveActor(MpEntity value)
    {
        if (value.Kind == MpEntityKind.Enemy && peerEnemies.TryGetValue(value.Id, out var enemy)) return enemy;
        if (value.Kind == MpEntityKind.Party && value.Id >= 0 && value.Id < MpLimits.Members &&
            world.Party.Get((PartyRole)value.Id) is { } member) return member;
        throw Failure(MpError.InvalidMessage);
    }

    private SimCharacter ResolveEntity(MpEntity value) => ResolveActor(value);

    private ISimPartyMember ResolvePartyMember(PartyRole role)
        => world.Party.Get(role) as ISimPartyMember ?? throw Failure(MpError.RoleRequired);

    private MpEntity ToEntity(SimCharacter? character)
    {
        if (character is null) throw Failure(MpError.InvalidMessage);
        foreach (var (role, member) in world.Party.FilledSlots())
            if (ReferenceEquals(member, character)) return MpEntity.Party(role);
        if (character is SimEnemy { IsActive: true } enemy)
        {
            var enemyId = GetEnemyId(enemy);
            if (capturingEvent) eventBuffer.ObserveEnemy(CaptureEnemy(enemy));
            return MpEntity.Enemy(enemyId);
        }
        throw Failure(MpError.InvalidMessage);
    }

    private MpEntity ToEntity(GameObjectId id)
    {
        if (IsNullObject(id)) throw Failure(MpError.InvalidMessage);
        foreach (var (role, member) in world.Party.FilledSlots())
            if (member.GameObjectId.ObjectId == id.ObjectId) return MpEntity.Party(role);
        foreach (var enemy in world.Children.OfType<SimEnemy>())
            if (enemy.IsActive && enemy.GameObjectId.ObjectId == id.ObjectId) return ToEntity(enemy);
        throw Failure(MpError.InvalidMessage);
    }

    private string ResolveActorKey(string path)
        => resources.TryGetActorKey(path, out var key) ? key : throw Failure(MpError.UnsupportedResource);

    private string ResolveActorVfx(string key)
        => resources.TryResolveActor(key, out var path) ? path : throw Failure(MpError.UnsupportedResource);

    private int GetEnemyId(SimEnemy enemy)
    {
        if (hostEnemies.TryGetValue(enemy, out var id)) return id;
        return hostEnemies[enemy] = AllocateId();
    }

    private int GetEventObjectId(SimEventObject value)
    {
        if (hostEventObjects.TryGetValue(value, out var id)) return id;
        return hostEventObjects[value] = AllocateId();
    }

    private int GetTetherId(SimTether value)
    {
        if (hostTethers.TryGetValue(value, out var id)) return id;
        return hostTethers[value] = AllocateId();
    }

    private int GetVisualId(SimOmen omen, int part)
    {
        var key = (omen, part);
        if (hostVisuals.TryGetValue(key, out var id)) return id;
        return hostVisuals[key] = AllocateId();
    }

    private int AllocateId()
    {
        if (nextNetId <= 0) throw Failure(MpError.Capacity);
        return nextNetId++;
    }

    private static (uint Health, uint MaxHealth) ReadHealth(SimCharacter member)
    {
        var native = member.BattleCharaPtr;
        return native == null ? (0u, 0u) : (native->Health, native->MaxHealth);
    }

    private static bool IsNullObject(GameObjectId id)
        => id.ObjectId == 0 || id.ObjectId == 0xE0000000;

    private void OnNetworkControl(WorldEvent value)
        => OnNetworkEvent(new SimNetworkWorldEvent(null, value));

    private void OnNetworkEvent(SimNetworkEvent item)
    {
        capturingEvent = true;
        try
        {
            EnsureHost();
            EnsureCurrent();
            var translated = item switch
            {
                SimNetworkWorldEvent worldEvent => TranslateWorldEvent(worldEvent),
                SimNetworkCastEvent cast => TranslateCast(cast),
                SimNetworkActionEffectEvent effect => TranslateActionEffect(effect),
                SimNetworkUnsupportedEvent => throw Failure(MpError.UnsupportedResource),
                SimNetworkFailureEvent failure => throw Failure(failure.Error),
                _ => throw Failure(MpError.InvalidMessage),
            };
            eventBuffer.Add(translated);
        }
        catch (MpProtocolException ex)
        {
            // A scenario helper may catch its native error. Keep the failure
            // latched so the next framework send still ends this run.
            eventBuffer.Fail(ex.Error);
        }
        catch (Exception)
        {
            eventBuffer.Fail(MpError.NativeFailure);
        }
        finally
        {
            capturingEvent = false;
        }
    }

    private void NativeCall(Action action)
    {
        EnsureCurrent();
        try
        {
            action();
            EnsureCurrent();
        }
        catch (MpProtocolException) { throw; }
        catch (Exception)
        {
            throw Failure(MpError.NativeFailure);
        }
    }

    private void EnsureHost()
    {
        if (disposed) throw Failure(MpError.Disposed);
        if (!host) throw Failure(MpError.InvalidMessage);
    }

    private void EnsurePeer()
    {
        if (disposed) throw Failure(MpError.Disposed);
        if (host) throw Failure(MpError.InvalidMessage);
    }

    private void EnsureCurrent()
    {
        if (disposed) throw Failure(MpError.Disposed);
        if (!isCurrent()) throw Failure(MpError.Cancelled);
    }

    private static MpProtocolException Failure(MpError error) => new(error);

    private static void RemoveStale<T>(Dictionary<int, T> values, HashSet<int> seen, Action<T> dispose)
    {
        foreach (var id in values.Keys.Where(id => !seen.Contains(id)).ToArray())
        {
            var value = values[id];
            dispose(value);
            values.Remove(id);
        }
    }
}
