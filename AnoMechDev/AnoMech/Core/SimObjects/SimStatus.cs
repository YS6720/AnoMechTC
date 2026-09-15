using System;
using AnoMech.Core.Game.Party;
using AnoMech.Core.Native;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace AnoMech.Core.SimObjects;

public sealed unsafe class SimStatus : ISimObject
{
    private readonly SimCharacter target;
    private GameObjectId sourceObject;
    private float duration;
    private float elapsed;

    public ushort StatusId { get; }
    public PartyRole? SourceRole { get; }
    internal GameObjectId SourceObject => sourceObject;
    public float? NativeRemainingOverride { get; private set; }
    public bool IsActive { get; private set; }
    public ushort Stacks { get; private set; }
    public float RemainingTime => duration <= 0f ? 0f : MathF.Max(0f, duration - elapsed);

    internal SimStatus(
        SimCharacter target,
        ushort statusId,
        float duration,
        ushort stacks,
        PartyRole? sourceRole = null,
        GameObjectId sourceObject = default,
        float? nativeRemainingOverride = null)
    {
        this.target = target;
        this.duration = MathF.Max(0f, duration);
        this.sourceObject = sourceObject;
        StatusId = statusId;
        SourceRole = sourceRole;
        NativeRemainingOverride = nativeRemainingOverride;
        IsActive = true;
        Stacks = stacks;
        Statuses.AddStatusInit(
            (Character*)target.BattleCharaPtr,
            statusId,
            stacks,
            refreshDuration: NativeRemainingOverride ?? RemainingTime,
            sourceObject: sourceObject);
    }

    public void Reapply(float duration, int stacks)
    {
        if (duration > 0f)
        {
            this.duration = duration;
            elapsed = 0f;
        }
        // Negative stacks decrement; clamp to 0 (callers handle removal at 0).
        Stacks = (ushort)Math.Max(0, Stacks + stacks);
    }

    // 維護者-snapshot refresh (peer side). Existence, countdown, raw param and
    // native convention all come from the owner, so pin them in place —
    // Statuses.Apply refreshes the exact source slot without the status-init
    // gain path, which at 20Hz snapshot rate would replay the gain VFX forever.
    internal void ApplyNetworkState(
        float remaining,
        ushort param,
        float? nativeRemainingOverride = null,
        GameObjectId sourceObject = default)
    {
        if (!IsActive) return;
        var previousSourceObject = this.sourceObject;
        duration = MathF.Max(0f, remaining);
        elapsed = 0f;
        NativeRemainingOverride = nativeRemainingOverride;
        Stacks = param;
        Statuses.Apply(
            (Character*)target.BattleCharaPtr,
            StatusId,
            NativeRemainingOverride ?? RemainingTime,
            param,
            sourceObject);
        this.sourceObject = sourceObject;
        if (previousSourceObject != sourceObject)
            Statuses.Remove((Character*)target.BattleCharaPtr, StatusId, previousSourceObject);
    }

    public void Tick(float deltaSeconds)
    {
        if (!IsActive) return;

        if (duration > 0f)
        {
            elapsed = MathF.Min(duration, elapsed + deltaSeconds);
            if (elapsed >= duration)
            {
                Despawn();
                return;
            }
        }

        // 原生槽不見了＝遊戲自己取消了這個狀態（武裝戌守移動即取消、被打斷、玩家手動解除、
        // 開始練習時的 ResetPlayerStatuses…）。遊戲對它自己的狀態是權威，模擬層只是鏡像：
        // 這時要跟著結束，不能把它寫回去。少了這一步，維護者 2026-09-15 實機看到的是
        // 「移動取消後打一個 GCD，翅膀又出現」——鏡像拿自己的計時器當唯一真相，反覆重建
        // 一個遊戲已經判定結束的狀態。
        if (!Statuses.Has((Character*)target.BattleCharaPtr, StatusId, sourceObject))
        {
            IsActive = false;   // 槽位已空，不必再 Remove
            return;
        }

        Statuses.Apply(
            (Character*)target.BattleCharaPtr,
            StatusId,
            NativeRemainingOverride ?? RemainingTime,
            Stacks,
            sourceObject);
    }

    public void Despawn()
    {
        if (!IsActive) return;
        Statuses.Remove((Character*)target.BattleCharaPtr, StatusId, sourceObject);
        IsActive = false;
    }
}
