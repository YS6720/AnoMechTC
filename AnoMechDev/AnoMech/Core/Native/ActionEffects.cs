namespace AnoMech.Core.Native;

// 上游 anomek/AnoMech 1646a70 Core/Native/Implementations/Interop/ActionEffects.cs 的同名序號源。
// The server's per-action id, unique and non-zero: an EffectResult finds its ActionEffect by it.
// 我方 RotationSim 原本自帶一條從 0x40000000 起、先加再回傳的序號；起點與遞增方式沿用它
// （單獨用時數值不變），敵方帶效果的 ActionEffect 與玩家合成回包共用這一條，序號不交錯。
internal static class ActionEffects
{
    private static uint globalSequence = 0x40000000;

    public static uint NextGlobalSequence() => ++globalSequence;
}
