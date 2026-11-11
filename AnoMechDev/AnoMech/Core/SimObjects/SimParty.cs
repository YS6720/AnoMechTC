using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.EnemyActions;
using AnoMech.Core.Combat;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Party;

namespace AnoMech.Core.SimObjects;

// Fixed-size 8-slot party indexed by PartyRole. Each slot holds either a
// SimPartyNpc (spawned doppel) or the SimPlayer (the player's own role) — both
// ISimPartyMember. Slots are stored/returned as SimCharacter so they thread
// straight into the character-typed engine APIs (world.Tether, AddStatus, ...);
// the party-slot facets (Role, Knockback) are reached by casting to
// ISimPartyMember.
//
// HUD mirroring is owned by SimWorld (not SimParty) — see SimWorld.partyHud —
// so the addon-lifecycle listener has the same lifespan as the plugin and the
// SimParty.Empty sentinel doesn't accidentally register one at static init.
public sealed class SimParty : ISimObject
{
    private static Random rnd = new();
    
    public static readonly SimParty Empty = new();

    private readonly SimCharacter?[] slots = new SimCharacter?[8];
    private readonly SimWorld? world;

    public SimParty() : this(null) { }

    // world 供 LimitBreak 轉接場景的練習 LB runtime；Empty 沒有 world，LimitBreak.Set 為 no-op。
    internal SimParty(SimWorld? world)
    {
        this.world = world;
        Find = new CharacterFind<SimCharacter>(ActiveMembers);
        LimitBreak = new LimitBreakGauge(world);
    }

    public CharacterFind<SimCharacter> Find { get; }

    // 上游 1646a70 同名：場景用 Party.LimitBreak.Set(bars) 設定練習 LB 量表。
    public LimitBreakGauge LimitBreak { get; }

    // 上游 1646a70 同名：The bot in `role` casts its job's limit break, emptying the gauge. False if
    // the slot is not a live bot (humans press their own), mitigation is not required (上游 bot 出招同一閘),
    // the practice gauge is not ready, or the job has no usable LB. 我方只有 LB3：量表、施放、坦 LB 減傷與
    // 扣量表全走既有練習 LB runtime（與玩家按 LB 同一個保留，不會重複扣）；職業 LB 取 ClassJob.LimitBreak3。
    public bool UseLimitBreak(PartyRole role)
    {
        if (Get(role) is not SimPartyNpc bot || !bot.IsAlive() || !UserActions.Mitigation.Required) return false;
        if (world?.LimitBreaks is not { IsAvailable: true } runtime || runtime.IsBusy(role)) return false;
        var actionId = runtime.ActionFor(role);
        if (actionId == 0 ||
            Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(actionId) is not { TargetArea: false })
            return false;
        // 同 TOP P6 既有 bot LB：補位職（坦／治療）以自己為目標，輸出職不指定目標。
        var support = role is PartyRole.MainTank or PartyRole.OffTank or PartyRole.RegenHealer or PartyRole.ShieldHealer;
        return runtime.TryStart(role, actionId, target: support ? bot : null);
    }

    // 上游 1646a70 同名：Whether a seat is played by AI here. 連線替身由遠端真人操作，一律 false
    //（我方沒有上游的 DebugBotControl 與遠端 bot 座位）。
    public bool IsBotDriven(SimCharacter member)
        => member is not SimNetworkPuppet && !ReferenceEquals(member, Player);

    public SimCharacter? Get(int roleId)
        => roleId >= 0 && roleId < slots.Length ? slots[roleId] : null;

    public SimCharacter? Get(PartyRole role) => Get((int)role);

    // The role of the slot currently holding the SimPlayer — set by SetSlot
    // when it receives one. Falls back to MainTank if no SimPlayer has been
    // placed (no scenario hits this today).
    public PartyRole PlayerRole { get; private set; } = PartyRole.MainTank;

    public SimPlayer? Player => Get(PlayerRole) as SimPlayer;

    internal void SetSlot(PartyRole role, ISimPartyMember slot)
    {
        slot.Role = role;
        slots[(int)role] = (SimCharacter)slot;
        if (slot is SimPlayer) PlayerRole = role;
    }

    // Encode once when the run is prepared, not on every party-HUD refresh.
    // A null slot keeps the character's native name. Only our HUD copy is overridden;
    // the real local character identity is never rewritten.
    private readonly byte[]?[] displayNames = new byte[]?[8];

    internal void SetDisplayName(PartyRole role, string? name)
    {
        if ((uint)role >= displayNames.Length) return;
        if (name is null)
        {
            displayNames[(int)role] = null;
            return;
        }
        var encoded = new byte[NetworkPartyIdentity.NativeNameBytes];
        if (!NetworkPartyIdentity.TryWriteName(name, encoded))
            throw new ArgumentException("Party display name does not fit the native name buffer.", nameof(name));
        displayNames[(int)role] = encoded;
    }

    public ReadOnlySpan<byte> EncodedDisplayName(PartyRole role)
        => (uint)role < displayNames.Length ? displayNames[(int)role].AsSpan() : ReadOnlySpan<byte>.Empty;

    internal void ForEachActive(Action<SimCharacter> action)
    {
        for (int i = 0; i < slots.Length; i++)
            if (slots[i] is { } m && m.IsAlive()) action(m);
    }

    // Kills every alive member with the given cause. Raidwide wipe primitive
    // used by mechanics whose failure is unsurvivable.
    public void WipeAllPlayers(string cause)
        => ForEachActive(m => { if (m.IsAlive()) m.Die(cause); });

    // Status a member carries while temporarily invulnerable (GiveInvuln). Game.Kill
    // swallows any death of a member holding it — every death path routes through Kill,
    // so this covers DamageSolver and direct .Die() alike. Hallowed Ground (409): a
    // recognizable buff icon that renders on doppels; the death gate reads it sim-side
    // (HasStatus), so it works on the local player too even though the icon doesn't show.
    public const ushort InvulnStatusId = 409;

    // Makes `role` immune to death for `seconds` (default 10). Backed by InvulnStatusId,
    // so it auto-expires and Game.Kill swallows any death — from DamageSolver or a direct
    // .Die() — while it's held. No-op if the slot is empty.
    public void GiveInvuln(PartyRole role, float seconds = 10f)
        => Get(role)?.AddStatus(InvulnStatusId, seconds);

    // Raidwide knockback: pushes every active slot `distance` units away from
    // `source`. Each slot resolves its own direction from its current position.
    public void Knockback(Vector3 source, float distance)
        => ForEachActive(m => (m as ISimPartyMember)?.Knockback(source, distance));

    // Raidwide knockback resolved from KnockbackTable + Lumina Knockback sheet.
    // Resolves once at the party level so an unmapped action logs a single
    // warning rather than one per slot.
    public void Knockback(Vector3 source, uint knockbackId)
    {
        if (!KnockbackLookup.TryGet(knockbackId, out var distance, out var speed))
            return;
        ForEachActive(m => (m as ISimPartyMember)?.Knockback(source, distance, speed));
    }

    internal IEnumerable<SimCharacter> ActiveMembers()
    {
        for (int i = 0; i < slots.Length; i++)
            if (slots[i] is { } m && m.IsAlive()) yield return m;
    }

    // All filled slots with their role index, alive or dead. Used by PartyFinder
    // proximity queries (which follow the same no-alive-filter contract the old
    // FindClosest* methods had).
    internal IEnumerable<(PartyRole role, SimCharacter member)> FilledSlots()
    {
        for (int i = 0; i < slots.Length; i++)
            if (slots[i] is { } m) yield return ((PartyRole)i, m);
    }

    // Every filled slot, alive or dead. Used by the party HUD so dead members
    // keep their slot (HP=0 is the visible "dead" state) instead of being
    // silently dropped — and so other members don't shift up the list when
    // someone dies. Mechanic resolvers should keep using ActiveMembers.
    internal IEnumerable<SimCharacter> AllMembers()
    {
        for (int i = 0; i < slots.Length; i++)
            if (slots[i] is { } m) yield return m;
    }

    public bool IsAlive
    {
        get
        {
            for (int i = 0; i < slots.Length; i++)
                if (slots[i] is { } m && m.IsAlive()) return true;
            return false;
        }
    }

    // Missing/loading actors are not dead, and an empty party is not a wipe.
    internal bool IsWiped
    {
        get
        {
            var occupied = false;
            for (var i = 0; i < slots.Length; i++)
            {
                if (slots[i] is not { } member) continue;
                occupied = true;
                if (member is not ISimPartyMember { Dead: true }) return false;
            }
            return occupied;
        }
    }

    // The party persists for the whole scenario and is torn down only by Reset /
    // Despawn — a wipe (every member dead) must NOT reap it, so this is not wired
    // to IsAlive.
    public bool IsActive => true;

    // 上游 1646a70 同名：Each confused member's current chase target, null while held.
    private readonly Dictionary<SimCharacter, SimCharacter?> confusedChases = new();
    // 隊員施放 EnemyAction（混亂揮擊）用的 handler；我方隊員沒有常駐 SimCast，同練習 LB runtime 依需要建立。
    private readonly Dictionary<SimCharacter, EnemyActionHandler> actionHandlers = new();

    // The server walks a confused player after their nearest live ally, retargeting as that
    // changes, for as long as its last ActorControl 0x52 said Confused. Whenever an ally stands in
    // the swing and nothing locks it, the confused player swings at them.
    private void TickConfusedChases()
    {
        foreach (var member in AllMembers())
        {
            if (!member.IsAlive() || !member.ActorControl.Confused)
            {
                if (confusedChases.Remove(member)) member.ForceFollow(null);
                continue;
            }
            // 上游另要求 !member.Casting.IsBusy；我方隊員沒有常駐 SimCast，揮擊硬直改由 HoldConfusedChase 擋。
            if (!member.ConfusedChaseHeld && Find.Closest(member.Position, member) is { } nearest)
                SwingIfInReach(member, nearest);
            var target = member.ConfusedChaseHeld ? null : Find.Closest(member.Position, member);
            if (confusedChases.TryGetValue(member, out var current) && ReferenceEquals(current, target)) continue;
            confusedChases[member] = target;
            member.ForceFollow(target);
        }
    }

    // D788（我方）：暫停時 Tick 不跑，原生 forced-follow 卻會自己繼續拖本機玩家。停掉每個追擊並清掉快取，
    // 恢復那一幀 TickConfusedChases 就會把仍混亂的人重新派出去（同一個目標也會重送）。每個暫停幀都可呼叫。
    internal void SuspendConfusedChases()
    {
        if (confusedChases.Count == 0) return;
        foreach (var member in confusedChases.Keys) member.ForceFollow(null);
        confusedChases.Clear();
    }

    // The swing aimed at `nearest`, as it would resolve; the chase stands still for its lock.
    private void SwingIfInReach(SimCharacter member, SimCharacter nearest)
    {
        if (world is null) return;
        var attack = CommonActions.ConfusedAttack;
        var aim = member.Placement().Face(nearest.Position);
        var query = new AoeQuery(attack.ActionId, aim, attack.Cast.Rotation, attack.Area.Size, attack.Area.CastTypeOverride, member.HitboxRadius);
        if (!query.Run(Find).Any(h => h != member)) return;
        if (!actionHandlers.TryGetValue(member, out var handler))
            actionHandlers[member] = handler = new EnemyActionHandler(member, new SimCast(member, world.Coordinates), world);
        handler.Start(attack, nearest, 0);
        member.HoldConfusedChase(attack.Cast.AnimationLock);
    }

    public void Tick(float deltaSeconds)
    {
        TickConfusedChases();
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] is null) continue;
            slots[i]!.Tick(deltaSeconds);
            // Reap a slot only if its member is fully gone (handle released).
            // A KO'd doppel keeps IsActive == true, so this is a safety net,
            // not the normal-path death behaviour.
            if (!slots[i]!.IsActive)
            {
                slots[i]!.Despawn();
                slots[i] = null;
            }
        }
    }

    public void Despawn()
    {
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i]?.Despawn();
            slots[i] = null;
        }
        Array.Clear(displayNames);
        confusedChases.Clear();
        foreach (var handler in actionHandlers.Values) handler.Despawned();
        actionHandlers.Clear();
    }

    public SimCharacter? GetRandom()
    {
        return Get(rnd.Next(8));
    }
}
