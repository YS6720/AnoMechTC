using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace AnoMech.Core.Native.Interfaces;

// 移植自上游 anomek/AnoMech 1646a70 Core/Native/Interfaces/ActionEffectData.cs。上游另有
// ActionEffectData（給它的 BattleCharaProxy 用）；我們的 ActionEffect 由 SimCast 直接組，不搬。
// 擊退條目（0x1F）也不搬：位移一律走我們的 Movement，條目會讓本機玩家客戶端再滑一次。

// A target's index in the ActionEffect's target list is what its EffectResult names. At most 8 effects.
public readonly record struct ActionEffectTarget(GameObjectId Id, IReadOnlyList<ActionEffectEntry>? Effects = null);

// One effect entry as the server sends it.
public readonly record struct ActionEffectEntry(byte Type, byte Param0, byte Param1, byte Param2, byte Param3, byte Param4, ushort Value)
{
    private const byte DodgeType = 0x01;
    private const byte DamageType = 0x03;
    private const byte StatusGainType = 0x0E;
    private const byte ActionMarkerType = 0x1B;
    private const byte UnaspectedElement = 7;
    // Param4 bit: Param3 carries bits 16-23 of the amount.
    private const byte LargeValueFlag = 0x40;
    // Param4 bit: the effect applies to the source.
    private const byte AtSourceFlag = 0x80;

    // AttackType as in the Action sheet: 1-4 physical, 5 magic, 6 darkness; 0 is typeless and sends
    // no element either, which is a push's zero hit.
    public static ActionEffectEntry Damage(uint amount, byte attackType) => new(
        DamageType, 0,
        attackType == 0 ? (byte)0 : (byte)(UnaspectedElement << 4 | (attackType & 0x0F)),
        0, (byte)(amount >> 16), amount > ushort.MaxValue ? LargeValueFlag : (byte)0, (ushort)amount);

    public static ActionEffectEntry Dodge() => new(DodgeType, 0, 0, 0, 0, 0, 0);

    // The gain flytext; the status list puts it on the character. Retail's Param0 is the low byte
    // of the status's strength (10 for Battle Litany's 10%); whether the client reads it is
    // UNVERIFIED.
    public static ActionEffectEntry StatusGain(ushort statusId, byte param0 = 0) => new(StatusGainType, param0, 0, 0, 0, 0, statusId);

    // Retail ends every target's list of an enemy action with it. On the local player's own action it
    // sets ActionManager.Combo, as a combo starter or, with comboContinuation, the next step.
    public static ActionEffectEntry ActionMarker(uint actionId, bool comboContinuation = false)
        => new(ActionMarkerType, comboContinuation ? (byte)1 : (byte)0, 0, 0, 0, AtSourceFlag, (ushort)actionId);
}
