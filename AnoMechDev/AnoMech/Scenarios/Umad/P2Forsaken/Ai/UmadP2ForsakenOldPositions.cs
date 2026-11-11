using System.Numerics;

namespace AnoMech.Scenarios.Umad.P2Forsaken.Ai;

// 我方保留的兩支 [old positions] 打法用的偶數輪座標。上游 master（1646a70）把 OldEven 從
// UmadP2ForsakenRinonAiHelper 刪掉，座標照 fork 點 v0.3.12.0 原值搬到這裡，helper 維持上游原檔。
internal static class UmadP2ForsakenOldPositions
{
    public static readonly Vector2?[] OldEven =
    [
        // active group
        new(3.2f, -3.2f),  // cone1
        new(8, -8),        // chariot1
        new(-3.2f, -3.2f), // cone2
        new(-8, -8),       // chariot2
        // passive group
        new(8.8f, -2.4f),  // cone bait1
        new(3.8f, 4.2f),   // clone bait1
        new(-3.8f, 4.2f),  // clone bait2
        new(-8.8f, -2.4f)  // cone bait2
    ];
}
