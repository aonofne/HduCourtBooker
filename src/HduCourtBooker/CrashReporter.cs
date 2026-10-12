using System.Text;

namespace HduCourtBooker;

/// <summary>
/// 崩溃兜底：把未处理异常完整写入文件，避免"双击一闪而过、什么线索都没有"。
///
/// 真实背景：用户双击 exe 后窗口一闪而过，Windows 事件日志只给出
/// 0xe0434352（.NET 未处理异常）+ KERNELBASE.dll，无法定位。
/// 有了这个兜底，任何崩溃都会在程序目录与 %LOCALAPPDATA% 各留一份 crash-*.log。
/// </summary>
internal static class CrashReporter
{
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("AppDomain.UnhandledException", e.ExceptionObject as Exception, e.IsTerminating);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("TaskScheduler.UnobservedTaskException", e.Exception, false);
            e.SetObserved();
        };

        Application.ThreadException += (_, e) =>
            Write("Application.ThreadException", e.Exception, false);
    }

    public static void Write(string source, Exception? ex, bool terminating)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("================ HduCourtBooker 崩溃报告 ================");
            sb.AppendLine($"时间      : {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"来源      : {source}");
            sb.AppendLine($"是否终止  : {terminating}");
            sb.AppendLine($"OS        : {Environment.OSVersion}");
            sb.AppendLine($"CLR       : {Environment.Version}");
            sb.AppendLine($"程序目录  : {AppContext.BaseDirectory}");
            sb.AppendLine($"工作目录  : {Environment.CurrentDirectory}");
            sb.AppendLine($"命令行    : {Environment.CommandLine}");
            sb.AppendLine($"管理员    : {IsElevated()}");
            sb.AppendLine();
            sb.AppendLine(ex?.ToString() ?? "(无异常对象)");
            sb.AppendLine("=========================================================");
            var text = sb.ToString();

            foreach (var dir in CandidateDirs())
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                    File.WriteAllText(path, text, Encoding.UTF8);
                }
                catch { /* 换下一个位置 */ }
            }

            // 同时弹窗告知，避免用户以为"没反应"
            if (!terminating || ex is not null)
            {
                try
                {
                    if (Environment.GetEnvironmentVariable("HDUBOOKER_NO_DIALOG") != "1")
                    {
                        MessageBox.Show(
                            "程序遇到问题需要关闭。\n\n"
                            + $"错误：{ex?.Message}\n\n"
                            + "详细信息已写入 crash-*.log：\n"
                            + string.Join("\n", CandidateDirs().Select(d => Path.Combine(d, "crash-*.log"))),
                            "杭电羽毛球自动预约 —— 启动失败",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                catch { /* 弹窗失败也不能再抛 */ }
            }
        }
        catch { /* 兜底不能自身报错 */ }
    }

    private static IEnumerable<string> CandidateDirs()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HduCourtBooker");
        yield return AppContext.BaseDirectory;
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
