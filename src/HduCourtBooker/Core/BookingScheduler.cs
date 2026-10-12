using System.Diagnostics;
using System.Text.Json;
using HduCourtBooker.Api;

namespace HduCourtBooker.Core;

public enum BookingOutcome
{
    Success,
    NoCandidate,
    LoginFailed,
    AllTaken,
    Error,
    Cancelled,

    /// <summary>已有抢约流程在运行，本次调用被忽略（防并发）。</summary>
    Busy,
}

public sealed class BookingResult
{
    public BookingOutcome Outcome { get; init; }
    public string Message { get; init; } = "";
    public string Date { get; init; } = "";
    public int SiteId { get; init; }
    public List<int> Slots { get; init; } = new();
    public string TimeRange { get; init; } = "";
    public int Attempts { get; init; }
    public double ElapsedMs { get; init; }
    public JsonElement? OrderResponse { get; init; }
    public bool IsSuccess => Outcome == BookingOutcome.Success;
}

/// <summary>
/// 抢约调度器：在每天 TriggerTime（默认 20:00:00.000）对准服务器时间发起预约。
/// 流程：预热（拉取场地图、建连） -> 精确定时 -> 快速拉取最新可约 -> 命中即提交 -> 冲突重试。
/// </summary>
public sealed class BookingScheduler
{
    private readonly GymbookClient _client;
    private readonly TimeSync _timeSync;
    private readonly Logger _log;

    public BookingScheduler(GymbookClient client, TimeSync timeSync, Logger log)
    {
        _client = client;
        _timeSync = timeSync;
        _log = log;
    }

    public event Action<string>? Status;

    private void Report(string msg)
    {
        try { Status?.Invoke(msg); } catch { }
    }

    /// <summary>
    /// 等待到指定本地时刻（实现见 <see cref="Timing.WaitUntilAsync"/>，独立出来便于测试）。
    /// </summary>
    public static Task WaitUntilAsync(DateTime targetLocal, CancellationToken ct)
        => Timing.WaitUntilAsync(targetLocal, ct);

    /// <summary>
    /// 全局互斥：同一时刻只允许一个抢约流程。
    ///
    /// ⚠️ 这是针对真实事故的修复：曾经因为调度器被重复启动，19:59:59.756 同时有 25 个循环
    /// 一起抢约（每个循环还自带 5 次时钟校准 + 每次 3 次重试），瞬间向服务器打出几百个请求，
    /// 直接触发限流（HTTP 403），一单都没提交成功。
    /// </summary>
    private static readonly SemaphoreSlim RunGate = new(1, 1);

    /// <summary>是否有抢约流程正在运行。</summary>
    public static bool IsRunning => RunGate.CurrentCount == 0;

    /// <summary>
    /// 执行一次抢约。
    /// </summary>
    /// <param name="fireAtLocal">预定发起时刻（本地时间）；null 表示按配置自动推算下一次。</param>
    public async Task<BookingResult> RunOnceAsync(
        AppConfig cfg, CancellationToken ct, DateTime? fireAtLocal = null)
    {
        // 拿不到锁说明已有流程在跑：立刻返回，绝不并发打请求
        if (!await RunGate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
        {
            _log.Warn("已有抢约流程正在执行，本次调用被忽略（防止并发请求触发服务器限流）");
            return new BookingResult
            {
                Outcome = BookingOutcome.Busy,
                Message = "已有抢约流程正在执行，本次调用被忽略",
            };
        }

        try
        {
            return await RunOnceCoreAsync(cfg, ct, fireAtLocal).ConfigureAwait(false);
        }
        finally
        {
            RunGate.Release();
        }
    }

    private async Task<BookingResult> RunOnceCoreAsync(
        AppConfig cfg, CancellationToken ct, DateTime? fireAtLocal)
    {
        var total = Stopwatch.StartNew();
        int attempts = 0;

        if (string.IsNullOrWhiteSpace(cfg.Token))
        {
            return new BookingResult { Outcome = BookingOutcome.LoginFailed, Message = "未配置 token，请先登录并粘贴 token" };
        }

        // token 过期就在本地直接拦下，避免白等一整轮
        var exp = ReadJwtExpiry(cfg.Token);
        if (exp is { } expiry && expiry <= DateTimeOffset.Now)
        {
            var msg = $"token 已于 {expiry:yyyy-MM-dd HH:mm} 过期，请在钉钉里重新打开「场馆预约」后重新获取";
            _log.Error(msg);
            return new BookingResult { Outcome = BookingOutcome.LoginFailed, Message = msg };
        }

        _client.Token = cfg.Token;

        // 1) 时钟校准
        if (cfg.UseServerTimeSync)
        {
            await _timeSync.SyncAsync(5, ct).ConfigureAwait(false);
        }

        var targetDate = ScheduleMath.ResolveTargetDate(cfg, _timeSync.ServerNow);
        _log.Info($"目标日期：{targetDate}（本地 {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}，服务器 {_timeSync.ServerNow:HH:mm:ss.fff}）");

        // 2) 预热：建立连接 + 拉取场地信息（时间表）
        await _client.WarmUpAsync(ct).ConfigureAwait(false);
        var venueInfo = await _client.GetVenueInfoAsync(cfg.VenueType, cfg.VenueName, targetDate, ct)
            .ConfigureAwait(false);
        if (venueInfo is null || venueInfo.TimeList.Count == 0)
        {
            return new BookingResult { Outcome = BookingOutcome.Error, Message = "无法获取场地时间表，请检查场地名/token" };
        }
        _log.Info($"时间表：{string.Join(",", venueInfo.TimeList)}");

        // 3) 精确等待触发时刻（fireAtLocal 由调用方给出，保证与预热计划一致）
        var triggerLocal = fireAtLocal ?? ScheduleMath.NextTriggerLocal(cfg, DateTime.Now);

        var waitMs = (triggerLocal - DateTime.Now).TotalMilliseconds;
        if (waitMs > 0)
        {
            _log.Info($"等待触发：{triggerLocal:HH:mm:ss.fff}（还有 {waitMs / 1000:F1} 秒）");
            Report($"等待 {triggerLocal:HH:mm:ss.fff}");
            await WaitUntilAsync(triggerLocal, ct).ConfigureAwait(false);
        }

        var fireAt = DateTime.Now;
        _log.Info($"▶ 触发预约（本地 {fireAt:HH:mm:ss.fff}）");

        // 4) 抢占循环
        int maxAttempts = Math.Max(1, cfg.MaxAttempts);
        string lastMessage = "";
        List<int> lastSlots = new();
        int throttleHits = 0;   // 被限流(403)的次数，用于指数退避

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            attempts = attempt;

            SiteSituationResponse? situation;
            try
            {
                situation = await _client.GetSiteSituationAsync(targetDate, cfg.VenueName, ct).ConfigureAwait(false);
            }
            catch (GymbookException ex) when (ex.StatusCode == 401)
            {
                return new BookingResult { Outcome = BookingOutcome.LoginFailed, Message = ex.Message, Date = targetDate, Attempts = attempt };
            }
            catch (GymbookException ex) when (ex.StatusCode == 403)
            {
                throttleHits++;
                var backoff = Math.Min(3000, 200 * (1 << Math.Min(throttleHits - 1, 4)));
                _log.Warn($"第 {attempt} 次查询被限流（403），退避 {backoff}ms 后重试");
                await DelayAsync(backoff, ct).ConfigureAwait(false);
                continue;
            }
            catch (Exception ex)
            {
                _log.Warn($"第 {attempt} 次查询失败：{ex.Message}");
                await DelayAsync(cfg.RetryIntervalMs, ct).ConfigureAwait(false);
                continue;
            }

            var candidates = BookingPlanner.BuildCandidates(cfg, situation);
            var plan = candidates.FirstOrDefault();
            if (plan is null)
            {
                lastMessage = "当前没有匹配的可预约时间段";
                if (attempt == 1)
                {
                    _log.Info($"第 {attempt} 次：{lastMessage}，继续重试…");
                }
                await DelayAsync(cfg.RetryIntervalMs, ct).ConfigureAwait(false);
                continue;
            }
            lastSlots = plan.Slots;

            var (startTime, endTime) = BookingPlanner.ResolveTimeRange(venueInfo, plan.Slots);
            var order = new OrderRequest
            {
                OrderData = new OrderData
                {
                    OpenId = cfg.OpenId,
                    Nickname = cfg.Nickname,
                    Phone = cfg.Phone,
                    Date = targetDate,
                    VenueName = cfg.VenueName,
                    VenueType = cfg.VenueType,
                    SiteId = plan.SiteId,
                    TotalPrice = 0,
                    TimeList = plan.Slots,
                    StartTime = startTime,
                    EndTime = endTime,
                }
            };

            var t0 = Stopwatch.GetTimestamp();
            BookInfoResponse? resp;
            int status;
            try
            {
                resp = await _client.SubmitBookInfoAsync(order, ct).ConfigureAwait(false);
                status = _client.LastStatusCode;
            }
            catch (GymbookException ex) when (ex.StatusCode == 401)
            {
                return new BookingResult { Outcome = BookingOutcome.LoginFailed, Message = ex.Message, Date = targetDate, Attempts = attempt };
            }
            catch (GymbookException ex) when (ex.StatusCode == 403)
            {
                // 403 = 被服务器/WAF 限流。此时继续高频重试只会越陷越深，必须退避。
                throttleHits++;
                if (throttleHits == 1)
                {
                    _log.Error("服务器返回 HTTP 403（疑似限流/拦截）。已切入指数退避，请勿同时运行多个抢约任务。");
                }
                var backoff = Math.Min(3000, 200 * (1 << Math.Min(throttleHits - 1, 4)));
                _log.Warn($"第 {attempt} 次提交被限流（403），退避 {backoff}ms 后重试");
                await DelayAsync(backoff, ct).ConfigureAwait(false);
                continue;
            }
            catch (Exception ex)
            {
                _log.Warn($"第 {attempt} 次提交异常：{ex.Message}");
                await DelayAsync(cfg.RetryIntervalMs, ct).ConfigureAwait(false);
                continue;
            }
            var elapsed = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

            var range = $"{order.OrderData.StartTime}-{order.OrderData.EndTime}";
            if (status == 200 && (resp?.ConflictTimes.Count ?? 0) == 0)
            {
                total.Stop();
                var msg = $"预约成功：{targetDate} {plan.SiteId} 号场 {range}";
                _log.Info($"✔ {msg}（提交耗时 {elapsed:F0} ms，第 {attempt} 次尝试，共 {total.Elapsed.TotalMilliseconds:F0} ms）");
                Report("预约成功");

                JsonElement? orderResp = null;
                if (cfg.AutoCreateOrder)
                {
                    try
                    {
                        orderResp = await _client.CreateOrderAsync(order, ct).ConfigureAwait(false);
                        _log.Info($"建单完成：{orderResp?.ToString()?[..Math.Min(300, orderResp?.ToString()?.Length ?? 0)]}");
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"建单失败（预约本身已成功）：{ex.Message}");
                    }
                }

                return new BookingResult
                {
                    Outcome = BookingOutcome.Success,
                    Message = msg,
                    Date = targetDate,
                    SiteId = plan.SiteId,
                    Slots = plan.Slots,
                    TimeRange = range,
                    Attempts = attempt,
                    ElapsedMs = total.Elapsed.TotalMilliseconds,
                    OrderResponse = orderResp,
                };
            }

            if (status == 201)
            {
                lastMessage = resp?.Message ?? "服务器拒绝了本次预约";
                _log.Warn($"第 {attempt} 次被拒绝：{lastMessage}");
            }
            else if ((resp?.ConflictTimes.Count ?? 0) > 0)
            {
                lastMessage = $"时间段冲突（{string.Join(",", resp!.ConflictTimes)}）";
                _log.Info($"第 {attempt} 次：{lastMessage}，重试…");
            }
            else
            {
                lastMessage = resp?.Message ?? $"HTTP {status}";
                _log.Warn($"第 {attempt} 次未成功：{lastMessage}（{elapsed:F0} ms）");
            }

            await DelayAsync(cfg.RetryIntervalMs, ct).ConfigureAwait(false);
        }

        total.Stop();
        return new BookingResult
        {
            Outcome = BookingOutcome.AllTaken,
            Message = lastMessage.Length > 0 ? lastMessage : "重试次数用尽",
            Date = targetDate,
            Slots = lastSlots,
            Attempts = attempts,
            ElapsedMs = total.Elapsed.TotalMilliseconds,
        };
    }

    private static async Task DelayAsync(int ms, CancellationToken ct)
    {
        if (ms > 0) await Task.Delay(ms, ct).ConfigureAwait(false);
    }

    /// <summary>解析 JWT 的 exp（本地时间）；解析失败返回 null。</summary>
    public static DateTimeOffset? ReadJwtExpiry(string token)
    {
        try
        {
            var part = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
            part = part.PadRight(part.Length + (4 - part.Length % 4) % 4, '=');
            using var doc = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(part));
            if (doc.RootElement.TryGetProperty("exp", out var e) && e.TryGetInt64(out var v))
            {
                return DateTimeOffset.FromUnixTimeSeconds(v).ToLocalTime();
            }
        }
        catch { /* 非 JWT 或解析失败 */ }
        return null;
    }
}
