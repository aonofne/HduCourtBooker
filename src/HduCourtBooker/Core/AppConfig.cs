using System.Text.Json.Serialization;

namespace HduCourtBooker.Core;

/// <summary>用户可配置的预约计划。</summary>
public sealed class AppConfig
{
    // ---------- 账号 ----------
    /// <summary>
    /// 登录令牌（Bearer），**明文**保存在配置里，便于查看与手工替换。
    /// 键名沿用 TokenProtected 以兼容旧配置（旧值为 dpapi: 密文时仍可读取）。
    /// </summary>
    public string TokenProtected { get; set; } = "";

    /// <summary>运行时使用的 token（不序列化）。</summary>
    [JsonIgnore] public string Token { get; set; } = "";

    /// <summary>openid（提交预约时必填，登录后在浏览器/钉钉里可见）。</summary>
    public string OpenId { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string Phone { get; set; } = "";

    // ---------- 场地 ----------
    public string VenueType { get; set; } = "badminton";
    public string VenueName { get; set; } = "综合馆羽毛球";

    /// <summary>场地优先级：列表靠前的场地优先（1..12）。留空表示按 1..12 顺序。</summary>
    public List<int> SitePreference { get; set; } = new();

    /// <summary>
    /// 时间段优先级：每个元素是一个"连续时间段"（time_list 下标），靠前优先。
    ///
    /// ⚠️ 每单最多只能约 2 小时（站点规则），所以每个片段不应超过
    /// <see cref="MaxSlotsPerBooking"/> 个下标。
    ///
    /// 时间下标对照（14 段，注意 11:40 / 13:20 是 40 分钟段）：
    ///   0=08:00 1=09:00 2=10:00 3=11:00 4=11:40 5=13:20 6=14:00
    ///   7=15:00 8=16:00 9=17:00 10=18:00 11=19:00 12=20:00 13=21:00
    ///
    /// 默认：第一优先 19:00-21:00（11,12），其次 18:00-20:00（10,11），再次 20:00-21:00（12）。
    /// </summary>
    public List<List<int>> SlotPreference { get; set; } = new()
    {
        new List<int> { 11, 12 },
        new List<int> { 10, 11 },
        new List<int> { 12 },
    };

    /// <summary>单次预约允许的最大连续时间段数（站点规则：每单最多 2 小时）。</summary>
    public int MaxSlotsPerBooking { get; set; } = 2;

    /// <summary>站点规定的单次预约时长上限（小时），用于界面提示与校验。</summary>
    public const int MaxHoursPerBooking = 2;

    // ---------- 目标日期 ----------
    /// <summary>目标日期偏移：2 = 后天（默认），1 = 明天，0 = 今天。</summary>
    public int DayOffset { get; set; } = 2;

    /// <summary>true 时忽略 DayOffset，固定预约该日期（yyyy-MM-dd），适合"提前定好某一天"。</summary>
    public bool UseFixedDate { get; set; }
    public string FixedDate { get; set; } = "";

    // ---------- 触发时刻 ----------
    /// <summary>每天触发时间（本地时间），默认 20:00:00.000。</summary>
    public string TriggerTime { get; set; } = "20:00:00.000";

    /// <summary>提前多少毫秒发起请求（补偿网络往返）。</summary>
    public int LeadMilliseconds { get; set; } = 250;

    /// <summary>是否用服务器时间校准本地时钟。</summary>
    public bool UseServerTimeSync { get; set; } = true;

    /// <summary>触发前多少秒开始预热（查询场地图、建立连接）。</summary>
    public int WarmUpSecondsBefore { get; set; } = 20;

    // ---------- 重试 ----------
    /// <summary>抢约失败后的重试总次数。</summary>
    public int MaxAttempts { get; set; } = 12;

    /// <summary>重试间隔（毫秒）。</summary>
    public int RetryIntervalMs { get; set; } = 120;

    /// <summary>是否每天在 TriggerTime 自动运行。</summary>
    public bool ScheduleEnabled { get; set; }

    /// <summary>预约成功后是否自动调用 creat_order 建单。</summary>
    public bool AutoCreateOrder { get; set; } = true;

    public string ApiBase { get; set; } = DefaultApiBase;

    /// <summary>是否把抢约的原始 HTTP 往返记录到 data\logs\exchange-*.log（便于核对接口）。</summary>
    public bool LogHttpExchanges { get; set; } = true;

    /// <summary>场馆预约接口根地址。</summary>
    public const string DefaultApiBase = "https://sportmeta.hdu.edu.cn/book";

    public static AppConfig CreateDefault() => new();
}
