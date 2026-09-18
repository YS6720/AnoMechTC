using AnoMech.Multiplayer;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;
using System;

namespace AnoMech.Core.Game.Party;

// 連線練習時把「真人外觀」帶進隊友替身（doppel）。
//
// 只搬**原版**外觀：CustomizeData 的 21 個具名欄位、五件裝備與雙手武器的 model id、
// 以及狀態特效尺寸用的 Height／VfxScale。Penumbra／Glamourer 的改造不在其中，
// 對方看到的是你的未改造外觀——要帶 mod 就得同步檔案，那是另一個量級的系統。
//
// 收端**一律 fail-closed**：任何一項驗不過就整份丟掉、退回既有的 Lalafell preset。
// 這不是潔癖——race／tribe／sex／bodyType 組合不合法會讓模型載入直接把客戶端打掛，
// 而這些位元組是遠端來的。
public static unsafe class PlayerAppearance
{
    /// <summary>
    /// <see cref="MpAppearance.Customize"/> 的欄位順序。**這是線路格式，不可重排**
    /// （重排＝舊版把髮色套到臉上）；要加欄位只能往後接，並同步 <c>MpLimits.ProtocolVersion</c>。
    /// </summary>
    public const int CustomizeLength = 21;

    /// <summary>本機玩家的原版外觀；讀不到角色時回傳 null（呼叫端退回 preset）。</summary>
    public static MpAppearance? Capture()
    {
        var local = Plugin.ObjectTable.LocalPlayer;
        if (local == null) return null;
        var chara = (BattleChara*)local.Address;
        if (chara == null) return null;

        ref var c = ref chara->DrawData.CustomizeData;
        var customize = new byte[CustomizeLength];
        customize[0] = c.Race;
        customize[1] = c.Sex;
        customize[2] = c.BodyType;
        customize[3] = c.Height;
        customize[4] = c.Tribe;
        customize[5] = c.Face;
        customize[6] = c.Hairstyle;
        customize[7] = c.SkinColor;
        customize[8] = c.EyeColorRight;
        customize[9] = c.EyeColorLeft;
        customize[10] = c.HairColor;
        customize[11] = c.HighlightsColor;
        customize[12] = c.TattooColor;
        customize[13] = c.Eyebrows;
        customize[14] = c.Nose;
        customize[15] = c.Jaw;
        customize[16] = c.LipColorFurPattern;
        customize[17] = c.MuscleMass;
        customize[18] = c.TailShape;
        customize[19] = c.BustSize;
        customize[20] = c.FacePaintColor;

        var equipment = new ulong[MpAppearance.EquipmentSlots];
        equipment[0] = chara->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Head).Value;
        equipment[1] = chara->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Body).Value;
        equipment[2] = chara->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Hands).Value;
        equipment[3] = chara->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Legs).Value;
        equipment[4] = chara->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Feet).Value;

        var appearance = new MpAppearance(
            customize,
            equipment,
            chara->DrawData.Weapon(DrawDataContainer.WeaponSlot.MainHand).ModelId.Value,
            chara->DrawData.Weapon(DrawDataContainer.WeaponSlot.OffHand).ModelId.Value,
            chara->Height,
            chara->VfxScale);
        return IsValid(appearance) ? appearance : null;
    }

    /// <summary>
    /// 驗證遠端外觀＝結構檢查（<see cref="MpValidation.Appearance"/>）＋race／tribe 真的存在於
    /// Lumina 表。這兩項決定骨架與模型路徑，錯了就是載入期當機；其餘是外觀索引，
    /// 遊戲對不存在的索引會退回預設而不是崩。
    /// </summary>
    public static bool IsValid(MpAppearance? appearance)
    {
        if (!MpValidation.Appearance(appearance) || appearance == null) return false;
        var customize = appearance.Customize;
        return Plugin.DataManager.GetExcelSheet<Race>().HasRow(customize[0])
            && Plugin.DataManager.GetExcelSheet<Tribe>().HasRow(customize[4]);
    }

    /// <summary>
    /// 把已驗證的外觀寫進 doppel。**只在生成時呼叫**：換種族＝換骨架，run 中改要走完整重繪，
    /// 會踩到 <see cref="SimObjects.SimNpc.Despawn"/> 註解裡那兩份 timeline teardown 當機。
    /// </summary>
    public static void Apply(BattleChara* chara, MpAppearance appearance)
    {
        var customize = appearance.Customize;
        ref var c = ref chara->DrawData.CustomizeData;
        c.Race = customize[0];
        c.Sex = customize[1];
        c.BodyType = customize[2];
        c.Height = customize[3];
        c.Tribe = customize[4];
        c.Face = customize[5];
        c.Hairstyle = customize[6];
        c.SkinColor = customize[7];
        c.EyeColorRight = customize[8];
        c.EyeColorLeft = customize[9];
        c.HairColor = customize[10];
        c.HighlightsColor = customize[11];
        c.TattooColor = customize[12];
        c.Eyebrows = customize[13];
        c.Nose = customize[14];
        c.Jaw = customize[15];
        c.LipColorFurPattern = customize[16];
        c.MuscleMass = customize[17];
        c.TailShape = customize[18];
        c.BustSize = customize[19];
        c.FacePaintColor = customize[20];

        chara->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Head).Value = appearance.Equipment[0];
        chara->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Body).Value = appearance.Equipment[1];
        chara->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Hands).Value = appearance.Equipment[2];
        chara->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Legs).Value = appearance.Equipment[3];
        chara->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Feet).Value = appearance.Equipment[4];
        chara->DrawData.Weapon(DrawDataContainer.WeaponSlot.MainHand).ModelId.Value = appearance.MainHand;
        chara->DrawData.Weapon(DrawDataContainer.WeaponSlot.OffHand).ModelId.Value = appearance.OffHand;

        // 替身的狀態特效尺寸是從 Customize 推的，客戶端自己生的物件推不出來 ⇒ 用來源端的真值。
        chara->Height = appearance.Height;
        chara->VfxScale = appearance.VfxScale;
    }
}
