using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using AnoMech.Core;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Geometry;
using AnoMech.Core.Game.Party;
using AnoMech.Core.Map;
using AnoMech.Multiplayer;

namespace AnoMech.Core.SimObjects;

public enum ScenarioResult
{
    Idle,
    Running,
    Completed,
    Failed,
}

// Holds the live game state Game manipulates: a children list of SimObjects
// (party, enemies, tethers, waymarks, hidden objects). Spawn entry points
// (CreateParty, SpawnEnemy, Tether, PlaceWaymarks, HideObject,
// EnforceArenaBoundary) construct the SimObject and register it for teardown.
// Zone loading and map effects go through world.Map.
public sealed class SimWorld : ISimObject, IDisposable
{
    
    // Observations are translated synchronously while native actors still exist.
    // Peers leave the sink null, preventing receiver-side application from echoing.
    private Action<SimNetworkEvent>? networkEventSink;
    // Ownership
    private readonly List<ISimObject> children = new();
    private readonly EnmityHud enmityHud = new();
    private readonly PartyHud partyHud = new();
    private readonly PartyListDisplayOrderHud partyListDisplayOrderHud = new();
    private readonly Waymarks waymarks;

    // Zone loading and map effects entry point.
    public MapController Map { get; } = new();

    // Scenario geometry: areas party bots steer around while moving. Empty by
    // default (straight-line movement). Scenarios mutate it over their timeline
    // (Add/Remove/Clear); wired to each doppel by PartyCreator, cleared on Despawn.
    public ObstacleField Obstacles { get; } = new();

    // Convenience reference — SimParty.Empty until CreateParty is called.
    public SimParty Party { get; private set; } = SimParty.Empty;
    /// <summary>Explicit result of the current scenario round.</summary>
    public ScenarioResult Result { get; private set; }

    /// <summary>
    /// Records the real scenario completion point. This is intentionally explicit:
    /// callers must not infer success from object or event-queue lifetime.
    /// </summary>
    public void CompleteScenario()
    {
        if (Result == ScenarioResult.Running)
            Result = ScenarioResult.Completed;
    }

    /// <summary>Latched failure for this round; a later revive cannot clear it.</summary>
    internal void LatchFailure()
    {
        if (Result is ScenarioResult.Running or ScenarioResult.Completed)
            Result = ScenarioResult.Failed;
    }

    public void FailScenario(string reason, PartyRole? role = null)
    {
        LatchFailure();
        ReportFailure(reason, role, false);
    }

    internal string ReportFailure(string reason, PartyRole? role, bool death)
    {
        var notice = new FailureNoticeEvent(role, reason, death);
        var text = PresentFailure(notice);
        RecordNetworkEvent(new SimNetworkWorldEvent(null, notice));
        return text;
    }

    // Presentation only: peers must not judge, kill, pause, or echo this notice.
    internal string PresentFailure(FailureNoticeEvent notice)
    {
        var who = "全隊";
        if (notice.Role is { } role)
        {
            var member = Party.Get(role);
            if (member is SimPartyNpc npc)
                who = npc.DisplayName;
            else
            {
                var encoded = Party.EncodedDisplayName(role);
                var end = encoded.IndexOf((byte)0);
                if (end >= 0) encoded = encoded[..end];
                var name = !encoded.IsEmpty ? Encoding.UTF8.GetString(encoded)
                    : member is SimNetworkPuppet puppet ? puppet.DisplayName
                    : member is SimPlayer ? Plugin.ObjectTable.LocalPlayer?.Name.TextValue : null;
                who = string.IsNullOrEmpty(name) ? $"[{role.ShortName()}]" : $"[{role.ShortName()}] {name}";
            }
        }
        var text = $"{who} {(notice.Death ? "已死亡" : "機制失敗")}：{notice.Reason}";
        ChatOutput.Coach(text);
        return text;
    }
    public IEnumerable<ISimObject> Children => children;
    public string PartyListDisplayOrderStatus => partyListDisplayOrderHud.Status;
    // Root container — Game owns its lifetime; no parent reaps it.
    public bool IsActive => true;
    /// <summary>
    /// Runtime movement-step registry shared by every AiManager in the active scenario.
    /// Main owns Begin/End lifecycle; no overlay is applied before Begin.
    /// </summary>
    public PracticePositions PracticePositions { get; }
    public EventScheduler Events { get; }
    public Vector3 ScenarioOrigin { get; set; }

    // Converts between scenario-local coordinates (the SimXxx public API) and
    // world/global coordinates (the engine's GameObject->Position). Shared by
    // every SimCharacter (injected as a protected field) and used by spawners
    // for the pre-construction native writes. Reads ScenarioOrigin live.
    public Coordinates Coordinates { get; }

    public SimWorld(EventScheduler events)
    {
        Events = events;
        Coordinates = new Coordinates(() => ScenarioOrigin);
        waymarks = new Waymarks(Coordinates);
        PracticePositions = new PracticePositions(new FilePracticePositionStore(
            () => Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "practice-positions.json")));
    }
    
    internal void SetNetworkEventSink(Action<SimNetworkEvent>? sink)
    {
        networkEventSink = sink;
        Map.NetworkEventSink = sink == null
            ? null
            : item => RecordNetworkEvent(new SimNetworkWorldEvent(null, item));

        foreach (var character in children.OfType<SimCharacter>())
            character.NetworkEventSink = sink == null ? null : RecordNetworkEvent;
        foreach (var character in Party.AllMembers())
            character.NetworkEventSink = sink == null ? null : RecordNetworkEvent;
    }

    private void RecordNetworkEvent(SimNetworkEvent item)
        => networkEventSink?.Invoke(item);

    private void BindNetworkEventSink(SimCharacter character)
        => character.NetworkEventSink = networkEventSink == null ? null : RecordNetworkEvent;

    // A fixed end is just the character; dynamic ends are End.Passable(...) /
    // End.FarthestPlayer(). `from` hosts the channeling VFX (drawn from → to), so
    // argument order picks the visual direction. The overload pair enforces "at
    // least one fixed end" at compile time: two dynamic ends match neither overload
    // (nor the both-character convenience) and won't compile — no runtime guard.
    public SimTether Tether(SimCharacter? from, ITetherEnd to, ushort tetherId, float duration = 0f,
        ushort debuffStatusId = 0, byte progress = 1)
        => CreateTether(End.Fixed(from), to, tetherId, duration, debuffStatusId, progress);
    public SimTether Tether(DynamicEnd from, SimCharacter? to, ushort tetherId, float duration = 0f,
        ushort debuffStatusId = 0, byte progress = 1)
        => CreateTether(from, End.Fixed(to), tetherId, duration, debuffStatusId, progress);
    public SimTether Tether(SimCharacter? a, SimCharacter? b, ushort tetherId, float duration = 0f,
        ushort debuffStatusId = 0, byte progress = 1)
        => CreateTether(End.Fixed(a), End.Fixed(b), tetherId, duration, debuffStatusId, progress);
    public SimTether TetherFarestPlayer(SimCharacter? a, ushort tetherId, float duration = 0f,
        ushort debuffStatusId = 0, byte progress = 1)
        => CreateTether(End.Fixed(a), End.FarthestPlayer(), tetherId, duration, debuffStatusId, progress);


    private SimTether CreateTether(ITetherEnd from, ITetherEnd to, ushort tetherId, float duration,
        ushort debuffStatusId, byte progress = 1)
    {
        var tether = new SimTether(from, to, new TetherContext(Party.Find, tetherId), debuffStatusId, duration, progress);
        children.Add(tether);
        return tether;
    }

    
    public SimEnemy? SpawnEnemy(EnemySpawnConfig config)
    {
        var enemy = SimEnemy.Spawn(config, this);
        if (enemy != null)
        {
            children.Add(enemy);
            BindNetworkEventSink(enemy);
        }
        return enemy;
    }

    // Allocates an EventObject actor in EventObjectManager's 40-slot pool and
    // wires it to the given EObj sheet row. Mirror of SpawnEnemy for the EObj
    // side of the engine — see SimEventObject / EventObjectSpawn for details.
    public SimEventObject? SpawnEventObject(EventObjectSpawnConfig config)
    {
        var eo = SimEventObject.Spawn(config, Coordinates, Events);
        if (eo != null) children.Add(eo);
        return eo;
    }

    // EventObject tower variant — picks `states[count]` each tick based on how
    // many party members stand within `radius` of the EObj (counts past the
    // array length clamp to the last entry). Bound to the current Party so AI
    // and scenario movement drive the visual.
    public SimTower? SpawnTower(EventObjectSpawnConfig config, ushort[] states, float radius)
    {
        var tower = SimTower.Spawn(config, Coordinates, Events, states, radius, Party);
        if (tower != null) children.Add(tower);
        return tower;
    }

    // Places the scenario's waymark layout. Offsets are scenario-relative;
    // Waymarks resolves them through Coordinates. Cleared in Reset (like
    // Markings) — Waymarks is a writer owned here, not a tracked child.
    public void PlaceWaymarks(IReadOnlyList<Waymark> layout)
        => waymarks.Place(layout);

    // Suppress a native GameObject (by BaseId) for the duration of the scenario.
    public void HideObject(uint baseId)
    {
        var hidden = SimHiddenObject.Hide(baseId);
        if (hidden != null) children.Add(hidden);
    }

    // Per-frame arena fence at `radius` from ScenarioOrigin. Kills any active
    // party member (player included) who leaves the ring, and spawns a VFX border.
    public void EnforceArenaBoundary(float radius, string cause = "離開場地範圍")
        => children.Add(new SimArenaBoundary(Party, this, radius, cause, showVfx: !Map.IsInInstance));

    // Opt-in replacement: remove every old fence and its VFX before registering
    // the single square used by both Tick and IsOutsideArena.
    public void EnforceSquareArenaBoundary(float halfWidth, string cause = "離開場地範圍")
    {
        for (var i = children.Count - 1; i >= 0; i--)
        {
            if (children[i] is not SimArenaBoundary previous) continue;
            previous.Despawn();
            children.RemoveAt(i);
        }
        children.Add(new SimArenaBoundary(Party, ArenaBoundaryBounds.Square(halfWidth), cause));
    }

    // True when `local` (scenario-local) is outside the active arena fence; false
    // when the current scenario enforces no boundary.
    public bool IsOutsideArena(Vector3 local)
        => children.OfType<SimArenaBoundary>().FirstOrDefault()?.IsOutside(local) ?? false;

    // Spawns a standalone AOE telegraph (omen StaticVfx) that auto-expires after
    // `durationSeconds` and is cleaned up on world reset. `placement` is scenario-local
    // (like the rest of the SimXxx API); SimOmen lifts it to world coords. `scale`
    // follows SimOmen's convention: scale.X = halfWidth, scale.Z = length for rect omens.
    public SimOmen SpawnOmen(string path, Placement placement, Vector3 scale,
        float? durationSeconds, Quaternion? rotation = null)
    {
        var omen = new SimOmen(Coordinates, path, placement, scale, durationSeconds, rotation);
        children.Add(omen);
        if (networkEventSink != null && !omen.IsNetworkVisualReady)
        {
            RecordNetworkEvent(new SimNetworkFailureEvent(MpError.NativeFailure));
            throw new MpProtocolException(MpError.NativeFailure);
        }
        return omen;
    }

    // Same, but derives path + scale from the action's Omen sheet entry — gives
    // instants (no cast bar) the game's own telegraph shape and radius.
    public void SpawnOmen(uint actionId, Vector3 origin, float rotation, float durationSeconds)
        => children.Add(new SimOmen(Coordinates, actionId, origin, rotation, durationSeconds));

    // Change the active weather mid-scenario. weatherId is a Weather-sheet row;
    // transition is the fade-in time in seconds. A scenario's default weather
    // (TargetInstance.WeatherId) is re-applied automatically on restart, so any
    // mid-run change made here is reset whenever the scenario is run again.
    public void SetWeather(byte weatherId, float transition = 0.5f)
        => Map.SetWeather(weatherId, transition);

    // Spawns the eight party slots and wires in the local player. Must be called
    // after ScenarioOrigin is set. Party is added first so it despawns last in
    // Reset's reverse-order teardown (tethers and enemies reference slot positions).
    public void CreateParty(
        uint playerJob,
        PartyRole? roleOverride = null,
        bool solo = false,
        IReadOnlyList<PartyMemberPreset?>? presetOverride = null,
        IReadOnlySet<PartyRole>? remoteRoles = null,
        IReadOnlyList<string?>? aliasNames = null,
        IReadOnlyList<Multiplayer.MpAppearance?>? appearances = null,
        Func<bool>? isCurrent = null)
    {
        var party = new SimParty();
        try
        {
            PartyCreator.Populate(
                party,
                new SimPlayer(Coordinates),
                playerJob,
                this,
                roleOverride,
                solo,
                presetOverride,
                remoteRoles,
                aliasNames,
                appearances,
                isCurrent);
        }
        catch
        {
            // Populate allocates native characters before the party becomes a
            // world child. Always release a partial allocation before propagating
            // cancellation or a real spawn failure to the session controller.
            party.Despawn();
            throw;
        }
        children.Add(party);
        Party = party;
        Result = ScenarioResult.Running;
        if (networkEventSink != null)
            foreach (var character in Party.AllMembers())
                BindNetworkEventSink(character);

    }
    public void Tick(float deltaSeconds)
    {
        Map.Tick();
        children.Update(deltaSeconds);
        enmityHud.Refresh(children.OfType<SimEnemy>(), deltaSeconds);
        partyHud.Refresh(Party);
        // P133 reads configuration only here; the adapter never changes party data.
        partyListDisplayOrderHud.Refresh(
            partyHud.IsDisplayActive,
            Plugin.Config.EnablePartyListDisplayOrder,
            partyHud.SlotRoles,
            Plugin.Config.PartyListDisplayOrder);
    }

    public void Despawn()
    {
        networkEventSink = null;
        children.Despawn();
        Party = SimParty.Empty;
        Result = ScenarioResult.Idle;
        Map.NetworkEventSink = null;
        enmityHud.Clear();
        partyHud.Clear();
        partyListDisplayOrderHud.Clear();
        waymarks.ClearAll();
        Obstacles.Clear();
        ScenarioOrigin = default;
    }

    public void Dispose()
    {
        Despawn();
        enmityHud.Dispose();
        partyListDisplayOrderHud.Dispose();
        Map.Dispose();
    }
}
