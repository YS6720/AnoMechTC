using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.SimObjects;

namespace AnoMech.Scenarios.Recorded;

/// <summary>
/// 播放一份從真副本錄影轉出來的場景（<see cref="RecordedTimeline"/>）——**通用層**。
///
/// 分工：**資料說「什麼、什麼時候、幾份」，程式只決定「給誰」**。
/// 時刻、詠唱長度、debuff 持續、頭標 id、塔的座標全部來自錄影，一個都不手寫。
///
/// 「給誰」為什麼還是要程式決定：錄影裡的目標是當時那 8 個真人，
/// 換到模擬環境要重新分配到 MT/ST/H1/H2/D1–D4。
///
/// 上游 TOP 的做法是逐事件手寫腳本（400–600 行/場景），忠實度高但數字得人工抄；
/// 這裡改成資料驅動，代價是少了 TOP 那些手工演出細節，換來的是「數字永遠等於實戰」。
///
/// 由來（2026-09-02，P-098）：本類別是從 <c>DsrRecordedScenario</c>（908 行 partial）
/// 抽出來的通用部分。抽的動機不是行數——是 FRU／M8S 一直在繼承一個叫「Dsr」的類別，
/// 而那個類別裡有一半是 DSR 專屬分支（迴旋／塔鏈／武神槍）。DSR 特化改由四個覆寫點接入：
/// <see cref="TryScheduleSpecialCast"/>／<see cref="TryResolveSpecial"/>／
/// <see cref="IsHandledStatus"/>／<see cref="IsHandledEObj"/>。
///
/// **v1 資料行為不變是硬約束**：實例演員、方向、generic 幾何結算一律只在
/// <c>Schema &gt;= 2</c> 啟用（AC6）。dsr-*.json 是 P3 反覆調校過的，行為變了沒有任何測試會紅。
/// </summary>
public partial class RecordedScenario : IScenario
{
    /// <summary>這個場景播的是哪一份 Data JSON。註冊端靠它去重，snapshot 也用它核對身分。</summary>
    public string DataFile { get; }

    private readonly float leadIn;

    public string Name { get; }
    public IPhase Phase { get; }
    public bool SupportsSolo => true;

    public IReadOnlyList<IScenarioAi> AiStrats { get; }

    protected RecordedTimeline? timeline;
    protected SimWorld world = null!;
    protected SimParty party = null!;
    protected DamageSolver damage = null!;

    /// <summary>每個 BNpcBase 的**第一隻**。v1 只有這一層；v2 仍維持，DSR 的 anchor 查法靠它。</summary>
    protected readonly Dictionary<uint, SimEnemy?> actors = new();

    /// <summary>v2 的每實例演員（key＝資料的 actor id）。同一個 base 可能同時有好幾隻。</summary>
    protected readonly Dictionary<int, SimEnemy?> instances = new();

    /// <param name="leadIn">錄影上的秒數要減掉多少才是場景時間（讓玩家有時間就位）。</param>
    protected RecordedScenario(string name, IPhase phase, string dataFile, float leadIn,
                               IReadOnlyList<IScenarioAi> aiStrats)
    {
        Name = name;
        Phase = phase;
        DataFile = dataFile;
        this.leadIn = leadIn;
        AiStrats = aiStrats;
    }

    protected float At(float recorded) => recorded - leadIn;

    /// <summary>v2 才啟用的路徑（實例演員／方向／generic 結算）。v1 一律走舊路徑，AC6。</summary>
    protected bool IsV2 => timeline is { Schema: >= 2 };

    public void Run(SimWorld worldParam, int? selectedAi)
        => RunWith(worldParam, selectedAi, RecordedTimeline.Load(DataFile));

    /// <summary>
    /// 用**已核准的** snapshot 跑（多人 prepare→commit 路徑）。不重開檔——
    /// prepare 核准一份 hash、commit 再讀一次的話，中間被轉換器重新輸出的新資料
    /// 會以舊 run 身分執行，而資源集合不變、ResourceMismatch 攔不到（BLIND-ASTRA-05）。
    ///
    /// snapshot 不是本場景那一份就直接丟例外＝fail-closed：呼叫端必須在任何
    /// native／世界副作用之前就失敗，不能退化成「安靜跑一份別的資料」。
    /// </summary>
    public void RunPrepared(SimWorld worldParam, int? selectedAi, RecordedTimelineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!string.Equals(snapshot.DataFile, DataFile, StringComparison.Ordinal))
            throw new ArgumentException(
                $"prepared snapshot 對應 {snapshot.DataFile}，本場景要的是 {DataFile}", nameof(snapshot));
        if (snapshot.Timeline is null)
            throw new ArgumentException("prepared snapshot 沒有 timeline", nameof(snapshot));
        RunWith(worldParam, selectedAi, snapshot.Timeline);
    }

    private void RunWith(SimWorld worldParam, int? selectedAi, RecordedTimeline? loaded)
    {
        world = worldParam;
        party = worldParam.Party;
        actors.Clear();
        instances.Clear();
        warnedActorLifetime = false;
        scheduledEndpoint = 0f;
        damage = new DamageSolver(party);

        timeline = loaded;
        if (timeline == null)
        {
            // fail-closed：沒有資料就沒有機制。安靜地跑一個空場景比報錯更糟——
            // 使用者會以為「機制壞了」而不是「資料沒載到」。
            Core.ChatOutput.Error($"[AnoMech] 場景資料 {DataFile} 載入失敗，本次不會有任何機制。");
            return;
        }

        // fail-closed 哨兵：anchor（boss）必須在資料裡讀得出 BNpcBase。
        // 2026-08-20 實踩：JSON 欄位對映錯（actor 的 "baseId" 被讀進 EObj 欄），
        // boss 生成 BNpcBase=0 的空殼、anchor 查不到 ⇒ boss 招整條沒排，
        // 而場上塔照跑——「BOSS都消失了」的假正常狀態，比直接報錯更糟。
        if (!timeline.Of("actor").Any(a => a.BaseId == timeline.AnchorBase))
        {
            Core.ChatOutput.Error($"[AnoMech] 場景資料 {DataFile} 讀不到 anchor {timeline.AnchorBase} 的演員，拒跑。");
            return;
        }
        if (!PreflightActorCapacity())
            return;

        // 缺能力只提示不擋：FRU 8/28 是唯一 P1→P5 全程錄影，拒播等於沒場景可練。
        // 但**一定要說出來**——不說的話「這個副本的瞬發招全部不存在」在畫面上零訊號。
        if (timeline.MissingCaps.Count > 0)
            Core.ChatOutput.Coach($"[AnoMech] 此份資料缺 {string.Join("／", timeline.MissingCaps)}"
                                + "（錄影機版本早於 2026-08-31），重錄可補");

        // 資料有完整移動軌跡（通關 log）→ AI 整段重播通關隊的走位，站位表策略不啟用。
        // 由來（2026-08-20）：4 張靜態站位表之間 AI 不會動，迴旋的進出、去塔、引導
        // 全是連續跑位——照靜態表站的人「很快就死」（維護者 實測）。
        if (!ScheduleMoves())
            RunAi(selectedAi);

        SpawnActors();
        ScheduleActorStates();
        ScheduleNpcMoves();
        SchedulePartyEvents();
        ScheduleCasts();
        ScheduleScene();
        ScheduleVisualEvidence();
        ScheduleDirectorSequence();
        ScheduleOutcome();
    }

    /// <summary>沒有移動軌跡時的後備策略（站位表 AI）。子類別自帶策略型別，基底不知道那個型別。</summary>
    protected virtual void RunAi(int? selectedAi) { }

    // ── 場景序列覆寫點（維持既有簽名，DSR P3 已在用）──────────────────────

    /// <summary>
    /// 階段進場的 DirectorUpdate 序列（實驗性，2026-08-20）：實機錄影中每次 P3 進場都有
    /// **逐條相同**的 director 指令串（attempt 1 與 5 完全一致）——這是場景視覺
    /// （P1 地板 → P3 地板）最有資料根據的候選機制。子類別覆寫提供序列；
    /// 空＝不重播。防火牆恆開，指令只影響本地 client 狀態。
    /// </summary>
    protected virtual (float t, uint cmd, uint a1, uint a2)[] DirectorSequence => [];

    /// <summary>
    /// 階段進場的 MapEffect 序列（2026-08-20 從錄影確證）：實機五次進 P3、每次都在
    /// 進場點 +5s 內出現 **index=8, state=1, flags=2**——這才是 P1→P3 地板變換的載體
    /// （director 序列重播已實測無效）。通道＝world.Map.AddEffect（僅本地）。
    ///
    /// 與資料裡的 <c>mapeffect</c> 事件**並存**：這裡是手寫的進場序列，
    /// 那邊是錄影原樣重播，兩者管的不是同一段。
    /// </summary>
    protected virtual (float t, ushort state, ushort flags, byte index)[] MapEffectSequence => [];

    /// <summary>階段天氣（null＝不動，改用資料裡的 weather 事件）。</summary>
    protected virtual byte? WeatherOverride => null;

    /// <summary>
    /// 演員出生點覆寫（null＝照錄影值）。FFLogs 的 spawn 座標是**剛出生那一刻**的位置，
    /// 有些演員出生後會先位移再開打（P4 雙眼：生在 (±2,0)、機制全在 (±10,0) 讀）——
    /// v1 沒有位移軌跡，照 spawn 座標擺會整場站錯（維護者 2026-08-21：「哪有這麼近」）。
    /// v2 有 npcmove 軌跡，通常不需要這個覆寫。
    /// </summary>
    protected virtual Vector3? ActorSpawnOverride(uint baseId) => null;

    private void ScheduleDirectorSequence()
    {
        if (WeatherOverride is { } wid)
            world.Events.Add(0.3f, () => world.Map.SetWeather(wid));
        foreach (var (t, cmd, a1, a2) in DirectorSequence)
            world.Events.Add(t, () =>
            {
                Core.CrashTrace.Log($"[場景] DirectorUpdate 0x{cmd:X8}({a1},{a2})");
                world.Map.DirectorUpdate(cmd, a1, a2);
            });
        foreach (var (t, state, flags, index) in MapEffectSequence)
            world.Events.Add(t, () =>
            {
                Core.CrashTrace.Log($"[場景] MapEffect idx={index} state=0x{state:X} flags=0x{flags:X}");
                world.Map.AddEffect(((uint)state << 16) | flags, index);
            });
    }

    // ── 演員 ──────────────────────────────────────────────────────────────
    // 資料裡出現過的每個演員都生。錨點（boss）可見可選取，其餘是隱形施法者
    // ——真副本就是這樣分工的（實測：12605 放 boss 招、12606 放塔與直線 AOE）。

    /// <summary>隱形 helper 的名字／等級。看不見所以純屬形式，沿用 v1 既有值免得動到既有場景。</summary>
    protected virtual uint HelperNameId => 3458;    // BNpcName 尼德霍格
    protected virtual byte HelperLevel => 90;

    private void SpawnActors()
    {
        if (IsV2) { SpawnActorInstances(); return; }

        // v1：每個 BNpcBase 只生一隻（資料裡本來就只有第一隻的出生點）。
        // 錄到的每一隻都生，包含完全不施法的遠景物——只生「會施法的」會讓場上少一整批東西。
        foreach (var e in timeline!.Of("actor"))
        {
            var id = e.BaseId;
            var isBoss = id == timeline.AnchorBase;
            var targetable = e.Targetable ?? isBoss;
            var pos = ActorSpawnOverride(id) ?? new Vector3(e.X, e.Y, e.Z);
            var nameId = e.NameId != 0 ? e.NameId : HelperNameId;
            var lv = e.Level != 0 ? e.Level : HelperLevel;
            actors[id] = null;
            world.Events.Add(At(e.T) < 0f ? 0f : At(e.T), () => actors[id] = world.SpawnEnemy(new EnemySpawnConfig(
                BNpcBaseId: id, NameId: nameId, Level: lv,
                Targetable: targetable,
                EnemyList: targetable ? EnemyListMode.Always : EnemyListMode.Never,
                IsVisible: e.Visible ?? true, ModelCharaId: e.ModelCharaId ?? 0,
                Scale: e.Scale ?? 0f, HitboxRadius: e.HitboxRadius ?? 0f,
                MainHandModel: e.WeaponMain, OffHandModel: e.WeaponOff,
                Placement: new Placement(pos, e.Rot ?? (isBoss ? MathF.PI : 0f)))));
        }
    }

    /// <summary>
    /// 同時存活的演員上限。
    ///
    /// 由來（2026-09-02 整合驗證）：引擎的 CharacterManager 只有 **100 格**，扣掉 8 個隊員
    /// 之後真正能給敵人的不到 92；而缺 `until` 的資料會把整個視窗出現過的演員全堆在場上
    /// （m8s-p2.json 實測 345 隻）。64 是「留得住合法場景、擋得住失控資料」的分界——
    /// DSR P3 實測同時存活 51 隻仍要能完整跑，所以不能訂得比那更低。
    /// </summary>
    private const int MaxConcurrentActors = 64;

    /// <summary>場上還活著的實例數。Despawn 過的 SimEnemy 會自己變成 IsActive=false。</summary>
    private int LiveActorCount() => instances.Values.Count(v => v is { IsActive: true });

    /// <summary>只講一次：使用者需要知道「畫面上少東西」是資料的問題，不是機制沒觸發。</summary>
    private bool warnedActorLifetime;

    private void WarnActorLifetimeIncomplete()
    {
        if (warnedActorLifetime) return;
        warnedActorLifetime = true;
        Core.ChatOutput.Coach("[AnoMech] 此份資料的演員生滅資訊不完整，部分敵人不會出現（見 trace）");
    }

    /// <summary>
    /// v2：**每個實例各生一隻**。v1 每個 BNpcBase 只留第一隻，於是 M8S 的一群小怪
    /// 只會出現一隻、boss 二次進場沒有第二尊（2026-09-02 稽核：NPC despawn 錄到 281 筆，
    /// 重播端一筆都沒用）。`until` 到時 Despawn——不收的話場上會愈積愈多。
    /// </summary>
    private void SpawnActorInstances()
    {
        foreach (var e in timeline!.Of("actor"))
        {
            var key = e.Id;
            if (instances.ContainsKey(key)) continue;
            var baseId = e.BaseId;
            var isBoss = baseId == timeline.AnchorBase;
            // targetable 有錄到就照錄影（分得出真身與隱形 helper）；沒錄到退回「只有 anchor 可選」。
            var targetable = e.Targetable ?? isBoss;
            var pos = ActorSpawnOverride(baseId) ?? new Vector3(e.X, e.Y, e.Z);
            var rot = e.Rot ?? (isBoss ? MathF.PI : 0f);
            var nameId = e.NameId != 0 ? e.NameId : HelperNameId;
            var lv = e.Level != 0 ? e.Level : HelperLevel;
            var isFirstOfBase = !actors.ContainsKey(baseId);
            instances[key] = null;
            if (isFirstOfBase) actors[baseId] = null;
            var at = At(e.T) < 0f ? 0f : At(e.T);
            world.Events.Add(at, () =>
            {
                // fail-closed 上限：`until` 缺席時沒有人會把演員收掉，於是「錄影裡出現過的
                // 每一隻」會同時堆在場上。2026-09-02 整合驗證實測 m8s-p2.json 有 345 隻
                // 沒有 until（那份錄影錄的時候還沒有 despawn 通道）⇒ 直接撐爆 CharacterManager。
                // 超過就不生：少幾隻敵人是看得出來的殘缺，記憶體踩爛是整個遊戲當掉。
                if (LiveActorCount() >= MaxConcurrentActors)
                {
                    Core.CrashTrace.Log($"[場景] 同時存活演員已達上限 {MaxConcurrentActors}，"
                                      + $"略過實例 {key}（base {baseId}）——資料缺 until？");
                    WarnActorLifetimeIncomplete();
                    return;
                }
                var spawned = world.SpawnEnemy(new EnemySpawnConfig(
                    BNpcBaseId: baseId, NameId: nameId, Level: lv,
                    Targetable: targetable,
                    EnemyList: targetable ? EnemyListMode.Always : EnemyListMode.Never,
                    IsVisible: e.Visible ?? true, ModelCharaId: e.ModelCharaId ?? 0,
                    Scale: e.Scale ?? 0f, HitboxRadius: e.HitboxRadius ?? 0f,
                    MainHandModel: e.WeaponMain, OffHandModel: e.WeaponOff,
                    Placement: new Placement(pos, rot)));
                instances[key] = spawned;
                if (isFirstOfBase) actors[baseId] = spawned;
            });
            if (e.Until is { } until && At(until) > at)
                world.Events.Add(At(until), () => instances.GetValueOrDefault(key)?.Despawn());
        }
    }

    private void ScheduleActorStates()
    {
        if (!IsV2) return;
        foreach (var e in timeline!.Of("actorstate"))
        {
            if (e.Id <= 0) continue;
            var at = At(e.T);
            if (at < 0f) continue;
            world.Events.Add(at, () =>
            {
                var actor = instances.GetValueOrDefault(e.Id);
                if (actor == null) return;
                if (e.Targetable is { } targetable) actor.SetTargetable(targetable);
                if (e.Visible is { } visible) actor.SetVisible(visible);
            });
        }
    }


    /// <summary>
    /// v2：NPC 位移軌跡重播（npcmove 事件，pos＝[[t,x,y,z,rot]…]）。
    ///
    /// 由來（2026-09-02 稽核）：boss 17823 整場移動 24m、M8S 小怪移動 150m，而 v1 生一隻
    /// 站著不動 ⇒ 「boss 站錯地方」是所有幾何判定的共同誤差源，而且畫面上只看得到
    /// 「boss 沒動」、看不出「所以扇形也錯了」。
    ///
    /// 一段取樣區間一筆指令（<see cref="RecordedNpcTrack"/>，純邏輯、離線可驗）：連續段交給
    /// <c>MoveRecorded</c>，那條路徑每個 tick 按錄影區間的 elapsed/duration 一起內插 XYZ
    /// 並準時抵達下一個取樣點；3D 速度超過 <see cref="RecordedNpcTrack.TeleportSpeed"/>
    /// 的段落仍歸類為瞬移，照舊用 SetPosition。
    /// </summary>
    private void ScheduleNpcMoves()
    {
        if (!IsV2) return;
        foreach (var e in timeline!.Of("npcmove"))
        {
            var key = e.Id;
            foreach (var segment in RecordedNpcTrack.Build(e.Positions))
            {
                var at = At(segment.Time);
                if (at < 0f) continue;
                var target = segment.To;
                var rot = segment.Rotation;
                if (segment.Teleport)
                    world.Events.Add(at, () =>
                    {
                        var actor = instances.GetValueOrDefault(key);
                        if (actor == null) return;
                        actor.SetPosition(new Placement(target, rot ?? actor.Rotation));
                    });
                else
                {
                    var duration = segment.Duration;
                    world.Events.Add(at, () =>
                        instances.GetValueOrDefault(key)?.MoveRecorded(target, duration, rot));
                }
            }
            // 完成證據的終點之一＝軌跡最後一格取樣（最後一段還在走的時候不算跑完）。
            if (e.Positions.Count > 0 && e.Positions[^1] is { Length: >= 1 } last)
                RequireEndpoint(At(last[0]));
        }
    }

    /// <summary>
    /// 重播錄到的全隊移動軌跡（move 事件：每 ~2s 一格、role 排序的 8 點）。
    /// 回傳 false＝資料裡沒有軌跡（舊資料），呼叫端退回站位表策略。
    /// </summary>
    private bool ScheduleMoves()
    {
        var moves = timeline!.Of("move").OrderBy(e => e.T).ToList();
        if (moves.Count == 0) return false;
        recordedTrack = moves;
        var ai = new AiManager(world);
        // TOP 式一步到位（維護者 2026-08-20：「能像正常玩家一樣走路嗎？原本 TOP 那種不能?」）。
        // 前兩版（0.5s waypoint 鏈、0.1s 純追蹤）都在重播「原始雜訊軌跡」——
        // 站定期的取樣抖動被當成移動指令，AI 原地抖。
        // 改成離線壓縮：把軌跡切成「停留點」（同一位置待 ≥1s＝一個定位），
        // AI 只做「時刻到 → 直線走去下一個定位 → 準時抵達 → 站定不動」，
        // 定位座標與到達時刻仍完全來自通關軌跡，只是走法變成正常玩家。
        var totalHolds = 0;
        for (int k = 0; k < 8; k++)
        {
            var samples = new List<(float t, Vector2 p)>();
            foreach (var m in moves)
                if (m.Positions.Count == 8)
                {
                    var point = m.Positions[k];
                    if (point.Length < 2) continue;
                    samples.Add((At(m.T), new Vector2(point[0],
                        point.Length >= 3 ? point[2] : point[1])));
                }
            if (samples.Count == 0) continue;

            var holds = new List<(float start, float end, Vector2 c)>();
            int i = 0;
            while (i < samples.Count)
            {
                int j = i; var anchor = samples[i].p;
                while (j + 1 < samples.Count && Vector2.Distance(samples[j + 1].p, anchor) < 0.9f) j++;
                if (samples[j].t - samples[i].t >= 1.0f)
                {
                    float cx = 0, cz = 0;
                    for (int q = i; q <= j; q++) { cx += samples[q].p.X; cz += samples[q].p.Y; }
                    var n = j - i + 1;
                    holds.Add((samples[i].t, samples[j].t, new Vector2(cx / n, cz / n)));
                    i = j + 1;
                }
                else i++;
            }

            // 混合驅動（離線審計全過的組合）：
            //   停留段＝一次指令到定位點中心，期間不再下令（零抖動，TOP 式站定）；
            //   轉移段＝0.1s 追蹤原始軌跡的 0.3s 前方點（閃避路徑忠實、連續奔跑不停頓）。
            // 純停留點＋準時到位驗過會壓死線（機制在半路結算），純追蹤驗過會原地抖——
            // 兩者各取對的一半。
            var slot = k;
            var prevEnd = samples[0].t;
            foreach (var h in holds)
            {
                for (var tt = prevEnd; tt < h.start - 0.05f; tt += 0.1f)
                {
                    var now = tt;
                    Vector2? reference = null;
                    ai.Move(now, () =>
                    {
                        var coords = new Vector2?[8];
                        reference = SampleTrack(now + 0.3f, slot);
                        if (party.Get((Core.Game.Party.PartyRole)slot) is { } member && member.IsAlive()
                            && reference is { } target
                            && Vector2.Distance(new(member.Position.X, member.Position.Z), target) >= 0.4f)
                            coords[slot] = target;
                        return AiMove.Create(coords).NaturalOrder();
                    }, jitter: 0f, practiceReference: () =>
                    {
                        var original = new Vector2?[8];
                        original[slot] = reference;
                        return original;
                    });
                }
                var hc = h.c;
                ai.Move(h.start, () =>
                {
                    var coords = new Vector2?[8];
                    coords[slot] = hc;
                    return AiMove.Create(coords).NaturalOrder();
                }, jitter: 0f);
                prevEnd = h.end;
                totalHolds++;
            }
        }
        Core.CrashTrace.Log($"[場景] 重播通關隊移動軌跡：{moves.Count} 格 → {totalHolds} 個定位點（停留＋追蹤混合）");

        // 練習不斷頭：AI 倒地 4 秒內自動扶起（玩家不在此列——玩家死亡是練習回饋）。
        // 沒有這個，一名 AI 被誤差判死＝塔／分攤連鎖失敗，後半段機制形同消失。
        var end = At(moves[^1].T) + 15f;
        for (var t = 2f; t < end; t += 3f)
            world.Events.Add(t, ReviveDeadAi);
        return true;
    }

    private List<RecordedTimeline.Event> recordedTrack = new();

    /// <summary>軌跡在場景時間 t、slot k 的線性內插位置。</summary>
    protected Vector2? SampleTrack(float t, int k)
    {
        if (recordedTrack.Count == 0) return null;
        RecordedTimeline.Event? prev = null, next = null;
        foreach (var m in recordedTrack)
        {
            if (At(m.T) <= t) prev = m;
            else { next = m; break; }
        }
        prev ??= next; next ??= prev;
        if (prev == null || next == null || prev.Positions.Count != 8 || next.Positions.Count != 8) return null;
        var t0 = At(prev.T); var t1 = At(next.T);
        var f = t1 <= t0 ? 0f : Math.Clamp((t - t0) / (t1 - t0), 0f, 1f);
        var a = prev.Positions[k]; var b = next.Positions[k];
        var az = a.Length >= 3 ? a[2] : a[1];
        var bz = b.Length >= 3 ? b[2] : b[1];
        return new Vector2(a[0] + (b[0] - a[0]) * f, az + (bz - az) * f);
    }

    /// <summary>completion 與已排事件終點之間允許的誤差（converter 的時間是兩位小數）。</summary>
    private const float CompletionTolerance = 0.05f;

    /// <summary>本次已排事件的最後實際終點（場景時間）：招式落下、軌跡最後一格、特效收掉。</summary>
    private float scheduledEndpoint;

    /// <summary>登記一個已排事件的實際終點。</summary>
    private void RequireEndpoint(float at)
    {
        if (at > scheduledEndpoint) scheduledEndpoint = at;
    }

    private void ScheduleOutcome()
    {
        if (timeline is not
            {
                Complete: true,
                Partial: false,
                DiagnosticOnly: false,
                Completion: { Complete: true } completion
            })
            return;
        var at = At(completion.Time);
        if (at < 0f) return;
        // 封存完整 ≠ 模擬跑完。completion 必須涵蓋**已排事件的實際終點**——落點判定、
        // 軌跡最後一格、已排特效的生命週期。早於終點就計成功的話，自動重試會在判定前
        // 把世界清掉，於是截斷的錄影（最後一發還在讀條）可以一直連勝（BLIND-ASTRA-02）。
        // 缺終點證據就維持「不完成」，不靠猜測延長時間補造一個完成。
        if (at + CompletionTolerance < scheduledEndpoint)
        {
            Core.CrashTrace.Log($"[場景] 不排完成：completion t={completion.Time:F2}"
                              + $"（場景時間 {at:F2}）早於已排事件終點 {scheduledEndpoint:F2}");
            return;
        }
        world.Events.Add(at, world.CompleteScenario);
    }

    private void ReviveDeadAi()
    {
        foreach (var m in party.AllMembers())
            if (m is Core.SimObjects.SimPartyNpc { Dead: true } npc)
            {
                npc.Revive();
                Core.ChatOutput.Coach($"[AnoMech] 隊友重新站起，繼續練習（{npc.DisplayName}）");
            }
    }
}
