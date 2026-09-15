using System;
using System.Collections.Generic;
using System.Numerics;

namespace AnoMech.Scenarios.Recorded;

/// <summary>
/// npcmove 軌跡（<c>pos = [[t,x,y,z,rot]…]</c>）→ 重播指令。**純邏輯，離線可驗**。
///
/// 由來（BLIND-ASTRA-07）：先前每一段都交給一般 locomotion（速度＝XZ 距離／時間差），
/// 而 locomotion 的 Tick 只在 XZ 推進、Y 保留現值、抵達才一次套上終點高度。於是
/// ① 純垂直段的 XZ 速度是 0 ⇒ 第一個 tick 就被判定「已到達」跳到終點高度；
/// ② 斜向段移動途中完全不改 Y ⇒ 到了才一次跳高。
/// 兩者在畫面上都只是「這隻的高度怪怪的」，看不出重播沒有照錄影時序還原 Y。
///
/// 現在每個取樣區間變成一筆指令：連續段交給 <c>SimCharacter.MoveRecorded</c>，回放端的
/// <c>TimedMotion</c> 每個 tick 按錄影時間一起內插 XYZ、準時抵達下一個取樣點；
/// 3D 速度過大的段落仍歸類為瞬移，走 SetPosition。一般地面 AI 的移動語意不受影響。
/// </summary>
public static class RecordedNpcTrack
{
    /// <summary>超過這個速度視為傳送／換場，不是走過去的。</summary>
    public const float TeleportSpeed = 20f;

    /// <param name="Time">錄影時間軸上要下這個指令的時刻（＝區間起點取樣時刻）。</param>
    /// <param name="From">區間起點座標（錄影值）。</param>
    /// <param name="To">區間終點座標（錄影值），連續段要在 <paramref name="Duration"/> 秒後準時抵達。</param>
    /// <param name="Duration">這個區間的錄影時長；0＝同刻，立即到位。</param>
    /// <param name="Rotation">抵達時的朝向；null＝朝行進方向。</param>
    /// <param name="Teleport">true＝直接設座標，不走過去。</param>
    public readonly record struct Segment(float Time, Vector3 From, Vector3 To, float Duration,
                                          float? Rotation, bool Teleport);

    /// <summary>軌跡點 → 座標。舊 4 欄資料（[t,x,z,rot]）沒有高度，維持 Y＝0。</summary>
    public static Vector3 Point(float[] point)
        => point.Length >= 5
            ? new Vector3(point[1], point[2], point[3])
            : new Vector3(point[1], 0f, point[2]);

    /// <summary>軌跡點的朝向；舊 4 欄資料的第 4 欄就是朝向。</summary>
    public static float? Rotation(float[] point)
        => point.Length >= 5 ? point[4] : point.Length > 3 ? point[3] : null;

    /// <summary>整條軌跡要下的指令序列（時間遞增，一段取樣區間一筆）。</summary>
    public static List<Segment> Build(IReadOnlyList<float[]>? track)
    {
        var segments = new List<Segment>();
        if (track == null) return segments;
        for (var i = 0; i + 1 < track.Count; i++)
        {
            var a = track[i];
            var b = track[i + 1];
            if (a is not { Length: >= 3 } || b is not { Length: >= 3 }) continue;
            var from = Point(a);
            var to = Point(b);
            var duration = MathF.Max(0f, b[0] - a[0]);
            // 瞬移判定用 3D 距離：垂直傳送的 XZ 速度是 0，用 XZ 判會被當成「慢慢走」。
            var teleport = Vector3.Distance(from, to) / MathF.Max(0.05f, duration) > TeleportSpeed;
            segments.Add(new Segment(a[0], from, to, duration, Rotation(b), teleport));
        }
        return segments;
    }
}
