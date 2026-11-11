using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using AnoMech.Scenarios.Top;
using static AnoMech.Scenarios.Umad.UmadConstants;

namespace AnoMech.Scenarios.Umad.P3BlackHole;

// Partial: UmadP3BlackHoleAi.Signs.cs places and reads the native head markers (YAN), kept out of
// the file Tests link.
public sealed partial class UmadP3BlackHoleAi(UmadP3BlackHoleAi.TetherOrder tetherOrder) : IScenarioAi<UmadP3BlackHoleState>
{
    public enum TetherOrder { DpsSupportAccretion, SupportDpsAccretion, Yan }

    public string Name => tetherOrder switch
    {
        TetherOrder.SupportDpsAccretion => "Black Hole: S>D>A",
        TetherOrder.Yan => "Black Hole: YAN(211)",
        _ => "Black Hole: D>S>A",
    };

    // The timeline below is written in D>S>A seats. S>D>A is the same choreography with the
    // three support seats and the three DPS seats trading places, line position kept; the two
    // Accretion seats (3 and 7) take the third tether either way.
    private static readonly int[] SupportsBeforeDps = [4, 5, 6, 3, 0, 1, 2, 7];

    private int TetherSeat(int seat) =>
        tetherOrder == TetherOrder.SupportDpsAccretion ? SupportsBeforeDps[seat] : seat;

    // YAN(211) runs the same timeline slots, each pulled by whoever wears that slot's head marker
    // (UmadP3BlackHoleYanMarks.Plan); only the 1st, 2nd, 9th and 10th pulls are its own. Until the
    // markers are read this holds the D>S>A seats.
    private PartyRole[] yanPullers = [];

    // The role pulling a timeline slot.
    private PartyRole PullerRole(int slot) =>
        tetherOrder == TetherOrder.Yan ? yanPullers[slot] : state.Roles[TetherSeat(slot)];

    private SimCharacter? Puller(int slot) => world.Party.Get(PullerRole(slot));

    // Buffs land at 8.37s. System: the Ai marks each buff line at random. Either way the pullers
    // are read back from the party's signs once, before the first grab at 26.2s, so a sign a player
    // moves by hand in between still counts.
    private const float YanMarkTime = 10f;
    private const float YanReadTime = 25.5f;

    private void ScheduleYanMarks(AiManager ai)
    {
        if (state.Markers == MarkerMode.System)
        {
            var marks = UmadP3BlackHoleYanMarks.Assign(state.Roles.List, world.Rng.Fork("UmadP3BlackHoleAi.YanMarks"));
            PlaceSigns(ai, YanMarkTime, marks);
        }
        world.Events.Add(YanReadTime, ReadYanMarks);
    }

    private void ReadYanMarks()
    {
        var (pullers, problems) = UmadP3BlackHoleYanMarks.Resolve(state.Roles.List, RoleWearing);
        yanPullers = pullers;
        foreach (var problem in problems)
            AnoMech.Core.DiagnosticLog.Warn($"[UmadP3BlackHoleAi] YAN marker {problem} Its slot goes to the line's unmarked member.");
        AnoMech.Core.DiagnosticLog.Info("[UmadP3BlackHoleAi] YAN pullers: "
            + string.Join(", ", UmadP3BlackHoleYanMarks.Plan.Select(p => $"{p.Sign}={pullers[p.Slot]}")));
    }

    private UmadP3BlackHoleState state = null!;
    private SimWorld world = null!;

    public void Run(UmadP3BlackHoleState stateParam, SimWorld worldParam)
    {
        state = stateParam;
        world = worldParam;
        assignedHole.Clear();
        setHoles.Clear();
        implosionChaos = null;
        implosionSpots = null;
        yanPullers = state.Roles.List;
        var yan = tetherOrder == TetherOrder.Yan;
        // YAN(211): MT tanks Exdeath and ST tanks Chaos; D>S>A and S>D>A keep MT on Chaos.
        state.ChaosTank = yan ? PartyRole.OffTank : PartyRole.MainTank;
        // 我方：opt-in 上游到點規劃（上游 AiManager 預設就是這套）。
        var ai = new AiManager(world, upstreamArrival: true);
        if (yan) ScheduleYanMarks(ai);

        // Kept here so a peer's debug-bot replay gets it from the same Run.
        world.Events.Add(25.17f, () => state.ScenarioObjects.TetherSortFrom = state.KefkaPosition[0]);
        world.Events.Add(55.70f, () => state.ScenarioObjects.TetherSortFrom = state.KefkaPosition[1]);
        world.Events.Add(89.95f, () => state.ScenarioObjects.TetherSortFrom = state.KefkaPosition[2]);
        world.Events.Add(123.83f, () => state.ScenarioObjects.TetherSortFrom = state.KefkaPosition[3]);

        ai.Move(7f, StackCentreTanksHoldBossesCentred);
        ai.Move(11f, StackCentre);
        // No arrivalTime: this wave has four staggered resolves behind one Move, and deferring
        // to the last one wiped the party on the first rows.
        ai.Move(18f, () => DodgeSlap(slapIndex: 0, kefkaIndex: 0));
        // A full second before GrabTether, or StackCentre's deferred MoveTo cancels the Intercept.
        ai.Move(25f, StackCentre);
        if (yan)
        {
            // 1st: Attack 2 alone. 2nd: Attack 1 takes both tethers of the set. Attack 2 holds its
            // pull spot, out of the 2nd set's rects, until the next party move: walking back through
            // a beam steals Attack 1's tether.
            world.Events.Add(26.2f, () => GrabNewTether(tetherIndex: 0, playerIndex: 0));
            ScheduleTetherRegrab(26.6f, 28.2f, tetherIndex: 0, playerIndex: 0);
            world.Events.Add(28.2f, () => PullTether(playerIndex: 0));
            world.Events.Add(32.4f, () => PullBothNewTethers(playerIndex: 4, pullAt: 34.4f, shotAt: 39.33f));
        }
        else
        {
            world.Events.Add(26.2f, () => GrabNewTether(tetherIndex: 0, playerIndex: 4));
            ScheduleTetherRegrab(26.6f, 28.2f, tetherIndex: 0, playerIndex: 4);
            world.Events.Add(28.2f, () => PullTether(playerIndex: 4));
            world.Events.Add(32.4f, () => GrabNewTether(tetherIndex: 0, playerIndex: 4));
            world.Events.Add(32.4f, () => GrabNewTether(tetherIndex: 1, playerIndex: 0));
            world.Events.Add(34.4f, () => PullTether(playerIndex: 4));
            world.Events.Add(34.4f, () => PullTether(playerIndex: 0));
        }
        // Follow self-sustains, so no AiMove. Right after the 39.33 Nothingness: MT's pull spot can
        // be ~19y from Exdeath, too far to run before the 42.63 hit.
        world.Events.Add(39.4f, ResolveFirstThunder);
        ai.Move(43.19f, SwapFirstThunderTanks);
        // Standing invariant for the Set 1 danger window; the swap lands at 43.5f.
        {
            var (set1First, set1Second) = ThunderIIIPlanning.Roles(state.ThunderSet1);
            ScheduleThunderClearance(39.5f, 43.19f, 43.5f, 46.2f, set1First, set1Second);
        }
        ai.Move(46.5f, DodgeEdict);
        // Sprint: DodgeEdict's spot can be ~20y from the slap target, more than RunSpeed covers
        // in the ~1.7s available.
        ai.Move(50.6f, () => DodgeSlap(slapIndex: 1, kefkaIndex: 1), sprint: true);
        ai.Move(56f, StackCentre);
        world.Events.Add(58.1f, () => GrabNewTether(tetherIndex: 0, playerIndex: 4));
        world.Events.Add(58.1f, () => GrabNewTether(tetherIndex: 1, playerIndex: 0));
        world.Events.Add(58.1f, () => GrabNewTether(tetherIndex: 2, playerIndex: 3));
        world.Events.Add(60.1f, () => PullTether(playerIndex: 4));
        world.Events.Add(60.1f, () => PullTether(playerIndex: 0));
        world.Events.Add(60.1f, () => PullTether(playerIndex: 3));
        world.Events.Add(64f, () => GrabTether(tetherIndex: 0, playerIndex: 5, intercept: 1f));
        world.Events.Add(66f, () => ReturnAfterHandoff(giverSlot: 4, tetherIndex: 0, giveUpAt: 73.1f));
        world.Events.Add(69f, () => GrabTether(tetherIndex: 1, playerIndex: 1, intercept: 1f));
        world.Events.Add(71f, () => ReturnAfterHandoff(giverSlot: 0, tetherIndex: 1, giveUpAt: 73.1f));
        ai.Move(73.1f, DodgeEdictAndLookUpon);
        ai.Move(80f, StackCentre);
        world.Events.Add(82f, ResolveSecondThunder);
        ai.Move(84.5f, SwapSecondThunderTanks);
        // Same as Set 1; the swap lands at 84.9f.
        {
            var (set2First, set2Second) = ThunderIIIPlanning.Roles(state.ThunderSet2);
            ScheduleThunderClearance(80.5f, 84.5f, 84.9f, 87.5f, set2First, set2Second);
        }
        ai.Move(88f, StackCentre);
        world.Events.Add(92.3f, () => GrabNewTether(tetherIndex: 0, playerIndex: 5));
        world.Events.Add(92.3f, () => GrabNewTether(tetherIndex: 1, playerIndex: 1));
        world.Events.Add(92.3f, () => GrabNewTether(tetherIndex: 2, playerIndex: 7));
        world.Events.Add(94.3f, () => PullTether(playerIndex: 5));
        world.Events.Add(94.3f, () => PullTether(playerIndex: 1));
        world.Events.Add(94.3f, () => PullTether(playerIndex: 7));
        world.Events.Add(98f, () => GrabTether(tetherIndex: 0, playerIndex: 6, intercept: 1f));
        world.Events.Add(100f, () => ReturnAfterHandoff(giverSlot: 5, tetherIndex: 0, giveUpAt: 109.5f));
        world.Events.Add(103f, () => GrabTether(tetherIndex: 1, playerIndex: 2, intercept: 1f));
        world.Events.Add(105f, () => ReturnAfterHandoff(giverSlot: 1, tetherIndex: 1, giveUpAt: 109.5f));
        // 7, 6 and 2 hold their tether spots until the wave's holes despawn (~109.3): earlier,
        // a still-tethered hole's next Nothingness would follow them into the stack. Recalled
        // with lead time before Implosion.
        world.Events.Add(109.5f, () => ReturnToMiddle(playerIndex: 7));
        world.Events.Add(110.5f, () => ReturnToMiddle(playerIndex: 6));
        world.Events.Add(111.5f, () => ReturnToMiddle(playerIndex: 2));
        if (yan) KeepYanTethersThroughSharedWaves();
        world.Events.Add(110f, () => AnchorChaosTankForImplosion(kefkaIndex: 3));
        // arrivalTime = each hit's actual resolve time, so a role that needs more than RunSpeed
        // sprints instead of arriving late.
        // 我方：衝刺是 opt-in（上游有抵達時間就自動判斷），這兩段要明示 sprint: true。
        ai.Move(117f, () => DodgeImplosion(shockwaveIndex: 0, slapIndex: 2, slapKefkaIndex: 3), jitter: 0f, arrivalTime: 119.09f, sprint: true);
        ai.Move(119.2f, () => DodgeImplosion(shockwaveIndex: 1, slapIndex: 2, slapKefkaIndex: 3), jitter: 0f, arrivalTime: 121.11f, sprint: true);
        ai.Move(121.3f, () => DodgeSlap(slapIndex: 2, kefkaIndex: 3), sprint: true);
        ai.Move(124f, StackCentre);
        // The last set's tether holder carries it through Look Upon.
        var lastPuller = yan ? 6 : 2;
        if (yan)
        {
            // 9th: Ignore 2 takes both tethers of the set. 10th: Ignore 1 takes the last one.
            world.Events.Add(125.9f, () => PullBothNewTethers(playerIndex: 2, pullAt: 127.9f, shotAt: 130.60f));
            world.Events.Add(130.8f, () => GrabTether(tetherIndex: 0, playerIndex: lastPuller));
            world.Events.Add(130.8f, () => ReturnToMiddle(playerIndex: 2));
        }
        else
        {
            world.Events.Add(125.9f, () => GrabNewTether(tetherIndex: 0, playerIndex: 6));
            world.Events.Add(125.9f, () => GrabNewTether(tetherIndex: 1, playerIndex: 2));
            world.Events.Add(127.9f, () => PullTether(playerIndex: 6));
            world.Events.Add(127.9f, () => PullTether(playerIndex: 2));
            world.Events.Add(130.8f, () => GrabTether(tetherIndex: 0, playerIndex: lastPuller));
            world.Events.Add(130.8f, () => ReturnToMiddle(playerIndex: 6));
        }
        ScheduleTetherRegrab(131.2f, 134f, tetherIndex: 0, playerIndex: lastPuller);
        ai.Move(134f, () => DodgeLookUponSplitHolder(tetherPlayerIndex: lastPuller, lookKefkaIndex: 4, leaveWithoutTether: false), sprint: true);
        ai.Move(134f, () => DodgeLookUponSplitOthers(tetherPlayerIndex: lastPuller, lookKefkaIndex: 4), sprint: true);
        ai.Move(134.5f, () => DodgeLookUponSplitHolder(tetherPlayerIndex: lastPuller, lookKefkaIndex: 4, leaveWithoutTether: false), sprint: true);
        ai.Move(135f, () => DodgeLookUponSplitHolder(tetherPlayerIndex: lastPuller, lookKefkaIndex: 4, leaveWithoutTether: true), sprint: true);
        ai.Move(139f, PrepositionForStomp);
        ai.Move(147f, StompBlizzardCorners);
        ai.Move(149.8f, StompStackAndTowers);
        ai.Move(152.4f, StompSwapWest);
        ai.Move(153.7f, StompSwapEast);
    }

    // Chaos's tank stands east and Exdeath's west: the bosses (Chaos spawned west, Exdeath east)
    // walk through to their tanks and stop centred.
    private IAiMove StackCentreTanksHoldBossesCentred()
    {
        var coords = new Vector2?[8];
        for (var i = 2; i < 8; i++) coords[i] = Vector2.Zero;
        coords[(int)state.ChaosTank] = new Vector2(6.0f, 0f);
        coords[(int)state.ExdeathTank] = new Vector2(-3.8f, 0f);
        return AiMove.Create(coords).NaturalOrder();
    }

    private static IAiMove StackCentre() => AiMove.All(new(0f, 0f));

    // Right's slots are grouped by role: each cone targets one tank, healer and dps, and the
    // co-members share the target's spot as an intentional shared soak.
    private IAiMove DodgeSlap(int slapIndex, int kefkaIndex)
    {
        var move = SlapSpots(slapIndex, kefkaIndex);
        for (int i = 0; i < 8; i++)
        {
            var member = world.Party.Get(i);
            if (member is null || !member.IsAlive()) continue;
            AnoMech.Core.DiagnosticLog.Info(
                $"[UmadP3BlackHoleAi] DodgeSlap({slapIndex},{kefkaIndex}): role{i} from ({member.Position.X:F1},{member.Position.Z:F1}) -> target {move[i]}.");
        }
        return move;
    }

    private IAiMove SlapSpots(int slapIndex, int kefkaIndex)
    {
        var direction = state.SlapAttacks[slapIndex] == ActionId.SlapHappy_Right
                            ? state.KefkaPosition[kefkaIndex].Flip()
                            : state.KefkaPosition[kefkaIndex];

        IAiMove move = state.SlapAttacks[slapIndex] == ActionId.SlapHappy_Left
                   ? AiMove.All(new(9f, 0f)).ApplyPositions(direction.Apply).ApplyPositions(p => p.Multiply(1, 9f/7f))
                   : (IAiMove)AiMove.Create(
                                        new(RoleSpreadDiagonal, RoleSpreadDiagonal), new(RoleSpreadDiagonal, RoleSpreadDiagonal),
                                        new(RoleSpreadRadius, 0f), new(RoleSpreadRadius, 0f),
                                        new(RoleSpreadDiagonal, -RoleSpreadDiagonal), new(RoleSpreadDiagonal, -RoleSpreadDiagonal),
                                        new(RoleSpreadDiagonal, -RoleSpreadDiagonal), new(RoleSpreadDiagonal, -RoleSpreadDiagonal))
                                    .NaturalOrder()
                                    .ApplyPositions(direction.Apply);
        return move;
    }

    // 我方（Owner 11-11「職能分散不要站那麼遠、不要拉到王、不要炸到人」）：Right 的三組由 9–9.9y 收到 8.5y，
    // 王在正中時近戰最遠打得到的距離（Chaos 目標圈 5.5＋3，與 YanPullRadius 同依據），坦克站近、王被拖離場中也較少。
    // 不再往內：場中最後一拍 r6 在同一步內判定（24.45／54.91／122.89），內爆後那次要從內爆點衝過來，7.5–8y 時
    // 模擬有種子判定當下人還在 r6 裡或扇形判定時沒到（PickImplosionSpots）。三組相隔 45°、扇形半角 30°，離隔壁扇形邊 15°（約 2.2y）。
    private const float RoleSpreadRadius = 8.5f;
    private static readonly float RoleSpreadDiagonal = RoleSpreadRadius * MathF.Sqrt(0.5f);

    // First Lightning III from Exdeath: a stack-radius tank buster snapshotted twice,
    // 3s apart, onto whoever is closest to Exdeath. The OT eats both by standing in the
    // middle of Exdeath's hitbox (so it stays the closest); everyone else clears the
    // blast by sliding straight out from Exdeath's centre along their own bearing.
    private const float ThunderBusterRadius = 8f;     // blast radius non-OTs must clear
    private const float ThunderClearDistance = 10f;   // where they park, just past it

    // Follow, not a coordinate snapshot: Exdeath keeps chasing OffTank until RunThunder's freeze.
    private void ResolveFirstThunder()
    {
        if (state.ScenarioObjects.Exdeath is not { } exdeath) return;
        var (first, _) = ThunderIIIPlanning.Roles(state.ThunderSet1);
        if (world.Party.Get((int)first) is not { } firstMember || !firstMember.IsAlive()) return;
        (firstMember as ISimPartyMember)?.UseSprint(3.5f);
        firstMember.Follow(exdeath, speed: AiManager.SprintSpeed);
    }

    private void ResolveSecondThunder()
    {
        if (state.ScenarioObjects.Exdeath is not { } exdeath) return;
        var (first, _) = ThunderIIIPlanning.Roles(state.ThunderSet2);
        if (world.Party.Get((int)first) is { } firstMember && firstMember.IsAlive())
            firstMember.Follow(exdeath);
    }

    // Shared by both sets' swap: after the first hit the two tanks trade spots, so "first"
    // slides into the middle and "second" takes over the seat. Reads live positions (Exdeath
    // is frozen across both hits). No-op if this set's plan doesn't call for a swap.
    private IAiMove SwapThunderTanks(ThunderIIIAssignment plan)
    {
        var (first, second) = ThunderIIIPlanning.Roles(plan);
        if (second is not { } secondRole) return AiMove.Create().NaturalOrder();
        var a = world.Party.Get(first);
        var b = world.Party.Get(secondRole);
        if (a is null || b is null) return AiMove.Create().NaturalOrder();

        var coords = new Vector2?[8];
        coords[(int)secondRole] = new Vector2(a.Position.X, a.Position.Z);
        coords[(int)first]      = new Vector2(b.Position.X, b.Position.Z);
        return AiMove.Create(coords).NaturalOrder();
    }

    private IAiMove SwapFirstThunderTanks() => SwapThunderTanks(state.ThunderSet1);
    private IAiMove SwapSecondThunderTanks() => SwapThunderTanks(state.ThunderSet2);

    // Keeps every other role at least ThunderClearDistance from Exdeath, reasserted repeatedly
    // rather than predicted once, since Exdeath keeps chasing its tank. Exdeath only: Chaos's tank
    // permanently holds Chaos, and only Exdeath casts Thunder III.
    private void EnforceThunderClearanceOnce(PartyRole first, PartyRole? second)
    {
        // Re-seated against first's live position each check; first is still converging on Exdeath.
        if (second is { } secondRole
            && world.Party.Get((int)first) is { } firstMember && firstMember.IsAlive()
            && world.Party.Get((int)secondRole) is { } secondMember && secondMember.IsAlive())
        {
            var firstPos = new Vector2(firstMember.Position.X, firstMember.Position.Z);
            var secondPos = new Vector2(secondMember.Position.X, secondMember.Position.Z);
            var offset = secondPos - firstPos;
            if (offset.LengthSquared() < ThunderClearDistance * ThunderClearDistance)
            {
                var away = firstPos.LengthSquared() > 1e-4f ? Vector2.Normalize(-firstPos) : new Vector2(0f, -1f);
                var seat = firstPos + RotateVec(away, MathF.PI / 2f) * ThunderClearDistance;
                // The seat can land past the wall when first is near the edge.
                var seatClearRadius = ArenaRadius - 1f;
                if (seat.LengthSquared() > seatClearRadius * seatClearRadius)
                    seat = seat.LengthSquared() > 1e-4f ? Vector2.Normalize(seat) * seatClearRadius : seat;
                secondMember.MoveTo(new Vector3(seat.X, 0f, seat.Y));
            }
        }

        if (state.ScenarioObjects.Exdeath is { } exdeath && exdeath.IsAlive())
        {
            var bossPos = new Vector2(exdeath.Position.X, exdeath.Position.Z);
            for (int i = 0; i < 8; i++)
            {
                var role = (PartyRole)i;
                if (role == first || (second is { } sec && role == sec)) continue;
                if (world.Party.Get(i) is not { } member || !member.IsAlive()) continue;
                var pos = new Vector2(member.Position.X, member.Position.Z);
                var offset = pos - bossPos;
                if (offset.LengthSquared() >= ThunderClearDistance * ThunderClearDistance) continue;
                var dir = offset.LengthSquared() > 1e-4f ? Vector2.Normalize(offset) : new Vector2(1f, 0f);
                var target = bossPos + dir * ThunderClearDistance;
                // "Away from Exdeath" can point through the wall when Exdeath is near the edge.
                var clearRadius = ArenaRadius - 1f;
                if (target.LengthSquared() > clearRadius * clearRadius)
                    target = target.LengthSquared() > 1e-4f ? Vector2.Normalize(target) * clearRadius : target;
                member.MoveTo(new Vector3(target.X, 0f, target.Y));
            }
        }
    }

    // Paused from swapStart to swapLand: seat-reassertion would cancel SwapThunderTanks
    // mid-crossing. After the swap, second is settled at the boss and first is a normal bystander.
    private const float ThunderClearanceInterval = 0.4f;

    private void ScheduleThunderClearance(float fromTime, float swapStart, float swapLand, float toTime, PartyRole first, PartyRole? second)
    {
        for (var t = fromTime; t < swapStart; t += ThunderClearanceInterval)
            world.Events.Add(t, () => EnforceThunderClearanceOnce(first, second));
        var postSwapExcluded = second ?? first;
        for (var t = swapLand; t <= toTime; t += ThunderClearanceInterval)
            world.Events.Add(t, () => EnforceThunderClearanceOnce(postSwapExcluded, null));
    }

    // Dodge the edict by tucking just behind the casting boss, but as close to arena
    // center as the rect allows so the follow-up slap dodge is a short hop. The edict
    // is a forward rect, so the whole half-plane behind the boss is safe; take the
    // point in it nearest center.
    private IAiMove DodgeEdict()
    {
        var boss = state.ScenarioObjects.Chaos;
        if (boss is null) return StackCentre();

        var bossPos = new Vector2(boss.Position.X, boss.Position.Z);
        var fwd = new Vector2(MathF.Sin(boss.Rotation), MathF.Cos(boss.Rotation));
        const float margin = 3f;   // clearance behind the boss (the rect's back edge)

        // Nearest point to center in the safe half-plane (P - bossPos)·fwd <= -margin.
        var s = margin - Vector2.Dot(bossPos, fwd);
        var target = s > 0f ? -fwd * s : Vector2.Zero;
        AnoMech.Core.DiagnosticLog.Info(
            $"[UmadP3BlackHoleAi] DodgeEdict: boss at ({bossPos.X:F1},{bossPos.Y:F1}) rot={boss.Rotation:F3} -> target ({target.X:F1},{target.Y:F1}).");
        return AiMove.All(target);
    }

    // Damning Edict (rect 60x80 projected from the boss) and Look Upon Me and Despair
    // (rect 100x16 along the KefkaPosition[2] axis through center) snapshot only ~1.4s
    // apart — too tight to dodge in two hops. Hold one spot safe from both: behind the
    // edict boss and clear of the Look-Upon corridor.
    private IAiMove DodgeEdictAndLookUpon()
    {
        var theta = state.KefkaPosition[2].RadiansFromNorth;
        var axis = new Vector2(-MathF.Sin(theta), MathF.Cos(theta));     // Look-Upon line direction
        var lookRight = new Vector2(MathF.Cos(theta), MathF.Sin(theta)); // perpendicular to it
        const float lookSafe = 11f;   // past the 8y half-width, with margin
        const float behind = 3f;      // distance to stand behind the boss

        var boss = state.ScenarioObjects.Chaos;
        if (boss is null) return AiMove.All(lookRight * lookSafe);

        var bossPos = new Vector2(boss.Position.X, boss.Position.Z);
        var fwd = new Vector2(MathF.Sin(boss.Rotation), MathF.Cos(boss.Rotation));
        var denom = Vector2.Dot(axis, fwd);

        float Fwd(Vector2 p) => Vector2.Dot(p - bossPos, fwd);   // < 0 = behind the boss

        // A corridor edge offset by sign*lookSafe (always clear of Look-Upon), slid along
        // the line only as far as needed to sit at least `behind` the boss. The slide is
        // capped at the arena wall: with the boss facing nearly across the line it would
        // otherwise run tens of yards out.
        Vector2 Seat(float sign)
        {
            var edge = lookRight * (sign * lookSafe);
            var f = Fwd(edge);
            if (f <= -behind || MathF.Abs(denom) < 0.1f) return edge;
            var maxSlide = MathF.Sqrt((ArenaRadius - 1f) * (ArenaRadius - 1f) - lookSafe * lookSafe);
            var t = Math.Clamp((-behind - f) / denom, -maxSlide, maxSlide);
            return edge + axis * t;
        }

        var a = Seat(1f);
        var b = Seat(-1f);
        bool aOk = Fwd(a) < -1f, bOk = Fwd(b) < -1f;
        if (aOk == bOk) return AiMove.All(a.LengthSquared() <= b.LengthSquared() ? a : b);
        return AiMove.All(aOk ? a : b);
    }

    // Implosion fires two +-45deg Shockwave cones from Chaos, the axis rotating 90deg between
    // the two shockwaves, so the only bearings safe from both are the four diagonals at 45deg
    // to the cone axis. Each role takes the point on any diagonal, clear of the slap circles,
    // nearest its own DodgeSlap spot: the slap-dodge follows with under 2s to get there. Each
    // shockwave then leans a few degrees off the diagonal toward the perpendicular of its own
    // cone so neither dodge stands on the (hit-counting) cone edge.
    private const float SideTieTolerance = 1e-3f;
    private const float ArenaRadius = 20f;             // ~outer Black Hole ring (z=-17), tune in-game
    private const float ImplosionBorderInset = 1f;
    private const float ImplosionMinChaosDistance = 4f; // closer in, the lean clears the cone edge by too little
    private const float ImplosionConeLean = 0.18f;     // ~10deg off the cone edge
    private const float SlapRadius = 13f;
    private const float SlapClearance = 1.5f;

    // Chaos resumes chasing the MT once the cast ends, but the shockwave helpers keep the
    // placement they copied, so both dodges read Chaos once.
    private (Vector2 Centre, float Rotation)? implosionChaos;
    private (int Diagonal, float Distance)[]? implosionSpots;

    private IAiMove DodgeImplosion(int shockwaveIndex, int slapIndex, int slapKefkaIndex)
    {
        var boss = state.ScenarioObjects.Chaos;
        if (boss is null) return StackCentre();

        if (shockwaveIndex == 0 || implosionChaos is null)
            implosionChaos = (new Vector2(boss.Position.X, boss.Position.Z), boss.Rotation);
        var (c, rotation) = implosionChaos.Value;
        var baseAxis = rotation + (state.ImplosionAttack == ActionId.LongitudinalImplosion ? 0f : MathF.PI / 2f);

        if (shockwaveIndex == 0 || implosionSpots is null)
            implosionSpots = PickImplosionSpots(c, baseAxis, slapIndex, slapKefkaIndex);

        var coneAxisAngle = baseAxis + shockwaveIndex * (MathF.PI / 2f);
        var coords = new Vector2?[8];
        for (var i = 0; i < 8; i++)
        {
            var (diagonal, distance) = implosionSpots[i];
            coords[i] = ImplosionSpot(c, baseAxis, coneAxisAngle, diagonal, distance);
        }
        return AiMove.Create(coords).NaturalOrder();
    }

    private (int Diagonal, float Distance)[] PickImplosionSpots(Vector2 c, float baseAxis, int slapIndex, int slapKefkaIndex)
    {
        var slapSpots = SlapSpots(slapIndex, slapKefkaIndex);
        var slapCentres = SlapCentres(slapIndex, slapKefkaIndex);
        var spots = new (int, float)[8];
        for (var i = 0; i < 8; i++)
        {
            var goal = slapSpots[i] ?? Vector2.Zero;
            var bestCost = float.MaxValue;
            for (var k = 0; k < 4; k++)
            {
                var d = Diagonal(baseAxis, k);
                var maxDistance = MathF.Min(RayToArenaBorder(c, RotateVec(d, ImplosionConeLean)),
                                            RayToArenaBorder(c, RotateVec(d, -ImplosionConeLean))) - ImplosionBorderInset;
                for (var t = ImplosionMinChaosDistance; t <= maxDistance; t += 0.25f)
                {
                    var first = ImplosionSpot(c, baseAxis, baseAxis, k, t);
                    var second = ImplosionSpot(c, baseAxis, baseAxis + MathF.PI / 2f, k, t);
                    var clearance = MathF.Min(SlapClearanceAt(first, slapCentres), SlapClearanceAt(second, slapCentres));
                    // 我方：職能分散收到較近後，從內爆點直線衝去時，場中最後一拍判定的當下人可能還在 r6 裡，
                    // 繞開的點又可能在扇形判定時還沒到：兩者都計成危險。
                    var routeIntrusion = FinalSlapRadius + FinalSlapClearance - PositionAt(second, goal, FinalSlapSnapshotAfterLeave).Length();
                    var coneLateness = Vector2.Distance(PositionAt(second, goal, ConeSnapshotAfterLeave), goal);
                    var cost = Vector2.Distance(second, goal)
                               + (MathF.Max(0f, -clearance) + MathF.Max(0f, routeIntrusion) + coneLateness) * 100f;
                    if (cost < bestCost) { bestCost = cost; spots[i] = (k, t); }
                }
            }
        }
        return spots;
    }

    private static Vector2 Diagonal(float baseAxis, int k)
    {
        var theta = baseAxis + MathF.PI / 4f + k * (MathF.PI / 2f);
        return new Vector2(MathF.Sin(theta), MathF.Cos(theta));
    }

    private static Vector2 ImplosionSpot(Vector2 c, float baseAxis, float coneAxisAngle, int diagonal, float distance)
    {
        var dir = Diagonal(baseAxis, diagonal);
        var coneAxis = new Vector2(MathF.Sin(coneAxisAngle), MathF.Cos(coneAxisAngle));
        var perp = RotateVec(coneAxis, MathF.PI / 2f);
        if (Vector2.Dot(perp, dir) < 0f) perp = -perp;
        var lean = MathF.Sign(dir.X * perp.Y - dir.Y * perp.X) * ImplosionConeLean;
        return c + RotateVec(dir, lean) * distance;
    }

    private Vector2[] SlapCentres(int slapIndex, int kefkaIndex)
    {
        var mul = state.SlapAttacks[slapIndex] == ActionId.SlapHappy_Left ? -1f : 1f;
        return Enumerable.Range(0, 3)
                         .Select(row => state.KefkaPosition[kefkaIndex].Apply(new Vector3(10f * mul, 0f, -10f + row * 10f)))
                         .Select(p => new Vector2(p.X, p.Z))
                         .ToArray();
    }

    private static float SlapClearanceAt(Vector2 p, Vector2[] slapCentres) =>
        slapCentres.Min(centre => Vector2.Distance(p, centre)) - SlapRadius - SlapClearance;

    private const float FinalSlapRadius = 6f;       // SlapHappy_FinalSlap circle at the centre
    private const float FinalSlapClearance = 0.8f;  // character hitbox 0.5 + move jitter 0.3
    // The post-implosion DodgeSlap sprints off at 121.3; the centre final slap snapshots at 122.59 (122.89 - 0.3)
    // and the role cones at 122.95 (123.25 - 0.3).
    private const float FinalSlapSnapshotAfterLeave = 1.29f;
    private const float ConeSnapshotAfterLeave = 1.65f;

    private static Vector2 PositionAt(Vector2 from, Vector2 to, float seconds)
    {
        var length = Vector2.Distance(from, to);
        var travelled = AiManager.SprintSpeed * seconds;
        return length <= travelled ? to : from + (to - from) * (travelled / length);
    }

    // Distance from `from` (inside the arena) along unit `dir` to the arena-circle border.
    private static float RayToArenaBorder(Vector2 from, Vector2 dir)
    {
        var proj = Vector2.Dot(from, dir);
        return -proj + MathF.Sqrt(proj * proj + ArenaRadius * ArenaRadius - from.LengthSquared());
    }

    // Rotate an XZ vector by `radians` (CCW in the X->Z plane).
    private static Vector2 RotateVec(Vector2 v, float radians)
    {
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        return new Vector2(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
    }

    // Park Chaos's tank 45 degrees clockwise from Kefka's facing (Kefka sits centre, oriented
    // to KefkaPosition[kefkaIndex]) so Chaos, which it holds, is dragged to that known
    // angle before the implosion cast locks in its cone axis. The tank looks at Chaos on arrival.
    private void AnchorChaosTankForImplosion(int kefkaIndex)
    {
        var tank = world.Party.Get(state.ChaosTank);
        if (tank is null) return;
        var spot = state.KefkaPosition[kefkaIndex].Rotate(1).Apply(new Vector3(0f, 0f, -5f));
        tank.MoveTo(spot);
    }

    private void ReturnToMiddle(int playerIndex) =>
        Puller(playerIndex)?.MoveTo(new Vector3(0f, 0f, 0f));

    // A handoff's giver walks back to the middle. YAN waits while the giver still holds the
    // tether: leaving before the taker has it drags the beam into the stack, and whoever it
    // sweeps over takes it. giveUpAt is the next party-wide move after the wave (73.1
    // DodgeEdictAndLookUpon, 109.5 the wave's recall): a return issued later would override it,
    // so past it the giver is left to that move. D>S>A and S>D>A leave on the timeline's clock.
    private void ReturnAfterHandoff(int giverSlot, int tetherIndex, float giveUpAt)
    {
        if (tetherOrder == TetherOrder.Yan
            && state.ScenarioObjects.Tethers.ElementAtOrDefault(tetherIndex) is { } tether
            && Puller(giverSlot) is { } giver && ReferenceEquals(tether.B, giver))
        {
            if (world.Events.Elapsed + YanKeepPollSeconds < giveUpAt)
                world.Events.Add(YanKeepPollSeconds, () => ReturnAfterHandoff(giverSlot, tetherIndex, giveUpAt));
            else
                AnoMech.Core.DiagnosticLog.Warn($"[UmadP3BlackHoleAi] YAN: {PullerRole(giverSlot)} still holds tetherIndex {tetherIndex} at the next party move -- the handoff never landed; not recalled.");
            return;
        }
        ReturnToMiddle(giverSlot);
    }

    // Look Upon (rect along the KefkaPosition[lookKefkaIndex] axis through centre, 16y
    // wide) split. The tether holder rides the last black hole's tether out to the arena
    // edge, but the hole's bearing may sit in the Look-Upon corridor; nudge it ±45° to the
    // side that clears the line and send the holder there. The rest take the opposite edge
    // (180°), which the centre-symmetric corridor leaves equally clear.
    private Vector3 LookUponHolderSpot(int holderSlot, int lookKefkaIndex)
    {
        var theta = state.KefkaPosition[lookKefkaIndex].RadiansFromNorth;   // Look-Upon line bearing

        // Bearing centre→black hole the holder is tethered to. Falls back to the line's
        // perpendicular (always clear) if no active tether is readable.
        var holder = Puller(holderSlot);
        var blackHole = (TetherHeldBy(holder) ?? state.ScenarioObjects.Tethers.FirstOrDefault())?.A;
        var alpha = blackHole is { } bh
                        ? MathF.Atan2(bh.Position.X, -bh.Position.Z)
                        : theta + MathF.PI / 2f;

        // Of alpha±45°, at least one clears the 8y half-width corridor (edge perpendicular
        // = edge·|sin(β-θ)|, ≥ 12.7y even with the hole on the line). Take the side farther
        // from the line — the larger |sin(β-θ)| — which is always the safe one. A hole on the
        // line or square to it ties, and the tie goes to `plus` on every client: a peer's copy
        // of the hole stands a float rounding away from the host's.
        var plus = alpha + MathF.PI / 4f;
        var minus = alpha - MathF.PI / 4f;
        var beta = MathF.Abs(MathF.Sin(minus - theta)) > MathF.Abs(MathF.Sin(plus - theta)) + SideTieTolerance ? minus : plus;

        const float edge = 18f;   // ride out to the arena edge, past the hole's r=17 ring
        return new Vector3(edge * MathF.Sin(beta), 0f, -edge * MathF.Cos(beta));
    }

    // Both trips sprint against a 3.67s deadline. tetherPlayerIndex is a timeline slot (see
    // PullerRole), not a PartyRole ordinal.
    private IAiMove DodgeLookUponSplitHolder(int tetherPlayerIndex, int lookKefkaIndex, bool leaveWithoutTether)
    {
        var coords = new Vector2?[8];
        if (!leaveWithoutTether && TetherHeldBy(Puller(tetherPlayerIndex)) is null)
            return AiMove.Create(coords).NaturalOrder();
        var holderSpot = LookUponHolderSpot(tetherPlayerIndex, lookKefkaIndex);
        var holderRole = PullerRole(tetherPlayerIndex);
        coords[(int)holderRole] = new Vector2(holderSpot.X, holderSpot.Z);
        return AiMove.Create(coords).NaturalOrder();
    }

    private IAiMove DodgeLookUponSplitOthers(int tetherPlayerIndex, int lookKefkaIndex)
    {
        var holderSpot = LookUponHolderSpot(tetherPlayerIndex, lookKefkaIndex);
        var holderRole = PullerRole(tetherPlayerIndex);
        var coords = new Vector2?[8];
        for (int i = 0; i < 8; i++)
            if ((PartyRole)i != holderRole)
                coords[i] = new Vector2(-holderSpot.X, -holderSpot.Z);
        return AiMove.Create(coords).NaturalOrder();
    }

    // The hole (tether.A) each slot's last GrabTether sent its puller to. PassableEnd seeds an
    // unrelated hole's tether with a random member, so PullTether must pull on the assigned
    // hole, not whatever tether the player happens to hold. The hole, not the SimTether: a
    // peer recreates its local SimTether on every endpoint change.
    private readonly Dictionary<int, SimCharacter?> assignedHole = new();

    // The hole each new set's grab took, by when that grab was due. The host swaps one set for the
    // next in a frame, but a peer's client shows the swap about a round trip after the clock that
    // times its grab, still showing the last set's tethers until then.
    private readonly Dictionary<SimCharacter, float> setHoles = new();

    private const float NewSetPollSeconds = 0.05f;
    private const float NewSetMaxWaitSeconds = 3f;

    // A new set's tether, grabbed once this client shows the set: a tether at tetherIndex, and none
    // of a hole an earlier set's grab took.
    private void GrabNewTether(int tetherIndex, int playerIndex) => GrabNewTether(tetherIndex, playerIndex, world.Events.Elapsed, 0f, holdOn: true);

    // holdOn: HoldOnTo afterwards; PullBothNewTethers re-tries its own grabs.
    private void GrabNewTether(int tetherIndex, int playerIndex, float dueAt, float waited, bool holdOn)
    {
        var tethers = state.ScenarioObjects.Tethers;
        var shown = tetherIndex < tethers.Count
                    && !tethers.Any(t => t.A is { } hole && setHoles.TryGetValue(hole, out var at) && at < dueAt);
        if (!shown && waited < NewSetMaxWaitSeconds)
        {
            world.Events.Add(NewSetPollSeconds, () => GrabNewTether(tetherIndex, playerIndex, dueAt, waited + NewSetPollSeconds, holdOn));
            return;
        }
        if (waited > 0f)
            AnoMech.Core.DiagnosticLog.Info($"[UmadP3BlackHoleAi] GrabTether: the new set showed {waited:F2}s after the grab was due{(shown ? "" : " -- gave up waiting")}.");
        if (GrabTether(tetherIndex, playerIndex)?.A is not { } hole) return;
        setHoles.TryAdd(hole, dueAt);
        if (holdOn)
            world.Events.Add(TetherRegrabInterval, () => HoldOnTo(hole, playerIndex, dueAt + NewSetMaxWaitSeconds));
    }

    // Back into the hole's tether line, as a player keeps stepping in, until this client shows the
    // tether on them: over a slow link the walk can end beside a line that has since moved.
    private void HoldOnTo(SimCharacter hole, int playerIndex, float until)
    {
        var player = Puller(playerIndex);
        var tether = state.ScenarioObjects.Tethers.FirstOrDefault(t => ReferenceEquals(t.A, hole));
        if (player is null || tether is null || ReferenceEquals(tether.B, player) || world.Events.Elapsed >= until) return;
        if (!player.IsIntercepting) player.Intercept(tether, 3f);
        world.Events.Add(TetherRegrabInterval, () => HoldOnTo(hole, playerIndex, until));
    }

    // YAN's 2nd and 9th pulls: one player takes both tethers of a two-tether set and pulls them out
    // together. Holding both is allowed: PassableEnd only skips a candidate hosting that tether id
    // itself (TetherEnd.cs), and the hole hosts it. One poll drives every step, each waiting on what
    // this client shows:
    // 1. the set's first tether, grabbed like any new set's, re-tried until it shows on the player;
    // 2. a step out between the two holes: walking straight to the second beam can cross the stack,
    //    and the first tether's beam (now running from this player to its hole) goes to whoever it
    //    sweeps over -- a new set's random holder can stand across the arena, so the second beam
    //    may run through the centre;
    // 3. the second tether, re-tried likewise;
    // 4. from pullAt, out to the holes' bisector once both show, or once waiting any longer would
    //    miss shotAt even sprinting. That spot is on the holes' side, so the walk never crosses the
    //    stack, and it takes a lone held tether's rect off the stack as well.
    private const float BothGrabStagingRadius = 5f;
    // Arrive this long before the shot: Nothingness reads positions 0.3s early, plus slack.
    private const float BothPullArrivalMargin = 0.8f;

    private void PullBothNewTethers(int playerIndex, float pullAt, float shotAt)
    {
        var dueAt = world.Events.Elapsed;
        GrabNewTether(tetherIndex: 0, playerIndex, dueAt, waited: 0f, holdOn: false);
        SimCharacter? second = null;
        var stagedAt = float.NaN;
        world.Events.Add(NewSetPollSeconds, Poll);

        void Poll()
        {
            var now = world.Events.Elapsed;
            if (Puller(playerIndex) is not { } player || now >= shotAt) return;
            // GrabNewTether records the first hole once the set shows; until then it is an earlier set's.
            var first = assignedHole.GetValueOrDefault(playerIndex) is { } hole && setHoles.GetValueOrDefault(hole, float.NaN) == dueAt ? hole : null;
            var holdsFirst = Holds(player, first);
            var holdsSecond = Holds(player, second);
            if (now >= pullAt && first is not null
                && (second ?? state.ScenarioObjects.Tethers.ElementAtOrDefault(1)?.A) is { } other && !ReferenceEquals(other, first))
            {
                var (spot, rawSpot) = BothPullSpot(Flat(first.Position), Flat(other.Position));
                var distance = Vector2.Distance(Flat(player.Position), spot);
                var available = shotAt - BothPullArrivalMargin - now;
                if ((holdsFirst && holdsSecond) || NewSetPollSeconds + distance / AiManager.SprintSpeed > available)
                {
                    LeaveForBothPullSpot(player, playerIndex, (holdsFirst ? 1 : 0) + (holdsSecond ? 1 : 0), spot, rawSpot, distance, available);
                    return;
                }
            }
            if (!holdsFirst && now - dueAt < NewSetMaxWaitSeconds)
                KeepIntercepting(player, first);
            else if (float.IsNaN(stagedAt))
                stagedAt = StepOutBetweenHoles(player, playerIndex, first, holdsFirst);
            else if (now >= stagedAt && !holdsSecond)
            {
                if (second is null)
                {
                    second = GrabTether(tetherIndex: 1, playerIndex, second: true)?.A;
                    if (second is not null) setHoles.TryAdd(second, dueAt);
                }
                else
                    KeepIntercepting(player, second);
            }
            world.Events.Add(NewSetPollSeconds, Poll);
        }
    }

    // Back into the hole's tether line, as HoldOnTo does, while no walk to it is under way.
    private void KeepIntercepting(SimCharacter player, SimCharacter? hole)
    {
        if (hole is null || player.IsIntercepting) return;
        if (state.ScenarioObjects.Tethers.FirstOrDefault(t => ReferenceEquals(t.A, hole)) is { } tether && !ReferenceEquals(tether.B, player))
            player.Intercept(tether, 3f);
    }

    // When the step out ends.
    private float StepOutBetweenHoles(SimCharacter player, int playerIndex, SimCharacter? first, bool holdsFirst)
    {
        var now = world.Events.Elapsed;
        if (!holdsFirst)
        {
            AnoMech.Core.DiagnosticLog.Warn($"[UmadP3BlackHoleAi] GrabTether: {PullerRole(playerIndex)} still lacks the set's first tether {NewSetMaxWaitSeconds:F0}s after the grab was due -- going for the second anyway.");
            return now;
        }
        var other = state.ScenarioObjects.Tethers.ElementAtOrDefault(1)?.A;
        if (first is null || other is null || Holds(player, other)) return now;
        var staging = Bisector(Flat(first.Position), Flat(other.Position)) * BothGrabStagingRadius;
        player.MoveTo(new Vector3(staging.X, 0f, staging.Y));
        return now + Vector2.Distance(Flat(player.Position), staging) / AiSprintRules.RunSpeed;
    }

    private void LeaveForBothPullSpot(SimCharacter player, int playerIndex, int held, Vector2 spot, Vector2 rawSpot, float distance, float available)
    {
        var role = PullerRole(playerIndex);
        if (held < 2)
            AnoMech.Core.DiagnosticLog.Warn($"[UmadP3BlackHoleAi] PullTether: {role} holds {held} of its two tethers when waiting longer would miss the shot -- {(held == 0 ? "nothing to pull" : "pulling the one it holds")}.");
        if (held == 0) return;
        var sprint = distance / AiSprintRules.RunSpeed > available;
        if (sprint) (player as ISimPartyMember)?.UseSprint(available);
        AnoMech.Core.DiagnosticLog.Info($"[UmadP3BlackHoleAi] PullTether: {role} holds {held} tethers at ({player.Position.X:F1},{player.Position.Z:F1}) -- {(sprint ? "sprinting" : "moving")} to ({spot.X:F1},{spot.Y:F1}){(spot != rawSpot ? $" [nudged from ({rawSpot.X:F1},{rawSpot.Y:F1}) to clear an obstacle]" : "")}.");
        player.MoveTo(new Vector3(spot.X, 0f, spot.Y), sprint ? AiManager.SprintSpeed : AiSprintRules.RunSpeed);
    }

    // second: the other tether of PullBothNewTethers, kept out of assignedHole.
    private SimTether? GrabTether(int tetherIndex, int playerIndex, float intercept = 3f, bool second = false)
    {
        var player = Puller(playerIndex);
        var tether = state.ScenarioObjects.Tethers.ElementAtOrDefault(tetherIndex);
        if (!second) assignedHole[playerIndex] = tether?.A;
        var role = (player as ISimPartyMember)?.Role.ToString() ?? $"slot#{playerIndex}";
        if (tether is null)
        {
            AnoMech.Core.DiagnosticLog.Warn($"[UmadP3BlackHoleAi] GrabTether: tetherIndex {tetherIndex} not found in ScenarioObjects.Tethers ({state.ScenarioObjects.Tethers.Count} known) -- {role} sent nowhere.");
            return null;
        }
        var bh = tether.A is { } a ? new Vector2(a.Position.X, a.Position.Z) : Vector2.Zero;
        var from = player is null ? "(no player)" : $"({player.Position.X:F1},{player.Position.Z:F1})";
        AnoMech.Core.DiagnosticLog.Info($"[UmadP3BlackHoleAi] GrabTether: {role} intercepting tetherIndex {tetherIndex} (black hole at ({bh.X:F1},{bh.Y:F1})) from {from}, margin={intercept}.");
        player?.Intercept(tether, intercept);
        return tether;
    }

    // Grab only starts the walk, and a black hole can be ~17y out; a Pull that lands before
    // arrival would otherwise no-op and strand the player.
    private const int PullTetherMaxRetries = 4;

    private void PullTether(int playerIndex, int retriesLeft = PullTetherMaxRetries)
    {
        var player = Puller(playerIndex);
        var role = (player as ISimPartyMember)?.Role.ToString() ?? $"slot#{playerIndex}";
        // Only the assigned hole's tether counts (see assignedHole); re-resolved each call.
        var hole = assignedHole.GetValueOrDefault(playerIndex);
        var assigned = hole is null ? null : state.ScenarioObjects.Tethers.FirstOrDefault(t => ReferenceEquals(t.A, hole));
        if (assigned is not { A: { } blackHole, B: { } held } || !ReferenceEquals(held, player))
        {
            AnoMech.Core.DiagnosticLog.Warn($"[UmadP3BlackHoleAi] PullTether: {role} does not hold their assigned tether yet -- pull skipped (still mid-Intercept, grab never happened, or a different hole's random seed briefly gave them someone else's). Current pos ({player?.Position.X:F1},{player?.Position.Z:F1}). Retries left: {retriesLeft}.");
            if (retriesLeft > 0)
                world.Events.Add(1f, () => PullTether(playerIndex, retriesLeft - 1));
            return;
        }
        var bhPos = new Vector2(blackHole.Position.X, blackHole.Position.Z);
        var heldPos = new Vector2(held.Position.X, held.Position.Z);
        var (spot, rawSpot) = PullSpot(bhPos, heldPos);
        AnoMech.Core.DiagnosticLog.Info($"[UmadP3BlackHoleAi] PullTether: {role} held at ({heldPos.X:F1},{heldPos.Y:F1}), black hole at ({bhPos.X:F1},{bhPos.Y:F1}) -- moving to ({spot.X:F1},{spot.Y:F1}) (dist {Vector2.Distance(heldPos, spot):F1}y){(spot != rawSpot ? $" [nudged from ({rawSpot.X:F1},{rawSpot.Y:F1}) to clear an obstacle]" : "")}.");
        player?.MoveTo(new Vector3(spot.X, 0f, spot.Y));
    }

    private (Vector2 Spot, Vector2 Raw) PullSpot(Vector2 bhPos, Vector2 heldPos)
    {
        if (tetherOrder == TetherOrder.Yan)
            return YanPullSpot(RotateVec(Vector2.Normalize(bhPos), MathF.PI / 4f));
        // Pull spot, then nudged 1.5y farther from the black hole along the bh→player axis.
        var rawSpot = CardinalClockwise(bhPos) + Vector2.Normalize(heldPos - bhPos) * 1.5f;
        // The raw spot can coincide with a passive hole's avoid radius (both ~14y out), where
        // ClampOutside and Steer fight and the bot stalls; push it clear up front.
        return (world.Obstacles.ClampOutside(rawSpot, margin: 2f), rawSpot);
    }

    // YAN(211) pulls to a diamond waymark corner (1-4 at (±6, ±6), 8.49y out): the farthest a melee
    // still reaches a boss tanked in the middle (Chaos's 5.5y hitbox + 3y). A single pull takes the
    // corner 45° clockwise of its hole; a two-tether set the corner between its two holes. Each
    // hole's Nothingness rect (6y wide, from the 17y-out hole through its holder and on across the
    // arena) then passes (17r/√2)/√(17² - 17r√2 + r²) = 8.1y from the centre, clear of the stack.
    // In half the waves a passive hole sits 9y out on every corner. Stepping in from it would leave
    // the holders ~6y out, where each rect sweeps the next corner's holder, so the spot slides along
    // the same radius instead, 20° clockwise -- just clear of that hole's avoid radius, the rects
    // still 8.4y from the centre.
    private const float YanPullRadius = 8.485f;
    private const float YanPullObstacleMargin = 0.7f;
    private const float YanPullSidestep = 20f * MathF.PI / 180f;

    private (Vector2 Spot, Vector2 Raw) YanPullSpot(Vector2 corner)
    {
        var rawSpot = corner * YanPullRadius;
        if (IsClear(rawSpot)) return (rawSpot, rawSpot);
        var aside = RotateVec(corner, YanPullSidestep) * YanPullRadius;
        return (IsClear(aside) ? aside : world.Obstacles.ClampOutside(aside, margin: YanPullObstacleMargin), rawSpot);

        bool IsClear(Vector2 p) => world.Obstacles.ClampOutside(p, margin: YanPullObstacleMargin) == p;
    }

    private (Vector2 Spot, Vector2 Raw) BothPullSpot(Vector2 holeA, Vector2 holeB) => YanPullSpot(Bisector(holeA, holeB));

    private bool Holds(SimCharacter? player, SimCharacter? hole) =>
        player is not null && hole is not null
        && state.ScenarioObjects.Tethers.Any(t => ReferenceEquals(t.A, hole) && ReferenceEquals(t.B, player));

    private static Vector2 Flat(Vector3 p) => new(p.X, p.Z);

    // Unit direction halfway between two directions from the arena centre.
    private static Vector2 Bisector(Vector2 a, Vector2 b) => Vector2.Normalize(Vector2.Normalize(a) + Vector2.Normalize(b));

    // A teammate walking through the beam takes the tether, and Intercept ends once the grab lands.
    private const float TetherRegrabInterval = 0.4f;

    private void ScheduleTetherRegrab(float fromTime, float toTime, int tetherIndex, int playerIndex)
    {
        for (var t = fromTime; t < toTime; t += TetherRegrabInterval)
            world.Events.Add(t, () => RegrabTetherIfLost(tetherIndex, playerIndex));
    }

    private void RegrabTetherIfLost(int tetherIndex, int playerIndex)
    {
        var player = Puller(playerIndex);
        if (player is null || player.IsIntercepting || TetherHeldBy(player) is not null) return;
        GrabTether(tetherIndex, playerIndex);
    }

    // YAN's 3rd-8th pulls run the shared D>S>A waves with the marked pullers. Anyone walking
    // through a beam takes its tether (PassableEnd), and the waves' simultaneous grabs and pulls
    // cross each other's beams now and then, so a tether can end on the wrong player. YAN watches
    // every tether until its last shot: a slot's puller without its tether goes back for it, and
    // pulls it out once it has it if the wave's pull has gone out. A handoff's taker meets the beam
    // a step out from the giver, so it pulls too once it has the tether, back to the corner. D>S>A
    // and S>D>A run without it.
    // From, Until: the slot's grab and its tether's last shot for it. PullAt: the wave's pull, or a
    // handoff's grab. Margin: the grab's.
    private static readonly (float From, float Until, int Tether, int Slot, float Margin, float PullAt)[] YanSharedHolds =
    [
        (58.1f, 62.83f, 0, 4, 3f, 60.1f), (64f, 73.03f, 0, 5, 1f, 64f),
        (58.1f, 67.93f, 1, 0, 3f, 60.1f), (69f, 73.03f, 1, 1, 1f, 69f),
        (58.1f, 73.03f, 2, 3, 3f, 60.1f),
        (92.3f, 97.12f, 0, 5, 3f, 94.3f), (98f, 107.32f, 0, 6, 1f, 98f),
        (92.3f, 102.22f, 1, 1, 3f, 94.3f), (103f, 107.32f, 1, 2, 1f, 103f),
        (92.3f, 107.32f, 2, 7, 3f, 94.3f),
    ];
    private const float YanKeepPollSeconds = 0.1f;

    private void KeepYanTethersThroughSharedWaves()
    {
        foreach (var (from, until, tether, slot, margin, pullAt) in YanSharedHolds)
            world.Events.Add(from, () => KeepYanTether(tether, slot, margin, pullAt, until, lost: false));
    }

    private void KeepYanTether(int tetherIndex, int slot, float margin, float pullAt, float until, bool lost)
    {
        var now = world.Events.Elapsed;
        if (now >= until || Puller(slot) is not { } player) return;
        if (state.ScenarioObjects.Tethers.ElementAtOrDefault(tetherIndex) is not { } tether)
        {
            world.Events.Add(YanKeepPollSeconds, () => KeepYanTether(tetherIndex, slot, margin, pullAt, until, lost));
            return;
        }
        var holds = ReferenceEquals(tether.B, player);
        if (!holds && !player.IsIntercepting)
        {
            AnoMech.Core.DiagnosticLog.Info($"[UmadP3BlackHoleAi] YAN: {PullerRole(slot)} lacks tetherIndex {tetherIndex} ({(tether.B as ISimPartyMember)?.Role.ToString() ?? "nobody"} holds it) -- intercepting again.");
            player.Intercept(tether, margin);
        }
        if (holds && lost && now >= pullAt)
            PullTether(slot, retriesLeft: 0);
        world.Events.Add(YanKeepPollSeconds, () => KeepYanTether(tetherIndex, slot, margin, pullAt, until, lost: !holds));
    }

    private SimTether? TetherHeldBy(SimCharacter? player) =>
        player is null
            ? null
            : state.ScenarioObjects.Tethers.FirstOrDefault(t => ReferenceEquals(t.B, player));

    // Non-tether players hold centre for the whole wave, so a pulled hole must clear
    // Nothingness's radius from there; 8y still tagged the stack.
    private const float TetherPullRadius = 14f;

    private static Vector2 CardinalClockwise(Vector2 cardinal)
    {
        var dir = Vector2.Normalize(cardinal);
        var c = MathF.Cos(MathF.PI / 3f);
        var s = MathF.Sin(MathF.PI / 3f);
        return new Vector2(dir.X * c - dir.Y * s, dir.X * s + dir.Y * c) * TetherPullRadius;
    }

    // Stomp a Mole resolves in Kefka's spawn frame (KefkaPosition[4]): the two towers
    // sit ±10 along Kefka's east-west, the spreads/stacks along its north-south. All the
    // formations below are authored in that frame and rotated onto it. Roles split into a
    // support group (0-3) and a DPS group (4-7); within each, even indices are the "west"
    // pair and odd indices the "east" pair, so a role's side is consistent from the
    // preposition through the corner spread to the towers.
    private static readonly int[] SupportGroup = [0, 1, 2, 3];
    private static readonly int[] DpsGroup = [4, 5, 6, 7];
    private const float StompTowerX = 10f;        // tower offset along Kefka's east-west axis
    private const float StompTowerPairGap = 1.5f; // the two soakers straddle the tower centre

    // Whichever group owns the first stack marker (StackTargets[0], always one support +
    // one DPS) stacks first; the other group takes the towers first, then they trade.
    private bool SupportsStackFirst() => !state.StackTargets[0].IsDps();

    // Supports gather north of centre, DPS south, ready for the first spread.
    private IAiMove PrepositionForStomp() =>
        AiMove.Create(new(0, -5), new (0, -5), new (0, -5), new (0, -5),
                    new (0, 5), new(0, 5), new (0, 5), new (0, 5))
              .NaturalOrder()
              .ApplyPositions(state.KefkaPosition[4].Apply);

    // First BlizzardIII spreads drop on the prepositioned spots; step out to the four
    // intercardinal corners two-per-corner (supports north, DPS south, west pair vs east).
    // The x=±10 seats are 7.5y from the nearer tower spot: Blizzard's r=6 puddle plus two
    // seats' jitter ruled out the 6.5y a symmetric corner gave.
    private IAiMove StompBlizzardCorners() =>
        AiMove.Create(
                  new(-8f, -10f), new(8f, -10f), new(-10f, -9f), new(10f, -9f),
                  new(-8f, 10f), new(8f, 10f), new(-10f, 9f), new(10f, 9f))
              .NaturalOrder()
              .ApplyPositions(state.KefkaPosition[4].Apply);

    // Second spread has dropped on the corners: the stacking group collapses to centre
    // for the first knockback stack, the other group pairs onto the two towers.
    private IAiMove StompStackAndTowers()
    {
        var coords = new Vector2?[8];
        foreach (var r in SupportsStackFirst() ? SupportGroup : DpsGroup) coords[r] = Vector2.Zero;
        PlaceOnTowers(coords, SupportsStackFirst() ? DpsGroup : SupportGroup);
        return AiMove.Create(coords).NaturalOrder().ApplyPositions(state.KefkaPosition[4].Apply);
    }

    // The swap is staggered by side because the west tower resolves ~1.3s before the east:
    // the west pairs trade the moment the first stack and the west tower are done, the east
    // pairs only once the east tower has resolved (StompSwapEast). Each half sends the old
    // tower pair back to centre for the second stack and the old stack pair out to its tower.
    private IAiMove StompSwapWest() => StompSwapHalf(0, 2, -StompTowerX);
    private IAiMove StompSwapEast() => StompSwapHalf(1, 3, StompTowerX);

    // pairA/pairB are the two group-array indices making up this side's pair; towerX is
    // the tower they trade onto/off of.
    private IAiMove StompSwapHalf(int pairA, int pairB, float towerX)
    {
        var newTower = SupportsStackFirst() ? SupportGroup : DpsGroup; // old stackers now soak
        var newStack = SupportsStackFirst() ? DpsGroup : SupportGroup; // old soakers now stack
        var coords = new Vector2?[8];
        coords[newTower[pairA]] = new Vector2(towerX, -StompTowerPairGap);
        coords[newTower[pairB]] = new Vector2(towerX, StompTowerPairGap);
        coords[newStack[pairA]] = Vector2.Zero;
        coords[newStack[pairB]] = Vector2.Zero;
        return AiMove.Create(coords).NaturalOrder().ApplyPositions(state.KefkaPosition[4].Apply);
    }

    private static void PlaceOnTowers(Vector2?[] coords, int[] group)
    {
        coords[group[0]] = new Vector2(-StompTowerX, -StompTowerPairGap);
        coords[group[2]] = new Vector2(-StompTowerX, StompTowerPairGap);
        coords[group[1]] = new Vector2(StompTowerX, -StompTowerPairGap);
        coords[group[3]] = new Vector2(StompTowerX, StompTowerPairGap);
    }
}
