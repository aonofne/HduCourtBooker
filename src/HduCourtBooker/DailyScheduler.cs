using HduCourtBooker.Core;

namespace HduCourtBooker;

/// <summary>
/// 每日自动调度：在配置的触发时刻运行一次抢约，并自动排下一次。
/// 触发时刻前 WarmUpSecondsBefore 秒做预热与时钟校准。
/// </summary>
public sealed class DailyScheduler : IDisposable
{
    private readonly Logger _log;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string _fingerprint = "";

    public DailyScheduler(Logger log) => _log = log;

    public bool IsRunning => _loop is { IsCompleted: false };

    public event Action<string>? StatusChanged;
    public event Action<BookingResult>? RunCompleted;
    public event Action<int>? CountdownTick;

    /// <summary>下一次触发时刻（本地时间）。</summary>
    public DateTime NextTrigger { get; private set; }

    public void Start(AppConfig cfg, Func<AppConfig, DateTime, CancellationToken, Task<BookingResult>> runOnce)
    {
        // ⚠️ 防重复启动：曾经每次调用 Start 都会再开一条循环，多条循环在同一时刻一起抢约，
        // 瞬间打出几百个请求触发服务器限流（HTTP 403），全军覆没。
        // 现在：已在运行且配置未变 → 直接忽略；配置变了 → 先停旧的再起新的。
        var fingerprint = Fingerprint(cfg);
        if (_loop is { IsCompleted: false })
        {
            if (_fingerprint == fingerprint)
            {
                _log.Info("自动预约已在运行且配置未变，忽略重复启动请求");
                return;
            }
            _log.Info("自动预约配置已变更，重启调度循环");
        }

        Stop();
        if (!cfg.ScheduleEnabled)
        {
            _log.Info("自动预约未启用");
            return;
        }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _fingerprint = fingerprint;
        _log.Info($"自动预约已启动：每天 {cfg.TriggerTime} 触发，目标日期偏移 {cfg.DayOffset} 天"
                  + $"（提前 {cfg.LeadMilliseconds}ms，目标 {cfg.VenueName}）");
        _loop = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var trigger = ScheduleMath.NextTriggerLocal(cfg, DateTime.Now);
                    NextTrigger = trigger;
                    StatusChanged?.Invoke($"下次自动预约：{trigger:yyyy-MM-dd HH:mm:ss}");

                    // 预热阶段：触发前 N 秒
                    var preHeat = trigger.AddSeconds(-Math.Max(0, cfg.WarmUpSecondsBefore));
                    while (DateTime.Now < preHeat && !ct.IsCancellationRequested)
                    {
                        var remain = (int)Math.Max(0, (preHeat - DateTime.Now).TotalSeconds);
                        CountdownTick?.Invoke(remain);
                        await Task.Delay(500, ct).ConfigureAwait(false);
                    }

                    _log.Info("进入预热阶段");
                    if (!ct.IsCancellationRequested)
                    {
                        var result = await runOnce(cfg, trigger, ct).ConfigureAwait(false);
                        RunCompleted?.Invoke(result);
                    }

                    // 排到第二天同一时刻，避免刚跑完立刻又触发
                    var next = trigger.AddDays(1);
                    var wait = next - DateTime.Now;
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.Error("自动调度异常：" + ex.Message);
                    try { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }, ct);
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch { /* 忽略 */ }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _loop = null;
            _fingerprint = "";
            StatusChanged?.Invoke("自动预约已停止");
        }
    }

    /// <summary>影响调度行为的配置指纹，用于判断是否需要重启循环。</summary>
    private static string Fingerprint(AppConfig cfg) =>
        $"{cfg.TriggerTime}|{cfg.DayOffset}|{cfg.UseFixedDate}|{cfg.FixedDate}|{cfg.LeadMilliseconds}"
        + $"|{cfg.VenueName}|{cfg.MaxAttempts}|{cfg.RetryIntervalMs}|{cfg.WarmUpSecondsBefore}"
        + $"|{cfg.ScheduleEnabled}";

    public void Dispose() => Stop();
}
