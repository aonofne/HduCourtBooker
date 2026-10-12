using System.Diagnostics;
using HduCourtBooker.Api;

namespace HduCourtBooker.Core;

/// <summary>
/// 用 /client/post_server_time 校准本地时钟，保证抢约时对齐服务器时间。
/// </summary>
public sealed class TimeSync
{
    private readonly GymbookClient _client;
    private readonly Logger _log;

    public TimeSync(GymbookClient client, Logger log)
    {
        _client = client;
        _log = log;
    }

    /// <summary>服务器时间 - 本地时间（毫秒），可正可负。</summary>
    public double OffsetMs { get; private set; }

    public double LastRttMs { get; private set; }
    public int Samples { get; private set; }

    public DateTime ServerNow => DateTime.Now.AddMilliseconds(OffsetMs);

    /// <summary>多次采样取最小 RTT 的那次结果（RTT 越小越接近真实偏移）。</summary>
    public async Task<double> SyncAsync(int samples = 5, CancellationToken ct = default)
    {
        double bestRtt = double.MaxValue;
        double bestOffset = OffsetMs;

        for (int i = 0; i < samples; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var t0 = Stopwatch.GetTimestamp();
                long serverMs = await _client.GetServerTimeMsAsync(ct).ConfigureAwait(false);
                var t1 = Stopwatch.GetTimestamp();
                double rtt = (t1 - t0) * 1000.0 / Stopwatch.Frequency;

                double localMidMs = DateTimeOffset.Now.ToUnixTimeMilliseconds() - rtt / 2.0;
                double offset = serverMs - localMidMs;

                if (rtt < bestRtt)
                {
                    bestRtt = rtt;
                    bestOffset = offset;
                }
                await Task.Delay(40, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn($"时间校准采样失败：{ex.Message}");
            }
        }

        if (bestRtt < double.MaxValue)
        {
            OffsetMs = bestOffset;
            LastRttMs = bestRtt;
            Samples++;
            _log.Info($"时钟校准完成：服务器 - 本地 = {OffsetMs:+0;-0;0} ms（往返 {bestRtt:F0} ms）");
        }
        else
        {
            _log.Warn("时钟校准全部失败，使用本地时间");
        }
        return OffsetMs;
    }
}
