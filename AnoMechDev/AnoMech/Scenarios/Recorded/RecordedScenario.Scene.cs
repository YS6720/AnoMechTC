using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;

namespace AnoMech.Scenarios.Recorded;

/// <summary>
/// 實戰資料重播——場面層（拆自主檔；職責＝把「非傷害」的錄影事件放回場上：
/// 頭標／狀態／接線／EventObject／場地特效／天氣／台詞／標點）。
/// </summary>
public partial class RecordedScenario
{
    /// <summary>這個 status 由子類別自己排（DSR 的波次 debuff 與跳型）。基底看到就跳過。</summary>
    protected virtual bool IsHandledStatus(ushort status) => false;

    /// <summary>這個 EObj 由子類別自己生（DSR 的塔走 SpawnTower，有踩塔判定）。</summary>
    protected virtual bool IsHandledEObj(uint baseId) => false;

    // ── 頭標與狀態 ────────────────────────────────────────────────────────

    protected virtual void SchedulePartyEvents()
    {
        ScheduleHeadmarkers();
        ScheduleStatuses();
    }

    /// <summary>
    /// 這個頭標落在哪些 role。預設＝錄影原樣（**不隨機**：跑位是跟著被點的頭標走的，
    /// 分配一亂全亂，維護者 2026-08-20）。子類別可加自己的波次推導當後備。
    /// </summary>
    protected virtual PartyRole[] MarkerRoles(RecordedTimeline.Event e)
        => e.Roles.Select(r => (PartyRole)r).ToArray();

    protected void ScheduleHeadmarkers()
    {
        foreach (var e in timeline!.Of("headmarker"))
        {
            var roles = MarkerRoles(e);
            if (roles.Length == 0) continue;
            var marker = e.Marker;
            world.Events.Add(At(e.T), () =>
            {
                foreach (var role in roles)
                    party.Get(role)?.AttachLockonVfx(marker, persistent: false);
            });
        }
    }

    /// <summary>
    /// debuff／buff。v2 多三件事：**層數**（v1 全被壓成 1，錄影裡層數 &gt;1 的有 811 筆）、
    /// **提前解除**（`status-`，錄影 8047 筆，v1 只靠 dur 到期 ⇒ 被淨化的 debuff 不會消）、
    /// **NPC 身上的狀態**（錄影 527 筆，v1 只留玩家的 ⇒ boss 階段 buff／易傷不重現）。
    /// </summary>
    protected void ScheduleStatuses()
    {
        void QueueApply(RecordedTimeline.Event e)
        {
            if (IsHandledStatus(e.Status)) return;
            var st = e.Status;
            var dur = e.Duration;
            var param = e.StatusParam ?? e.Stacks;

            if (IsV2 && e.Roles.Count == 0 && e.Id != 0)
            {
                // NPC 身上：掛在那隻演員實例，不是隊伍槽位。
                var key = e.Id;
                world.Events.Add(At(e.T), () =>
                    instances.GetValueOrDefault(key)?.AddStatusParam(st, param, dur));
                return;
            }

            // 通用點名（P4 的邪龍之爪／牙交換鏈等）：只要資料帶 roles 就照原樣發。
            if (e.Roles.Count == 0) return;
            var roles = e.Roles.Select(r => (PartyRole)r).ToArray();
            // 錄影的 stacks 是語意層數、param 是絕對原始值；v2 優先使用
            // raw Param，且 0 對 marker/status 仍是合法值，不能被 AddStatus 當成移除。
            world.Events.Add(At(e.T), () =>
            {
                foreach (var role in roles)
                    if (IsV2) party.Get(role)?.AddStatusParam(st, param, dur);
                    else party.Get(role)?.AddStatus(st, dur);
            });
        }

        void QueueRemove(RecordedTimeline.Event e)
        {
            if (IsHandledStatus(e.Status)) return;
            var st = e.Status;
            if (e.Roles.Count == 0 && e.Id != 0)
            {
                var key = e.Id;
                world.Events.Add(At(e.T), () => instances.GetValueOrDefault(key)?.RemoveStatus(st));
                return;
            }
            var roles = e.Roles.Select(r => (PartyRole)r).ToArray();
            world.Events.Add(At(e.T), () =>
            {
                foreach (var role in roles)
                    party.Get(role)?.RemoveStatus(st);
            });
        }

        if (!IsV2)
        {
            // Keep schema-1 scheduling exactly as before; it never consumed status-.
            foreach (var e in timeline!.Of("status"))
                QueueApply(e);
            return;
        }

        // Source changes are encoded as remove(old source) + add(new source).
        // At an identical timestamp removal must be queued first, otherwise the
        // game status manager can overwrite the new source with the old removal.
        foreach (var e in timeline!.Events
                     .Where(e => e.Kind is "status" or "status-")
                     .OrderBy(e => e.T)
                     .ThenBy(e => e.Kind == "status-" ? 0 : 1))
        {
            if (e.Kind == "status-") QueueRemove(e);
            else QueueApply(e);
        }
    }

    // ── 場面 ──────────────────────────────────────────────────────────────

    protected virtual void ScheduleScene()
    {
        ScheduleEventObjects();
        // 以下通道 v1 資料一筆都沒有（轉換器當時就沒輸出），但仍顯式擋掉——
        // 「剛好沒資料所以沒差」不是守衛，哪天有人補一份混合檔就靜默改行為了。
        if (!IsV2) return;
        ScheduleTethers();
        ScheduleMapEffects();
        ScheduleWeather();
        ScheduleChat();
        ScheduleWaymarksFromData();
    }

    /// <summary>
    /// EObj（塔／地板機關／場地物件）。v1 只有子類別認得的那些（DSR 塔）會生，
    /// 其餘一律略過——那是 2026-09-02 之前的行為，v1 檔不得改（FRU 26 筆／M8S 21 筆
    /// EObj 在 v2 重轉之後才會出現）。
    /// </summary>
    private void ScheduleEventObjects()
    {
        if (!IsV2) return;
        foreach (var e in timeline!.Of("eobj"))
        {
            if (IsHandledEObj(e.EObjId)) continue;
            var cfg = new EventObjectSpawnConfig
            {
                EObjId = e.EObjId,
                Placement = new Placement(new Vector3(e.X, e.Y, e.Z), e.Rot ?? 0f),
                TargetableStatus = e.TargetableStatus,
                VisibilityFlag = e.Vis,
                EventState = e.EventState,
                Radius = e.Radius,
                LayoutId = e.LayoutId,
                EventId = e.EventId,
                GimmickId = e.GimmickId,
                TimelineState = e.State,
                SpawnVisible = true,
            };
            SimEventObject? eo = null;
            world.Events.Add(At(e.T), () => eo = world.SpawnEventObject(cfg));
            if (e.Until is { } until)
                world.Events.Add(At(until), () => eo?.Despawn());
        }
    }

    /// <summary>
    /// 接線。**資料源是 ActorControl cat 35／36**，不是 tether VFX hook——那支
    /// hook 掛得上卻從未觸發（三份副本錄影皆 0，見 CombatRecorder 的自檢）。
    /// </summary>
    private void ScheduleTethers()
    {
        foreach (var e in timeline!.Of("tether"))
        {
            var id = e.TetherId;
            var dur = e.Until is { } u ? u - e.T : 10f;
            world.Events.Add(At(e.T), () =>
            {
                var from = e.Source.ValueKind == JsonValueKind.Undefined
                    ? instances.GetValueOrDefault(e.Src)
                    : ResolveRecordedEndpoint(e.Source);
                if (from == null) return;
                var to = ResolveRecordedEndpoint(e.Target);
                if (to == null) return;
                world.Tether(from, to, id, dur);
            });
        }
    }

    private SimCharacter? ResolveRecordedEndpoint(JsonElement endpoint)
    {
        if (endpoint.ValueKind != JsonValueKind.Object) return null;
        if (endpoint.TryGetProperty("role", out var role) && role.TryGetInt32(out var slot)
            && slot is >= 0 and < 8)
            return party.Get((PartyRole)slot);
        if (endpoint.TryGetProperty("actor", out var actor) && actor.TryGetInt32(out var id)
            && instances.GetValueOrDefault(id) is { IsActive: true } instance)
            return instance;
        return null;
    }

    /// <summary>場地變化（換階段地板等）。與 <see cref="MapEffectSequence"/> 並存：
    /// 那邊是手寫的進場序列，這邊是錄影原樣。</summary>
    private void ScheduleMapEffects()
    {
        foreach (var e in timeline!.Of("mapeffect"))
        {
            var state = e.State; var flags = e.Flags; var index = e.Index;
            world.Events.Add(At(e.T), () => world.Map.AddEffect(((uint)state << 16) | flags, index));
        }
    }

    /// <summary>副本天氣由 director 下發、不在 WeatherRate 表——只有執行期錄得到。</summary>
    private void ScheduleWeather()
    {
        foreach (var e in timeline!.Of("weather"))
        {
            var id = e.WeatherId;
            world.Events.Add(At(e.T), () => world.Map.SetWeather(id));
        }
    }

    /// <summary>boss 台詞常是機制預告（「聚集吧——」）；復刻演出要有。</summary>
    private void ScheduleChat()
    {
        foreach (var e in timeline!.Of("chat"))
        {
            var who = e.Who; var text = e.Text;
            world.Events.Add(At(e.T), () => Core.ChatOutput.Npc(who, text));
        }
    }

    /// <summary>錄影當下的場地標點。攻略敘述（「A 塔」「1 標分攤」）要對得上就得有它。</summary>
    private void ScheduleWaymarksFromData()
    {
        var marks = new List<Waymark>();
        foreach (var w in timeline!.Waymarks)
            if (w.Length >= 3 && w[0] is >= 0 and < 8)
                marks.Add(new Waymark((WaymarkSlot)(int)w[0], new Vector3(w[1], 0f, w[2])));
        if (marks.Count == 0) return;
        world.Events.Add(0.2f, () => world.PlaceWaymarks(marks));
    }
}
