namespace HduCourtBooker.Core;

/// <summary>
/// 触发时刻与目标日期的纯计算逻辑（无网络、无 IO），独立出来便于测试。
/// </summary>
public static class ScheduleMath
{
    /// <summary>计算本轮的目标日期（yyyy-MM-dd）。</summary>
    public static string ResolveTargetDate(AppConfig cfg, DateTime serverNow)
    {
        if (cfg.UseFixedDate && DateTime.TryParse(cfg.FixedDate, out var fixedDate))
        {
            return fixedDate.ToString("yyyy-MM-dd");
        }
        return serverNow.Date.AddDays(cfg.DayOffset).ToString("yyyy-MM-dd");
    }

    /// <summary>
    /// 解析每天的触发时刻。支持 "HH:mm"、"HH:mm:ss"、"HH:mm:ss.fff"、"H:mm:ss"（如 9:05:00）。
    /// 注意：必须用严格格式解析——<c>TimeSpan.TryParse("21:11:43")</c> 会把 "21:" 当成 **21 天**。
    /// </summary>
    public static TimeSpan ParseTriggerTime(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length > 0)
        {
            string[] formats =
            {
                @"hh\:mm", @"hh\:mm\:ss", @"hh\:mm\:ss\.f", @"hh\:mm\:ss\.ff", @"hh\:mm\:ss\.fff",
                @"h\:mm", @"h\:mm\:ss", @"h\:mm\:ss\.f", @"h\:mm\:ss\.ff", @"h\:mm\:ss\.fff",
            };
            if (TimeSpan.TryParseExact(t, formats, System.Globalization.CultureInfo.InvariantCulture, out var exact)
                && exact >= TimeSpan.Zero && exact < TimeSpan.FromDays(1))
            {
                return exact;
            }
        }
        return new TimeSpan(20, 0, 0);
    }

    /// <summary>返回下一次实际发起请求的本地时刻（已扣除提前量）。</summary>
    public static DateTime NextTriggerLocal(AppConfig cfg, DateTime localNow)
    {
        var tod = ParseTriggerTime(cfg.TriggerTime);
        var fireToday = localNow.Date.Add(tod).AddMilliseconds(-cfg.LeadMilliseconds);
        return localNow <= fireToday ? fireToday : fireToday.AddDays(1);
    }
}
