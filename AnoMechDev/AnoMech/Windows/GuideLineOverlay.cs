using System;
using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Ai;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AnoMech.Windows;

/// <summary>
/// 引導線（Owner 11-04 追加）：從本機玩家腳下畫線到打法指定給自己座位的下一個站位。
/// 目的地由 <see cref="AnoMech.Core.Game.Ai.GuideLine"/> 提供（與 AI 隊友同一份打法座標），
/// 這裡只負責畫；畫法沿用 <see cref="HitRangeOverlay"/> 的全螢幕透明視窗與深色底線。
/// </summary>
public sealed class GuideLineOverlay : Window
{
    private const int Segments = 32;
    private const float MarkerRadius = 0.6f;
    private const float LineWidth = 4f;
    private const float BackingWidth = 8f;
    private readonly Plugin plugin;

    public GuideLineOverlay(Plugin plugin)
        : base("###AnoMechGuideLineOverlay")
    {
        this.plugin = plugin;
        Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoMove;
        RespectCloseHotkey = false;
        IsOpen = false;
    }

    // 場景完成或失敗就收線（沒有下一步）；暫停時保留，方便看清楚該站哪。
    public override void PreOpenCheck()
        => IsOpen = plugin.Configuration.ShowGuideLine && plugin.Game.World.Map.IsInInstance
            && plugin.Game.ScenarioState is GameScenarioState.Running or GameScenarioState.Paused;

    public override void PreDraw()
    {
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(ImGui.GetIO().DisplaySize);
    }

    public override void Draw()
    {
        var world = plugin.Game.World;
        var delay = GuideLine.ClampDelay(plugin.Configuration.GuideLineDelaySeconds);
        if (world.Guide.TargetAt(world.Events.Time, delay) is not { } local) return;
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;

        var height = player.Position.Y;
        var target = world.Coordinates.ToGlobal(local with { Y = 0f });
        target.Y = height;
        if (!Plugin.GameGui.WorldToScreen(player.Position, out var from)) return;
        if (!Plugin.GameGui.WorldToScreen(target, out var to)) return;

        var draw = ImGui.GetWindowDrawList();
        var colour = ImGui.GetColorU32(new Vector4(0.2f, 0.95f, 1f, 1f));
        var backing = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.75f));

        draw.AddLine(from, to, backing, BackingWidth);
        draw.AddLine(from, to, colour, LineWidth);

        // 目的地：地面上的圓圈（世界尺寸，遠近自然縮放）＋十字。
        Span<Vector2> ring = stackalloc Vector2[Segments];
        var count = 0;
        for (var i = 0; i < Segments; i++)
        {
            var angle = MathF.Tau * i / Segments;
            var point = target + new Vector3(MathF.Sin(angle) * MarkerRadius, 0f, MathF.Cos(angle) * MarkerRadius);
            if (Plugin.GameGui.WorldToScreen(point, out var screen)) ring[count++] = screen;
        }
        if (count == Segments)
        {
            for (var i = 0; i < Segments; i++)
                draw.AddLine(ring[i], ring[(i + 1) % Segments], backing, BackingWidth);
            for (var i = 0; i < Segments; i++)
                draw.AddLine(ring[i], ring[(i + 1) % Segments], colour, LineWidth);
            Cross(draw, ring[0], ring[Segments / 2], backing, colour);
            Cross(draw, ring[Segments / 4], ring[Segments * 3 / 4], backing, colour);
        }
    }

    private static void Cross(ImDrawListPtr draw, Vector2 a, Vector2 b, uint backing, uint colour)
    {
        draw.AddLine(a, b, backing, BackingWidth);
        draw.AddLine(a, b, colour, LineWidth);
    }
}
