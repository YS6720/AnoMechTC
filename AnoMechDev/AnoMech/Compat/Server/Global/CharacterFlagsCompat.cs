// 國際服 FFXIVClientStructs 的 Character bitfield 屬性可寫，直接用具名成員
// （上游 fork 點 v0.3.12.0 SimEnemy.cs:187-188 同寫法）。台服版直寫位元，見 Compat/Server/Tc。

using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace AnoMech.Compat;

internal static unsafe class CharacterFlags
{
    public static void SetHostile(BattleChara* c, bool on) => c->Character.IsHostile = on;

    public static void SetInCombat(BattleChara* c, bool on) => c->Character.InCombat = on;

    public static void SetPartyMember(BattleChara* c, bool on) => c->Character.IsPartyMember = on;

    public static void SetAllianceMember(BattleChara* c, bool on) => c->Character.IsAllianceMember = on;

    public static void SetFriend(BattleChara* c, bool on) => c->Character.IsFriend = on;

    public static void SetOffhandDrawn(BattleChara* c, bool on) => c->Character.IsOffhandDrawn = on;

    public static void SetWeaponDrawn(BattleChara* c, bool on) => c->Character.Timeline.IsWeaponDrawn = on;
}
