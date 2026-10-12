using System.Text;
using System.Text.Json;

namespace HduCourtBooker.Core;

/// <summary>
/// 抢约往返报文记录：把真实发出的请求与收到的响应写入 exchange-*.log，
/// 便于事后核对接口契约（token 会自动脱敏）。
/// </summary>
public sealed class ExchangeLog : IDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly bool _enabled;

    public ExchangeLog(string logDir, bool enabled)
    {
        _enabled = enabled;
        _path = System.IO.Path.Combine(logDir, $"exchange-{DateTime.Now:yyyyMMdd}.log");
        if (_enabled)
        {
            // 目录不可写时不要让构造抛异常（否则又是一个"启动即崩溃"）
            try { Directory.CreateDirectory(logDir); }
            catch { _enabled = false; }
        }
    }

    public string Path => _path;

    public void Record(string direction, string text)
    {
        if (!_enabled) return;
        var masked = Mask(text);
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{direction}] {masked}";
        lock (_gate)
        {
            try { File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8); }
            catch { /* 记录失败不影响抢约 */ }
        }
    }

    /// <summary>脱敏：Bearer token 只保留头尾。</summary>
    internal static string Mask(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var idx = text.IndexOf("Bearer ", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return text;
        var start = idx + "Bearer ".Length;
        var end = start;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != '"') end++;
        var token = text[start..end];
        if (token.Length <= 12) return text;
        return text[..start] + token[..6] + "…" + token[^4..] + text[end..];
    }

    public void Dispose() { }
}

/// <summary>极简文件日志：同时供界面实时显示。</summary>
public sealed class Logger
{
    private readonly object _gate = new();
    private string _logDir;
    private bool _disabled;

    public Logger(string logDir)
    {
        _logDir = logDir;
        Directory.CreateDirectory(logDir);
        Shared = this;
    }

    /// <summary>
    /// 自动选址的构造方式：逐个候选目录尝试建 logs 并写一个真实文件。
    /// 全部失败也不抛异常 —— 宁可没有日志，也不能让程序起不来。
    /// 这条是为真实故障加的：曾出现"父目录探测通过、真正建 logs 子目录却被拒绝"，
    /// 结果启动阶段抛未处理异常，表现为"双击一闪而过"。
    /// </summary>
    public Logger(IEnumerable<string> candidateDataDirs)
    {
        Exception? last = null;
        foreach (var dir in candidateDataDirs)
        {
            try
            {
                var sub = Path.Combine(dir, "logs");
                Directory.CreateDirectory(sub);
                var probe = Path.Combine(sub, $".probe-{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);

                _logDir = sub;
                Shared = this;
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(60);
            }
        }

        // 所有位置都写不了：禁用落盘，但程序照常运行
        _disabled = true;
        _logDir = Path.Combine(Path.GetTempPath(), "HduCourtBooker", "logs");
        Shared = this;
        try { Console.Error.WriteLine("日志目录不可写，已禁用日志落盘：" + last?.Message); }
        catch { /* 忽略 */ }
    }

    public event Action<string>? LineWritten;

    public string CurrentFile => Path.Combine(_logDir, $"booker-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>日志所在目录（调用方可据此反推真正的数据目录）。</summary>
    public string LogDirectory => _logDir;

    /// <summary>日志是否被禁用（所有候选目录都不可写时）。</summary>
    public bool IsDisabled => _disabled;

    /// <summary>全局兜底日志（供无法拿到实例的静态代码使用，例如 HTTP 客户端的限流告警）。</summary>
    public static Logger? Shared { get; private set; }

    public static void StaticWarn(string message) => Shared?.Warn(message);
    public static void StaticInfo(string message) => Shared?.Info(message);

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    public void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] {message}";
        if (!_disabled)
        {
            lock (_gate)
            {
                try
                {
                    File.AppendAllText(CurrentFile, line + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // 写不进去就停用，避免每一行都抛异常拖慢抢约
                    _disabled = true;
                }
            }
        }
        try { LineWritten?.Invoke(line); } catch { /* 界面异常忽略 */ }
    }

    public void CleanupOldLogs(int keepDays = 30)
    {
        try
        {
            foreach (var f in Directory.GetFiles(_logDir, "booker-*.log")
                         .Concat(Directory.GetFiles(_logDir, "exchange-*.log")))
            {
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-keepDays))
                {
                    File.Delete(f);
                }
            }
        }
        catch { /* 忽略 */ }
    }
}

/// <summary>配置读写（JSON 落盘）。</summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, Opts);
                if (cfg is not null) return cfg;
            }
        }
        catch
        {
            // 配置损坏时回退默认值，并备份原文件
            try { File.Move(path, path + ".bak", overwrite: true); } catch { }
        }
        var def = AppConfig.CreateDefault();
        Save(path, def);
        return def;
    }

    public static void Save(string path, AppConfig cfg)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, Opts), Encoding.UTF8);
    }
}
