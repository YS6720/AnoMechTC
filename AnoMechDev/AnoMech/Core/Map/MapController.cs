using AnoMech.Helpers;
using AnoMech.Multiplayer;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace AnoMech.Core.Map;

// Unified entry point for zone loading and map effects. Owned by SimWorld as
// world.Map. Zone and effects state are reset by Reset(); zone hooks are
// released by Dispose().
public sealed class MapController : IDisposable
{
    private readonly MapEffects effects = new();
    private readonly ZoneSession zone = new();

    /// <summary>供 FirewallSelfCheck 讀取 hook 狀態與 Passive 模式的觀測計數（B-001 硬閘）。</summary>
    internal ZoneSession Session => zone;

    // Collider-deactivation state. Zone-load is async (resources stream in over
    // several frames), so each pending drop re-tries DisableSpawnAreaColliders
    // each frame until at least one SharedGroup is found near its center, or it
    // times out. Holds the spawn-ring barrier (armed by TryLoad) plus any arena
    // points a scenario requested via ArmColliderDrops.
    private readonly List<PendingColliderDrop> pendingColliderDrops = new();
    private const int BarrierDropMaxFrames = 300; // ~5s at 60fps
    private const float ColliderDropRadius = 10f; // per-point radius (matches spawn barrier)

    private struct PendingColliderDrop
    {
        public Vector3 Center;
        public float Radius;
        public int FramesLeft;
    }

    // ── Zone ─────────────────────────────────────────────────────────────────

    // True while a scenario was started by loading a client-side zone.
    // Cleared by Unload() and Reset().
    public bool IsInInstance { get; private set; }

    // 目前這個 zone 是用哪個 LayerFilterKey 載的。TryLoad 靠它判斷「要不要重載」——
    // 同 territory 不同 key ＝不同場地，不重載等於改了沒用。
    private uint loadedLayerFilterKey;

    public bool IsZoneLoaded => zone.IsActive;
    public bool IsInInn() => ZoneSession.IsInInn();

    // Load the target territory client-side. Must be called from the Inn.
    public void Load(uint territoryId, Vector3 playerPosition, byte levelSync, ushort itemLevelSync, uint layerFilterKey = 0) => zone.Enter(territoryId, playerPosition, levelSync, itemLevelSync, layerFilterKey);

    // Apply weather after a zone load (1-second delayed to let the engine settle).
    public void ApplyWeather(byte weatherId) => zone.ApplyWeather(weatherId);

    // Host-only sink installed by WorldReplicator. Peers leave it null so
    // applying a received map event cannot echo the event back.
    internal Action<WorldEvent>? NetworkEventSink { get; set; }

    // Immediately change the active weather (mid-scenario). transition = fade seconds.
    public void SetWeather(byte weatherId, float transition = 0.5f)
    {
        zone.SetWeather(weatherId, transition);
        NetworkEventSink?.Invoke(new WeatherEvent(weatherId, transition));
    }

    // Revert to the saved inn territory and restore position.
    public void Unload()
    {
        zone.Revert(false);
        IsInInstance = false;
        pendingColliderDrops.Clear();
    }

    // Per-frame poll. Called from SimWorld.Tick.
    internal void Tick()
    {
        for (int i = pendingColliderDrops.Count - 1; i >= 0; i--)
        {
            var drop = pendingColliderDrops[i];
            var disabled = DirectorFunctions.DisableSpawnAreaColliders(drop.Center, drop.Radius);
            if (disabled > 0) { pendingColliderDrops.RemoveAt(i); continue; }
            drop.FramesLeft--;
            if (drop.FramesLeft <= 0)
            {
                Plugin.Log.Warning($"[BarrierDrop] Gave up after {BarrierDropMaxFrames} frames — no SGs found near ({drop.Center.X:F2},{drop.Center.Y:F2},{drop.Center.Z:F2})");
                pendingColliderDrops.RemoveAt(i);
            }
            else
            {
                pendingColliderDrops[i] = drop;
            }
        }
    }

    // Enter the scenario's target instance if conditions are met.
    // Sets IsInInstance when the zone is already active or the Inn load succeeds.
    // No-op (IsInInstance stays false) when target is null or the player isn't in the Inn.
    public void TryLoad(TargetInstance? target, byte levelSync, ushort itemLevelSync)
    {
        if (zone.IsRestoring)
            throw new InvalidOperationException("Zone restoration must finish before loading or reusing a scenario.");
        if (target == null) return;
        // Fresh load only when no zone is active yet (must be in the Inn). When a
        // zone is already loaded we're switching scenarios within the same
        // territory — skip the reload but still fall through to re-apply weather.
        //
        // ⚠️ 但「已載入就跳過」對 **LayerFilterKey 改變** 是錯的：那是換一套場地，
        // 不重載就完全不會生效。2026-08-19 實測：改了 16 次 key 全部無效，
        // 因為 zone 早就載著、LoadZone 一次都沒再被呼叫（Reset 不卸載 zone）。
        // 條件改成「未載入 **或** 場地 key 變了」，並在追蹤檔留下跳過的理由。
        bool freshLoad = false;
        // 不在這裡自動卸載再重載：恢復須等待原場地／位置／同步讀回，
        // pending／failed仍持有zone與隔離。換key必須先完成正規Leave流程。
        if (IsZoneLoaded && loadedLayerFilterKey != target.LayerFilterKey)
        {
            Core.CrashTrace.Log($"[Map] 拒絕啟動：LayerFilterKey 由 {loadedLayerFilterKey} 改成 {target.LayerFilterKey}，"
                              + "但場地已載入。換場地必須先離開（Leave）回到房間。");
            Core.ChatOutput.Error($"[AnoMech] 場地 key 已改（{loadedLayerFilterKey} → {target.LayerFilterKey}），"
                                    + "請先按 Leave 回房間再 Start。");
            return;
        }
        if (!IsZoneLoaded)
        {
            if (!IsInInn())
            {
                Core.CrashTrace.Log("[Map] 需要重載場地但目前不在私人區域——已中止（請先 Leave 回房間）");
                return;
            }
            Load(target.TerritoryId, target.PlayerPosition, levelSync, itemLevelSync, target.LayerFilterKey);
            loadedLayerFilterKey = target.LayerFilterKey;
            freshLoad = true;
        }
        else
        {
            Core.CrashTrace.Log($"[Map] 沿用已載入的場地（LayerFilterKey={loadedLayerFilterKey}），未呼叫 LoadZone");
        }
        if (target.WeatherId is { } wid)
        {
            if (freshLoad) ApplyWeather(wid);   // fresh load: delay so the engine settles
            else SetWeather(wid);               // restart/switch in a loaded zone: apply now
        }
        IsInInstance = true;
        effects.Loaded = true;
        InstanceContentDirectorHelper.Commence();
        ArmBarrierDrop(target.PlayerPosition, 10f);
    }

    private void ArmBarrierDrop(Vector3 center, float radius)
    {
        pendingColliderDrops.Add(new PendingColliderDrop
        {
            Center = center,
            Radius = radius,
            FramesLeft = BarrierDropMaxFrames,
        });
    }

    // Arm collider drops at scenario-provided arena points (already converted to
    // world coordinates). Same async-load retry as the spawn-ring barrier.
    public void ArmColliderDrops(IEnumerable<Vector3> worldCenters)
    {
        foreach (var center in worldCenters)
            ArmBarrierDrop(center, ColliderDropRadius);
    }

    // ── Map effects ───────────────────────────────────────────────────────────

    // Replay a single MapEffect state change. packetFlags: high16=State, low8=Flags.
    public void AddEffect(uint packetFlags, byte index)
    {
        effects.Apply(packetFlags, index);
        NetworkEventSink?.Invoke(new MapEffectEvent(packetFlags, index));
    }

    // Replay a native DirectorUpdate event (instance progress / state sync) — the
    // server-side InstanceContentDirector message a scenario timeline replays. Thin
    // forwarder so scenarios address it through world.Map alongside AddEffect.
    public void DirectorUpdate(uint category, uint arg1 = 0, uint arg2 = 0, uint arg3 = 0, uint arg4 = 0)
    {
        InstanceContentDirectorHelper.ProcessDirectorUpdate(category, arg1, arg2, arg3, arg4);
        if (NativeCompatGate.ProcessDirectorUpdateVerified)
            NetworkEventSink?.Invoke(new DirectorEvent(category, arg1, arg2, arg3, arg4));
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        effects.Dispose();
        zone.Dispose();
    }
}
