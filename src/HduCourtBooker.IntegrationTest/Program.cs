using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HduCourtBooker.Api;
using HduCourtBooker.Core;

namespace HduCourtBooker.IntegrationTest;

/// <summary>
/// 端到端集成测试：起一个本地仿真服务端，用真实 HTTP 往返驱动 BookingScheduler，
/// 验证「20:00 触发精度、冲突重试、提交请求结构、建单调用、错误处理」。
/// 仿真响应体完全按真实站点实测到的报文构造（见 docs/API.md）。
/// </summary>
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

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== 抢约调度 端到端集成测试（本地仿真服务端）===\n");

        // 总看门狗：任何用例挂住都在 60 秒内以失败结束，避免无限等待
        var watchdog = Task.Delay(TimeSpan.FromSeconds(60));
        var work = RunAllAsync();
        if (await Task.WhenAny(work, watchdog) == watchdog)
        {
            Console.WriteLine("\n[FATAL] 测试超时（60 秒），判定为失败。");
            return 2;
        }
        return await work;
    }

    private static async Task<int> RunAllAsync()
    {
        using var server = new FakeGymbookServer();
        GymbookClientSetup.ApiBaseOverride = server.BaseUrl;
        GymbookClientSetup.UseFreshConnectionPerRequest = true;
        GymbookClientSetup.OnExchange = (dir, text) =>
            Console.WriteLine($"      [http] {DateTime.Now:HH:mm:ss.fff} {dir} {text}");
        Console.WriteLine($"仿真服务端：{server.BaseUrl}");

        var dataDir = Path.Combine(AppContext.BaseDirectory, "itest-data");
        var log = new Logger(Path.Combine(dataDir, "logs"));

        await TestTimeSyncAsync(server, log);
        await TestHappyPathAsync(server, log);
        await TestBookedOutCourtIsNotBookableAsync(server, log);
        await TestConflictRetryAsync(server, log);
        await TestTriggerPrecisionAsync(server, log);
        await TestUnauthorizedAsync(server, log);
        await TestConcurrentRunsAreSuppressedAsync(server, log);
        await TestDailySchedulerIsIdempotentAsync(server, log);

        Console.WriteLine($"\n=== {_passed} 通过 / {_failed} 失败 ===");
        GymbookClientSetup.ApiBaseOverride = null;
        return _failed == 0 ? 0 : 1;
    }

    private static AppConfig Config(Action<AppConfig>? tweak = null, DateTime? fireAt = null)
    {
        var cfg = AppConfig.CreateDefault();
        cfg.Token = "test-token";
        cfg.OpenId = "253040495";
        cfg.Nickname = "测试同学";
        cfg.Phone = "13900000000";
        cfg.UseServerTimeSync = true;
        cfg.MaxAttempts = 6;
        cfg.RetryIntervalMs = 20;
        cfg.LeadMilliseconds = 0;
        cfg.AutoCreateOrder = true;
        cfg.SitePreference = new List<int> { 3, 4 };
        cfg.SlotPreference = new List<List<int>> { new() { 10, 11 } };

        // 触发时刻：显式给定（立即抢约的用例传入"当前时刻"），否则用默认 20:00。
        // 注意：把触发时刻设在过去并不会立刻触发——NextTriggerLocal 会正确地排到明天的同一时刻。
        if (fireAt is { } t) cfg.TriggerTime = Fmt(t);

        tweak?.Invoke(cfg);
        return cfg;
    }

    /// <summary>统一用 24 小时两位小时格式输出触发时刻，避免解析歧义。</summary>
    private static string Fmt(DateTime t) =>
        t.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);

    private static (BookingScheduler Scheduler, TimeSync Sync, GymbookClient Client) Build(Logger log)
    {
        // 短超时：本地仿真若挂住，5 秒即暴露，而不是等 20 秒
        var client = new GymbookClient(timeoutSeconds: 5);
        var sync = new TimeSync(client, log);
        return (new BookingScheduler(client, sync, log), sync, client);
    }

    // ---------- 1) 时钟校准 ----------
    private static async Task TestTimeSyncAsync(FakeGymbookServer server, Logger log)
    {
        Console.WriteLine("\n[1] 服务器时间校准（post_server_time）");
        server.Reset();
        var (_, sync, client) = Build(log);
        var offset = await sync.SyncAsync(3);
        Check("采样成功且 RTT 有效", sync.LastRttMs > 0, $"offset={offset:+0;-0;0}ms rtt={sync.LastRttMs:F1}ms");
        Check("服务器时间接口被调用 3 次", server.ServerTimeCalls == 3, $"calls={server.ServerTimeCalls}");
        client.Dispose();
    }

    // ---------- 2) 正常抢约成功 ----------
    private static async Task TestHappyPathAsync(FakeGymbookServer server, Logger log)
    {
        Console.WriteLine("\n[2] 正常抢约成功（含建单）");
        server.Reset();

        // 3 号场有 10,11（偏好命中），1 号场只有 1
        server.SiteSituation = new[]
        {
            new FakeSite(1, new[] { 1 }),
            new FakeSite(2, new[] { 9 }),
            new FakeSite(3, new[] { 10, 11, 12 }),
        };

        var now = DateTime.Now;
        var cfg = Config(fireAt: now);
        var (scheduler, _, client) = Build(log);
        var result = await scheduler.RunOnceAsync(cfg, CancellationToken.None, now);

        Check("结果为成功", result.Outcome == BookingOutcome.Success, result.Message);
        Check("选中优先场地 3 号场", result.SiteId == 3, $"site={result.SiteId}");
        Check("时间段为偏好的 10,11", result.Slots.SequenceEqual(new[] { 10, 11 }), string.Join(",", result.Slots));
        Check("时间范围 18:00-20:00", result.TimeRange == "18:00-20:00", result.TimeRange);
        Check("成功前只提交一次", server.BookInfoCalls == 1, $"calls={server.BookInfoCalls}");
        Check("成功后调用了建单接口", server.CreateOrderCalls == 1, $"calls={server.CreateOrderCalls}");

        // 校验真实发出的请求结构
        var body = server.LastBookInfoBody ?? "";
        Check("请求体含 orderData 包装", body.Contains("\"orderData\""), Snippet(body));
        Check("含 openid/date/venue_name", body.Contains("\"openid\":\"253040495\"")
            && body.Contains("\"date\":\"") && body.Contains("\"venue_name\":\"综合馆羽毛球\""), Snippet(body));
        Check("含 site_id/time_list/start_time/end_time",
            body.Contains("\"site_id\":3") && body.Contains("\"time_list\":[10,11]")
            && body.Contains("\"start_time\":\"18:00\"") && body.Contains("\"end_time\":\"20:00\""), Snippet(body));
        Check("携带 Bearer token", server.LastBookInfoAuth == "Bearer test-token", server.LastBookInfoAuth ?? "(null)");
        client.Dispose();
    }

    // ---------- 3) 回归：整场被约满绝不能被当成可约 ----------
    private static async Task TestBookedOutCourtIsNotBookableAsync(FakeGymbookServer server, Logger log)
    {
        Console.WriteLine("\n[3] 回归：oci=开放 / book=已被预约 的语义");
        server.Reset();

        // 1 号场：开放 [10,11] 且【全部已被预约】 → 不可约（这就是线上 bug 的复现数据）
        // 2 号场：开放 [10,11]，只被约走 10       → 只剩 11 可约
        server.SiteSituation = new[]
        {
            new FakeSite(1, new[] { 10, 11 }, new[] { 10, 11 }),
            new FakeSite(2, new[] { 10, 11 }, new[] { 10 }),
        };

        var now = DateTime.Now;
        // 只跑一轮：仿真服务端的占用状态在抢约成功后才会变化，
        // 多轮重试会取到"更新后"的场地图，测的就不是本用例想验证的东西了。
        // 注意把 SitePreference 清空：默认值 [3,4] 在只有 1、2 号场的仿真数据里会过滤掉所有场地。
        var cfg = Config(c =>
        {
            c.AutoCreateOrder = false;
            c.MaxAttempts = 1;
            c.SitePreference = new List<int>();
        }, fireAt: now);

        var (scheduler, _, client) = Build(log);
        var result = await scheduler.RunOnceAsync(cfg, CancellationToken.None, now);

        Check("选中可约的 2 号场，而不是已约满的 1 号场", result.SiteId == 2, $"site={result.SiteId}");
        Check("只提交了实际空闲的时段 11", result.Slots.SequenceEqual(new[] { 11 }),
            string.Join(",", result.Slots));
        client.Dispose();

        // 全部场地都被约满时必须不产生任何提交
        server.Reset();
        server.SiteSituation = new[]
        {
            new FakeSite(1, new[] { 10, 11 }, new[] { 10, 11 }),
            new FakeSite(2, new[] { 10, 11 }, new[] { 10, 11 }),
        };
        var cfg2 = Config(c =>
        {
            c.AutoCreateOrder = false;
            c.MaxAttempts = 1;
            c.SitePreference = new List<int>();
        }, fireAt: now);
        var (scheduler2, _, client2) = Build(log);
        var r2 = await scheduler2.RunOnceAsync(cfg2, CancellationToken.None, now);
        Check("全部约满时不提交任何预约", server.BookInfoCalls == 0, $"calls={server.BookInfoCalls}");
        Check("全部约满时结论为无可约时段", r2.Outcome == BookingOutcome.AllTaken,
            $"{r2.Outcome}：{r2.Message}");
        client2.Dispose();
    }

    // ---------- 4) 冲突重试 ----------
    private static async Task TestConflictRetryAsync(FakeGymbookServer server, Logger log)
    {
        Console.WriteLine("\n[3] 冲突重试（前两次返回 conflict_times，第三次成功）");
        server.Reset();
        server.ConflictTimesBeforeSuccess = 2;
        server.SiteSituation = new[]
        {
            new FakeSite(1, new[] { 4 }),
            new FakeSite(3, new[] { 10, 11 }),
        };

        var now3 = DateTime.Now;
        var cfg = Config(c => c.AutoCreateOrder = false, fireAt: now3);
        var (scheduler, _, client) = Build(log);
        var result = await scheduler.RunOnceAsync(cfg, CancellationToken.None, now3);

        Check("重试后成功", result.Outcome == BookingOutcome.Success, result.Message);
        Check("共提交 3 次", server.BookInfoCalls == 3, $"calls={server.BookInfoCalls}");
        Check("记录尝试次数", result.Attempts == 3, $"attempts={result.Attempts}");
        client.Dispose();
    }

    // ---------- 4) 触发精度 ----------
    private static async Task TestTriggerPrecisionAsync(FakeGymbookServer server, Logger log)
    {
        Console.WriteLine("\n[4] 触发精度（目标时刻 = 现在 + 3.0 秒）");
        server.Reset();
        server.SiteSituation = new[] { new FakeSite(3, new[] { 10, 11 }) };

        var fireAt = DateTime.Now.AddSeconds(3);
        // 显式传入 fireAt，走"等待到指定时刻"的路径；TriggerTime 与之一致
        var cfg = Config(c =>
        {
            c.LeadMilliseconds = 0;
            c.AutoCreateOrder = false;
            c.UseServerTimeSync = true;
            c.TriggerTime = Fmt(fireAt);
        });

        var (scheduler, sync, client) = Build(log);
        await sync.SyncAsync(3); // 先校准，避免触发时刻因时钟偏移漂移

        var result = await scheduler.RunOnceAsync(cfg, CancellationToken.None, fireAt);

        var errorMs = server.FirstBookInfoAt.HasValue
            ? (server.FirstBookInfoAt.Value - fireAt).TotalMilliseconds
            : double.NaN;

        Check("成功触发", result.Outcome == BookingOutcome.Success, result.Message);
        Check("确实等待到目标时刻（未提前提交）", errorMs >= -15,
            $"首次提交相对目标时刻 {errorMs:F0} ms");
        Check("触发误差 |Δt| < 500 ms", Math.Abs(errorMs) < 500,
            $"目标 {fireAt:HH:mm:ss.fff}，实际 {server.FirstBookInfoAt:HH:mm:ss.fff}（偏差 {errorMs:F0} ms）");
        client.Dispose();
    }

    // ---------- 5) token 失效 ----------
    private static async Task TestUnauthorizedAsync(FakeGymbookServer server, Logger log)
    {
        Console.WriteLine("\n[5] token 失效处理（401）");
        server.Reset();
        server.Unauthorized = true;
        server.SiteSituation = new[] { new FakeSite(3, new[] { 10, 11 }) };

        var now5 = DateTime.Now;
        var cfg = Config(fireAt: now5);
        var (scheduler, _, client) = Build(log);
        var result = await scheduler.RunOnceAsync(cfg, CancellationToken.None, now5);

        Check("返回登录失效", result.Outcome == BookingOutcome.LoginFailed, result.Outcome.ToString());
        Check("错误信息含未登录/token", result.Message.Contains("登录") || result.Message.Contains("token"),
            result.Message);
        client.Dispose();
    }

    // ---------- 6) 并发保护：这正是 2026-10-09 20:00 抢约失败的直接原因 ----------
    private static async Task TestConcurrentRunsAreSuppressedAsync(FakeGymbookServer server, Logger log)
    {
        Console.WriteLine("\n[6] 并发保护（模拟同一时刻被触发 20 次）");
        server.Reset();
        server.SiteSituation = new[] { new FakeSite(3, new[] { 10, 11 }) };

        var now = DateTime.Now;
        const int parallel = 20;
        var results = new BookingResult[parallel];

        await Task.WhenAll(Enumerable.Range(0, parallel).Select(i => Task.Run(async () =>
        {
            var cfg = Config(c => { c.AutoCreateOrder = false; c.MaxAttempts = 1; }, fireAt: now);
            var (scheduler, _, client) = Build(log);
            results[i] = await scheduler.RunOnceAsync(cfg, CancellationToken.None, now);
            client.Dispose();
        })));

        int busy = results.Count(r => r.Outcome == BookingOutcome.Busy);
        int success = results.Count(r => r.IsSuccess);
        Check("并发 20 次只有 1 次真正执行", success == 1, $"success={success}");
        Check("其余 19 次被判定为 Busy 并忽略", busy == parallel - 1, $"busy={busy}");
        Check("服务器只收到 1 次提交（不再刷屏）", server.BookInfoCalls == 1,
            $"bookCalls={server.BookInfoCalls}");
        Check("请求总量可控（< 20）", server.RequestPaths.Count < 20,
            $"总请求 {server.RequestPaths.Count} 次");
    }

    // ---------- 7) 每日调度：重复 Start 不得产生多条循环 ----------
    private static async Task TestDailySchedulerIsIdempotentAsync(FakeGymbookServer server, Logger log)
    {
        Console.WriteLine("\n[7] 每日调度防重复启动（此前 25 条循环同时抢约）");
        server.Reset();
        server.SiteSituation = new[] { new FakeSite(3, new[] { 10, 11 }) };

        var cfg = Config(c =>
        {
            c.AutoCreateOrder = false;
            c.ScheduleEnabled = true;
            c.TriggerTime = DateTime.Now.AddHours(3).ToString("HH:mm:ss.fff");
        });

        var daily = new HduCourtBooker.DailyScheduler(log);
        int runCount = 0;
        for (int i = 0; i < 25; i++)
        {
            daily.Start(cfg, (c, fireAt, ct) =>
            {
                Interlocked.Increment(ref runCount);
                return Task.FromResult(new BookingResult { Outcome = BookingOutcome.Success });
            });
        }

        Check("重复 Start 25 次后仍处于运行状态", daily.IsRunning);
        Check("只有一条调度循环（重复调用被忽略）", runCount == 0 && daily.IsRunning,
            $"已触发 {runCount} 次（距离触发时刻还早，应为 0）");

        daily.Stop();
        Check("Stop 后不再运行", !daily.IsRunning);
        daily.Dispose();
    }

    private static string Snippet(string s) => s.Length <= 150 ? s : s[..150] + "…";
}

// ---------------------------------------------------------------------------
// 仿真服务端：按真实站点报文应答
// ---------------------------------------------------------------------------

/// <summary>
/// 仿真场地：Open = 已开放时段（oci_time_list），Booked = 其中已被预约的时段（book_time_list）。
/// 真实语义见 ApiModels.SiteSituationDto：可约 = Open - Booked。
/// </summary>
internal sealed record FakeSite(int SiteId, int[] Open, int[]? BookedSlots = null)
{
    /// <summary>已被预约的时段；默认空（全部空闲）。</summary>
    public int[] Booked { get; init; } = BookedSlots ?? Array.Empty<int>();

    /// <summary>空闲时段。</summary>
    public int[] Free => Open.Where(i => !Booked.Contains(i)).ToArray();
}

/// <summary>
/// 极简 HTTP/1.1 仿真服务端（TcpListener 实现，避免 http.sys 的 URL ACL 权限要求）。
/// 每个连接处理一个请求后即关闭，并显式发送 Connection: close，保证连接生命周期可预期。
/// </summary>
internal sealed class FakeGymbookServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _requests = new();

    public FakeGymbookServer()
    {
        var ip = IPAddress.Loopback;
        TcpListener? listener = null;
        for (int port = 18800; port < 18900; port++)
        {
            try
            {
                var l = new TcpListener(ip, port);
                l.Start();
                listener = l;
                BaseUrl = $"http://127.0.0.1:{port}";
                break;
            }
            catch
            {
                // 端口占用，换一个
            }
        }
        _listener = listener ?? throw new InvalidOperationException("无法启动仿真服务端");
        _ = Task.Run(LoopAsync);
    }

    public string BaseUrl { get; } = "";

    // 可配置行为
    public FakeSite[] SiteSituation { get; set; } = Array.Empty<FakeSite>();
    public int ConflictTimesBeforeSuccess { get; set; }
    public bool Unauthorized { get; set; }
    public bool SiteSituationEmpty { get; set; }

    // 观测
    public int ServerTimeCalls { get; private set; }
    public int BookInfoCalls { get; private set; }
    public int CreateOrderCalls { get; private set; }
    public string? LastBookInfoBody { get; private set; }
    public string? LastBookInfoAuth { get; private set; }

    /// <summary>首次收到 creat_book_info 的本地时刻（用于测量触发落点）。</summary>
    public DateTime? FirstBookInfoAt { get; private set; }
    public List<string> RequestPaths => _requests;

    private static readonly string[] TimeList =
    {
        "08:00", "09:00", "10:00", "11:00", "11:40", "13:20", "14:00",
        "15:00", "16:00", "17:00", "18:00", "19:00", "20:00", "21:00",
    };

    public void Reset()
    {
        SiteSituation = Array.Empty<FakeSite>();
        ConflictTimesBeforeSuccess = 0;
        Unauthorized = false;
        SiteSituationEmpty = false;
        ServerTimeCalls = 0;
        BookInfoCalls = 0;
        CreateOrderCalls = 0;
        LastBookInfoBody = null;
        LastBookInfoAuth = null;
        FirstBookInfoAt = null;
        lock (_requests) _requests.Clear();
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch
            {
                break;
            }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 5000;
                using var stream = client.GetStream();
                // 支持 keep-alive：同一个连接上循环处理多个请求
                while (!_cts.IsCancellationRequested)
                {
                    var (method, path, headers, body) = await ReadRequestAsync(stream).ConfigureAwait(false);
                    if (path is null) return;

                    lock (_requests) _requests.Add(method + " " + path);
                    Console.WriteLine($"      [stub] {DateTime.Now:HH:mm:ss.fff} <- {method} {path}");

                    var (status, response) = BuildResponse(path, headers, body);
                    await WriteResponseAsync(stream, status, response).ConfigureAwait(false);
                    Console.WriteLine($"      [stub] {DateTime.Now:HH:mm:ss.fff} -> {status} {path}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"      [stub] !! 连接异常：{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static async Task<(string Method, string? Path, Dictionary<string, string> Headers, string Body)>
        ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var header = new MemoryStream();
        int headerEnd = -1;

        // 逐字节边界不可靠，改用累积查找 \r\n\r\n
        while (headerEnd < 0)
        {
            int n = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (n <= 0) return ("", null, new Dictionary<string, string>(), "");
            header.Write(buffer, 0, n);
            headerEnd = FindHeaderEnd(header.GetBuffer(), (int)header.Length);
        }

        var all = header.ToArray();
        var headerText = Encoding.UTF8.GetString(all, 0, headerEnd);
        var leftover = all.AsSpan(headerEnd + 4).ToArray();

        var lines = headerText.Split("\r\n");
        var parts = lines[0].Split(' ');
        string method = parts.Length > 0 ? parts[0] : "";
        string path = parts.Length > 1 ? parts[1] : "";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            var idx = lines[i].IndexOf(':');
            if (idx > 0) headers[lines[i][..idx].Trim()] = lines[i][(idx + 1)..].Trim();
        }

        int contentLength = headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var len) ? len : 0;
        var bodyBytes = new List<byte>(contentLength);
        bodyBytes.AddRange(leftover);
        while (bodyBytes.Count < contentLength)
        {
            int n = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (n <= 0) break;
            bodyBytes.AddRange(buffer.AsSpan(0, n).ToArray());
        }
        var body = Encoding.UTF8.GetString(bodyBytes.Take(contentLength).ToArray());

        if (path.Contains('?')) path = path[..path.IndexOf('?')];
        return (method, path, headers, body);
    }

    private static int FindHeaderEnd(byte[] data, int length)
    {
        for (int i = 0; i + 3 < length; i++)
        {
            if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10) return i;
        }
        return -1;
    }

    private (int Status, string Body) BuildResponse(
        string path, Dictionary<string, string> headers, string body)
    {
        switch (path)
        {
            case "/client/post_server_time":
                ServerTimeCalls++;
                return (200, $"{{\"data\": {DateTimeOffset.Now.ToUnixTimeMilliseconds()}}}");

            case "/client/choose_venue":
                return (200, "{\"venue_list\":[{\"venue_name\":\"综合馆羽毛球\",\"venue_introduce\":\"12片场地\","
                             + "\"venue_image_url\":\"/static/venueImage/multipurposeHallBadminton.jpg\"}]}");

            case "/client/post_venue_info":
                return (200, JsonSerializer.Serialize(new
                {
                    time_list = TimeList,
                    days_can_appoint = 3,
                    base_price = 0.0,
                    rest_list = Enumerable.Range(0, 14).ToArray(),
                }));

            case "/client/post_site_situation":
                // 仿真报文与真实站点一致：
                //   oci_time_list  = 已开放（可被预约的全部时段）
                //   book_time_list = 已被预约（其中的一部分）
                var sites = SiteSituationEmpty
                    ? Array.Empty<object>()
                    : SiteSituation
                        .Select(s => new
                        {
                            site_id = s.SiteId,
                            book_time_list = s.Booked,
                            oci_time_list = s.Open,
                        })
                        .Cast<object>()
                        .ToArray();
                return (200, JsonSerializer.Serialize(new
                {
                    site_situation = sites,
                    extra_list = new { extra_price_time = Array.Empty<int>(), extra_price_list = Array.Empty<int>() },
                }));

            case "/client/creat_book_info":
                BookInfoCalls++;
                FirstBookInfoAt ??= DateTime.Now;
                LastBookInfoBody = body;
                LastBookInfoAuth = headers.TryGetValue("Authorization", out var auth) ? auth : null;
                if (Unauthorized)
                {
                    return (401, "{\"error\": \"未登录或缺少 token\"}");
                }
                if (BookInfoCalls <= ConflictTimesBeforeSuccess)
                {
                    // 与真实前端约定一致：200 + conflict_times 非空 + available_times 空 = 该时段已被占
                    return (200, "{\"message\":\"该时间段已被预约\",\"conflict_times\":[10,11],\"available_times\":[]}");
                }

                // 抢约成功：把该场地该时段标记为"已被预约"，后续查询结果随之变化
                ApplyBooking(body);
                return (200, "{\"message\":\"预约成功\",\"conflict_times\":[],\"available_times\":[],"
                             + "\"data\":{\"book_id\":\"B20261010001\"}}");

            case "/client/creat_order":
                CreateOrderCalls++;
                return (200, "{\"message\":\"下单成功\",\"data\":{\"order_id\":\"O20261010001\",\"with_friends\":false}}");

            default:
                return (404, "{\"status\":\"error\",\"message\":\"not found\"}");
        }
    }

    /// <summary>
    /// 抢约成功后更新仿真状态：把请求体里的 site_id / time_list 记为"已被预约"，
    /// 使后续 post_site_situation 的结果与真实系统一致。
    /// </summary>
    private void ApplyBooking(string requestBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            if (!doc.RootElement.TryGetProperty("orderData", out var od)) return;
            int siteId = od.GetProperty("site_id").GetInt32();
            var slots = od.GetProperty("time_list").EnumerateArray().Select(x => x.GetInt32()).ToArray();

            var site = SiteSituation.FirstOrDefault(s => s.SiteId == siteId);
            if (site is null) return;
            var booked = site.Booked.ToList();
            foreach (var s in slots) if (!booked.Contains(s)) booked.Add(s);
            site = site with { Booked = booked.ToArray() };
            SiteSituation = SiteSituation.Select(x => x.SiteId == siteId ? site : x).ToArray();
        }
        catch
        {
            // 请求体不合法时忽略，不影响测试
        }
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\n"
                   + "Content-Type: application/json; charset=utf-8\r\n"
                   + $"Content-Length: {bytes.Length}\r\n"
                   + "Connection: keep-alive\r\n\r\n";
        var headBytes = Encoding.ASCII.GetBytes(head);
        await stream.WriteAsync(headBytes).ConfigureAwait(false);
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
    }
}
