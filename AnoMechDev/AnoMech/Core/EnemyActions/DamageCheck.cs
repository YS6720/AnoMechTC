using System;
using System.Linq;

namespace AnoMech.Core.EnemyActions;

// 移植自上游 anomek/AnoMech 1646a70。判定拆成純函式（目標的狀態、是否坦克、減傷檢查都由呼叫端給），
// 可單元測試；接 SimCharacter 的轉接在 DamageEffect（EnemyActionEffect.cs）。死因說明改繁中。
internal static class DamageCheck
{
    // Null when the hit is survivable, otherwise why it kills ("" when it simply does).
    // stacksOf: the target's stacks of a status, null when it carries none (a carried status can
    // hold 0 stacks). survives: whether the target lives through a hit needing that much
    // mitigation (spending its shields); null when its mitigation isn't checked, which always survives.
    public static string? LethalCause(VulnSpec spec, Severity severity, Func<ushort, int?> stacksOf, bool isTank,
        Func<float, bool>? survives, Func<ushort, string> statusName)
    {
        if (spec.Protections.Any(id => stacksOf(id) != null)) return null;
        if (severity.Kind == SeverityKind.Lethal) return MissingProtection(spec, statusName) ?? "";
        var vuln = CarriedVulnerability(spec, stacksOf);
        if (vuln is { RequiredMitigation: null }) return "帶有易傷狀態";
        if (severity.Kind == SeverityKind.TankBuster && !isTank) return "非坦克承受死刑";
        var required = MathF.Max(severity.MinMitigation, vuln?.RequiredMitigation ?? 0f);
        if (required <= 0f || survives is null || survives(required)) return null;
        return vuln != null ? "帶有易傷狀態且減傷不足" : "減傷不足";
    }

    private static string? MissingProtection(VulnSpec spec, Func<ushort, string> statusName)
        => spec.Protections.Count > 0 ? $"沒有{statusName(spec.Protections[0])}" : null;

    private static Vulnerability? CarriedVulnerability(VulnSpec spec, Func<ushort, int?> stacksOf)
    {
        foreach (var vuln in spec.Vulnerabilities)
            if (stacksOf(vuln.StatusId) is { } stacks && stacks >= vuln.MinStacks)
                return vuln;
        return null;
    }
}
