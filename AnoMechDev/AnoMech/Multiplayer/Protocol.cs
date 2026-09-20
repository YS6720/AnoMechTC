using System;
using System.Numerics;
using System.Text.Json.Serialization;
using AnoMech.Core;
using AnoMech.Core.Game.Party;

namespace AnoMech.Multiplayer;

// TC-specific protocol. All identities are supplied by the relay connection,
// never accepted from the application payload. No compatibility fallback.
public static class MpLimits
{
    public const int ProtocolVersion = 10;
    public const string ProtocolName = "anomech-tc";
    public const int Members = 8;
    public const int Rooms = 4;
    public const int RunsPerRoom = 1024;
    public const int FrameBytes = 1024 * 1024;
    // 送出／接收佇列深度也由 liveness 推導，理由與下面 RelayIngressBurst 完全相同：
    // 固定 128／256 時，房主送 ~140/s（PoseHz＋SnapshotHz），成員主執行緒只要卡 0.9～1.8 秒
    // 佇列就滿——成員自己以 QueueOverflow 斷線、relay 也把送不出去的成員踢掉——
    // 而 liveness 明明宣稱 12 秒沒訊息才算死。2026-09-16 實機三次「有人被踢」都落在
    // 機制事件爆量的瞬間（事件會讓狀態合併失效），與這個 1.8 秒的窗完全吻合。
    // 深度＝liveness 內會累積的訊息量；超過 liveness 心跳逾時本來就會斷，再大沒有意義。
    public const int SendQueue = (int)(LivenessSeconds * (PoseHz + SnapshotHz));
    public const int ReceiveQueue = SendQueue;
    // 收訊排空改成時間預算而不是「每幀固定 N 筆」：房主 alt-tab 時遊戲 FPS 掉到 15～30，
    // 固定筆數的排空能力跟著腰斬，訊息堆積 → 成員看到延遲、嚴重就 QueueOverflow。
    // 每幀最多花 DrainBudgetMilliseconds 排空，DrainPerTickMax 只是防止單幀失控的硬上限。
    public const double DrainBudgetMilliseconds = 3;
    public const int DrainPerTickMax = 512;
    public const int MessagesPerSenderSecond = 256;
    // 突發額度＝relay 願意容忍的靜默時間 × 單向送出頻率。這個數字**不可以自己挑**：
    // 固定 512 時實測 5 秒收包停頓會把七個 peer 全部踢掉（512÷120≈4.3 秒就耗盡），
    // 但 liveness 同時宣稱更久沒訊息才算死——兩個數字互相矛盾，
    // 而使用者只看到「被踢出去」。由 LivenessSeconds 推導後兩者同源：relay 容忍的停頓，
    // 補送時就不會被自己的限流殺掉。再往上調沒有意義——超過 liveness 心跳逾時本來就會斷。
    // 持續速率仍是 MessagesPerSenderSecond（256/s），防洪主防線不受影響。
    public const int RelayIngressBurst = (int)(LivenessSeconds * PoseHz);
    public const int SnapshotHz = 20;
    // 60 Hz：遠端顯示固定落後 50 ms 插值，60 Hz 每個窗內仍有 3 筆取樣，畫面與 120 Hz 無異；
    // 而 relay／cloudflared 的訊息量直接砍半。另外姿勢只在**有變化**時送（見
    // MultiplayerSession.AfterGameTick），靜止時降到 SnapshotHz 的 keepalive（2026-09-17）。
    public const int PoseHz = 60;
    // 動畫 id **不設線路上限**。2026-09-19 事故：原本猜 9999，實表是 25001 列、最大 row id
    // 25000（乙太變移、跳擊這類位移技就在 9999 以上），於是那些技能一按，整房被自己的驗證
    // 判為無效訊息而斷線。唯一守得住又不會過期的閘是**套用前查 ActionTimeline 表**
    //（SimNetworkPuppet.IsKnownTimeline）——不存在的 id 永遠進不了原生呼叫，
    // 而且會隨遊戲版本自動跟上。ushort 本身就是 0–65535 的邊界。
    public const int AliasCharacters = 64;
    public const int Enemies = 64;
    public const int EventObjects = 40;
    public const int Tethers = 128;
    public const int StaticVisuals = 128;
    public const int Markers = 17;
    public const int Statuses = 60;
    public const int Streak = 100_000;
    public const float RetryDisplaySeconds = 600f;
    public const float CoordinateLimit = 4096f;
    public const int DetailLength = 64;
    public const double HeartbeatSeconds = 2;
    // 心跳每 2 秒一次，所以這是「連續漏幾次才判死」。8 秒（漏 4 次）對實際網路抖動太緊，
    // 維護者 回報最痛的是房主自己被踢、整房要重開重連；房主送 ~140/s（120 role/self ＋
    // 20 world）比 peer 更快燒完突發額度，中彈機率更高。12 秒＝漏 6 次。
    // 代價是真的斷線時全房要 12 秒才發現，不是零成本；再往上調那個延遲會蓋過好處。
    public const double LivenessSeconds = 12;
    public const double PrepareSeconds = 15;
    public const double IoTimeoutSeconds = 10;
}

/// <summary>
/// Transport-level health for the UI: none of this is authoritative for game state.
/// RttMilliseconds is heartbeat round-trip through the relay including local queue wait
/// (so a stalled send loop shows up here too); negative means not measured yet.
/// </summary>
public readonly record struct RelayTransportStats(
    double RttMilliseconds, int SendQueueDepth, int ReceiveQueueDepth, long SkippedLatestState);

public enum MpError
{
    None, InvalidEndpoint, Authentication, ProtocolMismatch, InvalidMessage,
    Capacity, QueueOverflow, RateLimit, Timeout, TransportFailure,
    HostDisconnected, PeerDisconnected, Disposed, BuildMismatch,
    SceneMismatch, ResourceMismatch, UnsupportedResource, RoleOccupied,
    RoleRequired, Busy, PrepareFailed, NativeFailure, Cancelled,
}

public readonly record struct MpVector(float X, float Y, float Z)
{
    public static MpVector From(Vector3 value) => new(value.X, value.Y, value.Z);
    public Vector3 ToVector() => new(X, Y, Z);
}

public readonly record struct MpQuaternion(float X, float Y, float Z, float W)
{
    public static MpQuaternion From(Quaternion value) => new(value.X, value.Y, value.Z, value.W);
    public Quaternion ToQuaternion() => new(X, Y, Z, W);
}

public readonly record struct MpPose(MpVector Position, float Rotation);
public readonly record struct RunScope(Guid RoomId, Guid RunId, long Generation);
public sealed record LobbyMember(Guid PeerId, string Alias, PartyRole? Role, string BuildFingerprint,
    MpAppearance? Appearance = null);

// 真人外觀（原版）。**一次性**：跟著 hello／lobby 走，不進 run 迴圈，也不得塞進
// RolesSnapshotMessage——那是 20 Hz 週期送的，常數級資料放進去會變成持續流量。
// 整包約 62 bytes（customize 21 ＋ 裝備 20 ＋ 雙手 16 ＋ 兩個 float 8）。
// Customize 欄位順序＝PlayerAppearance 的常數，不可重排。
public sealed record MpAppearance(
    byte[] Customize, ulong[] Equipment, ulong MainHand, ulong OffHand, float Height, float VfxScale)
{
    public const int EquipmentSlots = 5;
}
// Host-side only (never on the wire): which member's CheckRun/PrepareRun answer ended a start.
public sealed record MemberRejection(string Alias, PartyRole? Role, MpError Error, string? Detail);
public sealed record RunDescriptor(
    string SceneKey, string ProgressKey, string SceneFingerprint, string ResourceFingerprint,
    int AiIndex, int WaymarkIndex, float EventTimeScale, bool GodMode);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(LobbyMessage), "lobby")]
[JsonDerivedType(typeof(ClaimRoleMessage), "claim")]
[JsonDerivedType(typeof(RejectedMessage), "rejected")]
[JsonDerivedType(typeof(CheckRunMessage), "check")]
[JsonDerivedType(typeof(CheckedRunMessage), "checked")]
[JsonDerivedType(typeof(PrepareRunMessage), "prepare")]
[JsonDerivedType(typeof(PreparedRunMessage), "prepared")]
[JsonDerivedType(typeof(CommitRunMessage), "commit")]
[JsonDerivedType(typeof(EndRunMessage), "end")]
[JsonDerivedType(typeof(PauseMessage), "pause")]
[JsonDerivedType(typeof(ControlRequestMessage), "request")]
[JsonDerivedType(typeof(PartyMarkerRequestMessage), "party-marker")]
[JsonDerivedType(typeof(SelfPoseMessage), "pose")]
[JsonDerivedType(typeof(AbilityUseMessage), "ability")]
[JsonDerivedType(typeof(WorldSnapshotMessage), "world")]
[JsonDerivedType(typeof(RolesSnapshotMessage), "roles")]
[JsonDerivedType(typeof(PoseSnapshotMessage), "poses")]
[JsonDerivedType(typeof(WorldEventMessage), "event")]
[JsonDerivedType(typeof(RunStatusMessage), "run-status")]
public abstract record MpMessage;

public interface IHostMessage;
public interface IPeerMessage;
public interface IRunMessage;
public interface ILatestState;

public sealed record HelloMessage(string Alias, string BuildFingerprint, MpAppearance? Appearance = null)
    : MpMessage, IPeerMessage;
public sealed record LobbyMessage(LobbyMember[] Members) : MpMessage, IHostMessage;
public sealed record ClaimRoleMessage(PartyRole Role) : MpMessage, IPeerMessage;
public sealed record RejectedMessage(Guid RecipientId, MpError Error) : MpMessage, IHostMessage;
public sealed record CheckRunMessage(RunDescriptor Descriptor) : MpMessage, IHostMessage, IRunMessage;
public sealed record CheckedRunMessage(MpError Error, string? Detail = null) : MpMessage, IPeerMessage, IRunMessage;
public sealed record PrepareRunMessage : MpMessage, IHostMessage, IRunMessage;
public sealed record PreparedRunMessage(MpError Error) : MpMessage, IPeerMessage, IRunMessage;
public sealed record CommitRunMessage : MpMessage, IHostMessage, IRunMessage;
public sealed record EndRunMessage(bool ReturnToInn, MpError Reason) : MpMessage, IHostMessage, IRunMessage;
public sealed record PauseMessage(bool Paused) : MpMessage, IHostMessage, IRunMessage;
public enum MpControl { Reset, Leave, GiveInvulnerability }
public sealed record ControlRequestMessage(MpControl Control) : MpMessage, IPeerMessage, IRunMessage;
// RequestId: per-member monotonic counter. The host echoes the last applied id per member in
// every world snapshot (PartyMarkerAcks); a member ignores a snapshot's marker table until it
// covers its own latest request, so a pre-mark snapshot can never wipe a fresh local mark.
public sealed record PartyMarkerRequestMessage(Sign Sign, PartyRole? Role, long RequestId = 0) : MpMessage, IPeerMessage, IRunMessage;
public sealed record PartyMarkerAck(Guid PeerId, long RequestId);
// Timeline＝擁有者**當下實際播放**的 base ActionTimeline（slot 0）。先前替身只會用
// 位移猜「跑／站」兩種，走路、跳躍、衝刺、情感動作一律變成跑步或滑行
//（維護者 2026-09-18：「移動 走路 跳躍 都很卡，沒有正常的動畫」）。
// 改成直接搬擁有者的動畫 id，替身播什麼由來源端決定。
public sealed record SelfPoseMessage(MpPose Pose, bool IsMoving, bool IsActing, ushort Timeline = 0)
    : MpMessage, IPeerMessage, IRunMessage, ILatestState;
public sealed record AbilityUseMessage(uint ActionId, byte ClassJob, byte Level, MpEntity? Target,
    MpVector? Location = null, long LimitBreakRequestId = 0, bool CancelLimitBreak = false)
    : MpMessage, IPeerMessage, IRunMessage;

// The relay stamps RoomId, SenderId and IsHost. Sequence is monotonically
// increasing per sending socket, including lobby/run lifecycle barriers.
public sealed record ClientPacket(int Version, long Sequence, Guid RunId, MpMessage Message);
public sealed record ReceivedPacket(Guid RoomId, Guid SenderId, bool IsHost, long Sequence, Guid RunId, MpMessage Message);
