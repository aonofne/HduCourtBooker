using System.Diagnostics;

namespace HduCourtBooker.Core;

/// <summary>
/// 抢约时刻的精确等待。
///
/// ⚠️ 这里是真实事故的修复点之一：早期版本用 <c>DateTime.Now</c> 作为自旋判据，
/// 而它的精度只有约 15.6ms，导致最后一刻 remain 永远算不出 0 或负数 —— 于是死循环自旋、
/// 把 CPU 打满，后续 HTTP 请求全部被拖到超时/被服务器限流（HTTP 403）。
/// 现在统一用 <see cref="Stopwatch"/>（高精度时间源）计算剩余时间。
/// </summary>
public static class Timing
{
    /// <summary>等待到指定本地时刻；超过 60ms 交给 Task.Delay，最后一小段用自旋保证精度。</summary>
    public static async Task WaitUntilAsync(DateTime targetLocal, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        double targetMs = (targetLocal - DateTime.Now).TotalMilliseconds;
        if (targetMs <= 0) return;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            double remainMs = targetMs - sw.Elapsed.TotalMilliseconds;
            if (remainMs <= 0) return;

            if (remainMs > 60)
            {
                // 留 30ms 余量给自旋，避免 Task.Delay 的调度延迟
                await Task.Delay(TimeSpan.FromMilliseconds(remainMs - 30), ct).ConfigureAwait(false);
            }
            else if (remainMs > 1.5)
            {
                // 自旋期间让出时间片，避免独占 CPU（对精度影响 < 2ms）
                Thread.SpinWait(50);
                await Task.Yield();
            }
            else
            {
                Thread.SpinWait(200);
            }
        }
    }
}
