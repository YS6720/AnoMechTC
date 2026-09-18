using System;
using System.Collections.Generic;
using System.Numerics;

namespace AnoMech.Core.Game;

public enum HitRangeShape { Circle, Ring, Cone, Rect, Cross }

/// <summary>One judged area, in scenario-local XZ coordinates.</summary>
public readonly record struct HitRange(
    HitRangeShape Shape, Vector3 Origin, float Rotation,
    float A, float B, float ExpiresAt, bool Hit, float Remaining = 0f, string Label = "")
{
    // A/B meaning per shape: Circle(radius, —), Ring(inner, outer),
    // Cone(halfAngleRad, length), Rect(halfWidth, length), Cross(halfWidth, halfLength).
    public float Radius => A;
}

/// <summary>
/// Records the exact geometry each damage check used, so the overlay can draw the real
/// judged area for mechanics the game gives no omen for. Recording only happens while the
/// toggle is on: with it off this is a single bool test per hit test and nothing allocates.
/// The shapes are what actually kills — they are taken from the same call the judgment used,
/// not re-derived — so an overlay built on them cannot drift from the rule.
/// </summary>
public static class HitRangeDebug
{
    /// <summary>How long a judged area stays on screen after it resolved.</summary>
    public const float DisplaySeconds = 2f;
    private const int Capacity = 64;

    private static readonly List<HitRange> ranges = new(Capacity);
    // Standing zones (ground puddles and the like) are judged every frame, so they are held
    // as long as the scenario says they are live instead of being re-recorded per frame.
    private static readonly Dictionary<int, HitRange> zones = new();
    private static float clock;

    public static bool Enabled { get; set; }

    /// <summary>
    /// Names the next recorded area (the action being judged). Without it every outline looks
    /// the same on screen and an unrelated check is easy to mistake for the mechanic you are
    /// watching (維護者 2026-09-18：紅框與火圈對不起來，其實是不同的判定).
    /// </summary>
    public static string NextLabel { get; set; } = "";

    public static void Tick(float deltaSeconds)
    {
        clock += deltaSeconds;
        if (ranges.Count == 0) return;
        lock (ranges)
            ranges.RemoveAll(range => range.ExpiresAt <= clock);
    }

    public static void Clear()
    {
        lock (ranges)
        {
            ranges.Clear();
            zones.Clear();
        }
    }

    /// <summary>Add or move a standing judged area; <paramref name="key"/> identifies it.</summary>
    public static void SetZone(int key, HitRangeShape shape, Vector3 origin, float rotation,
        float a, float b, string label = "")
    {
        if (!Enabled) return;
        lock (ranges) zones[key] = new HitRange(shape, origin, rotation, a, b, float.MaxValue, false,
            Label: label);
    }

    public static void ClearZone(int key)
    {
        lock (ranges) zones.Remove(key);
    }

    public static IReadOnlyList<HitRange> Snapshot()
    {
        lock (ranges)
        {
            var now = clock;
            var result = new HitRange[ranges.Count + zones.Count];
            for (var i = 0; i < ranges.Count; i++)
                result[i] = ranges[i] with { Remaining = ranges[i].ExpiresAt - now };
            var index = ranges.Count;
            foreach (var zone in zones.Values)
                result[index++] = zone with { Remaining = DisplaySeconds };
            return result;
        }
    }

    public static void Record(HitRangeShape shape, Vector3 origin, float rotation, float a, float b, bool hit)
    {
        if (!Enabled) return;
        lock (ranges)
        {
            if (ranges.Count >= Capacity) ranges.RemoveAt(0);
            ranges.Add(new HitRange(shape, origin, rotation, a, b, clock + DisplaySeconds, hit,
                Label: NextLabel));
        }
        NextLabel = "";
    }
}
