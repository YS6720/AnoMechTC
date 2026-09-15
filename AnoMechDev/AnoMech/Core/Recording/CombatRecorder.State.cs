using System;
using System.Collections.Generic;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;

namespace AnoMech.Core.Recording;

internal sealed unsafe partial class CombatRecorder
{
    // State samples are deliberately not labelled play/stop/loop events. A clip
    // shorter than the sampling interval can be missed; no native ABI is guessed.
    private sealed class RecordedAnimationState
    {
        public AnimationFields Fields;
        public readonly ushort[] Timelines = new ushort[14];
        public readonly float[] Speeds = new float[14];
    }

    private readonly record struct AnimationFields(byte ModelState, byte Animation0, byte Animation1,
        ushort BaseOverride, float OverallSpeed, byte Mode, byte ModeParam, short TransformationId, bool Ready);
    private readonly record struct BgmFields(uint Scene, ushort Requested, ushort Playing, ushort Previous,
        uint Situation, uint PlayState, bool CustomFade, uint FadeOut, uint FadeIn, uint FadeStart, float InitialVolume);
    private readonly record struct LayoutFields(bool Available, int InitState, uint Territory, uint Cfc,
        uint LayerFilterKey, uint Level, byte LayerEntryType);

    private readonly Dictionary<ulong, RecordedAnimationState> knownAnimations = new();
    private BgmFields[] knownBgmScenes = [];
    private (bool Available, uint Situation, bool Battle)? lastBgmContext;
    private LayoutFields? lastLayout;

    private void ResetStateCaptureLocked()
    {
        knownAnimations.Clear();
        knownBgmScenes = [];
        lastBgmContext = null;
        lastLayout = null;
    }

    private void CaptureActorAnimation(ulong id, uint entityId, Character* character)
    {
        ref var timeline = ref character->Timeline;
        var fields = new AnimationFields(timeline.ModelState, timeline.AnimationState[0], timeline.AnimationState[1],
            timeline.BaseOverride, timeline.OverallSpeed, (byte)character->Mode, character->ModeParam,
            character->TransformationId, timeline.TimelineSequencer.Parent != null);
        var ids = timeline.TimelineSequencer.TimelineIds;
        var speeds = timeline.TimelineSequencer.TimelineSpeeds;
        if (!float.IsFinite(fields.OverallSpeed))
        {
            Write("capture-gap", $"\"channel\":\"actoranimation\",\"id\":{id},\"reason\":\"nonfinite-overall-speed\"");
            return;
        }
        for (var i = 0; i < speeds.Length; i++)
            if (!float.IsFinite(speeds[i]))
            {
                Write("capture-gap", $"\"channel\":\"actoranimation\",\"id\":{id},\"reason\":\"nonfinite-slot-speed\"");
                return;
            }

        var existed = knownAnimations.TryGetValue(id, out var old);
        if (existed && old!.Fields == fields && ids.SequenceEqual(old.Timelines) && speeds.SequenceEqual(old.Speeds)) return;
        old ??= new RecordedAnimationState();
        old.Fields = fields;
        ids.CopyTo(old.Timelines);
        speeds.CopyTo(old.Speeds);
        knownAnimations[id] = old;

        var body = new StringBuilder(400);
        body.Append($"\"id\":{id},\"entityId\":{entityId},\"source\":\"sampled\","
            + $"\"ready\":{(fields.Ready ? "true" : "false")},\"modelState\":{fields.ModelState},"
            + $"\"animationState\":[{fields.Animation0},{fields.Animation1}],\"baseOverride\":{fields.BaseOverride},"
            + $"\"overallSpeed\":{R(fields.OverallSpeed)},\"mode\":{fields.Mode},\"modeParam\":{fields.ModeParam},"
            + $"\"transformationId\":{fields.TransformationId},\"timelineIds\":[");
        for (var i = 0; i < ids.Length; i++)
        {
            if (i != 0) body.Append(',');
            body.Append(ids[i]);
        }
        body.Append("],\"timelineSpeeds\":[");
        for (var i = 0; i < speeds.Length; i++)
        {
            if (i != 0) body.Append(',');
            body.Append(R(speeds[i]));
        }
        body.Append(']');
        Write("actoranimation", body.ToString());
    }

    private void PollBgmState() => CaptureBgmState(BGMSystem.Instance());

    private void CaptureBgmState(BGMSystem* system)
    {
        var count = system == null ? 0 : system->Scenes.Count;
        // Defensive bound for a native container. Exceeding it is an explicit
        // capture gap, never a claim that omitted scenes have stopped playing.
        if (count < 0 || count > 128)
        {
            Write("capture-gap", "\"channel\":\"bgmstate\",\"reason\":\"scene-count-limit\",\"limit\":128");
            return;
        }
        var context = (system != null, system == null ? 0u : (uint)system->CurrentSituationKind,
            system != null && system->PlayBattleBGM);
        var changed = lastBgmContext != context || knownBgmScenes.Length != count;
        Span<BgmFields> captured = stackalloc BgmFields[count];
        for (var i = 0; i < count; i++)
        {
            ref var scene = ref system->Scenes[i];
            var value = new BgmFields(scene.SceneId, scene.BgmId, scene.PlayingBgmId, scene.PreviousBgmId,
                (uint)scene.SituationKind, (uint)scene.PlayState, scene.EnableCustomFade, scene.FadeOutTime,
                scene.FadeInTime, scene.FadeInStartTime, scene.InitialVolume);
            if (!float.IsFinite(value.InitialVolume))
            {
                Write("capture-gap", "\"channel\":\"bgmstate\",\"reason\":\"nonfinite-initial-volume\"");
                return;
            }
            changed |= i >= knownBgmScenes.Length || knownBgmScenes[i] != value;
            captured[i] = value;
        }
        if (!changed) return;
        // Validate the whole native sample before committing any cache entry.
        // Otherwise a later invalid scene can silently consume earlier changes.
        if (knownBgmScenes.Length != count) knownBgmScenes = new BgmFields[count];
        captured.CopyTo(knownBgmScenes);
        lastBgmContext = context;
        var body = new StringBuilder(300 * count + 128);
        body.Append($"\"source\":\"sampled\",\"available\":{(context.Item1 ? "true" : "false")},"
            + $"\"situation\":{context.Item2},\"battleBgm\":{(context.Item3 ? "true" : "false")},\"scenes\":[");
        for (var i = 0; i < count; i++)
        {
            if (i != 0) body.Append(',');
            var scene = knownBgmScenes[i];
            body.Append($"{{\"scene\":{scene.Scene},\"requested\":{scene.Requested},\"playing\":{scene.Playing},"
                + $"\"previous\":{scene.Previous},\"situation\":{scene.Situation},\"playState\":{scene.PlayState},"
                + $"\"customFade\":{(scene.CustomFade ? "true" : "false")},\"fadeOutMs\":{scene.FadeOut},"
                + $"\"fadeInMs\":{scene.FadeIn},\"fadeInStartMs\":{scene.FadeStart},\"initialVolume\":{R(scene.InitialVolume)}}}");
        }
        body.Append(']');
        Write("bgmstate", body.ToString());
    }

    private void PollLayoutState()
    {
        var world = LayoutWorld.Instance();
        var layout = world == null ? null : world->ActiveLayout;
        var state = layout == null ? new LayoutFields(false, 0, 0, 0, 0, 0, 0)
            : new LayoutFields(true, layout->InitState, layout->TerritoryTypeId, layout->CfcId,
                layout->LayerFilterKey, layout->LevelId, layout->LayerEntryType);
        if (lastLayout == state) return;
        lastLayout = state;
        Write("layoutstate", $"\"source\":\"sampled\",\"coverage\":\"identity-only\","
            + $"\"available\":{(state.Available ? "true" : "false")},\"initState\":{state.InitState},"
            + $"\"territory\":{state.Territory},\"cfc\":{state.Cfc},\"layerFilterKey\":{state.LayerFilterKey},"
            + $"\"level\":{state.Level},\"layerEntryType\":{state.LayerEntryType}");
    }
}
