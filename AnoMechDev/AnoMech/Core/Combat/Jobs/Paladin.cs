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
internal sealed class Paladin : IJobStatusRules
{
    internal const byte JobId = 19;
    internal static readonly Paladin Instance = new();

    // 武裝戌守（Passage of Arms）是引導技：站著不動就持續，移動或用任何技能立刻結束。
    // 實錄規則只有「7385 → 掛 1175 17.95 秒」，沒有取消條件；9/15 記為暫不處理，維護者
    // 2026-09-16 明確要求：「人物動了放技能他正常要馬上停止」——否則每個 GCD 新掛的連段狀態
    // 走 OnGainStatus 時，引擎會把還在槽裡的 1175 翅膀重播一次。
    internal const ushort PassageOfArms = 1175;

    /// <summary>移動輸入或按下任何技能的那一幀呼叫；有引導中的狀態就結束它。</summary>
    internal static void CancelChanneled(SimCharacter player)
    {
        // 實錄規則建立的 1175 帶著騎士角色來源；HasStatus／RemoveStatus 的無來源版本是
        // 「來源＝null」精確比對，永遠找不到（9/16 trace：放技能後沒有任何 remove）。
        player.RemoveStatusAnySource(PassageOfArms);
    }

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

    public bool IsKnownAction(uint actionId)
        => actionId is FightOrFlight or RoyalAuthority or GoringBlade or Atonement or
            Supplication or Sepulchre or HolySpirit or HolyCircle or Requiescat or
            Imperator or Confiteor or BladeOfFaith or BladeOfTruth or BladeOfValor;


    internal static bool AppliesStatus(uint actionId, bool comboOk)
        => actionId switch
        {
            FightOrFlight or GoringBlade or Atonement or Supplication or Sepulchre or
                HolySpirit or HolyCircle or Requiescat or Imperator or Confiteor or
                BladeOfFaith or BladeOfTruth or BladeOfValor => true,
            RoyalAuthority => comboOk,
            _ => false,
        };

    public IReadOnlyList<ushort> TouchedStatuses(uint actionId, bool comboOk)
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

    public void Apply(uint actionId, bool comboOk, SimCharacter player, PartyRole sourceRole)
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
