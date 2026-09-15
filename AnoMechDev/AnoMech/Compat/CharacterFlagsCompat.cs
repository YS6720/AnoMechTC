// 台服 FFXIVClientStructs 0.0.6966 相容層：Character 的 bitfield 屬性在該版是 get-only，
// 上游的 `chara->IsHostile = false` 這類指派編譯不過，必須直寫底層欄位。
//
// 位元遮罩**不是猜的**——由 `_scratch/il-probe/` 讀 getter 的 IL 抽出，
// 事實表在 docs/upstream/TC-CS-BITFIELDS.md。台服升版後要重跑探針重驗。
//
// 集中在這一個檔而不是散在呼叫端：這些位元一旦 CS 版本變動就要一起改，散開會漏掉。

using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace AnoMech.Compat;

internal static unsafe class CharacterFlags
{
    // 遮罩來源：台服 CS 0.0.6966 的 getter IL（見 TC-CS-BITFIELDS.md）
    private const byte HostileBit = 0x01;   // CharacterData.Flags
    private const byte InCombatBit = 0x02;  // CharacterData.Flags
    private const byte PartyMemberBit = 0x01;   // RelationFlags
    private const byte AllianceMemberBit = 0x02;   // RelationFlags
    private const byte FriendBit = 0x04;   // RelationFlags
    private const byte OffhandDrawnBit = 0x01;   // WeaponFlags
    private const byte WeaponDrawnBit = 0x40;   // Timeline.Flags3

    public static void SetHostile(BattleChara* c, bool on)
        => Set(ref c->Character.CharacterData.Flags, HostileBit, on);

    public static void SetInCombat(BattleChara* c, bool on)
        => Set(ref c->Character.CharacterData.Flags, InCombatBit, on);

    public static void SetPartyMember(BattleChara* c, bool on)
        => Set(ref c->Character.RelationFlags, PartyMemberBit, on);

    public static void SetAllianceMember(BattleChara* c, bool on)
        => Set(ref c->Character.RelationFlags, AllianceMemberBit, on);

    public static void SetFriend(BattleChara* c, bool on)
        => Set(ref c->Character.RelationFlags, FriendBit, on);

    public static void SetOffhandDrawn(BattleChara* c, bool on)
        => Set(ref c->Character.WeaponFlags, OffhandDrawnBit, on);

    /// <summary>上游走 <c>Timeline.IsWeaponDrawn</c>，該成員在本版的 TimelineContainer 上不存在。</summary>
    public static void SetWeaponDrawn(BattleChara* c, bool on)
        => Set(ref c->Character.Timeline.Flags3, WeaponDrawnBit, on);

    private static void Set(ref byte field, byte mask, bool on)
        => field = on ? (byte)(field | mask) : (byte)(field & ~mask);
}
