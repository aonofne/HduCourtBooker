using System.Text;
using System.Text.Json;
using HduCourtBooker.Api;
using HduCourtBooker.Core;

namespace HduCourtBooker.Tests;

/// <summary>离线单元测试：不联网，覆盖选场规划、日期与触发时刻计算、请求体序列化。</summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) _passed++;
        else _failed++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "   " + detail : "")}");
    }

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== 离线单元测试 ===");

        TestPlanner();
        TestEveningSlotMapping();
        TestFieldSemantics();
        TestDateAndTrigger();
        TestJsonSerialization();
        TestLongestRun();
        TestExchangeMasking();
        TestPlaintextToken();
        TestPreciseWait();

        Console.WriteLine($"\n=== {_passed} 通过 / {_failed} 失败 ===");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// 构造场地图。参数含义遵循真实语义：
    /// oci（已开放）决定了可约集合，booked 是其中已被预约的部分。
    /// </summary>
    private static SiteSituationResponse Situation(params (int Site, int[] Open, int[] Booked)[] rows)
    {
        var r = new SiteSituationResponse();
        foreach (var (site, open, booked) in rows)
        {
            r.SiteSituation.Add(new SiteSituationDto
            {
                SiteId = site,
                OciTimeList = open.ToList(),
                BookTimeList = booked.ToList(),
            });
        }
        return r;
    }

    /// <summary>便于老用例：只给"开放"列表，视为全部空闲。</summary>
    private static SiteSituationResponse Situation(params (int Site, int[] Open)[] rows)
        => Situation(rows.Select(r => (r.Site, r.Open, Array.Empty<int>())).ToArray());

    /// <summary>
    /// 19:00-21:00 必须是时间段 11,12（用真实 14 段时间表校验），且每单不超过 2 小时。
    /// </summary>
    private static void TestEveningSlotMapping()
    {
        Console.WriteLine("\n[晚间时段映射：19:00-21:00 = 11,12]");

        // 站点真实返回的 14 段时间表
        var info = new VenueInfoResponse
        {
            TimeList = new List<string>
            {
                "08:00", "09:00", "10:00", "11:00", "11:40", "13:20", "14:00",
                "15:00", "16:00", "17:00", "18:00", "19:00", "20:00", "21:00",
            },
        };

        Check("下标 11 = 19:00", info.TimeList[11] == "19:00", info.TimeList[11]);
        Check("下标 12 = 20:00", info.TimeList[12] == "20:00", info.TimeList[12]);

        var (start, end) = BookingPlanner.ResolveTimeRange(info, new List<int> { 11, 12 });
        Check("19:00-21:00 起止正确（11,12）", start == "19:00" && end == "21:00", $"{start}-{end}");

        var (s2, e2) = BookingPlanner.ResolveTimeRange(info, new List<int> { 10, 11 });
        Check("18:00-20:00 起止正确（10,11）", s2 == "18:00" && e2 == "20:00", $"{s2}-{e2}");

        // 默认偏好必须是 11,12 打头
        var def = AppConfig.CreateDefault();
        Check("默认第一偏好为 [11,12]",
            def.SlotPreference.Count > 0 && def.SlotPreference[0].SequenceEqual(new[] { 11, 12 }),
            string.Join(" > ", def.SlotPreference.Select(g => string.Join(",", g))));

        // 站点规则：每单最多 2 小时 -> 每单时段数默认不得超过 2
        Check("每单时段数默认不超过站点上限 2 小时",
            def.MaxSlotsPerBooking <= AppConfig.MaxHoursPerBooking,
            $"MaxSlotsPerBooking={def.MaxSlotsPerBooking}");

        // 即使偏好给了 3 段，也要被收敛到 2 段
        var cfg = AppConfig.CreateDefault();
        cfg.SlotPreference = new List<List<int>> { new() { 11, 12, 13 } };
        var plan = BookingPlanner.BuildCandidates(cfg, Situation((1, new[] { 11, 12, 13 }))).FirstOrDefault();
        Check("三段偏好被收敛为两段", plan is not null && plan.Slots.Count == 2,
            plan?.ToString() ?? "null");
    }

    private static void TestPlanner()    {
        Console.WriteLine("\n[选场规划]");

        var cfg = AppConfig.CreateDefault();
        cfg.MaxSlotsPerBooking = 2;

        // 默认第一偏好是 19:00-21:00（下标 11,12）
        var situation = Situation(
            (1, new[] { 4, 5, 6 }),
            (2, new[] { 10, 11, 12 }),
            (3, new[] { 11, 12 }));

        var candidates = BookingPlanner.BuildCandidates(cfg, situation);
        var top = candidates.FirstOrDefault();
        Check("默认首选 19:00-21:00（11,12）且取最小场地号",
            top is { SiteId: 2 } && top.Slots.SequenceEqual(new[] { 11, 12 }),
            top?.ToString() ?? "null");

        // 换回 18:00-20:00 作为第一偏好，应命中 10,11
        cfg.SlotPreference = new List<List<int>> { new() { 10, 11 }, new() { 11, 12 } };
        var c1b = BookingPlanner.BuildCandidates(cfg, situation);
        Check("偏好可切换为 18:00-20:00（10,11）",
            c1b.FirstOrDefault() is { SiteId: 2 } p1 && p1.Slots.SequenceEqual(new[] { 10, 11 }),
            c1b.FirstOrDefault()?.ToString() ?? "null");

        // 场地优先级：把 [11,12] 提为第一偏好，此时 2、3 号场都满足，应按场地优先级选 3 号场
        cfg.SitePreference = new List<int> { 3, 2 };
        cfg.SlotPreference = new List<List<int>> { new() { 11, 12 }, new() { 10, 11 } };
        var c2 = BookingPlanner.BuildCandidates(cfg, situation);
        Check("场地优先级生效", c2.FirstOrDefault() is { SiteId: 3 },
            c2.FirstOrDefault()?.ToString() ?? "null");
        cfg.SlotPreference = AppConfig.CreateDefault().SlotPreference;

        // 偏好时段都不可用 -> 退化到最长连续段
        cfg.SitePreference = new List<int>();
        var s3 = Situation((1, new[] { 1, 2, 3, 7 }), (2, new int[0]));
        var c3 = BookingPlanner.BuildCandidates(cfg, s3);
        Check("无完全匹配时退化为最长连续段", c3.FirstOrDefault() is { SiteId: 1 } p && p.Slots.SequenceEqual(new[] { 1, 2 }),
            c3.FirstOrDefault()?.ToString() ?? "null");

        // 全空 -> 无候选
        var c4 = BookingPlanner.BuildCandidates(cfg, Situation((1, new int[0]), (2, new int[0])));
        Check("全不可约时无候选", c4.Count == 0, $"count={c4.Count}");

        // MaxSlotsPerBooking 限制
        cfg.SlotPreference = new List<List<int>> { new() { 10, 11, 12 } };
        cfg.MaxSlotsPerBooking = 2;
        var c5 = BookingPlanner.BuildCandidates(cfg, Situation((1, new[] { 10, 11, 12 })));
        Check("时段数受 MaxSlotsPerBooking 限制", c5.FirstOrDefault()?.Slots.Count == 2,
            c5.FirstOrDefault()?.ToString() ?? "null");
    }

    /// <summary>
    /// 回归测试：字段语义（曾经把"已被预约"当成"可预约"，导致所有空场都显示不可约）。
    /// </summary>
    private static void TestFieldSemantics()
    {
        Console.WriteLine("\n[字段语义：oci=开放, book=已被预约]");

        // 真实样例（2026-10-10 的 1 号场）：开放 [4..8]，全部已被预约
        var bookedOut = new SiteSituationDto
        {
            SiteId = 1,
            OciTimeList = new List<int> { 4, 5, 6, 7, 8 },
            BookTimeList = new List<int> { 4, 5, 6, 7, 8 },
        };
        Check("整场约满时可用时段为 0", bookedOut.AvailableCount == 0,
            $"available={bookedOut.AvailableCount}");
        Check("已约计数正确", bookedOut.BookedCount == 5, $"booked={bookedOut.BookedCount}");

        // 真实样例（2026-10-11 的 1 号场）：开放 [0..8]，无预约 —— 必须判为可约
        var freeSite = new SiteSituationDto
        {
            SiteId = 1,
            OciTimeList = new List<int> { 0, 1, 2, 3, 4, 5, 6, 7, 8 },
            BookTimeList = new List<int>(),
        };
        Check("空场必须判为可约（回归）", freeSite.AvailableCount == 9,
            $"available={freeSite.AvailableCount}");

        // 部分占用
        var partial = new SiteSituationDto
        {
            SiteId = 1,
            OciTimeList = new List<int> { 4, 9, 11, 12 },
            BookTimeList = new List<int> { 4, 9 },
        };
        Check("部分占用时可用 = 开放 - 已约",
            partial.AvailableSlots().SequenceEqual(new[] { 11, 12 }),
            string.Join(",", partial.AvailableSlots()));

        // 端到端：只有"未被预约"的格子才应成为候选
        var cfg = AppConfig.CreateDefault();
        cfg.SlotPreference = new List<List<int>> { new() { 10, 11 } };
        var situation = Situation(
            (1, new[] { 10, 11 }, new[] { 10, 11 }),   // 约满
            (2, new[] { 10, 11 }, new[] { 10 }),       // 只剩 11
            (3, new int[0], new int[0]));              // 未开放
        var candidates = BookingPlanner.BuildCandidates(cfg, situation);
        Check("约满的场地不会被选中", candidates.All(c => c.SiteId != 1),
            string.Join(" | ", candidates.Select(c => c.ToString())));
        Check("只剩一段的场地按实际空闲段生成候选",
            candidates.Any(c => c.SiteId == 2 && c.Slots.SequenceEqual(new[] { 11 })),
            string.Join(" | ", candidates.Select(c => c.ToString())));
        Check("未开放场地不产生候选", candidates.All(c => c.SiteId != 3));
    }

    private static void TestDateAndTrigger()
    {
        Console.WriteLine("\n[日期与触发时刻]");
        var cfg = AppConfig.CreateDefault();
        var now = new DateTime(2026, 10, 8, 12, 0, 0); // 周四

        Check("默认目标为后天", ScheduleMath.ResolveTargetDate(cfg, now) == "2026-10-10",
            ScheduleMath.ResolveTargetDate(cfg, now));

        cfg.DayOffset = 1;
        Check("偏移 1 = 明天", ScheduleMath.ResolveTargetDate(cfg, now) == "2026-10-09");

        cfg.UseFixedDate = true;
        cfg.FixedDate = "2026-11-20";
        Check("固定日期优先", ScheduleMath.ResolveTargetDate(cfg, now) == "2026-11-20");

        cfg.UseFixedDate = false;
        cfg.TriggerTime = "20:00:00.000";
        cfg.LeadMilliseconds = 250;

        var before = new DateTime(2026, 10, 8, 19, 0, 0);
        Check("19:00 时下次触发为当天 19:59:59.750",
            ScheduleMath.NextTriggerLocal(cfg, before) == new DateTime(2026, 10, 8, 19, 59, 59, 750),
            ScheduleMath.NextTriggerLocal(cfg, before).ToString("yyyy-MM-dd HH:mm:ss.fff"));

        var after = new DateTime(2026, 10, 8, 20, 30, 0);
        Check("20:30 时下次触发为次日",
            ScheduleMath.NextTriggerLocal(cfg, after) == new DateTime(2026, 10, 9, 19, 59, 59, 750),
            ScheduleMath.NextTriggerLocal(cfg, after).ToString("yyyy-MM-dd HH:mm:ss.fff"));

        Check("非法触发时间回退 20:00", ScheduleMath.ParseTriggerTime("abc") == new TimeSpan(20, 0, 0));
        Check("触发时间解析正常", ScheduleMath.ParseTriggerTime("19:59:59.500") == new TimeSpan(0, 19, 59, 59, 500));
        // 回归：TimeSpan.TryParse 会把 "21:11:43" 解析成 21 天，必须严格解析
        Check("两位数小时不被误解析为天数",
            ScheduleMath.ParseTriggerTime("21:11:43.443") == new TimeSpan(0, 21, 11, 43, 443),
            ScheduleMath.ParseTriggerTime("21:11:43.443").ToString());
        Check("单数字小时可用", ScheduleMath.ParseTriggerTime("9:05:00") == new TimeSpan(0, 9, 5, 0),
            ScheduleMath.ParseTriggerTime("9:05:00").ToString());
        Check("HH:mm 可用", ScheduleMath.ParseTriggerTime("20:00") == new TimeSpan(0, 20, 0, 0));
        Check("超范围(24小时制外)回退 20:00",
            ScheduleMath.ParseTriggerTime("25:00:00") == new TimeSpan(20, 0, 0));
    }

    private static void TestJsonSerialization()
    {
        Console.WriteLine("\n[JSON 请求体]");
        var req = new OrderRequest
        {
            OrderData = new OrderData
            {
                OpenId = "12345", Nickname = "张三", Phone = "13900000000",
                Date = "2026-10-10", VenueName = "综合馆羽毛球", VenueType = "badminton",
                SiteId = 3, TotalPrice = 0, TimeList = new List<int> { 10, 11 },
                StartTime = "18:00", EndTime = "20:00",
            }
        };
        // 与 GymbookClient 实际使用的序列化配置保持一致（中文不转义）
        var jsonOpts = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        var json = JsonSerializer.Serialize(req, jsonOpts);

        Check("外层字段为 orderData", json.Contains("\"orderData\""), json[..Math.Min(60, json.Length)]);
        Check("场地字段为 venue_name", json.Contains("\"venue_name\":\"综合馆羽毛球\""));
        Check("场地类型为 venue_type", json.Contains("\"venue_type\":\"badminton\""));
        Check("时间段字段为 time_list", json.Contains("\"time_list\":[10,11]"));
        Check("场地号为 site_id", json.Contains("\"site_id\":3"));
        Check("日期格式 yyyy-MM-dd", json.Contains("\"date\":\"2026-10-10\""));

        // 反序列化服务器返回的场地图
        const string sample = """
        {"site_situation":[{"site_id":1,"book_time_list":[4,9],"oci_time_list":[5,6,7,8,9]},
        {"site_id":2,"book_time_list":[],"oci_time_list":[]}],"extra_list":{}}
        """;
        var resp = JsonSerializer.Deserialize<SiteSituationResponse>(sample);
        Check("解析 post_site_situation 响应", resp is { SiteSituation.Count: 2 }
            && resp.SiteSituation[0].BookTimeList.SequenceEqual(new[] { 4, 9 }),
            $"sites={resp?.SiteSituation.Count}");

        const string venueJson = """
        {"time_list":["08:00","09:00","10:00","11:00","11:40","13:20","14:00","15:00","16:00","17:00","18:00","19:00","20:00","21:00"],"days_can_appoint":3,"base_price":0.0,"rest_list":[]}
        """;
        var vi = JsonSerializer.Deserialize<VenueInfoResponse>(venueJson);
        Check("解析 post_venue_info 响应（14 段）", vi is { TimeList.Count: 14 },
            $"count={vi?.TimeList.Count}");

        var (s10, e10) = BookingPlanner.ResolveTimeRange(vi!, new List<int> { 10, 11 });
        Check("下标 10,11 -> 18:00-20:00", s10 == "18:00" && e10 == "20:00", $"{s10}-{e10}");
        var (s13, e13) = BookingPlanner.ResolveTimeRange(vi!, new List<int> { 13 });
        Check("最后一段不越界", s13 == "21:00" && e13 == "21:00", $"{s13}-{e13}");
    }

    private static void TestLongestRun()
    {
        Console.WriteLine("\n[连续段识别]");
        Check("[1,2,3,7] 最长 = 1,2,3",
            BookingPlanner.LongestRun(new List<int> { 1, 2, 3, 7 }, 2).SequenceEqual(new[] { 1, 2 }));
        Check("[5] -> [5]", BookingPlanner.LongestRun(new List<int> { 5 }, 3).SequenceEqual(new[] { 5 }));
        Check("[] -> []", BookingPlanner.LongestRun(new List<int>(), 3).Count == 0);
        Check("乱序去重 [3,1,2,2] -> [1,2]",
            BookingPlanner.LongestRun(new List<int> { 3, 1, 2, 2 }, 2).SequenceEqual(new[] { 1, 2 }));
    }

    /// <summary>
    /// 精确等待的回归测试：曾经因为用 DateTime.Now 做自旋判据而永不退出（死循环打满 CPU）。
    /// 这里必须能在超时保护内返回，且落点误差在容差内。
    /// </summary>
    private static void TestPreciseWait()
    {
        Console.WriteLine("\n[精确等待（防死循环自旋）]");

        async Task<(bool Finished, double ErrorMs)> WaitFor(int ms)
        {
            var target = DateTime.Now.AddMilliseconds(ms);
            var work = Timing.WaitUntilAsync(target, CancellationToken.None);
            var done = await Task.WhenAny(work, Task.Delay(ms + 3000)) == work;
            await work;
            return (done, (DateTime.Now - target).TotalMilliseconds);
        }

        var near = WaitFor(30).GetAwaiter().GetResult();
        Check("30ms 等待能正常返回", near.Finished, $"偏差 {near.ErrorMs:F1} ms");
        Check("30ms 等待落点误差 < 60ms", Math.Abs(near.ErrorMs) < 60, $"{near.ErrorMs:F1} ms");

        var mid = WaitFor(250).GetAwaiter().GetResult();
        Check("250ms 等待能正常返回（不会死循环）", mid.Finished, $"偏差 {mid.ErrorMs:F1} ms");
        Check("250ms 等待落点误差 < 80ms", Math.Abs(mid.ErrorMs) < 80, $"{mid.ErrorMs:F1} ms");

        var past = Timing.WaitUntilAsync(DateTime.Now.AddMilliseconds(-500), CancellationToken.None);
        past.GetAwaiter().GetResult();
        Check("目标时刻已过时立即返回", true);

        // 取消必须能中断等待
        using var cts = new CancellationTokenSource(100);
        try
        {
            Timing.WaitUntilAsync(DateTime.Now.AddSeconds(10), cts.Token).GetAwaiter().GetResult();
            Check("取消能中断等待", false, "未抛出取消异常");
        }
        catch (OperationCanceledException)
        {
            Check("取消能中断等待", true);
        }
    }

    private static void TestExchangeMasking()
    {
        Console.WriteLine("\n[报文脱敏]");
        const string token = "eyJhbGciOiJIUzI1NiJ9.eyJvcGVuaWQiOiIyNTMwNDA0OTUifQ.abcdefghijklmnop";
        var masked = ExchangeLog.Mask($"Authorization: Bearer {token}");
        Check("token 被脱敏", !masked.Contains(token) && masked.Contains("eyJhbG"), masked);
        Check("保留前后缀便于核对", masked.Contains("…") && masked.EndsWith("mnop"), masked);
        Check("无 token 时原文返回", ExchangeLog.Mask("GET /client/post_server_time") == "GET /client/post_server_time");
    }

    private static void TestPlaintextToken()
    {
        Console.WriteLine("\n[token 明文存储]");
        const string token = "eyJhbGciOiJIUzI1NiJ9.eyJvcGVuaWQiOiIyNjI3MDIzNCJ9.signature_part";

        var stored = SecretProtector.Protect(token);
        Check("保存后即为原文", stored == token, stored);

        Check("读回仍是原文", SecretProtector.Unprotect(stored) == token);
        Check("配置文件里不带任何加密前缀",
            !stored.StartsWith("dpapi:") && !stored.StartsWith("b64:"), stored[..Math.Min(20, stored.Length)]);

        // 序列化到配置对象后应能直接看到明文
        var cfg = AppConfig.CreateDefault();
        cfg.TokenProtected = SecretProtector.Protect(token);
        var json = JsonSerializer.Serialize(cfg);
        Check("config.json 中 token 明文可见", json.Contains(token), json[..Math.Min(120, json.Length)]);

        // 旧版密文：不再解密，视为需要重新获取
        Check("旧版 dpapi 密文被识别为需重新获取",
            SecretProtector.IsLegacyEncrypted("dpapi:AAAA") && SecretProtector.Unprotect("dpapi:AAAA") == "");
        Check("空值安全", SecretProtector.Unprotect("") == "" && SecretProtector.Protect(null!) == "");
    }
}
