namespace HduCourtBooker.Core;

/// <summary>
/// 数据目录的选址策略。
///
/// ⚠️ 背景：程序原先把所有数据（config.json、日志）写在 exe 所在目录。
/// 一旦 exe 放在普通用户没有写权限的位置，或某些启动方式下写入被拒绝，
/// 就会在启动阶段抛未处理异常 —— 表现为"双击一闪而过"。
///
/// 现在的策略（按顺序取第一个**可写**的）：
/// <list type="number">
///   <item>%LOCALAPPDATA%\HduCourtBooker —— 用户目录，始终可写，推荐；</item>
///   <item>exe 目录\data —— 便携模式（目录确实可写时才会用，例如 U 盘/自建目录）；</item>
///   <item>我的文档\HduCourtBooker —— 最后的兜底。</item>
/// </list>
/// </summary>
public static class StorageLocation
{
    private static string? _resolved;
    private static readonly object Gate = new();

    /// <summary>当前生效的数据目录（进程内只解析一次）。</summary>
    public static string DataDir
    {
        get
        {
            if (_resolved is not null) return _resolved;
            lock (Gate)
            {
                return _resolved ??= Resolve();
            }
        }
    }

    /// <summary>是否使用了"程序目录旁的 data"（便携模式）。</summary>
    public static bool IsPortable { get; private set; }

    /// <summary>便携模式标记文件名：放在 exe 旁即强制使用同目录的 data。</summary>
    public const string PortableMarker = "portable.txt";

    /// <summary>日志子目录名（选址时会把"能不能建出这个子目录"作为判据）。</summary>
    private const string LogSubDir = "logs";

    private static string Resolve()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HduCourtBooker");
        var portable = Path.Combine(AppContext.BaseDirectory, "data");
        var documents = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HduCourtBooker");
        var desktop = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "HduCourtBooker");
        var temp = Path.Combine(Path.GetTempPath(), "HduCourtBooker");

        // 便携模式需要显式标记文件，避免"测试残留的 data 目录"意外劫持正式配置
        var portableRequested = File.Exists(Path.Combine(AppContext.BaseDirectory, PortableMarker));

        var order = portableRequested
            ? new[] { portable, appData, documents, desktop, temp }
            : new[] { appData, documents, desktop, portable, temp };

        // 不只是"能不能建目录"，而是把真正会用到的写入路径端到端试一遍
        foreach (var candidate in order)
        {
            if (TryPrepare(candidate))
            {
                IsPortable = string.Equals(candidate, portable, StringComparison.OrdinalIgnoreCase);
                return candidate;
            }
            Thread.Sleep(60);   // 安全软件瞬时锁：稍等再试下一个
        }

        // 理论上到不了这里（Temp 通常可写），保留兜底避免抛异常
        var last = Path.Combine(Path.GetTempPath(), "HduCourtBooker-" + Environment.ProcessId);
        try { Directory.CreateDirectory(Path.Combine(last, LogSubDir)); } catch { }
        return last;
    }

    /// <summary>
    /// 按优先级列出所有候选数据目录。
    /// 排序原则：**已经存有 config.json 的位置优先**，这样换位置运行（比如把 exe 拖到桌面）
    /// 也不会突然"配置没了"；便携模式标记存在时 exe 旁目录排最前。
    /// </summary>
    public static IReadOnlyList<string> CandidateDataDirs()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HduCourtBooker");
        var portable = Path.Combine(AppContext.BaseDirectory, "data");
        var documents = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HduCourtBooker");
        var desktop = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "HduCourtBooker");
        var temp = Path.Combine(Path.GetTempPath(), "HduCourtBooker");

        var portableRequested = File.Exists(Path.Combine(AppContext.BaseDirectory, PortableMarker));
        var all = new List<string> { appData, documents, desktop, portable, temp };
        if (portableRequested)
        {
            all.Remove(portable);
            all.Insert(0, portable);
        }

        // 已解析出的数据目录排最前，保证日志与配置同处一地
        if (_resolved is not null)
        {
            all.RemoveAll(d => string.Equals(d, _resolved, StringComparison.OrdinalIgnoreCase));
            all.Insert(0, _resolved);
        }
        else
        {
            // 没有任何已解析结果时：有 config.json 的位置优先
            var withConfig = all.Where(d => File.Exists(Path.Combine(d, "config.json"))).ToList();
            if (withConfig.Count > 0)
            {
                all = withConfig.Concat(all.Except(withConfig)).ToList();
            }
        }

        return all;
    }

    /// <summary>
    /// 真正把会用到的写入路径试一遍：建 data\logs、写一个临时文件、读回校验、删除。
    /// 任何一步失败都说明这个位置不可用 —— 只探测父目录是不够的
    /// （实测出现过"父目录可写、建 logs 子目录却被拒绝"的情况）。
    /// </summary>
    private static bool TryPrepare(string dir)
    {
        foreach (var attempt in new[] { 0, 250 })
        {
            try
            {
                if (attempt > 0) Thread.Sleep(attempt);
                Directory.CreateDirectory(Path.Combine(dir, LogSubDir));

                var probe = Path.Combine(dir, LogSubDir, $".write-probe-{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probe, "ok");
                var read = File.ReadAllText(probe);
                File.Delete(probe);
                if (read != "ok") return false;

                // 顺带确认配置文件的路径也可写
                var cfgProbe = Path.Combine(dir, $".cfg-probe-{Guid.NewGuid():N}.tmp");
                File.WriteAllText(cfgProbe, "ok");
                File.Delete(cfgProbe);
                return true;
            }
            catch
            {
                // 再试一次，仍然失败就判定该位置不可用
            }
        }
        return false;
    }

    /// <summary>轻量可写探测（只建目录 + 写删一个文件），供界面显示用。</summary>
    public static bool IsWritable(string dir) => TryPrepare(dir);

    /// <summary>
    /// 把别处已有的数据搬到现在的位置，避免升级/换位置后丢失 token、配置与日志。
    /// 搜索顺序：exe 旁 data → 其它候选目录。只在目标文件不存在时复制，不删除源文件。
    /// </summary>
    public static string MigrateLegacyIfNeeded(Action<string>? log = null)
    {
        var target = DataDir;
        var sources = new List<string> { Path.Combine(AppContext.BaseDirectory, "data") };
        sources.AddRange(CandidateDataDirs());

        var copied = 0;
        foreach (var src in sources.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(src, target, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Directory.Exists(src)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(src, file);
                    var dest = Path.Combine(target, rel);
                    if (File.Exists(dest)) continue;
                    var dir = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.Copy(file, dest);
                    copied++;
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"从 {src} 迁移数据时出错（不影响运行）：{ex.Message}");
            }
        }

        if (copied > 0)
        {
            log?.Invoke($"已迁移 {copied} 个文件到 {target}");
            return $"已迁移 {copied} 个文件";
        }
        return "";
    }
}
