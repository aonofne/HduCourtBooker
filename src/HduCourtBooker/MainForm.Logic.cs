using System.Text;
using HduCourtBooker.Api;
using HduCourtBooker.Core;

namespace HduCourtBooker;

public sealed partial class MainForm
{
    // ---------------- 登录凭据 ----------------

    /// <summary>从钉钉/浏览器本地存储自动提取 token，并校验其真实有效性。</summary>
    private async Task AutoFetchCredentialAsync()
    {
        _btnAutoCred.Enabled = false;
        _lblTokenState.Text = "正在扫描钉钉本地数据…";
        _lblTokenState.ForeColor = Color.DimGray;
        try
        {
            var tempRoot = Path.Combine(_dataDir, "tmp");
            var best = await Task.Run(() => CredentialExtractor.FindBest(out var diag, null, tempRoot));
            _log.Info("已扫描本地凭据来源");

            if (best is null)
            {
                _lblTokenState.Text = "未找到 token：请先在钉钉里打开一次「场馆预约」再点自动获取";
                _lblTokenState.ForeColor = Color.Firebrick;
                _log.Warn("未在本地存储中找到 token（先在钉钉里打开一次「场馆预约」页面）");
                return;
            }

            if (!best.IsUsable)
            {
                _lblTokenState.Text = $"找到的 token 已过期（{best.ExpiryLocal:MM-dd HH:mm}）——"
                                      + "请在钉钉里重新打开「场馆预约」后再点一次自动获取";
                _lblTokenState.ForeColor = Color.Firebrick;
                _log.Warn($"找到的 token 已过期：{best.ExpiryLocal:yyyy-MM-dd HH:mm:ss}");
                return;
            }

            _txtToken.Text = best.Token;
            _txtOpenId.Text = best.OpenId;
            if (best.Nickname.Length > 0) _txtNickname.Text = best.Nickname;
            _lblTokenState.Text = $"已获取（{best.Source}）过期 {best.ExpiryLocal:MM-dd HH:mm}"
                                  + $"（剩余 {best.Remaining.TotalHours:F1} 小时）";
            _lblTokenState.ForeColor = best.IsUsable ? Color.SeaGreen : Color.Firebrick;
            _log.Info($"已获取凭据：openid={best.OpenId}，来源={best.Source}，"
                      + $"过期={best.ExpiryLocal:yyyy-MM-dd HH:mm:ss}");

            SaveConfigFromUi();
            await VerifyCredentialAsync(best.OpenId);

            // 拿到可用凭据后自动武装（除非用户手动关过）：避免"以为开着、其实没开"
            if (!_daily.IsRunning && !_cfg.ScheduleEnabled)
            {
                _chkSchedule.Checked = true;   // 触发 CheckedChanged → ApplyScheduleConfig
                _log.Info("已自动开启每日自动预约（如需关闭请取消勾选）");
            }
        }
        catch (Exception ex)
        {
            _lblTokenState.Text = "自动获取失败：" + ex.Message;
            _lblTokenState.ForeColor = Color.Firebrick;
            _log.Error("自动获取凭据失败：" + ex.Message);
        }
        finally
        {
            _btnAutoCred.Enabled = true;
        }
    }

    /// <summary>调用只读接口 post_order_info 验证 token 归属与有效性。</summary>
    private async Task VerifyCredentialAsync(string openId)
    {
        try
        {
            var info = await _client.GetOrderInfoAsync(openId).ConfigureAwait(true);
            int count = 0;
            if (info is { } el && el.TryGetProperty("order_info", out var arr)
                && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                count = arr.GetArrayLength();
            }
            _log.Info($"token 校验通过：账号 openid={openId}，历史订单 {count} 条");
            _lblState.Text = "登录有效";
            _lblTokenState.Text += $"\n已校验：历史订单 {count} 条";
        }
        catch (GymbookException ex) when (ex.StatusCode == 401)
        {
            _lblTokenState.Text = "token 被服务器拒绝，请在钉钉里重新打开「场馆预约」后再获取";
            _lblTokenState.ForeColor = Color.Firebrick;
            _log.Warn("token 校验失败：" + ex.Message);
        }
        catch (Exception ex)
        {
            _log.Warn("token 校验未完成（网络问题，不影响使用）：" + ex.Message);
        }
    }

    private static void OpenDingTalk()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dingtalk://dingtalkclient/action/open_app",
                UseShellExecute = true,
            });
        }
        catch
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://www.dingtalk.com/",
                    UseShellExecute = true,
                });
            }
            catch { /* 忽略 */ }
        }
    }

    /// <summary>更新 token 状态显示（由 UI 定时器调用）。</summary>
    private void UpdateTokenState()
    {
        var token = _txtToken.Text.Trim();
        if (token.Length == 0)
        {
            return;
        }
        if (token.Count(c => c == '.') != 2)
        {
            _lblTokenState.Text = "token 格式看起来不对（应为三段式 JWT）";
            _lblTokenState.ForeColor = Color.Firebrick;
            return;
        }
        var exp = ReadJwtExpiry(token);
        if (exp is null)
        {
            _lblTokenState.Text = "已填入 token（无法解析过期时间）";
            return;
        }
        var remain = exp.Value - DateTimeOffset.Now;
        if (remain <= TimeSpan.Zero)
        {
            _lblTokenState.Text = $"token 已过期（{exp.Value:MM-dd HH:mm}）——请在钉钉里重开「场馆预约」后点「从钉钉自动获取」";
            _lblTokenState.ForeColor = Color.Firebrick;
        }
        else
        {
            _lblTokenState.Text = $"token 有效，剩余 {remain.TotalHours:F1} 小时后过期（{exp.Value:MM-dd HH:mm}）";
            _lblTokenState.ForeColor = remain.TotalHours < 1 ? Color.DarkOrange : Color.SeaGreen;
        }
    }

    private static DateTimeOffset? ReadJwtExpiry(string token)
        => BookingScheduler.ReadJwtExpiry(token);

    // ---------------- 配置读写 ----------------

    private void LoadConfigToUi()
    {
        _numDayOffset.Value = Math.Clamp(_cfg.DayOffset, 0, 7);
        _chkFixedDate.Checked = _cfg.UseFixedDate;
        if (DateTime.TryParse(_cfg.FixedDate, out var fd)) _dtFixedDate.Value = fd;
        _txtToken.Text = SecretProtector.Unprotect(_cfg.TokenProtected);
        if (SecretProtector.IsLegacyEncrypted(_cfg.TokenProtected))
        {
            // 构造期不能弹模态窗（句柄还没建好），只记日志；窗口显示后由自动获取流程引导
            _log.Warn("配置里是旧版加密 token，将在启动后自动重新获取");
        }
        _txtOpenId.Text = _cfg.OpenId;
        _txtNickname.Text = _cfg.Nickname;
        _txtPhone.Text = _cfg.Phone;
        _txtVenueName.Text = _cfg.VenueName;
        _txtSites.Text = FormatSites(_cfg.SitePreference);
        _txtTriggerTime.Text = _cfg.TriggerTime;
        _numLead.Value = Math.Clamp(_cfg.LeadMilliseconds, 0, 3000);
        _numMaxSlots.Value = Math.Clamp(_cfg.MaxSlotsPerBooking, 1, 6);
        _numAttempts.Value = Math.Clamp(_cfg.MaxAttempts, 1, 200);
        _numRetryMs.Value = Math.Clamp(_cfg.RetryIntervalMs, 0, 5000);
        _chkUseServerTime.Checked = _cfg.UseServerTimeSync;
        _chkAutoCreateOrder.Checked = _cfg.AutoCreateOrder;
        _chkLogExchange.Checked = _cfg.LogHttpExchanges;
        _chkSchedule.Checked = _cfg.ScheduleEnabled;
        UpdatePreview();
    }

    private void SaveConfigFromUi()
    {
        _cfg.DayOffset = (int)_numDayOffset.Value;
        _cfg.UseFixedDate = _chkFixedDate.Checked;
        _cfg.FixedDate = _dtFixedDate.Value.ToString("yyyy-MM-dd");
        _cfg.Token = _txtToken.Text.Trim();
        _cfg.TokenProtected = SecretProtector.Protect(_cfg.Token);
        _cfg.OpenId = _txtOpenId.Text.Trim();
        _cfg.Nickname = _txtNickname.Text.Trim();
        _cfg.Phone = _txtPhone.Text.Trim();
        _cfg.VenueName = _txtVenueName.Text.Trim();
        _cfg.SitePreference = ParseSites(_txtSites.Text);
        _cfg.TriggerTime = _txtTriggerTime.Text.Trim();
        _cfg.LeadMilliseconds = (int)_numLead.Value;
        _cfg.MaxSlotsPerBooking = (int)_numMaxSlots.Value;        _cfg.MaxAttempts = (int)_numAttempts.Value;
        _cfg.RetryIntervalMs = (int)_numRetryMs.Value;
        _cfg.UseServerTimeSync = _chkUseServerTime.Checked;
        _cfg.AutoCreateOrder = _chkAutoCreateOrder.Checked;
        _cfg.LogHttpExchanges = _chkLogExchange.Checked;
        _cfg.ScheduleEnabled = _chkSchedule.Checked;
        _cfg.SlotPreference = BuildSlotPreferenceFromGrid();
        ConfigStore.Save(_configPath, _cfg);
        _client.Token = _cfg.Token;
    }

    private static string FormatSites(List<int> sites)
        => sites.Count == 0 ? "" : string.Join(",", sites);

    private static List<int> ParseSites(string text)
    {
        var result = new List<int>();
        foreach (var part in text.Split(new[] { ',', '，', ' ', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Trim();
            var dash = p.IndexOf('-');
            if (dash > 0 && int.TryParse(p[..dash], out var a) && int.TryParse(p[(dash + 1)..], out var b))
            {
                for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) result.Add(i);
            }
            else if (int.TryParse(p, out var v))
            {
                result.Add(v);
            }
        }
        return result.Distinct().ToList();
    }

    // ---------------- 偏好网格 ----------------

    /// <summary>单元格 (row=场地, col=时间段) -> 偏好得分；越大越优先。</summary>
    private readonly Dictionary<(int Site, int Slot), int> _picks = new();

    private List<List<int>> BuildSlotPreferenceFromGrid()
    {
        // 把被点击的单元格按优先级聚合成"连续时间段"列表
        var ordered = _picks.OrderByDescending(kv => kv.Value)
                            .Select(kv => (kv.Key.Site, kv.Key.Slot))
                            .ToList();
        var groups = new List<List<int>>();
        foreach (var (_, slot) in ordered)
        {
            var g = groups.FirstOrDefault(x => x.Contains(slot) || x[^1] + 1 == slot);
            if (g is null)
            {
                groups.Add(new List<int> { slot });
            }
            else if (!g.Contains(slot))
            {
                g.Add(slot);
            }
        }
        // 只保留连续段
        var result = new List<List<int>>();
        foreach (var g in groups)
        {
            var sorted = g.Distinct().OrderBy(x => x).ToList();
            var run = new List<int> { sorted[0] };
            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i] == run[^1] + 1) run.Add(sorted[i]);
                else
                {
                    result.Add(run);
                    run = new List<int> { sorted[i] };
                }
            }
            result.Add(run);
        }
        return result.Count > 0 ? result : _cfg.SlotPreference;
    }

    /// <summary>
    /// 把一组时间段下标格式化成可读的时间段文字。
    /// ⚠️ 不能简单用 下标+1 取结束时间：本馆 11:40 / 13:20 是 40 分钟段，
    /// 真正的结束时间是该片段最后一个时段的下一段起点。
    /// </summary>
    private string DescribeGroup(List<int> group)
    {
        if (group.Count == 0) return "-";
        var start = TimeLabel(group[0]);
        var endIdx = Math.Min(group[^1] + 1, Math.Max(_timeList.Count - 1, 0));
        var end = TimeLabel(endIdx);
        var hours = group.Count;   // 每段按 1 小时计（站点规则：每单最多 2 小时）
        return $"{start}-{end}({hours}h)";
    }

    private void Grid_CellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        if (_grid.Rows[e.RowIndex].Tag is not int siteId) return;
        if (_grid.Columns[e.ColumnIndex].Tag is not int slot) return;

        var key = (siteId, slot);
        if (_picks.ContainsKey(key))
        {
            _picks.Remove(key);
        }
        else
        {
            if (!IsSlotBookable(siteId, slot))
            {
                _log.Info($"{siteId} 号场 {TimeLabel(slot)} 当前不可预约");
                return;
            }
            int next = _picks.Count == 0 ? 1 : _picks.Values.Max() + 1;
            _picks[key] = next;
        }
        _grid.Invalidate();
        UpdatePreview();
    }

    private readonly HashSet<(int Site, int Slot)> _bookable = new();

    /// <summary>已被预约的格子（oci 开放但 book 里已有），用于着色区分。</summary>
    private readonly HashSet<(int Site, int Slot)> _booked = new();

    private bool IsSlotBookable(int siteId, int slot) => _bookable.Contains((siteId, slot));

    private string TimeLabel(int slot)
        => _timeList.Count > slot && slot >= 0 ? _timeList[slot] : $"#{slot}";

    private List<string> _timeList = new();

    private void UpdatePreview()
    {
        var sites = ParseSites(_txtSites.Text);
        var groups = _picks.Count > 0
            ? BuildSlotPreferenceFromGrid()
            : _cfg.SlotPreference;

        var sb = new StringBuilder();
        sb.Append("优先时间段：");
        sb.Append(groups.Count == 0
            ? "（自动选任意可约时段）"
            : string.Join(" > ", groups.Select(DescribeGroup)));
        sb.AppendLine();
        sb.Append("优先场地：");
        sb.Append(sites.Count == 0 ? "（1→12 顺序）" : string.Join(",", sites));
        sb.Append($"　目标日期偏移：{_numDayOffset.Value} 天");
        _lblPreview.Text = sb.ToString();
    }

    private void Grid_CellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        var row = _grid.Rows[e.RowIndex];
        if (row.Tag is not int siteId) return;
        if (_grid.Columns[e.ColumnIndex].Tag is not int slot) return;

        bool bookable = IsSlotBookable(siteId, slot);
        bool picked = _picks.ContainsKey((siteId, slot));
        bool booked = _booked.Contains((siteId, slot));

        if (booked)
        {
            // 已被别人预约：红底，不可点
            e.Graphics!.FillRectangle(new SolidBrush(Color.FromArgb(248, 220, 220)), e.CellBounds);
            TextRenderer.DrawText(e.Graphics, "已约", _grid.Font, e.CellBounds, Color.FromArgb(170, 60, 60),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            e.Paint(e.CellBounds, DataGridViewPaintParts.Border);
            e.Handled = true;
            return;
        }

        if (!bookable)
        {
            // 未开放：灰底，不可点
            e.Graphics!.FillRectangle(new SolidBrush(Color.FromArgb(238, 238, 238)), e.CellBounds);
            e.Paint(e.CellBounds, DataGridViewPaintParts.Border);
            e.Handled = true;
            return;
        }

        if (picked)
        {
            e.Graphics!.FillRectangle(new SolidBrush(Color.FromArgb(90, 140, 220)), e.CellBounds);
            var text = _picks[(siteId, slot)].ToString();
            TextRenderer.DrawText(e.Graphics, text, _grid.Font, e.CellBounds, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            e.Paint(e.CellBounds, DataGridViewPaintParts.Border);
            e.Handled = true;
        }
    }

    // ---------------- 网络操作 ----------------

    /// <summary>
    /// 检查登录：只调用**只读**接口判断 token 是否有效。
    ///
    /// ⚠️ 不要再往 creat_book_info 发"故意无效"的探测请求：
    /// 那会把服务端的业务校验错误（例如 HTTP 400「缺少必要参数」）误报成"检查登录失败"，
    /// 让用户以为 token 坏了。token 有没有效，看 post_order_info 是否 200 就够了。
    /// </summary>
    private async Task CheckLoginAsync()
    {
        SaveConfigFromUi();
        _client.Token = _cfg.Token;
        if (string.IsNullOrWhiteSpace(_cfg.Token))
        {
            _log.Warn("尚未填写 token");
            MessageBox.Show("请先填写 token。\n\n可点「★ 从钉钉自动获取」自动获取；"
                            + "手动获取方法见 README.md。", "检查登录",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // openid 优先用配置里的；没有就从 token 载荷里取
        var openId = _cfg.OpenId;
        if (string.IsNullOrWhiteSpace(openId))
        {
            openId = ReadJwtOpenId(_cfg.Token) ?? "";
            if (openId.Length > 0)
            {
                _txtOpenId.Text = openId;
                _cfg.OpenId = openId;
                SaveConfigFromUi();
            }
        }

        try
        {
            await _timeSync.SyncAsync(3).ConfigureAwait(true);
            var venue = await _client.GetVenueInfoAsync(
                _cfg.VenueType, _cfg.VenueName, DateTime.Now.ToString("yyyy-MM-dd")).ConfigureAwait(true);
            _log.Info($"场地信息读取成功：{venue?.TimeList.Count ?? 0} 个时间段，"
                      + $"最多可提前 {venue?.EffectiveMaxDaysAhead} 天预约");

            if (openId.Length == 0)
            {
                _log.Warn("缺少 openid，无法完成鉴权校验（不影响抢约，抢约时会自动从 token 取）");
                MessageBox.Show("已读取到场地信息，但缺少 openid，无法确认账号。\n"
                                + "请点「★ 从钉钉自动获取」补齐。", "检查登录",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 只读接口：能返回历史订单就说明 token 有效且指向该账号
            var info = await _client.GetOrderInfoAsync(openId).ConfigureAwait(true);
            int count = 0;
            if (info is { } el && el.TryGetProperty("order_info", out var arr)
                && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                count = arr.GetArrayLength();
            }

            _lblState.Text = "登录有效";
            _log.Info($"✔ token 有效：账号 openid={openId}，历史订单 {count} 条");
            MessageBox.Show($"token 有效。\n\n账号 openid：{openId}\n历史订单：{count} 条",
                "检查登录", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (GymbookException ex) when (ex.StatusCode == 401)
        {
            _log.Error("token 无效或已过期：" + ex.Message);
            MessageBox.Show("token 无效或已过期。\n\n请到钉钉里重新打开一次「场馆预约」，"
                            + "再点「★ 从钉钉自动获取」。", "检查登录",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            _log.Error("检查登录失败（网络或服务异常）：" + ex.Message);
            MessageBox.Show("检查登录未完成：" + ex.Message, "检查登录",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>从 JWT 载荷里取 openid。</summary>
    private static string? ReadJwtOpenId(string token)
    {
        try
        {
            var part = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
            part = part.PadRight(part.Length + (4 - part.Length % 4) % 4, '=');
            using var doc = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(part));
            if (doc.RootElement.TryGetProperty("openid", out var v))
            {
                return v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : v.ToString();
            }
        }
        catch { /* 非 JWT */ }
        return null;
    }

    private async Task RefreshMapAsync()
    {
        SaveConfigFromUi();
        try
        {
            var date = _chkFixedDate.Checked && DateTime.TryParse(_cfg.FixedDate, out var fd)
                ? fd.ToString("yyyy-MM-dd")
                : DateTime.Now.AddDays((int)_numDayOffset.Value).ToString("yyyy-MM-dd");

            _log.Info($"查询 {date} 的可预约情况…");
            var venue = await _client.GetVenueInfoAsync(_cfg.VenueType, _cfg.VenueName, date).ConfigureAwait(true);
            _timeList = venue?.TimeList ?? new List<string>();
            var situation = await _client.GetSiteSituationAsync(date, _cfg.VenueName).ConfigureAwait(true);

            _bookable.Clear();
            _booked.Clear();
            _grid.Rows.Clear();
            _grid.Columns.Clear();

            foreach (var (t, i) in _timeList.Select((t, i) => (t, i)))
            {
                var col = new DataGridViewTextBoxColumn
                {
                    HeaderText = $"{t}\n({i})",
                    Tag = i,
                    SortMode = DataGridViewColumnSortMode.NotSortable,
                };
                _grid.Columns.Add(col);
            }

            if (situation?.SiteSituation is not null)
            {
                // ⚠️ 语义（见 ApiModels.SiteSituationDto）：
                //   oci_time_list  = 已开放的时间段
                //   book_time_list = 已被预约的时间段
                //   可预约 = 开放 且 未被预约
                foreach (var site in situation.SiteSituation)
                {
                    foreach (var slot in site.AvailableSlots()) _bookable.Add((site.SiteId, slot));
                    foreach (var slot in site.BookTimeList) _booked.Add((site.SiteId, slot));
                }

                foreach (var site in situation.SiteSituation.OrderBy(s => s.SiteId))
                {
                    var idx = _grid.Rows.Add();
                    var row = _grid.Rows[idx];
                    row.HeaderCell.Value = $"{site.SiteId} 号场";
                    row.Tag = site.SiteId;
                    int free = site.AvailableCount;
                    row.HeaderCell.ToolTipText =
                        $"{site.SiteId} 号场：可约 {free} 段，已约 {site.BookedCount} 段，开放 {site.OciTimeList.Count} 段";
                    if (free == 0)
                    {
                        row.DefaultCellStyle.BackColor = Color.FromArgb(250, 235, 235);
                    }
                }
            }

            _grid.Invalidate();
            int totalOpen = situation?.SiteSituation.Sum(s => s.OciTimeList.Count) ?? 0;
            int totalBooked = situation?.SiteSituation.Sum(s => s.BookedCount) ?? 0;
            _log.Info($"{date}：{_grid.Rows.Count} 片场地，已开放 {totalOpen} 段，"
                      + $"已被预约 {totalBooked} 段，可预约 {_bookable.Count} 段");

            if (_bookable.Count == 0 && situation is not null)
            {
                var maxDays = venue?.EffectiveMaxDaysAhead ?? 2;
                _log.Warn($"该日期没有可预约时段。本馆最多可提前 {maxDays} 天预约"
                          + $"（今天 {DateTime.Now:MM-dd} → 最远 {DateTime.Now.AddDays(maxDays):MM-dd}）；"
                          + "更远的日期要等每天 20:00 放号。");
                _lblPreview.Text = $"⚠ {date} 暂无可约时段\n"
                                   + $"本馆最多可提前 {maxDays} 天预约（最远 {DateTime.Now.AddDays(maxDays):MM-dd}）";
            }

            _previewDayOffset = (int)_numDayOffset.Value;
            UpdatePreview();
        }
        catch (Exception ex)
        {
            _log.Error("刷新场地图失败：" + ex.Message);
        }
    }

    private async Task RunBookingAsync(bool dryRun)
    {
        SaveConfigFromUi();
        if (!dryRun && string.IsNullOrWhiteSpace(_cfg.Token))
        {
            MessageBox.Show("请先填写 token。", "立即抢约", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // 防止按钮被连点造成多个并发抢约（曾因此触发服务器限流）
        if (_manualRunning)
        {
            _log.Warn("已有抢约任务在执行，忽略本次点击");
            return;
        }
        _manualRunning = true;
        _btnRunNow.Enabled = false;
        _btnTest.Enabled = false;

        _lblState.Text = dryRun ? "演练中…" : "抢约中…";
        try
        {
            var cfg = _cfg;
            BookingResult result;
            if (dryRun)
            {
                result = await DryRunAsync(cfg).ConfigureAwait(true);
            }
            else
            {
                result = await _scheduler.RunOnceAsync(cfg, CancellationToken.None).ConfigureAwait(true);
            }
            _lblState.Text = result.IsSuccess ? "预约成功" : result.Message;
            if (result.IsSuccess)
            {
                MessageBox.Show(result.Message + $"\n\n耗时 {result.ElapsedMs:F0} ms，尝试 {result.Attempts} 次",
                    "预约成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else if (result.Outcome == BookingOutcome.Busy)
            {
                MessageBox.Show("已有抢约任务正在执行（可能是自动预约已开始），本次点击已忽略。",
                    "请稍候", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                _log.Warn("未成功：" + result.Message);
            }
        }
        catch (Exception ex)
        {
            _log.Error("执行失败：" + ex.Message);
            _lblState.Text = "执行失败";
        }
        finally
        {
            _manualRunning = false;
            _btnRunNow.Enabled = true;
            _btnTest.Enabled = true;
        }
    }

    /// <summary>演练：只查询与规划，不提交预约。</summary>
    private async Task<BookingResult> DryRunAsync(AppConfig cfg)
    {
        await _timeSync.SyncAsync(3).ConfigureAwait(true);
        var date = ScheduleMath.ResolveTargetDate(cfg, _timeSync.ServerNow);
        var venue = await _client.GetVenueInfoAsync(cfg.VenueType, cfg.VenueName, date).ConfigureAwait(true);
        if (venue is null || venue.TimeList.Count == 0)
        {
            return new BookingResult { Outcome = BookingOutcome.Error, Message = "无法读取时间表" };
        }
        var situation = await _client.GetSiteSituationAsync(date, cfg.VenueName).ConfigureAwait(true);
        _log.Info($"演练目标日期 {date}：");
        if (situation?.SiteSituation is not null)
        {
            foreach (var s in situation.SiteSituation.OrderBy(x => x.SiteId))
            {
                var free = s.AvailableSlots();
                var freeText = string.Join(",", free.Select(i => venue.TimeList[i]));
                var bookedText = string.Join(",", s.BookTimeList.Select(i => venue.TimeList[i]));
                _log.Info($"  {s.SiteId,2} 号场 可约 {free.Count,2} 段"
                          + (free.Count > 0 ? $"：{freeText}" : "")
                          + $"｜已约 {s.BookedCount} 段"
                          + (s.BookedCount > 0 ? $"（{bookedText}）" : ""));
            }
            var plan = BookingPlanner.BuildPlan(cfg, situation);
            if (plan is null)
            {
                _log.Warn("按当前偏好找不到可约组合，请刷新场地图后重新设定偏好");
            }
            else
            {
                var (st, en) = BookingPlanner.ResolveTimeRange(venue, plan.Slots);
                _log.Info($"演练结论：将抢 {plan.SiteId} 号场 {st}-{en}（时段 {string.Join(",", plan.Slots)}）");
            }
        }
        return new BookingResult
        {
            Outcome = BookingOutcome.NoCandidate,
            Message = "演练完成（未提交预约）",
            Date = date,
        };
    }

    /// <summary>显示各凭据来源的扫描结果（诊断用）。</summary>
    private void ShowSourceDiagnostics()
    {
        var sb = new StringBuilder("凭据来源扫描结果：\n\n");
        foreach (var (path, exists) in CredentialExtractor.DescribeSources())
        {
            sb.AppendLine((exists ? "[有] " : "[无] ") + path);
        }
        MessageBox.Show(sb.ToString(), "扫描来源", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ShowHelp()
    {
        var text =
            "使用流程\n" +
            "1) 点「★ 从钉钉自动获取」拿到 token（详细说明见 README.md）；\n" +
            "2) 「刷新场地图」查看目标日期哪些场地可约；\n" +
            "3) 在网格上点击想约的时段（可多选，数字越小越优先），\n" +
            "   或在「场地优先级」里填 3,4,5；\n" +
            "4) 「演练一次」确认目标日期与时段识别正确；\n" +
            "5) 勾选「启用每日自动预约」，软件会在每天 20:00:00\n" +
            "   自动预约后天的场地，无需守候。\n\n" +
            "时段对照（每单最多 2 小时）：\n" +
            "   18:00-20:00 = 时段 10,11　19:00-21:00 = 时段 11,12\n" +
            "   20:00-21:00 = 时段 12　　界面预览会显示实际时间\n\n" +
            "重要：token 只有几小时有效期。抢约当天（尤其 20:00 前）\n" +
            "请在钉钉里重新打开一次「场馆预约」，再点一次自动获取。\n" +
            "软件会在状态栏显示 token 剩余时间。\n\n" +
            "建议：预约开放时间为每天 20:00，软件默认提前 250ms 发起，\n" +
            "并按服务器时间校准本机时钟。请保持软件运行、网络畅通。\n\n" +
            "数据与日志目录：\n" +
            "   " + _dataDir + "\n" +
            "   （程序会自动创建；点「打开日志」看本次运行日志）\n\n" +
            "风险提示：本工具为个人自用，自动预约可能与场馆规定冲突，\n" +
            "请自行确认并承担相应后果。";
        MessageBox.Show(text, "使用说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
