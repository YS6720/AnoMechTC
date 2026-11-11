using System;
using System.Numerics;

namespace AnoMech.Core.Game.Ai;

// 引導線（Owner 11-04 追加）：本機玩家下一個機制該站哪裡。目的地就是打法給玩家座位的同一份座標，不另存站位資料：
// AiManager 每一步的該座位位置（站位練習已套用、不抖動），以及打法直接對玩家下的 MoveTo（PlayerMovement 轉來）。
// 純邏輯，畫面在 Windows/GuideLineOverlay。
// 一步的目的地一直顯示到打法發出下一步為止：到點後常要原地站好幾秒等判定（UMAD P2 消滅之腳轉向在到點後 3 秒以上），
// 到點就消失會在最需要站住的時候沒線。場景結束（完成／失敗）由 overlay 看場景狀態收掉，拆場時 Clear。
// 延遲出現（Owner 11-05 追加）：目的地換成新的點後，等設定的秒數才顯示；計時用場景時間（EventScheduler.Time），
// 所以暫停時不走、倍速時跟著場景時間走。重複寫入同一點不重新計時，延遲中換點就從新點重新計時。
public sealed class GuideLine
{
    public const float MaxDelaySeconds = 10f;
    public const float DelayStepSeconds = 0.5f;
    // 同一點的容差：打法同一步重下 MoveTo 的浮點誤差不算「換點」。
    private const float SamePointTolerance = 0.05f;

    private sealed record Step(Vector3 Target, float IssuedAt);

    // 整筆替換（參考指派是原子的），畫面讀到的一定是同一步的目的地與發出時刻。
    private Step? step;

    // local：場景座標 XZ；now：場景時間（EventScheduler.Time）。
    public void Publish(Vector2 local, float now)
    {
        var target = new Vector3(local.X, 0f, local.Y);
        // 同一點且不是場景時間倒回（重開）後的寫入：沿用原發出時刻（延遲不重算），座標仍換成最新的。
        var issuedAt = step is { } s && now >= s.IssuedAt
            && Vector3.DistanceSquared(s.Target, target) <= SamePointTolerance * SamePointTolerance
            ? s.IssuedAt
            : now;
        step = new Step(target, issuedAt);
    }

    // delaySeconds：設定的延遲出現秒數（ClampDelay 後）。場景時間倒回（重開）時發出時刻在未來，不顯示上一輪的目的地。
    public Vector3? TargetAt(float now, float delaySeconds)
        => step is { } s && now >= s.IssuedAt + delaySeconds ? s.Target : null;

    public void Clear() => step = null;

    // 設定值收斂到 0–10 秒、每格 0.5 秒；設定檔被手改成非數字時當 0（立即顯示）。
    public static float ClampDelay(float seconds)
    {
        if (!float.IsFinite(seconds)) return 0f;
        var snapped = MathF.Round(seconds / DelayStepSeconds) * DelayStepSeconds;
        return Math.Clamp(snapped, 0f, MaxDelaySeconds);
    }
}
