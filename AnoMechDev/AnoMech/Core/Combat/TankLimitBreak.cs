namespace AnoMech.Core.Combat;

// Tank LB action -> party status. These are display rules, never damage modifiers.
internal static class TankLimitBreak
{
    internal const uint GeneralActionId = 3;

    internal static bool IsTank(byte job) => job is 1 or 3 or 19 or 21 or 32 or 37;

    internal static bool TryGetStatus(uint action, byte job, out ushort status, out float duration)
    {
        status = (action, job) switch
        {
            (197, _) when IsTank(job) => 194, // Shield Wall
            (198, _) when IsTank(job) => 195, // Stronghold
            (199, 19) => 196,                // Last Bastion
            (4240, 21) => 863,               // Land Waker
            (4241, 32) => 864,               // Dark Force
            (17105, 37) => 1931,             // Gunmetal Soul
            _ => 0,
        };
        duration = action == 197 ? 10f : action == 198 ? 15f : 8f;
        return status != 0;
    }
}
