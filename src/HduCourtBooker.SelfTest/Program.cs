using System.Text;
using HduCourtBooker.Api;
using HduCourtBooker.Core;

namespace HduCourtBooker.SelfTest;

/// <summary>
/// 自检工具：对真实场馆接口跑一遍只读流程，验证接口契约、时间同步与抢约规划逻辑。
/// 用法：
///   HduCourtBooker.SelfTest                        # 只读自检（默认）
///   HduCourtBooker.SelfTest --token &lt;token&gt;       # 额外验证 token 是否有效
///   HduCourtBooker.SelfTest --plan                 # 打印抢约规划结果
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string? token = GetArg(args, "--token");
        bool planOnly = args.Contains("--plan");

        // 独立功能：从钉钉/浏览器本地存储中自动提取 token
        if (args.Contains("--extract-credentials"))
        {
            return ExtractCredentials();
        }

        var dir = Path.Combine(AppContext.BaseDirectory, "selftest-data");
        var log = new Logger(Path.Combine(dir, "logs"));
        var client = new GymbookClient();
        var sync = new TimeSync(client, log);
        int failures = 0;

        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
            if (!ok) failures++;
        }

        Console.WriteLine("=== 杭电羽毛球预约 自检 ===");
        Console.WriteLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        // 0) 连通性预检（TLS/网络问题单独提示，避免一堆误导性 FAIL）
        try
        {
            await client.GetServerTimeMsAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine("\n[!] 无法连接场馆服务器，后续检查已跳过。");
            Console.WriteLine($"    错误：{ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is not null)
            {
                Console.WriteLine($"    内层：{ex.InnerException.Message}");
            }
            Console.WriteLine("\n可能原因：");
            Console.WriteLine("  - 未连接校园网/VPN（杭电内网站点）；");
            Console.WriteLine("  - 系统时间不正确导致 TLS 失败；");
            Console.WriteLine("  - 杀毒软件/代理拦截 TLS。");
            Console.WriteLine("\n=== 自检结束：1 项失败 ===");
            return 1;
        }

        // 1) 服务器时间
        Console.WriteLine("\n[1] 服务器时间与时钟校准");
        var offset = await sync.SyncAsync(5);
        Check("post_server_time 可达", sync.LastRttMs > 0, $"往返 {sync.LastRttMs:F0} ms");
        Check("时钟偏移合理(|offset| < 60s)", Math.Abs(offset) < 60000, $"offset={offset:+0;-0;0} ms");

        var serverNow = sync.ServerNow;

        // 2) 场地列表
        Console.WriteLine("\n[2] 场地列表 choose_venue");
        var venues = await client.GetVenueListAsync("badminton");
        Check("返回场地列表", venues is { VenueList.Count: > 0 },
            venues is null ? "" : string.Join(" / ", venues.VenueList.Select(v => v.VenueName)));
        string venueName = venues?.VenueList.FirstOrDefault()?.VenueName ?? "综合馆羽毛球";

        // 3) 时间表与可预约天数
        Console.WriteLine("\n[3] 时间表 post_venue_info");
        var targetDate = serverNow.Date.AddDays(2).ToString("yyyy-MM-dd");
        var info = await client.GetVenueInfoAsync("badminton", venueName, targetDate);
        Check("返回时间表", info is { TimeList.Count: > 0 },
            info is null ? "" : $"{info.TimeList.Count} 段：{string.Join(",", info.TimeList)}");
        Check("可预约天数 >= 2", (info?.DaysCanAppoint ?? 0) >= 2, $"days_can_appoint={info?.DaysCanAppoint}");

        // 4) 场地图
        // ⚠️ 语义：oci_time_list = 已开放；book_time_list = 已被预约；可约 = 开放 - 已约
        Console.WriteLine("\n[4] 场地图 post_site_situation");
        var situation = await client.GetSiteSituationAsync(targetDate, venueName);
        int sites = situation?.SiteSituation.Count ?? 0;
        int openSlots = situation?.SiteSituation.Sum(s => s.OciTimeList.Count) ?? 0;
        int bookedSlots = situation?.SiteSituation.Sum(s => s.BookedCount) ?? 0;
        int freeSlots = situation?.SiteSituation.Sum(s => s.AvailableCount) ?? 0;
        Check("返回场地图", sites > 0,
            $"{sites} 片场地（目标日期 {targetDate}）");
        Check("字段语义自洽：可约 = 开放 - 已约",
            freeSlots == openSlots - bookedSlots,
            $"开放 {openSlots} 段，已约 {bookedSlots} 段，可约 {freeSlots} 段");

        // 5) 预约窗口：超出可提前天数时应不可约
        var maxDaysAhead = info?.EffectiveMaxDaysAhead ?? 2;
        var beyond = serverNow.Date.AddDays(maxDaysAhead + 1).ToString("yyyy-MM-dd");
        var sBeyond = await client.GetSiteSituationAsync(beyond, venueName);
        int freeBeyond = sBeyond?.SiteSituation.Sum(s => s.AvailableCount) ?? 0;
        Check($"T+{maxDaysAhead + 1}（超出可提前 {maxDaysAhead} 天）不可约",
            freeBeyond == 0 || sBeyond is null,
            $"{beyond} 可约 {freeBeyond} 段");

        // 6) 逐日可约情况：直接回答"为什么某天全都不可约"
        Console.WriteLine("\n[6] 逐日可约情况（本馆最多可提前 " + maxDaysAhead + " 天）");
        Console.WriteLine($"    {"日期",-12}{"偏移",-7}{"已开放",-9}{"已被预约",-11}{"可预约",-9}状态");
        for (int dayOff = 0; dayOff <= maxDaysAhead + 1; dayOff++)
        {
            var d = serverNow.Date.AddDays(dayOff).ToString("yyyy-MM-dd");
            var s = dayOff == 2 && situation is not null && d == targetDate
                ? situation
                : await client.GetSiteSituationAsync(d, venueName);
            if (s is null) continue;
            int open = s.SiteSituation.Sum(x => x.OciTimeList.Count);
            int booked = s.SiteSituation.Sum(x => x.BookedCount);
            int free = s.SiteSituation.Sum(x => x.AvailableCount);
            var state = free > 0 ? "可预约" : (open > 0 ? "已全部约满" : "尚未开放（等 20:00 放号）");
            Console.WriteLine($"    {d,-12}T+{dayOff,-5}{open,-9}{booked,-11}{free,-9}{state}");
        }

        // 7) 抢约规划
        Console.WriteLine("\n[7] 抢约规划（模拟 20:00 抢后天）");
        if (situation is not null && info is not null)
        {
            var (siteId, slots, desc) = Plan(situation, info, new List<List<int>>
            {
                new() { 10, 11 }, new() { 11, 12 }, new() { 10 },
            });
            // 只有确实存在可约时段时才要求规划成功
            if (freeSlots > 0)
            {
                Check("能规划出候选", siteId > 0, desc);
            }
            else
            {
                Console.WriteLine($"  [SKIP] 目标日期当前没有可约时段（可约 0 段），跳过规划断言");
                Console.WriteLine($"         通常说明：{targetDate} 还没到放号时间（每天 20:00 放出）。");
            }
        }

        // 7) token 校验（可选）
        if (!string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("\n[6] token 校验");
            client.Token = token;
            try
            {
                var order = new OrderRequest
                {
                    OrderData = new OrderData
                    {
                        OpenId = GetArg(args, "--openid") ?? "",
                        Nickname = "selftest", Phone = "",
                        Date = targetDate, VenueName = venueName, VenueType = "badminton",
                        SiteId = 1, TimeList = new List<int> { 0 },
                        StartTime = info?.TimeList.FirstOrDefault() ?? "08:00",
                        EndTime = info?.TimeList.Skip(1).FirstOrDefault() ?? "09:00",
                    }
                };
                await client.SubmitBookInfoAsync(order);
                Check("token 通过鉴权", true, "服务端已接受请求（未真正占场）");
            }
            catch (GymbookException ex) when (ex.StatusCode == 401)
            {
                Check("token 通过鉴权", false, ex.Message);
            }
            catch (GymbookException ex)
            {
                Check("token 通过鉴权（非 401 视为通过）", true, ex.Message);
            }
        }
        else if (planOnly)
        {
            Console.WriteLine("\n（未提供 --token，跳过鉴权校验）");
        }

        Console.WriteLine($"\n=== 自检结束：{(failures == 0 ? "全部通过" : failures + " 项失败")} ===");
        client.Dispose();
        return failures == 0 ? 0 : 1;
    }

    /// <summary>从钉钉/Chromium 本地存储自动提取登录凭据。</summary>
    private static int ExtractCredentials()
    {
        Console.WriteLine("=== 自动获取登录凭据（扫描钉钉/Edge/Chrome 本地存储）===\n");
        var best = CredentialExtractor.FindBest(out var diag);
        Console.WriteLine("扫描过程：");
        Console.WriteLine(diag);

        if (best is null)
        {
            Console.WriteLine("未找到任何 token。");
            Console.WriteLine("请先在钉钉里打开一次「场馆预约」（从工作台进入），再重新运行本命令。");
            return 1;
        }

        Console.WriteLine("=== 找到凭据 ===");
        Console.WriteLine($"来源      : {best.Source}");
        Console.WriteLine($"来源路径  : {best.SourcePath}");
        Console.WriteLine($"openid    : {best.OpenId}");
        Console.WriteLine($"昵称      : {(best.Nickname.Length > 0 ? best.Nickname : "(未识别，可不填)")}");
        Console.WriteLine($"手机      : {(best.Phone.Length > 0 ? best.Phone : "(未识别，可不填)")}");
        Console.WriteLine($"token 过期: {best.ExpiryLocal:yyyy-MM-dd HH:mm:ss}（剩余 {best.Remaining.TotalHours:F1} 小时）");
        Console.WriteLine($"状态      : {(best.IsUsable ? "有效" : "已过期")}");
        Console.WriteLine($"token     : {best.Token}");

        if (!best.IsUsable)
        {
            Console.WriteLine("\n注意：该 token 已过期。请在钉钉里重新打开一次「场馆预约」页面，再运行本命令。");
            return 2;
        }

        Console.WriteLine("\n把这个 token 与 openid 填入主程序即可（或直接点主程序的「从钉钉自动获取」）。");
        return 0;
    }

    /// <summary>
    /// 与 BookingScheduler 相同的选场逻辑（独立实现用于交叉验证）。
    /// ⚠️ 可约 = oci_time_list（开放）- book_time_list（已被预约）。
    /// </summary>
    private static (int SiteId, List<int> Slots, string Desc) Plan(
        SiteSituationResponse situation, VenueInfoResponse info, List<List<int>> preference)
    {
        foreach (var wanted in preference)
        {
            foreach (var site in situation.SiteSituation.OrderBy(s => s.SiteId))
            {
                var free = site.AvailableSlots();
                if (wanted.All(free.Contains))
                {
                    var times = string.Join("~", new[]
                    {
                        info.TimeList[wanted[0]],
                        info.TimeList[Math.Min(wanted[^1] + 1, info.TimeList.Count - 1)],
                    });
                    return (site.SiteId, wanted, $"{site.SiteId} 号场 {times}（优先段 {string.Join(",", wanted)}）");
                }
            }
        }
        var fallback = situation.SiteSituation.FirstOrDefault(s => s.AvailableCount > 0);
        if (fallback is null) return (0, new List<int>(), "没有任何可约时段");
        var one = fallback.AvailableSlots().Take(1).ToList();
        return (fallback.SiteId, one, $"退化候选：{fallback.SiteId} 号场 {info.TimeList[one[0]]}");
    }

    private static string? GetArg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
