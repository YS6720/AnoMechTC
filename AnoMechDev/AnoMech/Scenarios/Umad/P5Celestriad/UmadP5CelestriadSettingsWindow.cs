using System;
using AnoMech.Core.Game.Party;
using Dalamud.Bindings.ImGui;

namespace AnoMech.Scenarios.Umad.P5Celestriad;

// Fight-wide rolls (which element doubles when, the sector layout, which towers light, each
// Catastrophic Choice's variant) in Draw;
// the per-seat debuff in DrawPerPlayer.
public sealed class UmadP5CelestriadSettingsWindow
{
    public UmadP5CelestriadStateOverrides Overrides { get; } = new();

    // Which seat the per-player rows are showing. UI state only; never broadcast.
    private PartyRole editingSeat = PartyRole.MainTank;

    private static readonly string[] DebuffLabels = ["隨機", "火", "冰", "雷", "Free（無 debuff）"];
    private static readonly string[] VariantLabels = ["隨機", "Aero（綠）", "Earth（棕）"];
    private static readonly string[] LitTowerLabels = ["隨機", "單屬性亮中間、重複屬性亮外側兩座"];
    private static readonly string[] OrderLabels =
    [
        "隨機",
        "火、冰、雷", "火、雷、冰",
        "冰、火、雷", "冰、雷、火",
        "雷、火、冰", "雷、冰、火",
    ];

    // Solo, the player's own debuff sits in this panel; a host assigns seats from the Multiplayer
    // window.
    public void Draw()
    {
        var solo = !PerRole.SeatsActive;
        if (ImGui.Button("自動"))
        {
            ResetAll();
            if (solo) Overrides.Debuff.Mine = null;
        }

        if (SettingsGrid.Begin("##p5celestriad"))
        {
            if (solo) DrawDebuff();

            SettingsGrid.Row("重複的屬性：");
            DrawOrder("##celdoubleorder", Overrides.DoubleOrder, v => Overrides.DoubleOrder = v);

            SettingsGrid.Row("扇區（從東北起）：");
            DrawOrder("##celsectororder", Overrides.SectorOrder, v => Overrides.SectorOrder = v);

            SettingsGrid.Row("亮起的塔：");
            var lit = Overrides.FixedLitTowers ? 1 : 0;
            SettingsGrid.ItemWidth(200);
            if (ImGui.Combo("##cellit", ref lit, LitTowerLabels, LitTowerLabels.Length))
                Overrides.FixedLitTowers = lit == 1;

            SettingsGrid.Row("第 1 組變化：");
            DrawVariant("##celset1", Overrides.Set1, v => Overrides.Set1 = v);

            SettingsGrid.Row("第 3 組變化：");
            DrawVariant("##celset3", Overrides.Set3, v => Overrides.Set3 = v);

            SettingsGrid.End();
        }
    }

    public void DrawPerPlayer()
    {
        if (ImGui.Button("自動")) Overrides.Debuff.Clear();
        if (SettingsGrid.Begin("##p5celestriadplayers"))
        {
            editingSeat = SettingsGrid.SeatRow("##celseat", editingSeat);
            DrawDebuff();
            SettingsGrid.ForcedRecapRow("已指定 debuff：", Overrides.Debuff);
            SettingsGrid.End();
        }
        SettingsGrid.ConflictRows(Overrides.Validate());
    }

    private void DrawDebuff()
    {
        SettingsGrid.Row($"{(PerRole.SeatsActive ? "" : "你的")} debuff：");
        var debuff = Overrides.Debuff.Effective(editingSeat) is { } d ? (int)d + 1 : 0;
        SettingsGrid.ItemWidth(180);
        if (ImGui.Combo("##celdebuff", ref debuff, DebuffLabels, DebuffLabels.Length))
            Overrides.Debuff.Set(editingSeat, debuff == 0 ? null : (CelestriadDebuff)(debuff - 1));
    }

    private static void DrawOrder(string id, CelestriadElementOrder? current, Action<CelestriadElementOrder?> set)
    {
        var index = current is { } o ? (int)o + 1 : 0;
        SettingsGrid.ItemWidth(200);
        if (ImGui.Combo(id, ref index, OrderLabels, OrderLabels.Length))
            set(index == 0 ? null : (CelestriadElementOrder)(index - 1));
    }

    private static void DrawVariant(string id, CatastrophicVariantOverride current, Action<CatastrophicVariantOverride> set)
    {
        var index = (int)current;
        SettingsGrid.ItemWidth(200);
        if (ImGui.Combo(id, ref index, VariantLabels, VariantLabels.Length))
            set((CatastrophicVariantOverride)index);
    }

    private void ResetAll()
    {
        Overrides.DoubleOrder = null;
        Overrides.SectorOrder = null;
        Overrides.FixedLitTowers = false;
        Overrides.Set1 = CatastrophicVariantOverride.Random;
        Overrides.Set3 = CatastrophicVariantOverride.Random;
    }
}
