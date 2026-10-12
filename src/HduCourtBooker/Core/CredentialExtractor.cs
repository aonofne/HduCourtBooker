using System.Text;
using System.Text.Json;

namespace HduCourtBooker.Core;

/// <summary>从本地凭据源提取到的登录信息。</summary>
public sealed class ExtractedCredential
{
    public string Token { get; init; } = "";
    public string OpenId { get; init; } = "";
    public string Nickname { get; init; } = "";
    public string Phone { get; init; } = "";
    public long Exp { get; init; }
    public string Source { get; init; } = "";
    public string SourcePath { get; init; } = "";

    public DateTime ExpiryLocal => DateTimeOffset.FromUnixTimeSeconds(Exp).LocalDateTime;
    public bool IsExpired => Exp > 0 && DateTimeOffset.Now.ToUnixTimeSeconds() >= Exp;
    public TimeSpan Remaining => Exp > 0
        ? DateTimeOffset.FromUnixTimeSeconds(Exp) - DateTimeOffset.Now
        : TimeSpan.Zero;

    public bool IsUsable => Token.Length > 0 && !IsExpired;
}

/// <summary>
/// 自动获取登录 token：钉钉（及 Edge/Chrome）用 Chromium 内核，把 H5 页面的
/// localStorage 存在 `&lt;profile&gt;\Default\Local Storage\leveldb` 里，
/// 其中 `_https://sportmeta.hdu.edu.cn\0token` 就是我们要的 JWT。
///
/// 实现方式：把 leveldb 快照复制到临时目录后按字节扫描（LevelDB 是 SSTable + WAL 结构，
/// 承载数据的块大多未压缩，JWT 作为可见 ASCII 完整保留在文件中）。
/// </summary>
public static class CredentialExtractor
{
    private const string Origin = "https://sportmeta.hdu.edu.cn";

    /// <summary>
    /// 常见 Chromium 系凭据来源（按优先级排列）。
    /// 钉钉的目录名带版本号后缀（如 DingTalk_133），因此动态枚举所有 DingTalk* 目录，
    /// 避免钉钉升级后目录名变化导致自动获取失效。
    /// </summary>
    public static IEnumerable<string> DefaultProfileRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // 1) 动态发现的所有钉钉 profile（按最近修改时间排序，优先最新的）
        var dingtalkDirs = new List<string>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(local, "DingTalk*"))
            {
                if (Directory.Exists(Path.Combine(dir, "Default", "Local Storage", "leveldb")))
                {
                    dingtalkDirs.Add(dir);
                }
            }
        }
        catch { /* 目录不可枚举时忽略 */ }

        foreach (var dir in dingtalkDirs
                     .OrderByDescending(d =>
                     {
                         try { return Directory.GetLastWriteTimeUtc(Path.Combine(d, "Default")); }
                         catch { return DateTime.MinValue; }
                     }))
        {
            yield return dir;
        }

        // 2) 固定候选，防止动态枚举被权限影响
        yield return Path.Combine(local, "DingTalk_133");
        yield return Path.Combine(local, "DingTalk");

        // 3) 浏览器（如果曾在浏览器里登录过同一个站点）
        yield return Path.Combine(local, "Microsoft", "Edge", "User Data");
        yield return Path.Combine(local, "Google", "Chrome", "User Data");
    }

    /// <summary>列出所有可扫描来源，供界面提示用。</summary>
    public static List<(string Path, bool Exists)> DescribeSources()
    {
        var result = new List<(string, bool)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in DefaultProfileRoots())
        {
            var leveldb = Path.Combine(root, "Default", "Local Storage", "leveldb");
            if (!seen.Add(leveldb)) continue;
            result.Add((leveldb, Directory.Exists(leveldb)));
        }
        return result;
    }

    /// <summary>扫描所有默认来源，返回可用的凭据（未过期、过期时间最晚的优先）。</summary>
    public static ExtractedCredential? FindBest(out string diagnostics,
        IEnumerable<string>? profileRoots = null, string? tempRoot = null)
    {
        var log = new StringBuilder();
        var all = new List<ExtractedCredential>();

        foreach (var root in profileRoots ?? DefaultProfileRoots())
        {
            var leveldb = Path.Combine(root, "Default", "Local Storage", "leveldb");
            if (!Directory.Exists(leveldb))
            {
                log.AppendLine($"  跳过（不存在）：{leveldb}");
                continue;
            }
            var found = ScanLevelDb(leveldb, Path.GetFileName(root.TrimEnd('\\')), tempRoot, log);
            all.AddRange(found);
        }

        diagnostics = log.ToString();
        if (all.Count == 0) return null;

        var usable = all.Where(c => c.IsUsable).ToList();
        var pool = usable.Count > 0 ? usable : all;
        return pool.OrderByDescending(c => c.Exp).First();
    }

    /// <summary>扫描单个 leveldb 目录。</summary>
    public static List<ExtractedCredential> ScanLevelDb(
        string leveldbDir, string sourceLabel, string? tempRoot, StringBuilder? log = null)
    {
        var result = new List<ExtractedCredential>();
        if (!Directory.Exists(leveldbDir)) return result;

        var temp = tempRoot ?? Path.Combine(Path.GetTempPath(), "HduCourtBooker-cred");
        var work = Path.Combine(temp, "ldb-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);

        try
        {
            // 钉钉运行时会锁住 leveldb，先做快照
            var blob = new MemoryStream();
            foreach (var file in Directory.EnumerateFiles(leveldbDir))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("LOCK", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var dest = Path.Combine(work, name);
                    using (var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        src.CopyTo(dst);
                    }
                    using (var fs = new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        fs.CopyTo(blob);
                    }
                    blob.WriteByte(0);
                }
                catch (Exception ex)
                {
                    log?.AppendLine($"  读取失败 {name}：{ex.Message}");
                }
            }

            var data = blob.ToArray();
            if (data.Length == 0) return result;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in FindJwtTokens(data))
            {
                if (!seen.Add(token)) continue;
                var payload = DecodeJwtPayload(token);
                if (payload is null) continue;
                if (!payload.Value.TryGetProperty("openid", out var oid)) continue; // 站点 token 才带 openid

                long exp = payload.Value.TryGetProperty("exp", out var e) && e.TryGetInt64(out var v) ? v : 0;
                var openid = oid.ValueKind == JsonValueKind.String
                    ? oid.GetString() ?? ""
                    : oid.ToString();

                result.Add(new ExtractedCredential
                {
                    Token = token,
                    OpenId = openid,
                    Nickname = FindNearby(data, token, "nickname"),
                    Phone = FindNearby(data, token, "phone"),
                    Exp = exp,
                    Source = sourceLabel,
                    SourcePath = leveldbDir,
                });
            }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* 忽略 */ }
        }
        return result;
    }

    /// <summary>按 JWT 结构在字节流中查找 token（eyJ... 三段式）。</summary>
    private static IEnumerable<string> FindJwtTokens(byte[] data)
    {
        for (int i = 0; i < data.Length - 30; i++)
        {
            if (data[i] != (byte)'e' || data[i + 1] != (byte)'y' || data[i + 2] != (byte)'J') continue;

            int end = i;
            while (end < data.Length && IsJwtChar(data[end])) end++;
            if (end - i < 30) continue;

            var token = Encoding.ASCII.GetString(data, i, end - i);
            if (token.Count(c => c == '.') != 2) continue;
            var parts = token.Split('.');
            if (parts.Any(p => p.Length < 8)) continue;

            i = end;
            yield return token;
        }
    }

    private static bool IsJwtChar(byte b) =>
        (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || (b >= '0' && b <= '9')
        || b == '-' || b == '_' || b == '.';

    private static JsonElement? DecodeJwtPayload(string token)
    {
        try
        {
            var part = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
            part = part.PadRight(part.Length + (4 - part.Length % 4) % 4, '=');
            var json = Convert.FromBase64String(part);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>在 token 附近的可见文本里找同源的字段值（LevelDB 键值相邻存放）。</summary>
    private static string FindNearby(byte[] data, string token, string key)
    {
        var tokenBytes = Encoding.ASCII.GetBytes(token);
        int pos = IndexOf(data, tokenBytes);
        if (pos < 0) return "";

        int from = Math.Max(0, pos - 8000);
        int to = Math.Min(data.Length, pos + 8000);
        var window = Encoding.Latin1.GetString(data, from, to - from);

        var idx = 0;
        while ((idx = window.IndexOf(key, idx, StringComparison.Ordinal)) >= 0)
        {
            int p = idx + key.Length;
            // 跳过 LevelDB 键值之间的少量控制字节
            int skip = 0;
            while (p + skip < window.Length && skip < 6 && !IsPrintable(window[p + skip])) skip++;
            var value = new StringBuilder();
            for (int q = p + skip; q < window.Length && value.Length < 40; q++)
            {
                if (!IsPrintable(window[q])) break;
                value.Append(window[q]);
            }
            var text = value.ToString().Trim();
            if (text.Length >= 2 && !text.StartsWith("__") && !text.StartsWith("META")
                && !text.Contains('/') && !text.Contains('{'))
            {
                return text;
            }
            idx = p;
        }
        return "";
    }

    private static bool IsPrintable(char c) => c >= 0x20 && c < 0x7f;

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }
}
