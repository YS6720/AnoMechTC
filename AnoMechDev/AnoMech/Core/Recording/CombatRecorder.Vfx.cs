using System;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace AnoMech.Core.Recording;

internal sealed unsafe partial class CombatRecorder
{
    // ── 特效通道 ──────────────────────────────────────────────────────────

    /// <summary>角色特效：記錄原生 path、端點、參數與本次錄影的 instance id。</summary>
    private FFXIVClientStructs.FFXIV.Client.Graphics.Vfx.VfxData* ActorVfxDetour(
        byte* path, GameObject* caster, GameObject* target, float a4, byte a5, ushort a6, byte a7)
    {
        RecordingWriter? session = null;
        var pathText = string.Empty;
        var sourceId = 0u;
        var targetId = 0u;
        int? causeSeq = null;
        // Capture the session and metadata before Original. The native call must never run
        // while recordingGate is held: hook re-entry can otherwise deadlock the game.
        // 整段包 try：detour 內任何 managed 例外逃進 native ＝ 遊戲當場崩潰
        // （2026-09-12 實證，見 StaticVfxDetour 的註解）。
        try
        {
            lock (recordingGate)
            {
                session = writer;
                causeSeq = CurrentCauseSequence;
                if (session?.IsHealthy == true && path != null)
                {
                    pathText = CStr(path);
                    sourceId = caster != null ? caster->EntityId : 0;
                    targetId = target != null ? target->EntityId : 0;
                }
            }
        }
        catch
        {
            session = null;
            causeSeq = null;
        }

        // The native create is always called exactly once. Tracking happens only after it
        // returns, so a null result cannot leave a phantom lifetime entry.
        var result = actorVfxHook!.Original(path, caster, target, a4, a5, a6, a7);
        try
        {
            lock (recordingGate)
            {
                if (session == null || !ReferenceEquals(writer, session) || !session.IsHealthy
                    || result == null || pathText.Length == 0)
                    return result;
                if (!TryStartVfxLocked(session, RecordedVfxKind.Actor, (nint)result, out var tracked))
                    return result;
                WriteHook("actorVfx", "vfx",
                    $"\"vfxId\":{tracked.Id},\"path\":\"{Escape(pathText)}\","
                  + $"\"src\":{sourceId},\"target\":{targetId},"
                  + $"\"params\":[{R(a4)},{a5},{a6},{a7}]"
                  + CauseSuffix(causeSeq));
            }
        }
        catch { }
        return result;
    }

    /// <summary>場地特效（omen／地面持續 VFX）。建立後由 Update 提供 live transform。</summary>
    private FFXIVClientStructs.FFXIV.Client.Graphics.Scene.VfxObject* StaticVfxDetour(byte* path, byte* pool)
    {
        RecordingWriter? session = null;
        var pathText = string.Empty;
        int? causeSeq = null;
        // 整段包 try：detour 內任何 managed 例外逃進 native ＝ 遊戲當場崩潰。
        // 2026-09-12：CurrentCauseSequence 第一次被讀時觸發 static cctor，cctor 裡的
        // `(nint)(&((Character*)null)->Vfx)` 丟 NullReferenceException（Vfx 偏移 0x1978
        // ≥ 4KB，JIT 對 ldflda 補一道實體 null check），例外自此 detour 一路衝到
        // Framework.HandleFrameworkUpdate ⇒ Dalamud 判定 AnoMech 崩潰、每次載入必炸。
        try
        {
            lock (recordingGate)
            {
                session = writer;
                causeSeq = CurrentCauseSequence;
                if (session?.IsHealthy == true && path != null)
                    pathText = CStr(path);
            }
        }
        catch
        {
            session = null;
            causeSeq = null;
        }

        // The native create is always called exactly once. The cleanup hook is installed from
        // this live result using the already verified VfxObject::CleanupRender vtable entry.
        var result = staticVfxHook!.Original(path, pool);
        if (result != null) EnsureStaticCleanupHook(result);
        try
        {
            lock (recordingGate)
            {
                if (session == null || !ReferenceEquals(writer, session) || !session.IsHealthy
                    || result == null || pathText.Length == 0)
                    return result;
                if (!TryStartVfxLocked(session, RecordedVfxKind.Static, (nint)result, out var tracked))
                    return result;
                WriteHook("staticVfx", "svfx",
                    $"\"vfxId\":{tracked.Id},\"path\":\"{Escape(pathText)}\""
                  + CauseSuffix(causeSeq));
            }
        }
        catch { }
        return result;
    }

    private FFXIVClientStructs.FFXIV.Client.Graphics.Vfx.VfxData* ActorVfxDtorDetour(
        FFXIVClientStructs.FFXIV.Client.Graphics.Vfx.VfxData* thisPtr, byte a2)
    {
        try
        {
            lock (recordingGate)
            {
                var session = writer;
                if (session?.IsHealthy == true
                    && vfxTracker.TryEnd(RecordedVfxKind.Actor, (nint)thisPtr, out var tracked))
                    WriteHook("actorVfxDtor", "vfx-end", $"\"vfxId\":{tracked.Id}");
            }
        }
        catch { }
        return actorVfxDtorHook!.Original(thisPtr, a2);
    }

    private void StaticVfxUpdateDetour(
        FFXIVClientStructs.FFXIV.Client.Graphics.Scene.VfxObject* thisPtr, float deltaSeconds, int unk)
    {
        try { CaptureStaticTransform(thisPtr); }
        catch { }
        staticVfxUpdateHook!.Original(thisPtr, deltaSeconds, unk);
    }

    private void StaticVfxCleanupDetour(
        FFXIVClientStructs.FFXIV.Client.Graphics.Scene.VfxObject* thisPtr)
    {
        try
        {
            lock (recordingGate)
            {
                var session = writer;
                if (session?.IsHealthy == true
                    && vfxTracker.TryEnd(RecordedVfxKind.Static, (nint)thisPtr, out var tracked))
                    WriteHook("staticVfxCleanup", "svfx-end", $"\"vfxId\":{tracked.Id}");
            }
        }
        catch { }
        staticVfxCleanupHook!.Original(thisPtr);
    }

    private unsafe void EnsureStaticCleanupHook(
        FFXIVClientStructs.FFXIV.Client.Graphics.Scene.VfxObject* vfx)
    {
        if (vfx == null || staticVfxCleanupHook != null || cleanupRenderHookAttempted) return;
        cleanupRenderHookAttempted = true;
        try
        {
            var vtbl = *(void***)vfx;
            var address = vtbl == null ? nint.Zero : (nint)vtbl[CleanupRenderVtblIndex];
            if (address == nint.Zero)
            {
                Plugin.Log.Warning("[CombatRecorder] CleanupRender vtable address unavailable; static VFX ends will be absent");
                return;
            }
            staticVfxCleanupHook = Plugin.GameInterop.HookFromAddress<CleanupRenderDelegate>(
                address, StaticVfxCleanupDetour);
            try { staticVfxCleanupHook.Enable(); }
            catch
            {
                staticVfxCleanupHook.Dispose();
                staticVfxCleanupHook = null;
                throw;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[CombatRecorder] CleanupRender hook 掛載失敗，場地 VFX 結束不會被錄到：{ex.Message}");
        }
    }

    private unsafe bool CaptureStaticTransform(
        FFXIVClientStructs.FFXIV.Client.Graphics.Scene.VfxObject* vfx)
    {
        if (vfx == null) return false;
        lock (recordingGate)
        {
            var session = writer;
            if (session?.IsHealthy != true) return false;
            var address = (nint)vfx;
            var transform = new VfxTransform(vfx->Position, vfx->Rotation, vfx->Scale);
            if (!vfxTracker.TryTransform(RecordedVfxKind.Static, address, transform,
                                         out var tracked, out var changed) || !changed)
                return false;
            return WriteHook("staticVfxUpdate", "svfx-transform", $"\"vfxId\":{tracked.Id},"
                 + $"\"x\":{R(transform.Position.X)},\"y\":{R(transform.Position.Y)},"
                 + $"\"z\":{R(transform.Position.Z)},"
                 + $"\"rotation\":[{R(transform.Rotation.X)},{R(transform.Rotation.Y)},"
                 + $"{R(transform.Rotation.Z)},{R(transform.Rotation.W)}],"
                 + $"\"scale\":[{R(transform.Scale.X)},{R(transform.Scale.Y)},{R(transform.Scale.Z)}]") > 0;
        }
    }

    private bool TryStartVfxLocked(RecordingWriter session, RecordedVfxKind kind,
                                   nint address, out TrackedVfx tracked)
    {
        tracked = default;
        if (!ReferenceEquals(writer, session) || !session.IsHealthy) return false;
        if (vfxTracker.TryStart(kind, address, out tracked, out var replaced))
        {
            if (replaced.Id != 0)
                Write("vfx-gap", $"\"reason\":\"address-reused-before-end\",\"kind\":\"{kind}\","
                              + $"\"vfxId\":{replaced.Id},\"replacementVfxId\":{tracked.Id}");
            return true;
        }
        if (!vfxTrackingCappedLogged)
        {
            vfxTrackingCappedLogged = true;
            Write("vfx-gap", $"\"reason\":\"tracking-limit\",\"kind\":\"{kind}\","
                          + $"\"tracked\":{vfxTracker.Count},\"limit\":{VfxCaptureTracker.MaxTrackedEntries}");
            CrashTrace.Log($"[錄影] ⚠ VFX lifetime tracking gap（{kind}, address=0x{address.ToInt64():X}）："
                         + $"tracked={vfxTracker.Count},limit={VfxCaptureTracker.MaxTrackedEntries};"
                         + " start/end are not fabricated");
        }
        return false;
    }

    /// <summary>接線視覺：ActorControl cat 35 之外的第二證據，直接給 client 解析後的
    /// (slot, tetherId, 目標, progress)——連線 id 對 Channeling 表，重播可原樣重建。</summary>
    private void TetherDetour(FFXIVClientStructs.FFXIV.Client.Game.Character.VfxContainer* thisPtr,
                              byte tetherIndex, ushort tetherId, ulong targetId, byte tetherProgress)
    {
        try
        {
            lock (recordingGate)
            {
                var session = writer;
                if (session?.IsHealthy == true && thisPtr != null)
                {
                    // 施法者直接讀 VfxContainer.OwnerObject（Character*，容器內 0x8），
                    // 不用 `container 位址 − offsetof(Character, Vfx)` 反推：那個 offsetof
                    // 常數本身算不出來（見 StaticVfxDetour 註解），而 OwnerObject 是 client
                    // 自己維護的欄位，不隨 Character 版面位移而失效。
                    var owner = thisPtr->OwnerObject;
                    if (owner != null)
                        WriteHook("tether", "tether",
                            $"\"src\":{owner->EntityId},\"slot\":{tetherIndex},"
                          + $"\"tetherId\":{tetherId},\"target\":{targetId},\"progress\":{tetherProgress}");
                }
            }
        }
        catch { }
        tetherHook!.Original(thisPtr, tetherIndex, tetherId, targetId, tetherProgress);
    }
}
