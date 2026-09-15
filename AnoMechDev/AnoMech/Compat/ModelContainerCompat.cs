// 台服 FFXIVClientStructs 0.0.6966 的 ModelContainer 沒有宣告 ModeAttributeFlags，
// 但那只是「沒命名」，不是「佈局不同」——兩版位移逐欄比對過：
//   ModelCharaId 0x10／ModelSkeletonId 0x14／ModelCharaId_2 0x18／
//   ModelSkeletonId_2 0x1C／UnscaledRadius 0x24
// 完全一致，0x20–0x23 這段空隙兩版都存在，國際服只是額外把 0x21 命名為
// ModelScaleId、0x22 命名為 ModeAttributeFlags。
//
// 更正（2026-08-18）：先前實作此檔時判定「國際服多出兩欄 ⇒ struct 佈局本身變動」
// 因而降級為 no-op，那個判斷是**錯的**——當時只看了欄位清單、沒有比對位移。
// 逐欄比對後確認佈局相同，故改為直接寫入。
//
// 完整對照表見 docs/upstream/TC-CS-BITFIELDS.md。台服升版後要重跑探針重驗。

using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace AnoMech.Compat;

internal static unsafe class ModelContainerCompat
{
    // Character.ModelContainer 起算
    private const int ModeAttributeFlagsOffset = 0x22;

    public static bool TryGetModeAttributeFlags(BattleChara* chara, out byte value)
    {
        value = 0;
        if (chara == null) return false;
        var container = &chara->Character.ModelContainer;
        value = *(byte*)((nint)container + ModeAttributeFlagsOffset);
        return true;
    }

    /// <summary>
    /// 設定 boss 視覺子網格（例：Omega-M 的盾＝0x00、無盾＝0x10）。
    /// 回傳是否套用成功——呼叫端據此決定要不要重建模型。
    /// </summary>
    public static bool TrySetModeAttributeFlags(BattleChara* chara, byte value)
    {
        if (chara == null) return false;
        var container = &chara->Character.ModelContainer;
        *(byte*)((nint)container + ModeAttributeFlagsOffset) = value;
        return true;
    }
}
