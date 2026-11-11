// 國際服 FFXIVClientStructs 宣告了 ModelContainer.ModeAttributeFlags（0x22），直接用具名成員
// （上游 fork 點 v0.3.12.0 SimNpc.cs:56、SimEnemy.cs:194 同寫法）。台服版直寫位移，見 Compat/Server/Tc。

using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace AnoMech.Compat;

internal static unsafe class ModelContainerCompat
{
    public static bool TryGetModeAttributeFlags(BattleChara* chara, out byte value)
    {
        value = 0;
        if (chara == null) return false;
        value = chara->Character.ModelContainer.ModeAttributeFlags;
        return true;
    }

    /// <summary>
    /// 設定 boss 視覺子網格（例：Omega-M 的盾＝0x00、無盾＝0x10）。
    /// 回傳是否套用成功——呼叫端據此決定要不要重建模型。
    /// </summary>
    public static bool TrySetModeAttributeFlags(BattleChara* chara, byte value)
    {
        if (chara == null) return false;
        chara->Character.ModelContainer.ModeAttributeFlags = value;
        return true;
    }
}
