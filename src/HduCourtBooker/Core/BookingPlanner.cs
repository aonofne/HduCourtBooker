using HduCourtBooker.Api;

namespace HduCourtBooker.Core;

/// <summary>一个候选方案：某片场地的某个连续时间段。</summary>
public sealed record BookingPlan(int SiteId, List<int> Slots)
{
    public override string ToString() => $"{SiteId}号场[{string.Join(",", Slots)}]";
}

/// <summary>
/// 选场逻辑：按「时间段优先级 → 场地优先级」挑选一个可预约的组合。
/// 独立成类，便于离线单元测试。
///
/// ⚠️ 可预约时段 = <c>oci_time_list</c>（开放） 去掉 <c>book_time_list</c>（已被预约），
/// 语义见 <see cref="SiteSituationDto"/>。若改回直接用 BookTimeList，
/// 会把"已被约满"当成"可约"、把空场判成"不可约"（这是曾经的线上 bug）。
/// </summary>
public static class BookingPlanner
{
    /// <summary>
    /// 生成候选方案（按优先级排序）。返回空列表表示当前没有可约时段。
    /// </summary>
    public static List<BookingPlan> BuildCandidates(
        AppConfig cfg, SiteSituationResponse? situation)
    {
        var result = new List<BookingPlan>();
        if (situation?.SiteSituation is null || situation.SiteSituation.Count == 0) return result;

        var byId = new Dictionary<int, SiteSituationDto>();
        foreach (var s in situation.SiteSituation) byId[s.SiteId] = s;

        // 每片场地"此刻真正可约"的时段
        var available = situation.SiteSituation.ToDictionary(s => s.SiteId, s => s.AvailableSlots());

        var siteOrder = cfg.SitePreference.Count > 0
            ? cfg.SitePreference.Where(byId.ContainsKey).ToList()
            : situation.SiteSituation.Select(s => s.SiteId).OrderBy(x => x).ToList();

        int maxSlots = Math.Max(1, cfg.MaxSlotsPerBooking);

        // 1) 严格按偏好匹配
        foreach (var wanted in cfg.SlotPreference)
        {
            if (wanted.Count == 0) continue;
            var slots = wanted.Take(maxSlots).ToList();
            foreach (var siteId in siteOrder)
            {
                var free = available[siteId];
                if (free.Count == 0) continue;
                if (slots.All(free.Contains))
                {
                    result.Add(new BookingPlan(siteId, slots));
                }
            }
        }

        // 2) 退化：该场地任意可用时段，尽量取连续的一段
        foreach (var siteId in siteOrder)
        {
            var free = available[siteId];
            if (free.Count == 0) continue;
            var slots = LongestRun(free, maxSlots);
            if (!result.Any(p => p.SiteId == siteId && p.Slots.SequenceEqual(slots)))
            {
                result.Add(new BookingPlan(siteId, slots));
            }
        }

        return result;
    }

    /// <summary>首个候选（最优先的方案）。</summary>
    public static BookingPlan? BuildPlan(
        AppConfig cfg, SiteSituationResponse? situation) =>
        BuildCandidates(cfg, situation).FirstOrDefault();

    /// <summary>从可用下标里取最长的一段连续区间（不超过 max 个）。</summary>
    public static List<int> LongestRun(List<int> sorted, int max)
    {
        var list = sorted.Distinct().OrderBy(x => x).ToList();
        if (list.Count == 0) return new List<int>();

        var best = new List<int> { list[0] };
        var run = new List<int> { list[0] };
        for (int i = 1; i < list.Count; i++)
        {
            if (list[i] == run[^1] + 1)
            {
                run.Add(list[i]);
            }
            else
            {
                if (run.Count > best.Count) best = new List<int>(run);
                run = new List<int> { list[i] };
            }
        }
        if (run.Count > best.Count) best = run;
        return best.Take(max).ToList();
    }

    /// <summary>由时间段下标推出起止时间文本，例如 [10,11] -> 18:00 / 20:00。</summary>
    public static (string Start, string End) ResolveTimeRange(VenueInfoResponse info, List<int> slots)
    {
        if (info.TimeList.Count == 0 || slots.Count == 0) return ("", "");
        var start = info.TimeList[Math.Clamp(slots[0], 0, info.TimeList.Count - 1)];
        var endIdx = Math.Min(slots[^1] + 1, info.TimeList.Count - 1);
        return (start, info.TimeList[endIdx]);
    }
}
