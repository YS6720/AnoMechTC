using System.Collections.Generic;
using AnoMech.Core.Game.Party;

namespace AnoMech.Scenarios.Umad.P3BlackHole;

// 上游 anomek/AnoMech 1646a70 Scenarios/Umad/P3BlackHole/UmadP3BlackHoleState.cs 的座位求解段落，原樣搬出：
// 只依賴 PartyRole／PerRole，Tests 以 Compile Include 連結本檔與 UmadP3BlackHoleStateOverrides.cs
// 驗衝突檢查；需要 rng 的洗牌多載留在主檔。
public sealed partial class UmadP3BlackHoleState
{
    // Final-slot (post-swap) line number and Accretion, mirroring the per-index status
    // assignment in UmadP3BlackHoleScenario.Run_OtherDebuffs. Slots 0-3 hold the supports
    // (slot 3 never a tank), slots 4-7 the DPS; Swap(3,7) trades the two Accretion holders.
    private static readonly int[] SlotLine = [1, 2, 3, 1, 1, 2, 3, 2];          // 1/2/3 = First/Second/Third in line
    private static readonly bool[] SlotAccretion = [false, false, false, true, false, false, false, true];

    // The buff line (1/2/3) a final slot carries; YAN's head markers are picked by it.
    internal static int SlotLineOf(int slot) => SlotLine[slot];

    // Whether one swap can seat every request at once. The settings panel asks this (through
    // UmadP3BlackHoleStateOverrides.Validate) so an impossible combination is caught while the
    // host is still editing, not dropped mid-run.
    internal static bool CanSeat(Dictionary<PartyRole, (int? Line, bool? Accretion)> requests)
        => TrySeatRoles(false, requests, PerRole.All) != null
           || TrySeatRoles(true, requests, PerRole.All) != null;

    private static PartyRole[]? TrySeatRoles(bool swap, Dictionary<PartyRole, (int? Line, bool? Accretion)> requests, PartyRole[] order)
    {
        var slots = new PartyRole[8];
        var used = new bool[8];

        bool Fill(int slot)
        {
            if (slot == 8) return true;
            foreach (var role in order)
            {
                if (used[(int)role] || !SlotAllows(role, slot, swap)) continue;
                if (requests.TryGetValue(role, out var want)
                    && ((want.Line is { } line && SlotLine[slot] != line)
                        || (want.Accretion is { } accretion && SlotAccretion[slot] != accretion)))
                    continue;
                slots[slot] = role;
                used[(int)role] = true;
                if (Fill(slot + 1)) return true;
                used[(int)role] = false;
            }
            return false;
        }

        return Fill(0) ? slots : null;
    }

    // Which roles a final slot can hold. Swap(3,7) trades the one non-tank support slot with
    // the DPS block's Accretion slot, so it decides which of 3/7 is which.
    private static bool SlotAllows(PartyRole role, int slot, bool swap)
    {
        var dpsSlot = swap ? slot is 3 or >= 4 and <= 6 : slot >= 4;
        var nonTankSupportSlot = swap ? slot == 7 : slot == 3;
        if (dpsSlot) return role.IsDps();
        if (nonTankSupportSlot) return !role.IsDps() && !role.IsTank();
        return !role.IsDps();
    }
}
