using System.Text.Json;
using System.Text.Json.Serialization;

namespace HduCourtBooker.Api;

/// <summary>
/// API 响应中的时间段数据。
///
/// ⚠️ 字段语义经前端源码确认（pages-book-soloBook 的 renderSiteInfo / selectTimes）：
/// <list type="bullet">
///   <item><c>oci_time_list</c> = **开放**（管理员放出、可被预约）的时间段下标；</item>
///   <item><c>book_time_list</c> = **已被预约**的时间段下标（前端据此显示"已预约"并把 used 置为 true）。</item>
/// </list>
/// 因此"可预约" = <c>oci_time_list</c> 去掉 <c>book_time_list</c>。
/// 早期版本误把 book_time_list 当成可约时段，导致把空场判成"不可预约"，不要改回去。
/// </summary>
public sealed class SiteSituationDto
{
    [JsonPropertyName("site_id")] public int SiteId { get; set; }

    /// <summary>已被预约的时间段（不可再选）。</summary>
    [JsonPropertyName("book_time_list")] public List<int> BookTimeList { get; set; } = new();

    /// <summary>已开放的时间段（场地排期里放出来的时段）。</summary>
    [JsonPropertyName("oci_time_list")] public List<int> OciTimeList { get; set; } = new();

    /// <summary>此刻真正可预约的时间段（开放 且 未被预约）。</summary>
    public List<int> AvailableSlots()
        => OciTimeList.Where(i => !BookTimeList.Contains(i)).OrderBy(i => i).ToList();

    /// <summary>可预约时段数。</summary>
    public int AvailableCount => OciTimeList.Count(i => !BookTimeList.Contains(i));

    /// <summary>已被预约的时段数（仅统计开放时段内的）。</summary>
    public int BookedCount => OciTimeList.Count(BookTimeList.Contains);
}

public sealed class SiteSituationResponse
{
    [JsonPropertyName("site_situation")] public List<SiteSituationDto> SiteSituation { get; set; } = new();

    [JsonPropertyName("extra_list")] public JsonElement? ExtraList { get; set; }
}

public sealed class Venue
{
    [JsonPropertyName("venue_name")] public string VenueName { get; set; } = "";
    [JsonPropertyName("venue_introduce")] public string VenueIntroduce { get; set; } = "";
    [JsonPropertyName("venue_image_url")] public string VenueImageUrl { get; set; } = "";
}

public sealed class VenueListResponse
{
    [JsonPropertyName("venue_list")] public List<Venue> VenueList { get; set; } = new();
}

public sealed class VenueInfoResponse
{
    /// <summary>时间点数组，长度 14；下标 i 表示第 i 个时间段，起始时间为 TimeList[i]。</summary>
    [JsonPropertyName("time_list")] public List<string> TimeList { get; set; } = new();

    /// <summary>可提前预约的天数（实测常为 2，即最多约到后天）。</summary>
    [JsonPropertyName("days_can_appoint")] public int DaysCanAppoint { get; set; }

    /// <summary>另一个可预约上限字段，与 days_can_appoint 取较大值使用。</summary>
    [JsonPropertyName("max_appoint")] public int MaxAppoint { get; set; }

    [JsonPropertyName("base_price")] public double BasePrice { get; set; }
    [JsonPropertyName("rest_list")] public List<int> RestList { get; set; } = new();

    /// <summary>可提前预约的最大天数（两个字段取较大值，取不到时回退 2）。</summary>
    public int EffectiveMaxDaysAhead
    {
        get
        {
            var v = Math.Max(DaysCanAppoint, MaxAppoint);
            return v > 0 ? v : 2;
        }
    }
}

/// <summary>creat_book_info 请求体：{ orderData: { ... } }</summary>
public sealed class OrderData
{
    [JsonPropertyName("openid")] public string OpenId { get; set; } = "";
    [JsonPropertyName("nickname")] public string Nickname { get; set; } = "";
    [JsonPropertyName("phone")] public string Phone { get; set; } = "";
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("venue_name")] public string VenueName { get; set; } = "";
    [JsonPropertyName("venue_type")] public string VenueType { get; set; } = "badminton";
    [JsonPropertyName("site_id")] public int SiteId { get; set; }
    [JsonPropertyName("total_price")] public double TotalPrice { get; set; }
    [JsonPropertyName("time_list")] public List<int> TimeList { get; set; } = new();
    [JsonPropertyName("start_time")] public string StartTime { get; set; } = "";
    [JsonPropertyName("end_time")] public string EndTime { get; set; } = "";

    public OrderData Clone() => new()
    {
        OpenId = OpenId, Nickname = Nickname, Phone = Phone, Date = Date,
        VenueName = VenueName, VenueType = VenueType, SiteId = SiteId,
        TotalPrice = TotalPrice, StartTime = StartTime, EndTime = EndTime,
        TimeList = new List<int>(TimeList),
    };
}

public sealed class OrderRequest
{
    [JsonPropertyName("orderData")] public OrderData OrderData { get; set; } = new();
}

/// <summary>creat_book_info 的响应（冲突时给出 conflict_times / available_times）。</summary>
public sealed class BookInfoResponse
{
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("conflict_times")] public List<int> ConflictTimes { get; set; } = new();
    [JsonPropertyName("available_times")] public List<int> AvailableTimes { get; set; } = new();
    [JsonPropertyName("data")] public JsonElement? Data { get; set; }
}

public sealed class ApiMessage
{
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("status")] public JsonElement? Status { get; set; }
    [JsonPropertyName("msg")] public string Msg { get; set; } = "";
    [JsonPropertyName("error")] public string Error { get; set; } = "";
}

public sealed class ServerTimeResponse
{
    [JsonPropertyName("data")] public long Data { get; set; }
}
