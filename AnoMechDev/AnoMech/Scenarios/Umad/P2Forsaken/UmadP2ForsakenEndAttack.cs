using System;
using System.Text.Json.Serialization;
using AnoMech.Core.EnemyActions;
using static AnoMech.Core.EnemyActions.EnemyActionEffects;
using static AnoMech.Core.EnemyActions.Severity;
using static AnoMech.Scenarios.Umad.UmadConstants;

namespace AnoMech.Scenarios.Umad.P2Forsaken;

// 我方：上游 1646a70 這段在 UmadP2ForsakenState.cs，原樣搬到這個檔，讓 Tests 只連結狀態與打法
// （EnemyAction 帶原生相依，連不進測試）；內容不改。
public sealed record EndAttack(uint CastBarAction)
{
    // Future's End cleaves the half facing the player as the cast starts, Past's End the other half.
    public static readonly EndAttack FuturesEnd = new(ActionId.FutureSEnd)
    {
        KefkaHit = EndHit(ActionId.FutureSEnd_Resolve),
        CloneHit = EndHit(ActionId.FutureSEnd_CloneResolve),
        AllThingsEnding = AllThingsEndingHit(ActionId.AllThingsEnding_Future, 0f),
    };

    public static readonly EndAttack PastsEnd = new(ActionId.PastSEnd)
    {
        KefkaHit = EndHit(ActionId.PastSEnd_Resolve),
        CloneHit = EndHit(ActionId.PastSEnd_CloneResolve),
        AllThingsEnding = AllThingsEndingHit(ActionId.AllThingsEnding_Past, MathF.PI),
    };

    [JsonIgnore] public EnemyAction KefkaHit { get; private init; } = null!;
    [JsonIgnore] public EnemyAction CloneHit { get; private init; } = null!;
    [JsonIgnore] public EnemyAction AllThingsEnding { get; private init; } = null!;

    private static EnemyAction EndHit(uint actionId) => new(actionId)
    {
        Cast = new() { AnimationLock = 6f },
        Effects = [Damage(UmadActions.MagicVulns), UmadActions.LongMagicVulnerabilityUp],
        Timing = new() { DamageDelay = 0.75f },
    };

    // Snapshots as the ability lands, not at bar end: from bar end there is no time left to reach
    // the tower that follows. UNVERIFIED in game.
    private static EnemyAction AllThingsEndingHit(uint actionId, float rotation) => new(actionId)
    {
        Cast = new() { AnimationLock = 3f, Rotation = rotation },
        Area = new() { Size = Geometry.AllThingsEndHalfCone },
        Effects = [Damage(UmadActions.MagicVulns, Lethal)],
        Timing = new() { DamageDelay = 1.10f, ResolveSnapshotOffset = CastSpec.ActionEffectOffset },
    };
}
