using System;
using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Party;
using AnoMech.Core.Native;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace AnoMech.Core.SimObjects;

public sealed unsafe class SimPartyNpc : SimNpc, ISimPartyMember
{
    public PartyRole Role { get; set; }
    public bool Dead { get; private set; }
    public byte ClassJob { get; }
    public string DisplayName { get; }

    internal SimPartyNpc(int index, Coordinates coordinates, PartyRole role, byte classJob, string name) : base(index, coordinates)
    {
        Role = role;
        ClassJob = classJob;
        DisplayName = name;
    }

    public void Knockback(Vector3 source, float distance, float speed) => Movement.Knockback(source, distance, speed);

    public override void Despawn()
    {
        base.Despawn();
    }

    public void OnKilled()
    {
        Dead = true;
        StopMoving();
        var bc = BattleCharaPtr;
        if (bc == null) return;
        ApplyDeadState(bc);
        this.PlayKoActionTimeline();
    }

    /// <summary>
    /// 練習模式復活（2026-08-20，維護者：「死人就沒辦法繼續下去」）。
    /// AI 隊友死亡會讓塔／分攤連鎖失敗、後半段機制形同消失——訓練工具裡
    /// 「一人失誤全場作廢」沒有價值，AI 倒地幾秒後自動扶起、軌跡繼續。
    /// 只給 AI 用；玩家死亡仍走正常流程（那是練習回饋）。
    /// </summary>
    public void Revive()
    {
        if (!Dead) return;
        Dead = false;
        var bc = BattleCharaPtr;
        if (bc == null) return;
        bc->Health = bc->MaxHealth;
        bc->Mana = bc->MaxMana;
        bc->Mode = CharacterModes.Normal;
    }

    private static void ApplyDeadState(BattleChara* bc)
    {
        bc->Health = 0;
        bc->Mana = 0;
        bc->Mode = CharacterModes.Dead;
    }
}
