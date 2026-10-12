namespace HduCourtBooker;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 最先安装崩溃兜底：任何未处理异常都会留下 crash-*.log，而不是"一闪而过"
        CrashReporter.Install();

        try
        {
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            CrashReporter.Write("Program.Main", ex, terminating: true);
            throw;
        }
    }
}
