using System;
using System.Collections.Generic;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace AnoMech.Core.Combat.Jobs;

// Samurai has two deliberately separate state paths. Status transitions are
// host-owned and source-qualified; native gauge fields are local-only.
internal sealed unsafe class Samurai : IJobStatusRules, IJobGaugeRules
{
    internal const byte JobId = 34;
    internal static readonly Samurai Instance = new();

    // Actions
    private const uint Hakaze = 7477;
    private const uint Jinpu = 7478;
    private const uint Shifu = 7479;
    private const uint Yukikaze = 7480;
    private const uint Gekko = 7481;
    private const uint Kasha = 7482;
    private const uint Fuga = 7483;
    private const uint Mangetsu = 7484;
    private const uint Oka = 7485;
    private const uint Enpi = 7486;
    private const uint Iaijutsu = 7867;
    private const uint Higanbana = 7489;
    private const uint TenkaGoken = 7488;
    private const uint MidareSetsugekka = 7487;
    private const uint Shinten = 7490;
    private const uint Kyuten = 7491;
    private const uint Gyoten = 7492;
    private const uint Yaten = 7493;
    private const uint Hagakure = 7495;
    private const uint Guren = 7496;
    private const uint Meditate = 7497;
    private const uint ThirdEye = 7498;
    private const uint Meikyo = 7499;
    private const uint Senei = 16481;
    private const uint Ikishoten = 16482;
    private const uint KaeshiNamikiri = 25782;
    private const uint KaeshiGoken = 16485;
    private const uint KaeshiSetsugekka = 16486;
    private const uint Shoha = 16487;
    private const uint Fuko = 25780;
    private const uint OgiNamikiri = 25781;
    private const uint Gyofu = 36963;
    private const uint Tengentsu = 36962;
    private const uint Zanshin = 36964;
    private const uint TendoGoken = 36965;
    private const uint TendoSetsugekka = 36966;
    private const uint TendoKaeshiGoken = 36967;
    private const uint TendoKaeshiSetsugekka = 36968;

    // Statuses
    private const ushort Meditation = 1231;
    private const ushort ThirdEyeStatus = 1232;
    private const ushort MeikyoShisui = 1233;
    private const ushort EnhancedEnpi = 1236;
    private const ushort Fugetsu = 1298;
    private const ushort Fuka = 1299;
    private const ushort OgiReady = 2959;
    private const ushort TsubameGokenReady = 3852;
    private const ushort TengentsuStatus = 3853;
    private const ushort TengentsuForesight = 3854;
    private const ushort ZanshinReady = 3855;
    private const ushort Tendo = 3856;
    private const ushort TsubameSetsugekkaReady = 4216;
    private const ushort TendoTsubameGokenReady = 4217;
    private const ushort TendoTsubameSetsugekkaReady = 4218;

    // The SDK enum only defines the four established values. The Dawntrail
    // Tendo Kaeshi byte is intentionally not written until its native value is
    // independently verified; the four status IDs above remain authoritative.
    private const byte KaeshiNone = 0;
    private const byte KaeshiGokenValue = 2;
    private const byte KaeshiSetsugekkaValue = 3;
    private const byte KaeshiNamikiriValue = 4;

    public bool IsKnownAction(uint actionId) => actionId is
        Hakaze or Gyofu or Jinpu or Shifu or Yukikaze or Gekko or Kasha or Fuga or
        Fuko or Mangetsu or Oka or Enpi or Iaijutsu or Higanbana or TenkaGoken or
        MidareSetsugekka or Shinten or Kyuten or Gyoten or Yaten or Hagakure or
        Guren or Meditate or ThirdEye or Meikyo or Senei or Ikishoten or
        KaeshiNamikiri or KaeshiGoken or KaeshiSetsugekka or Shoha or OgiNamikiri or
        Tengentsu or Zanshin or TendoGoken or TendoSetsugekka or TendoKaeshiGoken or
        TendoKaeshiSetsugekka;

    public IReadOnlyList<ushort> TouchedStatuses(uint actionId, bool comboOk) => actionId switch
    {
        Jinpu or Mangetsu => [Fugetsu, MeikyoShisui],
        Shifu or Oka => [Fuka, MeikyoShisui],
        Yukikaze => [MeikyoShisui],
        Gekko => [MeikyoShisui, Fugetsu],
        Kasha => [MeikyoShisui, Fuka],
        Yaten or Enpi => [EnhancedEnpi],
        Meditate => [Meditation],
        Ikishoten => [OgiReady, ZanshinReady],
        OgiNamikiri => [OgiReady],
        Zanshin => [ZanshinReady],
        Meikyo => [MeikyoShisui, Tendo],
        TenkaGoken => [TsubameGokenReady],
        MidareSetsugekka => [TsubameSetsugekkaReady],
        TendoGoken => [Tendo, TendoTsubameGokenReady],
        TendoSetsugekka => [Tendo, TendoTsubameSetsugekkaReady],
        KaeshiGoken => [TsubameGokenReady],
        KaeshiSetsugekka => [TsubameSetsugekkaReady],
        TendoKaeshiGoken => [TendoTsubameGokenReady],
        TendoKaeshiSetsugekka => [TendoTsubameSetsugekkaReady],
        Tengentsu => [TengentsuStatus, TengentsuForesight],
        ThirdEye => [ThirdEyeStatus],
        _ => Array.Empty<ushort>(),
    };

    public void Apply(in JobActionContext context)
    {
        var meikyo = context.Find(MeikyoShisui);
        var combo = context.ComboOk || meikyo is not null;

        switch (context.ActionId)
        {
            case Jinpu or Mangetsu:
                if (combo)
                    context.Grant(Fugetsu, 0, 40f);
                if (meikyo is not null)
                    context.Consume(MeikyoShisui);
                break;
            case Shifu or Oka:
                if (combo)
                    context.Grant(Fuka, 0, 40f);
                if (meikyo is not null)
                    context.Consume(MeikyoShisui);
                break;
            case Yukikaze or Gekko or Kasha:
                if (meikyo is not null)
                {
                    if (context.ActionId == Gekko) context.Grant(Fugetsu, 0, 40f);
                    else if (context.ActionId == Kasha) context.Grant(Fuka, 0, 40f);
                    context.Consume(MeikyoShisui);
                }
                break;
            case Yaten:
                context.Grant(EnhancedEnpi, 0, 15f);
                break;
            case Enpi:
                context.Remove(EnhancedEnpi);
                break;
            case Meditate:
                context.Grant(Meditation, 0, 15f);
                break;
            case Ikishoten:
                if (context.Level >= 90)
                    context.Grant(OgiReady, 0, 30f);
                if (context.Level >= 96)
                    context.Grant(ZanshinReady, 0, 30f);
                break;
            case OgiNamikiri:
                context.Remove(OgiReady);
                break;
            case Zanshin:
                context.Remove(ZanshinReady);
                break;
            case Meikyo:
                context.Grant(MeikyoShisui, 3, 20f);
                if (context.Level >= 100)
                    context.Grant(Tendo, 0, 30f);
                break;
            case TenkaGoken:
                if (context.Level >= 74)
                    context.Grant(TsubameGokenReady, 0, 30f);
                break;
            case MidareSetsugekka:
                if (context.Level >= 74)
                    context.Grant(TsubameSetsugekkaReady, 0, 30f);
                break;
            case TendoGoken:
                context.Remove(Tendo);
                if (context.Level >= 100)
                    context.Grant(TendoTsubameGokenReady, 0, 30f);
                break;
            case TendoSetsugekka:
                context.Remove(Tendo);
                if (context.Level >= 100)
                    context.Grant(TendoTsubameSetsugekkaReady, 0, 30f);
                break;
            case KaeshiGoken:
                context.Remove(TsubameGokenReady);
                break;
            case KaeshiSetsugekka:
                context.Remove(TsubameSetsugekkaReady);
                break;
            case TendoKaeshiGoken:
                context.Remove(TendoTsubameGokenReady);
                break;
            case TendoKaeshiSetsugekka:
                context.Remove(TendoTsubameSetsugekkaReady);
                break;
            case Tengentsu:
                context.Grant(TengentsuStatus, 0, 4f);
                break;
            case ThirdEye:
                context.Grant(ThirdEyeStatus, 0, 4f);
                break;
        }
    }

    public void OnMechanicHit(in JobActionContext context, SimCharacter target)
    {
        if (!ReferenceEquals(target, context.Player))
            return;

        var hitThirdEye = context.Find(ThirdEyeStatus) is not null;
        var hitTengentsu = context.Find(TengentsuStatus) is not null;
        if (!hitThirdEye && !hitTengentsu)
            return;

        if (hitTengentsu)
        {
            context.Remove(TengentsuStatus);
            context.Grant(TengentsuForesight, 0, 9f);
        }
        else
        {
            context.Remove(ThirdEyeStatus);
        }

        if (context.Level >= 52)
            context.AddResource(JobResource.Kenki, 10);
    }

    public void ResetStatusState()
    {
    }

    // ---- Local native gauge ----

    private float meditationTimer;

    private static SamuraiGauge* Gauge()
    {
        var manager = JobGaugeManager.Instance();
        if (manager == null || manager->ClassJobId != JobId || manager->CurrentGauge == null)
            return null;
        return (SamuraiGauge*)manager->CurrentGauge;
    }

    private static bool LocalHas(ushort statusId) => LocalJobResources.HasStatus(statusId);

    public void OnLocalFire(uint actionId, bool comboOk, byte level)
    {
        var gauge = Gauge();
        if (gauge == null)
            return;

        var combo = comboOk || LocalHas(MeikyoShisui);
        var kenki = (int)gauge->Kenki;
        var meditation = (int)gauge->MeditationStacks;
        var sen = gauge->SenFlags;
        var kaeshi = (byte)gauge->Kaeshi;
        var hasKenki = level >= 52;

        switch (actionId)
        {
            case Hakaze or Gyofu:
                if (hasKenki) kenki += 5;
                break;
            case Jinpu or Shifu:
                if (hasKenki && combo) kenki += 5;
                break;
            case Fuga:
                if (hasKenki) kenki += 5;
                break;
            case Fuko:
                if (hasKenki) kenki += 10;
                break;
            case Yukikaze:
                if (hasKenki && combo)
                {
                    kenki += 15;
                    sen |= SenFlags.Setsu;
                }
                break;
            case Gekko:
                if (hasKenki && combo)
                {
                    kenki += 10;
                    sen |= SenFlags.Getsu;
                }
                break;
            case Kasha:
                if (hasKenki && combo)
                {
                    kenki += 10;
                    sen |= SenFlags.Ka;
                }
                break;
            case Mangetsu:
                if (hasKenki && combo)
                {
                    kenki += 10;
                    sen |= SenFlags.Getsu;
                }
                break;
            case Oka:
                if (hasKenki && combo)
                {
                    kenki += 10;
                    sen |= SenFlags.Ka;
                }
                break;
            case Enpi:
                if (hasKenki) kenki += 10;
                break;
            case Hagakure:
                if (hasKenki)
                {
                    kenki += 10 * SenCount(sen);
                    sen = SenFlags.None;
                }
                break;
            case Higanbana:
                sen = SenFlags.None;
                meditation++;
                kaeshi = KaeshiNone;
                break;
            case TenkaGoken:
                sen = SenFlags.None;
                meditation++;
                kaeshi = KaeshiGokenValue;
                break;
            case MidareSetsugekka:
                sen = SenFlags.None;
                meditation++;
                kaeshi = KaeshiSetsugekkaValue;
                break;
            case TendoGoken or TendoSetsugekka:
                sen = SenFlags.None;
                meditation++;
                // The native enum has no verified Dawntrail value. Do not
                // leave a stale pre-Dawntrail Kaeshi selection active.
                kaeshi = KaeshiNone;
                break;
            case KaeshiGoken or KaeshiSetsugekka or TendoKaeshiGoken or TendoKaeshiSetsugekka:
                kaeshi = KaeshiNone;
                break;
            case OgiNamikiri:
                meditation++;
                kaeshi = KaeshiNamikiriValue;
                break;
            case KaeshiNamikiri:
                kaeshi = KaeshiNone;
                break;
            case Shoha:
                meditation = 0;
                break;
            case Meditate:
                meditationTimer = 0f;
                break;
            case Ikishoten:
                if (hasKenki) kenki += 50;
                break;
            case Shinten or Kyuten or Guren or Senei:
                if (hasKenki) kenki -= 25;
                break;
            case Gyoten or Yaten:
                if (hasKenki) kenki -= 10;
                break;
            case Zanshin:
                if (hasKenki) kenki -= 50;
                break;
            default:
                return;
        }

        gauge->Kenki = (byte)Math.Clamp(kenki, 0, 100);
        gauge->MeditationStacks = (byte)Math.Clamp(meditation, 0, 3);
        gauge->SenFlags = sen;
        gauge->Kaeshi = (KaeshiAction)kaeshi;
    }

    public void Tick(float deltaSeconds, byte level)
    {
        var gauge = Gauge();
        if (gauge == null || level < 60 || !LocalHas(Meditation))
        {
            meditationTimer = 0f;
            return;
        }
        if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0f)
            return;

        meditationTimer += deltaSeconds;
        while (meditationTimer >= 3f)
        {
            meditationTimer -= 3f;
            gauge->Kenki = (byte)Math.Clamp(gauge->Kenki + 10, 0, 100);
            // This tick runs only during active practice, which is simulated combat.
            if (level >= 80)
                gauge->MeditationStacks = (byte)Math.Min(gauge->MeditationStacks + 1, 3);
        }
    }

    public void Reset(byte level)
    {
        meditationTimer = 0f;
        var gauge = Gauge();
        if (gauge == null)
            return;
        gauge->Kenki = 0;
        gauge->MeditationStacks = 0;
        gauge->SenFlags = SenFlags.None;
        gauge->Kaeshi = (KaeshiAction)KaeshiNone;
    }

    private static int SenCount(SenFlags sen)
        => ((sen & SenFlags.Setsu) != 0 ? 1 : 0)
            + ((sen & SenFlags.Getsu) != 0 ? 1 : 0)
            + ((sen & SenFlags.Ka) != 0 ? 1 : 0);
}
