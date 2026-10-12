using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HduCourtBooker.Api;

/// <summary>
/// 客户端可注入的运行期设置（测试用：指向本地仿真服务端）。
/// </summary>
public static class GymbookClientSetup
{
    /// <summary>覆盖 API 根地址；null 表示使用真实站点。</summary>
    public static string? ApiBaseOverride { get; set; }

    /// <summary>接受无效 TLS 证书（仅本地仿真服务端使用，便于在受控环境做端到端验证）。</summary>
    public static bool AcceptInvalidCertificates { get; set; }

    /// <summary>抓包钩子：(方向, 文本)。仅用于诊断。</summary>
    public static Action<string, string>? OnExchange { get; set; }

    /// <summary>每个请求使用新连接（仅集成测试使用，避免连接复用与仿真服务端生命周期冲突）。</summary>
    public static bool UseFreshConnectionPerRequest { get; set; }
}

/// <summary>
/// 场馆预约系统的 HTTP 客户端。
/// 关键设计：全部走 HTTPS 直连（不经过浏览器），并保持长连接，把抢约时的延迟压到最低。
/// </summary>
public sealed class GymbookClient : IDisposable
{
    public const string DefaultHost = "https://sportmeta.hdu.edu.cn";
    public const string DefaultApiBase = Core.AppConfig.DefaultApiBase;

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/154.0.0.0 Safari/537.36 Edg/154.0.0.0";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
        WriteIndented = false,
        // 中文直接以 UTF-8 输出，便于日志排查（服务端解析不受影响）
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http;

    /// <summary>
    /// 全局请求闸门 + 最小间隔。
    ///
    /// ⚠️ 针对真实事故的防护：曾因多个抢约循环并发，瞬间向服务器打出几百个请求，
    /// 被判定为异常流量直接返回 HTTP 403，导致一单都没成功。
    /// 现在无论上层怎么调用，请求都是串行且带最小间隔的。
    /// </summary>
    private static readonly SemaphoreSlim HttpGate = new(1, 1);
    private static long _lastRequestTicks;

    /// <summary>
    /// 两次 HTTP 请求之间的最小间隔（毫秒）。
    /// 抢约时每次往返约 30~60ms，25ms 间隔不会成为瓶颈，但足以避免请求风暴。
    /// </summary>
    public static int MinRequestIntervalMs { get; set; } = 25;

    /// <summary>统计用：窗口内(最近2秒)的请求数，超过阈值时记警告。</summary>
    public static int RecentRequestCount => _recentRequests.Count;

    private static readonly Queue<long> _recentRequests = new();
    private static readonly object _recentGate = new();

    private static void NoteRequestAndWarn()
    {
        long now = Environment.TickCount64;
        int count;
        lock (_recentGate)
        {
            _recentRequests.Enqueue(now);
            while (_recentRequests.Count > 0 && now - _recentRequests.Peek() > 2000) _recentRequests.Dequeue();
            count = _recentRequests.Count;
        }
        if (count == 40 || count == 80 || count == 160)
        {
            Core.Logger.StaticWarn($"⚠ 请求过于频繁：最近 2 秒内已发出 {count} 个请求，可能被服务器限流");
        }
    }

    public GymbookClient(string? apiBase = null, int timeoutSeconds = 20)
    {
        ApiBase = (apiBase ?? GymbookClientSetup.ApiBaseOverride ?? DefaultApiBase).TrimEnd('/');
        var handler = new SocketsHttpHandler
        {
            // 抢约场景：预热连接 + 长连接复用，避免 TLS 握手落在关键路径上
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 8,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            AutomaticDecompression = DecompressionMethods.All,
        };
        if (GymbookClientSetup.UseFreshConnectionPerRequest)
        {
            handler.PooledConnectionLifetime = TimeSpan.Zero;
            handler.PooledConnectionIdleTimeout = TimeSpan.Zero;
            handler.MaxConnectionsPerServer = 1;
        }
        if (GymbookClientSetup.AcceptInvalidCertificates)
        {
            handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            };
        }
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        _http.DefaultRequestHeaders.Add("Origin", DefaultHost);
        _http.DefaultRequestHeaders.Referrer = new Uri(DefaultHost + "/book/dingtalk/");
    }

    public string ApiBase { get; }

    /// <summary>当前使用的登录 token；为空表示未登录。</summary>
    public string? Token { get; set; }

    public int LastStatusCode { get; private set; }
    public string LastRawBody { get; private set; } = "";

    private HttpRequestMessage NewRequest(HttpMethod method, string path, object? payload, bool auth)
    {
        var req = new HttpRequestMessage(method, ApiBase + path);
        if (payload is not null)
        {
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        if (auth && !string.IsNullOrWhiteSpace(Token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        }
        return req;
    }

    private async Task<(int Status, string Body)> SendRawAsync(
        HttpMethod method, string path, object? payload, bool auth, CancellationToken ct)
    {
        // 全局闸门：保证同一时刻只有一个请求在飞，并强制最小间隔
        await HttpGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var gap = MinRequestIntervalMs - (int)((Environment.TickCount64 - Interlocked.Read(ref _lastRequestTicks)));
            if (gap > 0) await Task.Delay(gap, ct).ConfigureAwait(false);

            NoteRequestAndWarn();

            using var req = NewRequest(method, path, payload, auth);
            if (payload is not null && GymbookClientSetup.OnExchange is not null)
            {
                GymbookClientSetup.OnExchange("REQ",
                    $"{method} {path} {System.Text.Json.JsonSerializer.Serialize(payload, JsonOpts)}");
            }
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            LastStatusCode = (int)resp.StatusCode;
            LastRawBody = body;
            GymbookClientSetup.OnExchange?.Invoke("RESP", $"{LastStatusCode} {path} {Truncate(body, 400)}");
            return (LastStatusCode, body);
        }
        finally
        {
            Interlocked.Exchange(ref _lastRequestTicks, Environment.TickCount64);
            HttpGate.Release();
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private async Task<T?> SendAsync<T>(
        HttpMethod method, string path, object? payload, bool auth, CancellationToken ct)
    {
        var (status, body) = await SendRawAsync(method, path, payload, auth, ct).ConfigureAwait(false);
        if (status is < 200 or >= 300)
        {
            throw new GymbookException(DescribeError(status, body), status, body);
        }
        if (string.IsNullOrWhiteSpace(body))
        {
            return default;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new GymbookException($"响应解析失败：{ex.Message}", status, body);
        }
    }

    private static string DescribeError(int status, string body)
    {
        string extra = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            foreach (var key in new[] { "message", "msg", "error" })
            {
                if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    extra = v.GetString() ?? "";
                    if (extra.Length > 0) break;
                }
            }
        }
        catch
        {
            extra = body.Length > 200 ? body[..200] : body;
        }
        return status switch
        {
            401 => $"未登录或 token 失效（HTTP 401）{(extra.Length > 0 ? "：" + extra : "")}",
            403 => $"被服务器拒绝（HTTP 403）{(extra.Length > 0 ? "：" + extra : "")}",
            _ => $"HTTP {status} {(extra.Length > 0 ? "：" + extra : "")}".Trim(),
        };
    }

    // ---------------- 业务接口 ----------------

    /// <summary>服务器毫秒时间戳，用于校准本地时钟。</summary>
    public async Task<long> GetServerTimeMsAsync(CancellationToken ct = default)
    {
        var r = await SendAsync<ServerTimeResponse>(HttpMethod.Get, "/client/post_server_time", null, false, ct)
            .ConfigureAwait(false);
        return r?.Data ?? 0;
    }

    public Task<VenueListResponse?> GetVenueListAsync(string venueType, CancellationToken ct = default)
        => SendAsync<VenueListResponse>(HttpMethod.Post, "/client/choose_venue",
            new { venue_type = venueType }, false, ct);

    public Task<VenueInfoResponse?> GetVenueInfoAsync(
        string venueType, string venueName, string date, CancellationToken ct = default)
        => SendAsync<VenueInfoResponse>(HttpMethod.Post, "/client/post_venue_info",
            new { venue_type = venueType, venue_name = venueName, date }, false, ct);

    /// <summary>查询某天各场地可预约的时间段。</summary>
    public Task<SiteSituationResponse?> GetSiteSituationAsync(
        string date, string venueName, CancellationToken ct = default)
        => SendAsync<SiteSituationResponse>(HttpMethod.Post, "/client/post_site_situation",
            new { date, venue_name = venueName }, false, ct);

    /// <summary>提交预约（真正的抢场动作）。</summary>
    public Task<BookInfoResponse?> SubmitBookInfoAsync(OrderRequest request, CancellationToken ct = default)
        => SendAsync<BookInfoResponse>(HttpMethod.Post, "/client/creat_book_info", request, true, ct);

    /// <summary>创建订单（提交成功后调用，返回订单号等信息）。</summary>
    public Task<JsonElement?> CreateOrderAsync(OrderRequest request, CancellationToken ct = default)
        => SendAsync<JsonElement?>(HttpMethod.Post, "/client/creat_order", request, true, ct);

    public Task<ApiMessage?> ShowNoticeAsync(CancellationToken ct = default)
        => SendAsync<ApiMessage>(HttpMethod.Post, "/client/show_notice", new { }, false, ct);

    /// <summary>
    /// 校验 token 并读取账号信息（只读接口，不会产生任何预约）。
    /// 成功返回原始 JSON；token 无效会抛 <see cref="GymbookException"/>（401）。
    /// </summary>
    public Task<JsonElement?> GetOrderInfoAsync(string openId, CancellationToken ct = default)
        => SendAsync<JsonElement?>(HttpMethod.Post, "/client/post_order_info",
            new { openid = openId }, true, ct);

    /// <summary>取消预约。</summary>
    public Task<ApiMessage?> CancelBookAsync(string openId, CancellationToken ct = default)
        => SendAsync<ApiMessage>(HttpMethod.Post, "/client/cancel_book", new { openid = openId }, true, ct);

    /// <summary>提前建立 TLS/TCP 连接，避免抢约瞬间的握手开销。</summary>
    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            await GetServerTimeMsAsync(ct).ConfigureAwait(false);
            sw.Stop();
            LastWarmUpMs = sw.Elapsed.TotalMilliseconds;
        }
        catch
        {
            LastWarmUpMs = -1;
        }
    }

    public double LastWarmUpMs { get; private set; }

    public void Dispose() => _http.Dispose();
}

public sealed class GymbookException : Exception
{
    public GymbookException(string message, int statusCode, string body) : base(message)
    {
        StatusCode = statusCode;
        Body = body;
    }

    public int StatusCode { get; }
    public string Body { get; }
}
