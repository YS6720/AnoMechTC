using System;
using System.Numerics;

namespace AnoMech.Core.Game;

/// <param name="Position">這一刻的座標（XYZ 一起前進）。</param>
/// <param name="Rotation">這一刻該面向哪裡；null＝維持現有朝向。</param>
/// <param name="Arrived">已經走到終點（呼叫端收掉這段移動）。</param>
internal readonly record struct TimedMotionSample(Vector3 Position, float? Rotation, bool Arrived);

/// <summary>
/// 計時位移：**位置是經過時間的函數**，不是速度的累積。
///
/// 由來（BLIND-ASTRA-07）：一般 locomotion 用速度走，Tick 只在 XZ 推進、Y 保留現值、
/// 抵達那一刻才一次套上終點高度。重播錄影軌跡的事實是**取樣時刻**——純垂直段的 XZ
/// 速度是 0 ⇒ 第一個 tick 就被當成「已到達」跳到終點高度；斜向段的高度則整段憋到抵達。
///
/// 這個型別是該行為的唯一計算來源：<see cref="Movement.MoveRecorded"/> 每個 tick
/// 呼叫 <see cref="Advance"/>，離線驗證呼叫 <see cref="At"/>——兩者算的是同一份東西。
/// </summary>
internal sealed class TimedMotion(Vector3 from, Vector3 to, float duration, float? finalRotation)
{
    public Vector3 From { get; } = from;
    public Vector3 To { get; } = to;

    /// <summary>錄影上這段走多久。0＝同刻，立即到位。</summary>
    public float Duration { get; } = MathF.Max(0f, duration);

    /// <summary>抵達時要套的朝向；null＝面向行進方向。</summary>
    public float? FinalRotation { get; } = finalRotation;

    /// <summary>已經走掉的秒數（依錄影時間推進，不因動畫鎖定而停表）。</summary>
    public float Elapsed { get; private set; }

    /// <summary>行進方向（弧度，0＝南）；null＝這段只有高度變化，維持原朝向。</summary>
    public float? Heading { get; } =
        (to.X - from.X) * (to.X - from.X) + (to.Z - from.Z) * (to.Z - from.Z) > 1e-6f
            ? MathF.Atan2(to.X - from.X, to.Z - from.Z)
            : null;

    /// <summary>有水平位移＝真的在跑（純高度變化不播跑步循環）。</summary>
    public bool HasHorizontalTravel => Heading is not null;

    /// <summary>推進 <paramref name="deltaSeconds"/> 秒並取樣。</summary>
    public TimedMotionSample Advance(float deltaSeconds)
    {
        Elapsed += MathF.Max(0f, deltaSeconds);
        return At(Elapsed);
    }

    /// <summary>這段移動開始後 <paramref name="elapsed"/> 秒時的位置與朝向。</summary>
    public TimedMotionSample At(float elapsed)
    {
        var progress = Duration <= 0f ? 1f : Math.Clamp(elapsed / Duration, 0f, 1f);
        var arrived = progress >= 1f;
        return new TimedMotionSample(
            arrived ? To : Vector3.Lerp(From, To, progress),
            arrived ? FinalRotation ?? Heading : Heading,
            arrived);
    }
}
