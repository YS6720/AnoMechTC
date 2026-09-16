using System;
using System.Collections.Generic;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace AnoMech.Core.Combat.Jobs;

// 武士（7.x，等級 100）。兩層各走各的路：
//   狀態（風月／風花／明鏡止水／天道／燕回返預備／奧義斬浪預備／殘心預備／燕飛效果提高）
//     → IJobStatusRules：房主替每個角色跑，狀態走既有同步；成員端不跑（同騎士）。
//   量譜（劍氣／閃／默想層數／回返種類）
//     → IJobGaugeRules：只在自己的客戶端、只寫自己的 JobGaugeManager；不進網路。
// 技能／狀態 id 取自台服資料表（cn_Action／cn_Status，2026-09-16）：注意 1231 是「默想」
// 不是明鏡止水，明鏡止水是 1233；風花是 1299。
// 尚未實機確認的一項：7.x「燕回返預備」在台服目前版本是 3852 還是 4216～4218 三分身，
// 先用 3852；若燕回返按不出來，換 id 即可（只此一處）。
internal sealed unsafe class Samurai : IJobStatusRules, IJobGaugeRules
{
    internal const byte JobId = 34;
    internal static readonly Samurai Instance = new();

    // ---- actions ----
    private const uint Hakaze = 7477, Gyofu = 36963;
    private const uint Jinpu = 7478, Shifu = 7479, Yukikaze = 7480, Gekko = 7481, Kasha = 7482;
    private const uint Fuko = 25780, Mangetsu = 7484, Oka = 7485;
    private const uint Enpi = 7486, Hagakure = 7495;
    private const uint Higanbana = 7489, TenkaGoken = 7488, MidareSetsugekka = 7487;
    private const uint TendoGoken = 36965, TendoSetsugekka = 36966;   // 41452~41455 是別的（台服表 lvl 0）
    private const uint KaeshiGoken = 16485, KaeshiSetsugekka = 16486;
    private const uint TendoKaeshiGoken = 36967, TendoKaeshiSetsugekka = 36968;
    private const uint Shinten = 7490, Kyuten = 7491, Gyoten = 7492, Yaten = 7493, Guren = 7496, Senei = 16481;
    private const uint Ikishoten = 16482, OgiNamikiri = 25781, KaeshiNamikiri = 25782;
    private const uint Shoha = 16487, Meikyo = 7499, Zanshin = 36964, Tengentsu = 36962, ThirdEye = 7498;

    // ---- statuses ----
    private const ushort Fugetsu = 1298;        // 風月
    private const ushort Fuka = 1299;           // 風花
    private const ushort MeikyoShisui = 1233;   // 明鏡止水（3 層）
    private const ushort Tendo = 3856;          // 天道
    private const ushort TsubameReady = 3852;   // 燕回返預備
    private const ushort OgiReady = 2959;       // 奧義斬浪預備
    private const ushort ZanshinReady = 3855;   // 殘心預備
    private const ushort EnhancedEnpi = 1236;   // 燕飛效果提高
    private const ushort TengentsuStatus = 3853;
    private const ushort ThirdEyeStatus = 1232;

    // 回返種類寫進 SamuraiGauge.Kaeshi 的原始位元組。安裝版 ClientStructs 的 KaeshiAction 是 7.0 前的
    // 列舉（Higanbana=1／Goken=2／Setsugekka=3／Namikiri=4），沒有天道版；7.x 實際值以模擬區外的
    // 探針（ProbeOutsideSim）實測為準，確認後只改這幾個常數。
    private const byte KaeshiNone = 0, KaeshiGokenValue = 2, KaeshiSetsugekkaValue = 3, KaeshiNamikiriValue = 4;
    private const byte KaeshiTendoGokenValue = 5, KaeshiTendoSetsugekkaValue = 6;

    // ---- IJobStatusRules ----

    public bool IsKnownAction(uint actionId) => actionId is
        Jinpu or Shifu or Yukikaze or Gekko or Kasha or Mangetsu or Oka or
        Enpi or Yaten or Ikishoten or OgiNamikiri or Zanshin or Meikyo or
        TendoGoken or TendoSetsugekka or
        KaeshiGoken or KaeshiSetsugekka or TendoKaeshiGoken or TendoKaeshiSetsugekka or
        Tengentsu or ThirdEye;

    public IReadOnlyList<ushort> TouchedStatuses(uint actionId, bool comboOk) => actionId switch
    {
        Jinpu or Mangetsu => [Fugetsu, MeikyoShisui],
        Shifu or Oka => [Fuka, MeikyoShisui],
        Yukikaze or Gekko or Kasha => [MeikyoShisui],
        Yaten or Enpi => [EnhancedEnpi],
        Ikishoten => [OgiReady, ZanshinReady],
        OgiNamikiri => [OgiReady],
        Zanshin => [ZanshinReady],
        Meikyo => [MeikyoShisui, Tendo, TsubameReady],
        TendoGoken or TendoSetsugekka => [Tendo],
        KaeshiGoken or KaeshiSetsugekka or TendoKaeshiGoken or TendoKaeshiSetsugekka => [TsubameReady],
        Tengentsu => [TengentsuStatus],
        ThirdEye => [ThirdEyeStatus],
        _ => Array.Empty<ushort>(),
    };

    public void Apply(uint actionId, bool comboOk, SimCharacter player, PartyRole role)
    {
        var source = player.GameObjectId;
        var meikyo = player.FindStatus(MeikyoShisui, role);
        // 明鏡止水：連段技不需前置就算連段成立，每用一招少一層。
        var combo = comboOk || meikyo is not null;
        switch (actionId)
        {
            case Jinpu or Mangetsu:
                if (combo) Add(player, Fugetsu, 0, role, source, 40f);
                ConsumeMeikyo(player, role, source, meikyo);
                break;
            case Shifu or Oka:
                if (combo) Add(player, Fuka, 0, role, source, 40f);
                ConsumeMeikyo(player, role, source, meikyo);
                break;
            case Yukikaze or Gekko or Kasha:
                ConsumeMeikyo(player, role, source, meikyo);
                break;
            case Yaten:
                Add(player, EnhancedEnpi, 0, role, source, 15f);
                break;
            case Enpi:
                player.RemoveStatus(EnhancedEnpi, role);
                break;
            case Ikishoten:
                Add(player, OgiReady, 0, role, source, 30f);
                Add(player, ZanshinReady, 0, role, source, 30f);
                break;
            case OgiNamikiri:
                player.RemoveStatus(OgiReady, role);
                break;
            case Zanshin:
                player.RemoveStatus(ZanshinReady, role);
                break;
            case Meikyo:
                Add(player, MeikyoShisui, 3, role, source, 20f);
                Add(player, Tendo, 0, role, source, 30f);
                Add(player, TsubameReady, 0, role, source, 30f);
                break;
            case TendoGoken or TendoSetsugekka:
                player.RemoveStatus(Tendo, role);
                break;
            case KaeshiGoken or KaeshiSetsugekka or TendoKaeshiGoken or TendoKaeshiSetsugekka:
                player.RemoveStatus(TsubameReady, role);
                break;
            case Tengentsu:
                Add(player, TengentsuStatus, 0, role, source, 4f);
                break;
            case ThirdEye:
                Add(player, ThirdEyeStatus, 0, role, source, 4f);
                break;
        }
    }

    private static void ConsumeMeikyo(SimCharacter player, PartyRole role, GameObjectId source, SimStatus? meikyo)
    {
        if (meikyo is null) return;
        if (meikyo.Stacks <= 1) player.RemoveStatus(MeikyoShisui, role);
        else Add(player, MeikyoShisui, meikyo.Stacks - 1, role, source, meikyo.RemainingTime);
    }

    private static void Add(SimCharacter player, ushort statusId, int param, PartyRole role,
        GameObjectId source, float duration)
        => player.AddStatusParam(statusId, param, duration, role, source);

    // ---- IJobGaugeRules（本機、只寫自己）----

    private static SamuraiGauge* Gauge()
    {
        var manager = JobGaugeManager.Instance();
        if (manager == null || manager->ClassJobId != JobId || manager->CurrentGauge == null) return null;
        return (SamuraiGauge*)manager->CurrentGauge;
    }

    private static bool LocalHas(ushort statusId)
    {
        if (Plugin.ObjectTable.LocalPlayer is not { } lp || lp.Address == 0) return false;
        var slots = ((BattleChara*)lp.Address)->StatusManager.Status;
        for (var i = 0; i < slots.Length; i++)
            if (slots[i].StatusId == statusId) return true;
        return false;
    }

    public void OnLocalFire(uint actionId, bool comboOk)
    {
        var g = Gauge();
        if (g == null) return;
        var combo = comboOk || LocalHas(MeikyoShisui);
        int kenki = g->Kenki, meditation = g->MeditationStacks;
        var sen = g->SenFlags;
        var kaeshi = (byte)g->Kaeshi;
        switch (actionId)
        {
            case Hakaze or Gyofu: kenki += 5; break;
            case Jinpu or Shifu: if (combo) kenki += 5; break;
            case Yukikaze: if (combo) { kenki += 15; sen |= SenFlags.Setsu; } break;
            case Gekko: if (combo) { kenki += 10; sen |= SenFlags.Getsu; } break;
            case Kasha: if (combo) { kenki += 10; sen |= SenFlags.Ka; } break;
            case Fuko: kenki += 10; break;
            case Mangetsu: if (combo) { kenki += 10; sen |= SenFlags.Getsu; } break;
            case Oka: if (combo) { kenki += 10; sen |= SenFlags.Ka; } break;
            case Enpi: kenki += 10; break;
            case Hagakure: kenki += 10 * SenCount(sen); sen = SenFlags.None; break;
            case Higanbana: sen = SenFlags.None; meditation++; kaeshi = KaeshiNone; break;
            case TenkaGoken: sen = SenFlags.None; meditation++; kaeshi = KaeshiGokenValue; break;
            case MidareSetsugekka: sen = SenFlags.None; meditation++; kaeshi = KaeshiSetsugekkaValue; break;
            case TendoGoken: sen = SenFlags.None; meditation++; kaeshi = KaeshiTendoGokenValue; break;
            case TendoSetsugekka: sen = SenFlags.None; meditation++; kaeshi = KaeshiTendoSetsugekkaValue; break;
            case KaeshiGoken or KaeshiSetsugekka or TendoKaeshiGoken or TendoKaeshiSetsugekka:
                meditation++; kaeshi = KaeshiNone; break;
            case OgiNamikiri: meditation++; kaeshi = KaeshiNamikiriValue; break;
            case KaeshiNamikiri: meditation++; kaeshi = KaeshiNone; break;
            case Shoha: meditation = 0; break;
            case Ikishoten: kenki += 50; break;
            case Shinten or Kyuten or Guren or Senei: kenki -= 25; break;
            case Gyoten or Yaten: kenki -= 10; break;
            case Zanshin: kenki -= 50; break;
            default: return;
        }
        g->Kenki = (byte)Math.Clamp(kenki, 0, 100);
        g->MeditationStacks = (byte)Math.Clamp(meditation, 0, 3);
        g->SenFlags = sen;
        g->Kaeshi = (KaeshiAction)kaeshi;
        Core.CrashTrace.Log($"[量譜] SAM a={actionId} combo={combo} 劍氣={g->Kenki} 閃={sen} 默想={g->MeditationStacks} 回返={kaeshi}");
    }

    private static int SenCount(SenFlags sen)
        => ((sen & SenFlags.Setsu) != 0 ? 1 : 0) + ((sen & SenFlags.Getsu) != 0 ? 1 : 0) + ((sen & SenFlags.Ka) != 0 ? 1 : 0);

    public void Reset()
    {
        var g = Gauge();
        if (g == null) return;
        g->Kenki = 0;
        g->MeditationStacks = 0;
        g->SenFlags = SenFlags.None;
        g->Kaeshi = (KaeshiAction)KaeshiNone;
    }

}
