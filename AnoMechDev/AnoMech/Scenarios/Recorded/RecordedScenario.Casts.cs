using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace AnoMech.Scenarios.Recorded;

/// <summary>實戰資料重播——施法排程層（拆自主檔；職責＝把 cast 事件變成場上的出手）。</summary>
public partial class RecordedScenario
{
    /// <summary>
    /// 子類別完全接手這一筆施法（含演出）。回 true＝基底不再排任何東西。
    ///
    /// 為什麼是「全包」而不是「補一段」：DSR 的特化分支（迴旋／塔鏈／武神槍引導／
    /// P4 珠子）在**演出本身**就不一樣（提前現身、引導期持續轉向、動態落點），
    /// 拆成「基底演出＋子類結算」會讓那些行為無處可放。
    /// </summary>
    protected virtual bool TryScheduleSpecialCast(RecordedTimeline.Event e) => false;

    // ── 施法 ──────────────────────────────────────────────────────────────
    // 詠唱長度直接用錄影值。先前手寫 3 秒，真值是 4.7 / 7.3 / 4.2 / 2.2——
    // 使用者一眼就看出「讀條沒那麼快」。
    protected virtual void ScheduleCasts()
    {
        foreach (var e in timeline!.Of("cast"))
        {
            var t = At(e.T);

            // 完全發生在開場前的施法直接略過。錄影的 pre 視窗會夾到上一階段的尾刀
            // （2026-08-20 實測：P2→P3 轉場的 25539 乙太爆發、圓 r50 全域斬，
            // 排到 t=-19 被引擎在第一個 tick 補發 ⇒「一進場全滅」）。
            if (t + e.CastSeconds < 0f)
            {
                Core.CrashTrace.Log($"[場景] 略過開場前的施法 {e.Action}（t={t:F1}）");
                continue;
            }

            if (TryScheduleSpecialCast(e)) continue;
            if (IsV2) ScheduleInstanceCast(e, t);
            else ScheduleLegacyCast(e, t);
        }
    }

    /// <summary>
    /// v1 路徑（<c>Schema &lt; 2</c>）：anchor 只排演出、不做 generic 結算；
    /// 其餘演員生一隻隱形 helper 站在錄到的座標上放招。**與 2026-09-02 之前逐行相同**
    /// ——dsr-*.json 是 P3 反覆調校過的資料，這條路徑一個字都不該變（AC6）。
    /// </summary>
    private static Vector3? RecordedPoint(float[] point)
        => point.Length >= 4
            ? new Vector3(point[0], point[1], point[2])
            : point.Length >= 2 ? new Vector3(point[0], 0f, point[1]) : null;

    private static float? RecordedRotation(float[] point)
        => point.Length >= 4 ? point[3] : point.Length > 2 ? point[2] : null;

    private void ScheduleLegacyCast(RecordedTimeline.Event e, float t)
    {
        var action = e.Action; var cast = e.CastSeconds;
        if (e.BaseId == timeline!.AnchorBase)
        {
            var anchorBase = timeline.AnchorBase;
            world.Events.Add(t, () =>
            {
                if (actors.TryGetValue(anchorBase, out var b) && b != null)
                    b.Cast(action, castSeconds: cast, targetId: b.GameObjectId);
            });
            return;
        }

        // helper：**生在錄到的座標上**。塔與直線 AOE 的位置就是施法者站的地方；
        // 全部生在場中心的話，玩家永遠不知道該去哪，AOE 也全部重疊。
        var baseId = e.BaseId;
        foreach (var p in e.Positions)
        {
            var pos = RecordedPoint(p);
            if (pos is not { } hp) continue;
            var rot = RecordedRotation(p) ?? 0f;
            SimEnemy? helper = null;
            world.Events.Add(t - 0.3f, () => helper = world.SpawnEnemy(new EnemySpawnConfig(
                BNpcBaseId: baseId, NameId: HelperNameId, Level: HelperLevel,
                Targetable: false, EnemyList: EnemyListMode.Never, IsVisible: false,
                Placement: new Placement(hp, rot))));
            world.Events.Add(t, () => helper?.Cast(action, castSeconds: cast, targetId: helper?.GameObjectId));
            RequireEndpoint(t + cast);
            if (!IsRaidwide(action) && !IsNoResolve(action) && !IsSelfCast(action))
                world.Events.Add(t + cast, () => ResolvePlayerLethal(helper, action));
            world.Events.Add(t + cast + 1f, () => helper?.Despawn());
        }
    }

    /// <summary>
    /// v2 路徑：**由錄到的那隻演員實例出手**，帶朝向／目標／範圍提示提前量。
    ///
    /// 由來（2026-09-02 稽核）：v1 把每一招都交給一隻臨時隱形 helper，於是
    /// ① 施法者朝向恆 0（南）⇒ 所有扇形／直線一律朝南
    /// ② 坦克點名沒有目標 ⇒ 看不出打誰
    /// ③ 範圍提示出現時刻靠猜（omenDelay 有錄到但沒人消費）。
    /// 三件事在畫面上都「看起來正常」，只有打過那個副本的人才知道不對。
    ///
    /// **同刻多份逐點各自出手**（`srcs` 與 `pos` 一一對齊）：三座塔同刻讀條就要有三座。
    /// 2026-09-02 驗收實測 FRU P2 有 17 筆多點事件，先前只有第一份會出現——
    /// 而少掉的那幾份在畫面上只是「這次沒有那個機制」，看不出資料掉了。
    /// </summary>
    private void ScheduleInstanceCast(RecordedTimeline.Event e, float t)
    {
        // 沒有任何座標時仍排一份（由 src 那隻出手）——boss 的自身型招沒有落點座標。
        var points = Math.Max(e.Positions.Count, 1);
        for (var i = 0; i < points; i++)
        {
            var p = i < e.Positions.Count ? e.Positions[i] : null;
            var pos = p is not null ? RecordedPoint(p) : null;
            // 逐點朝向優先（多方向直線每份朝向不同），沒有才退回事件層的 rot。
            var rot = p is not null ? RecordedRotation(p) ?? e.Rot : e.Rot;
            // srcs 缺席（早期 v2）時只有第 0 份對得到實例，其餘一律臨時 helper——
            // 那仍比「後面幾份整個不出現」好：位置是錄到的，玩家看得到該閃哪裡。
            var src = i < e.Srcs.Count ? e.Srcs[i] : (i == 0 ? e.Src : null);
            // 擊退只由第一份套用：kb／hits 是**事件層**欄位（一發擊退），
            // 逐點各推一次會把玩家連推三次——多點事件（塔／多方向直線）一定會踩到。
            var effect = i < e.Effects.Count ? e.Effects[i] : null;
            ScheduleOnePointCast(e, t, src is > 0 ? src : null, pos, rot,
                knockback: i == 0, effect);
        }
    }

    /// <summary>單一份施法：找實例 → 找不到就生臨時隱形 helper → 出手 → 結算 → 收。</summary>
    private void ScheduleOnePointCast(RecordedTimeline.Event e, float t, int? src, Vector3? pos,
                                      float? rot, bool knockback, RecordedTimeline.NativeEffect? effect)
    {
        var action = e.Action; var cast = e.CastSeconds; var omen = e.Omen;
        var native = effect is { IsComplete: true } ? effect : null;
        var hitAt = effect?.Hit is { } pointHit ? At(pointHit)
            : e.Hit is { } h ? At(h) : t + cast;
        SimEnemy? caster = null;
        SimEnemy? temp = null;

        world.Events.Add(t, () =>
        {
            caster = src is { } s ? instances.GetValueOrDefault(s) : null;
            if (caster == null)
            {
                // 退回 v1 的臨時 helper：位置用錄到的施法座標（沒有座標就整份放棄，
                // 生在場中心比不生更糟——玩家會往錯的地方閃）。
                if (pos is not { } hp) return;
                caster = temp = world.SpawnEnemy(new EnemySpawnConfig(
                    BNpcBaseId: e.BaseId, NameId: HelperNameId, Level: HelperLevel,
                    Targetable: false, EnemyList: EnemyListMode.Never, IsVisible: false,
                    Placement: new Placement(hp, rot ?? 0f)));
                if (caster == null) return;
            }
            if (rot is { } r) caster.SetRotation(r);
            var here = caster.Position;
            // targetLocation 只在「招式落點 ≠ 施法者腳下」時傳：傳了才會讓引擎把
            // 圓形／落地型 AOE 畫在落點；同點還傳只是多一次座標換算。
            Vector3? loc = pos is { } lp
                && Vector2.Distance(new(lp.X, lp.Z), new(here.X, here.Z)) > 0.5f ? lp : null;
            if (native != null)
            {
                if (cast > 0f)
                    caster.BeginRecordedCast(action, (ActionType)native.ActionType!.Value, cast,
                        omen, rot, loc,
                        native.CastTarget.ValueKind == JsonValueKind.String
                            && native.CastTarget.GetString() == "self"
                            ? caster.GameObjectId : ResolveRecordedEndpoint(native.CastTarget)?.GameObjectId);
            }
            else
                caster.Cast(action, targetLocation: loc, castSeconds: cast,
                    targetId: effect is { CastTarget.ValueKind: JsonValueKind.Object }
                        ? ResolveRecordedEndpoint(effect.CastTarget)?.GameObjectId
                        : ResolveTargetId(e, caster), omenDelay: omen);
        });

        if (native != null)
            world.Events.Add(hitAt, () =>
            {
                if (caster == null) return;
                Span<GameObjectId> targets = stackalloc GameObjectId[native.Targets.Count];
                var count = 0;
                foreach (var endpoint in native.Targets)
                    if (ResolveRecordedEndpoint(endpoint) is { } target)
                        targets[count++] = target.GameObjectId;
                Vector3? landing = native.HasPosition == true
                    ? new Vector3(native.X!.Value, native.Y!.Value, native.Z!.Value) : null;
                caster.NativeActionEffect(action, native.AnimationLock!.Value, native.SpellId!.Value,
                    native.AnimationVariation!.Value, (ActionType)native.ActionType!.Value,
                    native.Flags!.Value, targets[..count], native.Rotation, landing,
                    ResolveRecordedEndpoint(native.AnimationTarget)?.GameObjectId,
                    ResolveRecordedEndpoint(native.BallistaTarget)?.GameObjectId);
            });

        // 兩種演出路徑共用幾何結算；完整 metadata 只由已錄到的 hit 時刻釋放一次。
        world.Events.Add(hitAt, () => ResolveRecordedCast(e, caster, hitAt, knockback));
        RequireEndpoint(hitAt);
        world.Events.Add(hitAt + 1f, () => temp?.Despawn());
    }

    /// <summary>這筆事件的施法／落點座標（第一份）。沒有座標＝資料沒錄到。</summary>
    protected static Vector3? FirstPosition(RecordedTimeline.Event e)
        => e.Positions.Count > 0 ? RecordedPoint(e.Positions[0]) : null;

    protected GameObjectId? ResolveTargetId(RecordedTimeline.Event e, SimEnemy? caster)
    {
        if (TargetRole(e) is { } role && party.Get(role) is { } member)
            return member.GameObjectId;
        if (e.Target.ValueKind == JsonValueKind.Object
            && e.Target.TryGetProperty("actor", out var a) && a.TryGetInt32(out var id)
            && instances.GetValueOrDefault(id) is { } actor)
            return actor.GameObjectId;
        return caster?.GameObjectId;
    }

    /// <summary>這一招指名的隊伍槽位（沒有＝不是點名，或指的是別的 NPC）。</summary>
    protected static PartyRole? TargetRole(RecordedTimeline.Event e)
        => e.Target.ValueKind == JsonValueKind.Object
           && e.Target.TryGetProperty("role", out var r) && r.TryGetInt32(out var k)
           && k is >= 0 and < 8
            ? (PartyRole)k
            : null;
}
