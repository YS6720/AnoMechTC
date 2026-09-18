namespace AnoMech.Scenarios.Top.P5Omega;

/// <summary>
/// Standard 的複製，唯一差別是盤面：第二目標**至少一個是潛能量高漲 2 層**
///（維護者 2026-09-18 實戰回報；原本的抽法會擲出兩個第二目標都只有 1 層的盤，真副本沒有）。
/// 走位與其餘解法完全沿用 <see cref="TopP5OmegaAi"/>，原本的打法一行都沒動——
/// 兩個打法並存才能在同一次練習裡對照。
/// </summary>
public sealed class TopP5OmegaSecondTargetAi : TopP5OmegaAi
{
    public override string Name => "Standard（第二目標帶 2 層）";

    protected override void PrepareState() => state.EnsureSecondTargetHasDoubleDynamis();
}
