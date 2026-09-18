using System;
using System.Numerics;
using AnoMech.Core.Game;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AnoMech.Windows;

/// <summary>
/// Draws the area each damage check actually judged, for mechanics the game gives no omen
/// for. The shapes come from the judgment itself (<see cref="HitRangeDebug"/>), so what you
/// see is what kills — it cannot drift from the rule the way a hand-drawn telegraph would.
/// Skills that already have a native omen keep it; this only adds an outline on top.
/// </summary>
public sealed class HitRangeOverlay : Window
{
    private const int Segments = 48;
    // Thin single-pixel outlines disappeared under the game's own effects (維護者 2026-09-18).
    // Every edge is drawn three times: a dark backing stroke so it reads over bright VFX, the
    // bright colour on top, and a translucent fill so the area is visible even when the
    // outline is behind a particle. Alpha rides the remaining display time so the newest
    // judgement is the brightest one on screen.
    private const float OutlineWidth = 5f;
    private const float BackingWidth = 9f;
    private readonly Plugin plugin;

    public HitRangeOverlay(Plugin plugin)
        : base("###AnoMechHitRangeOverlay")
    {
        this.plugin = plugin;
        Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoMove;
        RespectCloseHotkey = false;
        IsOpen = false;
    }

    public override void PreOpenCheck()
    {
        var enabled = plugin.Configuration.ShowHitRanges;
        HitRangeDebug.Enabled = enabled;
        IsOpen = enabled && plugin.Game.World.Map.IsInInstance;
        if (!IsOpen) HitRangeDebug.Clear();
    }

    public override void PreDraw()
    {
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(ImGui.GetIO().DisplaySize);
    }

    public override void Draw()
    {
        var ranges = HitRangeDebug.Snapshot();
        if (ranges.Count == 0) return;
        var draw = ImGui.GetWindowDrawList();
        var coordinates = plugin.Game.World.Coordinates;
        foreach (var range in ranges)
        {
            // Newest judgement at full strength, fading out over its display time.
            var life = Math.Clamp(range.Remaining / HitRangeDebug.DisplaySeconds, 0f, 1f);
            var alpha = 0.35f + 0.65f * life;
            var rgb = range.Hit ? new Vector3(1f, 0.25f, 0.25f) : new Vector3(1f, 0.85f, 0.15f);
            var colour = ImGui.GetColorU32(new Vector4(rgb, alpha));
            var backing = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.75f * alpha));
            var fill = ImGui.GetColorU32(new Vector4(rgb, 0.16f * alpha));
            Caption(draw, coordinates, range, colour, backing);
            switch (range.Shape)
            {
                case HitRangeShape.Circle:
                    Ring(draw, coordinates, range.Origin, range.A, colour, backing, fill);
                    break;
                case HitRangeShape.Ring:
                    // Only the outer edge is filled; the inner circle marks the safe hole.
                    Ring(draw, coordinates, range.Origin, range.B, colour, backing, fill);
                    Ring(draw, coordinates, range.Origin, range.A, colour, backing, 0);
                    break;
                case HitRangeShape.Cone:
                    Cone(draw, coordinates, range, colour, backing, fill);
                    break;
                case HitRangeShape.Rect:
                    // Back edge at the origin, extending forward — same as the hit test.
                    Rect(draw, coordinates, range.Origin, range.Rotation, range.A, 0f, range.B,
                        colour, backing, fill);
                    break;
                case HitRangeShape.Cross:
                    Rect(draw, coordinates, range.Origin, range.Rotation, range.A, -range.B, range.B,
                        colour, backing, fill);
                    Rect(draw, coordinates, range.Origin, range.Rotation + MathF.PI / 2f,
                        range.A, -range.B, range.B, colour, backing, fill);
                    break;
            }
        }
    }

    // Which check this outline belongs to, printed at its centre: an unrelated judgement is
    // otherwise indistinguishable from the mechanic being watched.
    private void Caption(ImDrawListPtr draw, Coordinates coordinates, HitRange range, uint colour, uint backing)
    {
        if (!Project(coordinates, range.Origin, out var centre)) return;
        var size = range.Shape switch
        {
            HitRangeShape.Circle => $"r={range.A:0.#}",
            HitRangeShape.Ring => $"r={range.A:0.#}~{range.B:0.#}",
            HitRangeShape.Cone => $"{range.A * 2f * 180f / MathF.PI:0}° L={range.B:0.#}",
            HitRangeShape.Rect => $"w={range.A * 2f:0.#} L={range.B:0.#}",
            HitRangeShape.Cross => $"w={range.A * 2f:0.#} L={range.B * 2f:0.#}",
            _ => "",
        };
        var text = range.Label.Length == 0 ? size : $"{range.Label}  {size}";
        for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0)
                    draw.AddText(centre + new Vector2(dx, dy), backing, text);
        draw.AddText(centre, colour, text);
    }

    private bool Project(Coordinates coordinates, Vector3 local, out Vector2 screen)
    {
        var world = coordinates.ToGlobal(local with { Y = 0f });
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player != null) world.Y = player.Position.Y;
        return Plugin.GameGui.WorldToScreen(world, out screen);
    }

    private void Ring(ImDrawListPtr draw, Coordinates coordinates, Vector3 centre, float radius,
        uint colour, uint backing, uint fill)
    {
        if (radius <= 0f) return;
        Span<Vector2> points = stackalloc Vector2[Segments];
        var count = 0;
        for (var i = 0; i < Segments; i++)
        {
            var angle = MathF.Tau * i / Segments;
            var point = centre + new Vector3(MathF.Sin(angle) * radius, 0f, MathF.Cos(angle) * radius);
            if (!Project(coordinates, point, out var screen)) continue;
            points[count++] = screen;
        }
        Polyline(draw, points[..count], colour, backing, fill, closed: count == Segments);
    }

    private void Cone(ImDrawListPtr draw, Coordinates coordinates, HitRange range,
        uint colour, uint backing, uint fill)
    {
        var steps = Math.Max(2, (int)(Segments * range.A / MathF.PI));
        Span<Vector2> points = stackalloc Vector2[steps + 2];
        var count = 0;
        if (Project(coordinates, range.Origin, out var apex)) points[count++] = apex;
        for (var i = 0; i <= steps; i++)
        {
            var angle = range.Rotation - range.A + 2f * range.A * i / steps;
            var edge = range.Origin + new Vector3(MathF.Sin(angle) * range.B, 0f, MathF.Cos(angle) * range.B);
            if (Project(coordinates, edge, out var screen)) points[count++] = screen;
        }
        Polyline(draw, points[..count], colour, backing, fill, closed: true);
    }

    private void Rect(ImDrawListPtr draw, Coordinates coordinates, Vector3 origin, float rotation,
        float halfWidth, float back, float front, uint colour, uint backing, uint fill)
    {
        var forward = new Vector3(MathF.Sin(rotation), 0f, MathF.Cos(rotation));
        var right = new Vector3(MathF.Cos(rotation), 0f, -MathF.Sin(rotation));
        Span<Vector3> corners =
        [
            origin + forward * back + right * halfWidth,
            origin + forward * front + right * halfWidth,
            origin + forward * front - right * halfWidth,
            origin + forward * back - right * halfWidth,
        ];
        Span<Vector2> points = stackalloc Vector2[4];
        var count = 0;
        foreach (var corner in corners)
            if (Project(coordinates, corner, out var screen))
                points[count++] = screen;
        Polyline(draw, points[..count], colour, backing, fill, closed: count == 4);
    }

    private static void Polyline(ImDrawListPtr draw, ReadOnlySpan<Vector2> points,
        uint colour, uint backing, uint fill, bool closed)
    {
        if (points.Length < 2) return;
        if (fill != 0 && closed && points.Length >= 3)
        {
            // Convex fill is enough for our shapes (circle/cone/rect); a concave projection
            // would only make the tint slightly wrong, never the outline.
            Span<Vector2> copy = stackalloc Vector2[points.Length];
            points.CopyTo(copy);
            unsafe
            {
                fixed (Vector2* first = copy)
                    draw.AddConvexPolyFilled(first, copy.Length, fill);
            }
        }
        for (var pass = 0; pass < 2; pass++)
        {
            var stroke = pass == 0 ? backing : colour;
            var width = pass == 0 ? BackingWidth : OutlineWidth;
            for (var i = 0; i + 1 < points.Length; i++)
                draw.AddLine(points[i], points[i + 1], stroke, width);
            if (closed) draw.AddLine(points[^1], points[0], stroke, width);
        }
    }
}
