using System;
using System.Collections.Generic;

using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace AnoMech.Core.Combat.Jobs;

// Paladin's proc chain is an explicit legacy convention, not a generic
// recorded-ability rule. SimStatus owns the positive 30s lifetime and the
// source-qualified replacement/removal, keeping the old gain-before-remove
// ordering without letting a peer or an independent input hook mutate the
// player.
//
// 原本原生槽寫的是 -30（負值＝永久／不顯示倒數的慣例）。維護者 2026-09-15 實機回報
// 「能力技按完之後都沒秒數」，診斷版證實原生槽的值確實是固定不動的：騎士 proc 在
// 遊戲本體是有 30 秒倒數的，寫負值等於把它藏掉。改成不覆寫——SimStatus.Tick 每幀把
// 遞減中的 RemainingTime 寫進原生槽，倒數就跟著跑。狀態的存活仍由 SimStatus 決定，
// 不是交給引擎：它在 RemainingTime 歸零時 Despawn，與寫入值一致。
internal static class Paladin
{
    private const uint FightOrFlight = 20;
    private const uint RoyalAuthority = 3539;
    private const uint GoringBlade = 3538;
    private const uint Atonement = 16460;
    private const uint Supplication = 36918;
    private const uint Sepulchre = 36919;
    private const uint HolySpirit = 7384;
    private const uint HolyCircle = 16458;
    private const uint Requiescat = 7383;
    private const uint Imperator = 36921;
    private const uint Confiteor = 16459;
    private const uint BladeOfFaith = 25748;
    private const uint BladeOfTruth = 25749;
    private const uint BladeOfValor = 25750;

    private const ushort DivineMight = 2673;        // 神聖魔法效果提高
    private const ushort AtonementReady = 1902;     // 贖罪劍預備
    private const ushort SupplicationReady = 3827;  // 祈告劍預備
    private const ushort SepulchreReady = 3828;     // 葬送劍預備
    private const ushort RequiescatStatus = 1368;   // 安魂祈禱
    private const ushort ConfiteorReady = 3019;     // 悔罪預備
    private const ushort GoringBladeReady = 3847;   // 瀝血劍預備（戰逃反應給）

    internal static bool IsKnownAction(uint actionId)
        => actionId is FightOrFlight or RoyalAuthority or GoringBlade or Atonement or
            Supplication or Sepulchre or HolySpirit or HolyCircle or Requiescat or
            Imperator or Confiteor or BladeOfFaith or BladeOfTruth or BladeOfValor;


    internal static bool IsOwnedTransition(uint actionId, ushort statusId)
    {
        foreach (var owned in TouchedStatuses(actionId, comboOk: true))
            if (owned == statusId) return true;
        return false;
    }

    internal static bool AppliesStatus(uint actionId, bool comboOk)
        => actionId switch
        {
            FightOrFlight or GoringBlade or Atonement or Supplication or Sepulchre or
                HolySpirit or HolyCircle or Requiescat or Imperator or Confiteor or
                BladeOfFaith or BladeOfTruth or BladeOfValor => true,
            RoyalAuthority => comboOk,
            _ => false,
        };

    internal static IReadOnlyList<ushort> TouchedStatuses(uint actionId, bool comboOk)
        => actionId switch
        {
            FightOrFlight or GoringBlade => [GoringBladeReady],
            RoyalAuthority when comboOk => [DivineMight, AtonementReady],
            Atonement => [SupplicationReady, AtonementReady],
            Supplication => [SepulchreReady, SupplicationReady],
            Sepulchre => [SepulchreReady],
            HolySpirit or HolyCircle => [DivineMight],
            Requiescat or Imperator => [RequiescatStatus, ConfiteorReady],
            Confiteor or BladeOfFaith or BladeOfTruth or BladeOfValor => [RequiescatStatus, ConfiteorReady],
            _ => Array.Empty<ushort>(),
        };

    internal static void Apply(uint actionId, bool comboOk, SimCharacter player, PartyRole sourceRole)
    {
        if (!AppliesStatus(actionId, comboOk)) return;
        var sourceObject = player.GameObjectId;
        switch (actionId)
        {
            case FightOrFlight:
                Add(player, GoringBladeReady, 0, sourceRole, sourceObject);
                break;
            case GoringBlade:
                Remove(player, GoringBladeReady, sourceRole);
                break;
            case RoyalAuthority when comboOk:
                Add(player, DivineMight, 0, sourceRole, sourceObject);
                Add(player, AtonementReady, 0, sourceRole, sourceObject);
                break;
            // Native RemoveStatus clears lazily. Keep the proven order: insert the
            // replacement first, then remove the old proc.
            case Atonement:
                if (Add(player, SupplicationReady, 0, sourceRole, sourceObject))
                    Remove(player, AtonementReady, sourceRole);
                break;
            case Supplication:
                if (Add(player, SepulchreReady, 0, sourceRole, sourceObject))
                    Remove(player, SupplicationReady, sourceRole);
                break;
            case Sepulchre:
                Remove(player, SepulchreReady, sourceRole);
                break;
            case HolySpirit or HolyCircle:
                Remove(player, DivineMight, sourceRole);
                break;
            case Requiescat or Imperator:
                Add(player, RequiescatStatus, 4, sourceRole, sourceObject);
                Add(player, ConfiteorReady, 0, sourceRole, sourceObject);
                break;
            case Confiteor or BladeOfFaith or BladeOfTruth or BladeOfValor:
                var status = player.FindStatus(RequiescatStatus, sourceRole);
                var stacks = status?.Stacks ?? 0;
                if (stacks <= 1)
                {
                    Remove(player, RequiescatStatus, sourceRole);
                    Remove(player, ConfiteorReady, sourceRole);
                }
                else
                {
                    // 剩餘時間沿用原本那份，不重置倒數；原生槽跟著 SimStatus 的遞減值走。
                    Add(player, RequiescatStatus, stacks - 1, sourceRole, sourceObject,
                        status!.RemainingTime);
                }
                break;
        }
    }

    private static bool Add(SimCharacter player, ushort statusId, int param, PartyRole sourceRole,
        GameObjectId sourceObject, float duration = 30f)
        => player.AddStatusParam(statusId, param, duration, sourceRole, sourceObject) is not null;

    private static void Remove(SimCharacter player, ushort statusId, PartyRole sourceRole)
        => player.RemoveStatus(statusId, sourceRole);
}
