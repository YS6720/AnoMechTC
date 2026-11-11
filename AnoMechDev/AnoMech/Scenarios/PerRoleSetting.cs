using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AnoMech.Core.Game.Party;

namespace AnoMech.Scenarios;

// Non-generic surface so ScenarioSettingsSummary and ScenarioSettingsSync can handle any
// per-player setting without knowing its value type.
public interface IPerRoleSetting
{
    // One line for the lobby summary, or null when nothing is forced.
    string? Describe();

    // Drops values this build has no member for; a settings blob arrives from the host.
    void Sanitize();

    void Clear();
}

// Whether the eight seats are real right now. They are only meaningful while this client hosts
// a session: solo has one player whose seat follows their job and isn't known until the run
// starts, and a peer never builds scenario state at all.
//
// 我方：房主判定在 PerRole.Multiplayer.cs（接 MultiplayerSession 的 ClaimRole 座位），這裡只留
// 純邏輯，Tests 才能以 Compile Include 連結本檔；沒有那半邊時 ProbeHosting 被編譯器移除＝不在房主。
public static partial class PerRole
{
    public static bool SeatsActive
    {
        get
        {
            if (ForceSeats) return true;
            var hosting = false;
            ProbeHosting(ref hosting);
            return hosting;
        }
    }

    static partial void ProbeHosting(ref bool hosting);

    // Lets headless runs set every seat's overrides without hosting a session.
    internal static bool ForceSeats { get; set; }

    public static readonly PartyRole[] All =
    [
        PartyRole.MainTank, PartyRole.OffTank, PartyRole.RegenHealer, PartyRole.ShieldHealer,
        PartyRole.MeleeDpsA, PartyRole.MeleeDpsB, PartyRole.PhysRangedDps, PartyRole.CasterDps,
    ];

    // 上游借 ScenarioSettingsSummary.FormatValue（大廳摘要，不在 B9）；同一組規則：
    // bool 是／否、座位用 ShortName、enum 用成員名，其餘不受地區設定影響。
    internal static string FormatValue(object value) => value switch
    {
        bool b => b ? "是" : "否",
        PartyRole role => role.ShortName(),
        Enum e => e.ToString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}

// A setting every player has their own copy of -- your number, your wind, your tether -- as
// opposed to one roll the whole sim shares (which clone starts, which way they walk). The two
// kinds used to be conflated: a single value plus an "applies to" seat picker, so forcing one
// player's number silently unforced everyone else's.
//
// Solo and hosting are stored apart, so moving between them doesn't overwrite either.
public sealed class PerRoleSetting<T> : IPerRoleSetting where T : struct
{
    // The local player's own value, used when not hosting.
    public T? Mine { get; set; }

    // Indexed by PartyRole; null = that seat is left to the fight's own roll.
    public T?[] Seats { get; set; } = new T?[8];

    public T? this[PartyRole role]
    {
        get => (int)role >= 0 && (int)role < Seats.Length ? Seats[(int)role] : null;
        set { if ((int)role >= 0 && (int)role < Seats.Length) Seats[(int)role] = value; }
    }

    public bool AnySet => Mine.HasValue || Seats.Any(v => v.HasValue);

    public void Clear()
    {
        Mine = null;
        Seats = new T?[8];
    }

    // What the run applies, in seat order. Callers resolve conflicts themselves (two people
    // cannot both be number 3) and should say so in the log rather than silently dropping one.
    public IReadOnlyList<(PartyRole Role, T Value)> Resolve(PartyRole localPlayerRole)
    {
        if (!PerRole.SeatsActive)
            return Mine is { } mine ? [(localPlayerRole, mine)] : [];
        var result = new List<(PartyRole, T)>(8);
        for (var i = 0; i < 8 && i < Seats.Length; i++)
            if (Seats[i] is { } value) result.Add(((PartyRole)i, value));
        return result;
    }

    // The value for one seat as the run will see it, for a UI that wants to grey out a
    // dependent row.
    public T? Effective(PartyRole role) => PerRole.SeatsActive ? this[role] : Mine;

    public void Set(PartyRole role, T? value)
    {
        if (PerRole.SeatsActive) this[role] = value;
        else Mine = value;
    }

    public string? Describe()
    {
        if (!AnySet) return null;
        if (!PerRole.SeatsActive)
            return Mine is { } mine ? PerRole.FormatValue(mine) : null;
        var parts = PerRole.All
            .Where(r => this[r].HasValue)
            .Select(r => $"{r.ShortName()} {PerRole.FormatValue(this[r]!.Value)}")
            .ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    public void Sanitize()
    {
        if (Seats.Length != 8)
            Seats = Seats.Concat(new T?[8]).Take(8).ToArray();
        if (!typeof(T).IsEnum) return;
        if (Mine is { } mine && !Enum.IsDefined(typeof(T), mine)) Mine = null;
        for (var i = 0; i < 8; i++)
            if (Seats[i] is { } value && !Enum.IsDefined(typeof(T), value)) Seats[i] = null;
    }
}
