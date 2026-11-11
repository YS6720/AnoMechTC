using System;
using System.Numerics;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using AnoMech.Scenarios.Umad.P3BlackHole;
using static AnoMech.Scenarios.Umad.P3LimitCut.UmadP3LimitCutState;

namespace AnoMech.Scenarios.Umad.P3LimitCut;

public sealed class UmadP3LimitCutAi : IScenarioAi<UmadP3LimitCutState>
{
    public string Name => "Standard";

    private UmadP3LimitCutState state = null!;
    private SimWorld world = null!;

    public void Run(UmadP3LimitCutState stateParam, SimWorld worldParam)
    {
        state = stateParam;
        world = worldParam;
        // 我方：opt-in 上游到點規劃（上游 AiManager 預設就是這套）。
        var ai = new AiManager(world, upstreamArrival: true);

        ai.Move(0.5f, StackAtBosses);
        ai.Move(4.5f, BaitOut, arrivalTime: 7.5f);
        ai.Move(8.3f, TanksIntoStack, arrivalTime: 12.0f);
        ai.Move(8.4f, BaitBack, arrivalTime: 12.6f);
        world.Events.Add(14.94f, MainTankLimitBreakIfBothTanksAreBots);
        world.Events.Add(15.3f, FaceForVacuumWave);
        ai.Move(17.5f, StackForCyclones, arrivalTime: 19.6f);
        ai.Move(20.6f, ChargeSpots, arrivalTime: 30.2f);
        ai.Move(32.7f, ClearForThunder, arrivalTime: 37.5f);
        world.Events.Add(33.6f, FirstTankOntoExdeath);
        ai.Move(38.9f, SwapThunderTanks, arrivalTime: 41.2f);
        ScheduleThunderClearance(36.5f, 38.9f, 42.2f);
        ai.Move(47.0f, StackAtBosses);
    }

    private (PartyRole First, PartyRole? Second) ThunderRoles => ThunderIIIPlanning.Roles(state.ThunderPlan);
    private PartyRole ThunderBystanderTank => ThunderRoles.First == PartyRole.MainTank ? PartyRole.OffTank : PartyRole.MainTank;

    private IAiMove ClearForThunder()
    {
        var coords = new Vector2?[8];
        // 我方（D816）：偏離上游 1646a70 Ai.cs:45 的固定點 OnCircle(Hold, 10.6)。王此時已被第一坦拖走，分擔計畫的另一坦改以王為基準站位，避免路過時被點名或進雷三圈。
        coords[(int)ThunderBystanderTank] = ThunderRoles.Second is not null && state.Objects.Exdeath is { } ex && world.Party.Get(ThunderBystanderTank) is { } bystander
            ? SecondTankSeat(Flat(ex.Position), Flat(bystander.Position))
            : Flat(OnCircle(Hold, 10.6f));
        for (var slot = 2; slot < 8; slot++)
            coords[slot] = Flat(OnCircle(Hold + MathF.PI, 4f) + OnCircle(Hold + MathF.PI / 2f, (slot % 3 - 1) * 1.5f) + OnCircle(Hold + MathF.PI, slot / 3 * 1.5f));
        return AiMove.Create(coords).NaturalOrder();
    }

    private void FirstTankOntoExdeath()
    {
        if (state.Objects.Exdeath is not { } exdeath) return;
        if (world.Party.Get(ThunderRoles.First) is { } first && first.IsAlive()) first.Follow(exdeath);
    }

    private IAiMove SwapThunderTanks()
    {
        var (first, second) = ThunderRoles;
        if (second is not { } secondRole) return AiMove.Create().NaturalOrder();
        var a = world.Party.Get(first);
        var b = world.Party.Get(secondRole);
        if (a is null || b is null) return AiMove.Create().NaturalOrder();
        var coords = new Vector2?[8];
        coords[(int)secondRole] = Flat(a.Position);
        coords[(int)first] = Flat(b.Position);
        return AiMove.Create(coords).NaturalOrder();
    }

    private const float ThunderClearDistance = 10f;
    private const float ThunderClearanceInterval = 0.4f;

    private void ScheduleThunderClearance(float fromTime, float swapTime, float toTime)
    {
        var (first, second) = ThunderRoles;
        for (var t = fromTime; t < swapTime; t += ThunderClearanceInterval)
            world.Events.Add(t, () => EnforceThunderClearanceOnce(first, second));
        // 我方（D816）：偏離上游 1646a70 Ai.cs:78-80（互換後只排除第二坦、把第一坦往外推再被 19y 夾回）。改成讓剛吃過第一發的坦克以王為基準避開第二坦。
        for (var t = swapTime; t <= toTime; t += ThunderClearanceInterval)
            world.Events.Add(t, () =>
            {
                if (second is { } s) EnforceThunderClearanceOnce(s, first);
                else EnforceThunderClearanceOnce(first, null);
            });
    }

    private void EnforceThunderClearanceOnce(PartyRole first, PartyRole? second)
    {
        if (second is { } secondRole
            && world.Party.Get(first) is { } firstMember && firstMember.IsAlive()
            && world.Party.Get(secondRole) is { } secondMember && secondMember.IsAlive())
        {
            var firstPos = Flat(firstMember.Position);
            var secondPos = Flat(secondMember.Position);
            // 我方（D816）：偏離上游 1646a70 Ai.cs:91-99（座位以第一坦位置重算、每 0.4 秒亂跳）。改用以王為基準的固定座位。
            if ((secondPos - firstPos).LengthSquared() < ThunderClearDistance * ThunderClearDistance
                && state.Objects.Exdeath is { } ex)
            {
                var seat = SecondTankSeat(Flat(ex.Position), secondPos);
                secondMember.MoveTo(new Vector3(seat.X, 0f, seat.Y));
            }
        }

        if (state.Objects.Exdeath is not { } exdeath || !exdeath.IsAlive()) return;
        var bossPos = Flat(exdeath.Position);
        for (var i = 0; i < 8; i++)
        {
            var role = (PartyRole)i;
            if (role == first || (second is { } sec && role == sec)) continue;
            if (world.Party.Get(i) is not { } member || !member.IsAlive()) continue;
            var offset = Flat(member.Position) - bossPos;
            if (offset.LengthSquared() >= ThunderClearDistance * ThunderClearDistance) continue;
            var dir = offset.LengthSquared() > 1e-4f ? Vector2.Normalize(offset) : new Vector2(1f, 0f);
            var target = bossPos + dir * ThunderClearDistance;
            var clearRadius = 19f;
            if (target.LengthSquared() > clearRadius * clearRadius)
                target = target.LengthSquared() > 1e-4f ? Vector2.Normalize(target) * clearRadius : target;
            member.MoveTo(new Vector3(target.X, 0f, target.Y));
        }
    }

    // 我方（D816）：分擔計畫另一坦的座位＝離王 10y、方向垂直於「王→場中心」、取靠近該坦克（from）的一側。超出 19y 時沿 19y 圈
    // 滑到仍離王 10y 的點（不往中心夾：夾回會靠近王，貼王的吃雷坦克 ≈4.2y 時只剩 5–5.5y 餘裕）。不換到另一側：換邊要繞過王，
    // 模擬中互換後換邊的坦克會在路上進第二發雷三圈。
    private const float ArenaClearRadius = 19f;

    internal static Vector2 SecondTankSeat(Vector2 boss, Vector2 from)
    {
        var inward = boss.LengthSquared() > 1e-4f ? Vector2.Normalize(-boss) : new Vector2(0f, -1f);
        var side = RotateVec(inward, MathF.PI / 2f);
        if (Vector2.Dot(from - boss, side) < 0f) side = -side;
        var seat = boss + side * ThunderClearDistance;
        if (seat.LengthSquared() <= ArenaClearRadius * ArenaClearRadius) return seat;
        // 19y 圈與王的 10y 圈在 side 那一側的交點。
        var r = boss.Length();
        var along = (r * r + ArenaClearRadius * ArenaClearRadius - ThunderClearDistance * ThunderClearDistance) / (2f * r);
        return boss / r * along + side * MathF.Sqrt(MathF.Max(0f, ArenaClearRadius * ArenaClearRadius - along * along));
    }

    private static Vector2 RotateVec(Vector2 v, float radians)
    {
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        return new Vector2(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
    }

    private void MainTankLimitBreakIfBothTanksAreBots()
    {
        if (world.Party.Get(PartyRole.MainTank) is not { } mainTank || world.Party.Get(PartyRole.OffTank) is not { } offTank) return;
        if (!world.Party.IsBotDriven(mainTank) || !world.Party.IsBotDriven(offTank)) return;
        world.Party.UseLimitBreak(PartyRole.MainTank);
    }

    private float Hold => SpotHeading(state.BossSpot);

    private Vector2 Flat(Vector3 v) => new(v.X, v.Z);

    private Vector2 StackSpot(int slot)
    {
        var spread = OnCircle(Hold + MathF.PI / 2f, (slot % 4 - 1.5f) * 1.2f) + OnCircle(Hold, slot / 4 * 1.4f);
        return Flat(OnCircle(Hold, 5.5f) + spread);
    }

    private IAiMove StackAtBosses()
    {
        var coords = new Vector2?[8];
        coords[(int)PartyRole.MainTank] = Flat(OnCircle(Hold, 7.5f));
        coords[(int)PartyRole.OffTank] = Flat(OnCircle(Hold, 9.1f) + OnCircle(Hold + MathF.PI / 2f, 2.5f));
        for (var slot = 2; slot < 8; slot++) coords[slot] = StackSpot(slot);
        return AiMove.Create(coords).NaturalOrder();
    }

    private IAiMove TanksIntoStack()
    {
        var coords = new Vector2?[8];
        coords[(int)PartyRole.MainTank] = StackSpot((int)PartyRole.MainTank);
        coords[(int)PartyRole.OffTank] = StackSpot((int)PartyRole.OffTank);
        return AiMove.Create(coords).NaturalOrder();
    }

    private IAiMove StackForCyclones()
    {
        var away = state.Objects.Exdeath is { } exdeath && exdeath.Position.LengthSquared() > 1f
            ? MathF.Atan2(-exdeath.Position.X, -exdeath.Position.Z)
            : Hold + MathF.PI;
        var centre = OnCircle(away, 4.3f);
        var coords = new Vector2?[8];
        for (var slot = 0; slot < 8; slot++)
            coords[slot] = Flat(centre + OnCircle(away + MathF.PI / 2f, slot % 3 - 1f) + OnCircle(away, slot / 3));
        return AiMove.Create(coords).NaturalOrder();
    }

    private IAiMove BaitOut() => AiMove.Single(state.BaitRole, Flat(OnCircle(Hold + MathF.PI, 17.7f)));

    private IAiMove BaitBack() => AiMove.Single(state.BaitRole, StackSpot((int)state.BaitRole));

    private IAiMove ChargeSpots()
    {
        var coords = new Vector2?[8];
        for (var k = 0; k < 8; k++)
            coords[(int)state.Numbers[k]] = Flat(OnCircle(state.SafeHeading(k), 19f));
        return AiMove.Create(coords).NaturalOrder();
    }

    private void FaceForVacuumWave()
    {
        if (state.Objects.Exdeath is not { } exdeath) return;
        for (var slot = 0; slot < 8; slot++)
        {
            var member = world.Party.Get(slot);
            if (member is null || !member.IsAlive() || member is not ISimPartyMember pm) continue;
            // 我方：上游 DebugBotControl 除錯旗標不移植＝恆關，玩家自己的朝向不代轉。
            if (member is SimPlayer) continue;
            var away = member.Position - exdeath.Position;
            member.Face(state.Winds[pm.Role] == Wind.Headwind ? member.Position + away : exdeath.Position);
        }
    }
}
