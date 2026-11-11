namespace AnoMech.Core.Game.Ai;

// 隊友 AI 衝刺規則（上游 1646a70 AiManager 的 sprint／SprintSpeed；我方改成 opt-in）。
// sprint:false（預設）完全不衝刺，既有場景不變；sprint:true 沒給抵達時間＝一律衝刺；
// sprint:true 有抵達時間＝以跑速走不到才衝刺（上游有抵達時間時一律自動規劃，我方只在要求時才做）。
internal static class AiSprintRules
{
    public const float RunSpeed = 6f;
    public const float SprintSpeed = 8.3f;   // 上游實測值

    // available：距抵達時間還有幾秒；null＝沒給抵達時間。
    public static bool Applies(bool sprint, float distance, float? available)
    {
        if (!sprint) return false;
        if (available is not { } seconds) return true;
        return seconds <= 0f || distance / seconds > RunSpeed;
    }

    // 衝刺狀態的秒數：跑完全程再多 1 秒（上游同式）。
    public static float StatusSeconds(float distance) => distance / SprintSpeed + 1f;

    // 上游 1646a70 AiManager 的到點規劃（RunSpeed、MoveDeadlineSafetyMargin、PlanArrival），供 opt-in 的場景用。
    public const float UpstreamRunSpeed = 6.5f;
    public const float UpstreamArrivalMargin = 0.25f;

    // available：出發時刻到抵達時間的秒數（未扣邊際）。走得到就盡量晚出發、提早 UpstreamArrivalMargin 秒到；
    // 走不到就立刻衝刺。
    public static (bool Sprint, float Delay) PlanArrival(float distance, float available)
    {
        var slack = available - UpstreamArrivalMargin - distance / UpstreamRunSpeed;
        return slack >= 0f ? (false, slack) : (true, 0f);
    }
}
