using System.Collections.Generic;
using AnoMech.Core.SimObjects;

namespace AnoMech.Scenarios.Top.P5Omega;

public sealed record MonitorSide(int Mul, uint ActionId)
{
    public static readonly MonitorSide Left = new(1, TopConstants.ActionId.OversampledWaveCannonLeft);
    public static readonly MonitorSide Right = new(-1, TopConstants.ActionId.OversampledWaveCannonRight);
}

public sealed class TopP5OmegaState
{
    private readonly Rng rng = new();
    
    public RoleList HelloWorldTargets { get; }
    public RoleList DoubleDynamicTargets { get; private set; }

    public IReadOnlyList<Direction> AttackDirections { get; }
    public IReadOnlyList<OmegaAttack> OmegaAttacks { get; } 
    public Direction BettleSpawnDirection { get; }
    public bool FirstWaveCannonFront { get; }

    public MonitorSide MonitorSide { get; }
    public MarkerMode Markers { get; }

    public TopP5OmegaState(SimParty party, TopP5OmegaStateOverrides overrides)
    {
        Markers = overrides.Markers;
        var firstAttackDirection = rng.NextIntercardinal();
        var secondAttackDirection = firstAttackDirection.Rotate(rng.NextSign() * 2);
        AttackDirections = [firstAttackDirection, firstAttackDirection.Flip(), secondAttackDirection, secondAttackDirection.Flip()];
        HelloWorldTargets = new RoleListBuilder
        {
            Size = 4,
            ForcePlayerIndex = (overrides.HelloWorldOrder, overrides.HelloWorldType) switch
            {
                (HelloWorldOrderOption.Auto,   HelloWorldTypeOption.Near) => [0, 2],
                (HelloWorldOrderOption.Auto,   HelloWorldTypeOption.Far)  => [1, 3],
                (HelloWorldOrderOption.Any,    HelloWorldTypeOption.Auto) => [0, 1, 2, 3],
                (HelloWorldOrderOption.Any,    HelloWorldTypeOption.Near) => [0, 2],
                (HelloWorldOrderOption.Any,    HelloWorldTypeOption.Far)  => [1, 3],
                (HelloWorldOrderOption.First,  HelloWorldTypeOption.Auto) => [0, 1],
                (HelloWorldOrderOption.First,  HelloWorldTypeOption.Near) => [0],
                (HelloWorldOrderOption.First,  HelloWorldTypeOption.Far)  => [1],
                (HelloWorldOrderOption.Second, HelloWorldTypeOption.Auto) => [2, 3],
                (HelloWorldOrderOption.Second, HelloWorldTypeOption.Near) => [2],
                (HelloWorldOrderOption.Second, HelloWorldTypeOption.Far)  => [3],
                _ => [],
            },
            IncludePlayer = overrides.HelloWorldOrder == HelloWorldOrderOption.None ? false : null,
        }.Build(party);
        DoubleDynamicTargets = new RoleListBuilder
        {
            Size = 4,
            IncludePlayer = overrides.ExtraDynamis,
        }.Build(party);
        BettleSpawnDirection = overrides.BettleSpawnDirection ?? rng.NextCardinal();
        MonitorSide = overrides.MonitorSide ?? rng.NextObj(MonitorSide.Left, MonitorSide.Right);
        FirstWaveCannonFront = overrides.FirstWaveCannonFront ?? rng.NextBool();
        var firstFAttack = overrides.FirstFAttack ?? RandomFAttack();
        var firstMAttack = overrides.FirstMAttack ?? RandomMAttack();
        OmegaAttack secondFAttack;
        OmegaAttack secondMAttack;
        while (true)
        {
            secondFAttack = overrides.SecondFAttack ?? RandomFAttack();
            secondMAttack = overrides.SecondMAttack ?? RandomMAttack();
            if ((firstFAttack, firstMAttack) != (secondFAttack, secondMAttack)) break;
            // Both seconds user-set to match firsts: trust the user.
            if (overrides is { SecondFAttack: not null, SecondMAttack: not null }) break;
        }
        OmegaAttacks = [firstFAttack, firstMAttack, secondFAttack, secondMAttack];
    }

    /// <summary>
    /// 真副本的第二目標**至少有一個是潛能量高漲 2 層**（兩個都是 2 層也會出現）。
    /// 原本兩份名單各自獨立亂數，會擲出「兩個第二目標都只有 1 層」的盤面——實戰沒有這種盤。
    /// 修法＝出現 0 重疊時，把其中一個第二目標換進 2 層名單（換掉一個非第二目標的 2 層）；
    /// 自然擲出 1 個或 2 個重疊的盤面不動，所以「兩個都 2 層」的機率維持原樣。
    /// 回傳是否動過名單。
    /// </summary>
    public bool EnsureSecondTargetHasDoubleDynamis()
    {
        var second1 = HelloWorldTargets[2];
        var second2 = HelloWorldTargets[3];
        if (DoubleDynamicTargets.Contains(second1) || DoubleDynamicTargets.Contains(second2))
            return false;

        var promoted = rng.NextObj(second1, second2);
        var doubles = DoubleDynamicTargets.List;
        var demoted = rng.NextInt(doubles.Length);
        doubles[demoted] = promoted;
        DoubleDynamicTargets = new RoleList(DoubleDynamicTargets.Party, doubles);
        return true;
    }

    private OmegaAttack RandomFAttack() => rng.NextObj(OmegaAttack.Legs, OmegaAttack.Staff);
    private OmegaAttack RandomMAttack() => rng.NextObj(OmegaAttack.Shield, OmegaAttack.Sword);
}
