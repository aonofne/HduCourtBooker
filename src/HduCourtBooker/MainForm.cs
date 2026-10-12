using System.Text;
using System.Text.Json;
using HduCourtBooker.Api;
using HduCourtBooker.Core;

namespace HduCourtBooker;

/// <summary>
/// 主界面：左侧配置，右侧场地可约地图；顶部操作按钮，底部日志。
/// </summary>
public sealed partial class MainForm : Form
{
    private AppConfig _cfg = AppConfig.CreateDefault();
    private readonly Logger _log;
    private readonly GymbookClient _client;
    private readonly TimeSync _timeSync;
    private readonly BookingScheduler _scheduler;
    private readonly DailyScheduler _daily;
    private readonly string _configPath;
    private readonly string _dataDir;

    // 界面控件
    private NumericUpDown _numDayOffset = null!;
    private CheckBox _chkFixedDate = null!;
    private DateTimePicker _dtFixedDate = null!;
    private TextBox _txtToken = null!;
    private TextBox _txtOpenId = null!;
    private TextBox _txtNickname = null!;
    private TextBox _txtPhone = null!;
    private TextBox _txtVenueName = null!;
    private TextBox _txtSites = null!;
    private TextBox _txtTriggerTime = null!;
    private NumericUpDown _numLead = null!;
    private NumericUpDown _numMaxSlots = null!;
    private NumericUpDown _numAttempts = null!;
    private NumericUpDown _numRetryMs = null!;
    private CheckBox _chkAutoCreateOrder = null!;
    private CheckBox _chkLogExchange = null!;
    private CheckBox _chkUseServerTime = null!;
    private CheckBox _chkSchedule = null!;
    private DataGridView _grid = null!;
    private RichTextBox _logBox = null!;
    private StatusStrip _status = null!;
    private ToolStripStatusLabel _lblNext = null!;
    private ToolStripStatusLabel _lblClock = null!;
    private ToolStripStatusLabel _lblState = null!;
    private ToolStripStatusLabel _lblArmed = null!;
    private Label _lblPreview = null!;
    private System.Windows.Forms.Timer _uiTimer = null!;
    private int _previewDayOffset;
    private bool _loading;
    private bool _autoFetchPending;
    private bool _manualRunning;
    private SplitContainer? _split;
    private readonly ExchangeLog _exchangeLog;

    public MainForm()
    {
        // 数据目录不写在 exe 旁边（普通用户可能没有写权限），
        // 优先 %LOCALAPPDATA%\HduCourtBooker，详见 Core\StorageLocation.cs
        _dataDir = StorageLocation.DataDir;

        // 逐个候选目录真写一遍再决定，全部失败也只禁用日志、不抛异常
        _log = new Logger(StorageLocation.CandidateDataDirs());

        // 日志实际落点可能与预期不同（该位置曾写失败并自动降级），以实际为准
        var actualDataDir = Path.GetDirectoryName(_log.LogDirectory.TrimEnd(Path.DirectorySeparatorChar));
        if (!string.IsNullOrEmpty(actualDataDir)
            && !string.Equals(actualDataDir, _dataDir, StringComparison.OrdinalIgnoreCase))
        {
            _dataDir = actualDataDir;
        }

        _configPath = Path.Combine(_dataDir, "config.json");
        _log.CleanupOldLogs();
        // 先把 exe 旁旧 data 里的配置/日志搬过来，再加载配置，避免"看起来配置丢了"
        var migrateNote = StorageLocation.MigrateLegacyIfNeeded(_log.Info);
        _cfg = ConfigStore.Load(_configPath);
        _exchangeLog = new ExchangeLog(_log.LogDirectory, _cfg.LogHttpExchanges);
        GymbookClientSetup.OnExchange = _exchangeLog.Record;
        _client = new GymbookClient(_cfg.ApiBase);
        _timeSync = new TimeSync(_client, _log);
        _scheduler = new BookingScheduler(_client, _timeSync, _log);
        _daily = new DailyScheduler(_log);

        BuildUi();
        _loading = true;
        try
        {
            LoadConfigToUi();
        }
        finally
        {
            _loading = false;
        }
        _log.LineWritten += AppendLog;
        _log.Info("程序启动");
        _log.Info($"API：{_cfg.ApiBase}");
        _log.Info($"数据目录：{_dataDir}"
                  + (StorageLocation.IsPortable ? "（便携模式）" : "（用户目录，无需管理员权限）"));
        _log.Info($"配置：{_configPath}");
        _log.Info($"日志：{_log.CurrentFile}");
        if (migrateNote.Length > 0) _log.Info("旧数据迁移：" + migrateNote);

        _daily.StatusChanged += s => RunOnUi(() => _lblNext.Text = s);
        _daily.RunCompleted += r => RunOnUi(() =>
        {
            if (r.Outcome == BookingOutcome.Busy) return; // 并发被忽略，不打扰用户
            _lblState.Text = r.IsSuccess ? "预约成功" : "未成功：" + r.Message;
            UpdateScheduleBadge();
        });

        // 唯一的调度启动入口（内部已有防重复启动保护）
        ApplyScheduleConfig();

        // 首次启动没有凭据时，窗口显示后自动尝试从钉钉本地存储获取
        _autoFetchPending = _cfg.Token.Length == 0;
    }

    /// <summary>
    /// 根据配置启用/停用每日自动预约。
    ///
    /// ⚠️ 这是唯一的调度入口，不要在别处直接调用 _daily.Start：
    /// 曾经多处调用导致同一时刻有 25 个抢约循环并发，把服务器打出 403 限流。
    /// </summary>
    private void ApplyScheduleConfig()
    {
        if (_cfg.ScheduleEnabled)
        {
            _daily.Start(_cfg, (c, fireAt, ct) => _scheduler.RunOnceAsync(c, ct, fireAt));
        }
        else
        {
            _daily.Stop();
        }
        UpdateScheduleBadge();
    }

    /// <summary>在状态栏显著提示"自动预约是否已武装"，避免用户以为开着其实没开。</summary>
    private void UpdateScheduleBadge()
    {
        if (_lblArmed is null) return;
        if (_daily.IsRunning)
        {
            var next = _daily.NextTrigger;
            _lblArmed.Text = $"● 自动预约已启用（下次 {next:MM-dd HH:mm:ss}）";
            _lblArmed.ForeColor = Color.SeaGreen;
        }
        else
        {
            _lblArmed.Text = "○ 自动预约未启用";
            _lblArmed.ForeColor = Color.Firebrick;
        }
    }

    /// <summary>窗口显示后再做需要句柄的操作（自动获取凭据、修正分割位置）。</summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplySplitterDistance();

        // 自动预约没开就明确提醒：这是"20:00 什么都没发生"最常见的原因
        if (!_cfg.ScheduleEnabled)
        {
            WarnScheduleDisabled();
        }

        if (!_autoFetchPending) return;
        _autoFetchPending = false;
        BeginInvoke(async () => await AutoFetchCredentialAsync());
    }

    /// <summary>
    /// 提醒用户自动预约未启用，并可直接开启。
    /// 2026-10-09 与 10-11 两次都发现配置被静默关掉（ScheduleEnabled=false）：
    /// 用户以为软件在守候，实际 20:00 不会做任何事。
    /// </summary>
    private void WarnScheduleDisabled()
    {
        // 自动化测试（冒烟/UI 探针）下跳过弹窗，避免无人应答导致进程挂起
        if (Environment.GetEnvironmentVariable("HDUBOOKER_NO_DIALOG") == "1")
        {
            _log.Warn("自动预约未启用（已跳过提示弹窗：HDUBOOKER_NO_DIALOG=1）");
            return;
        }

        var tod = ScheduleMath.ParseTriggerTime(_cfg.TriggerTime);
        var next = DateTime.Now.Date.Add(tod);
        if (next <= DateTime.Now) next = next.AddDays(1);

        var answer = MessageBox.Show(
            "自动预约当前是【关闭】的 —— 到点不会自动抢场。\n\n"
            + $"触发时刻  ：每天 {tod:hh\\:mm\\:ss}\n"
            + $"目标日期  ：今天 +{_cfg.DayOffset} 天\n"
            + $"下一次触发：{next:yyyy-MM-dd HH:mm:ss}\n\n"
            + "现在开启吗？",
            "自动预约未启用", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        if (answer == DialogResult.Yes)
        {
            _chkSchedule.Checked = true;   // 触发 CheckedChanged → ApplyScheduleConfig
            _log.Info("用户确认开启自动预约");
        }
        else
        {
            _log.Warn("自动预约保持关闭；到点不会自动抢场");
        }
    }

    /// <summary>
    /// 在布局稳定后设置分割位置与最小宽度。
    ///
    /// ⚠️ 不要删除本方法，也不要把这些值挪回 BuildUi()：
    /// 构造期设置 SplitterDistance 会被 SplitContainer 的重排逻辑重置（实测 430 → 121px），
    /// 于是表格第二列（所有输入框与按钮所在的百分比列）被压成 1px 宽——
    /// 控件都存在、也能被程序读到，但在界面上完全看不见（曾导致「★ 从钉钉自动获取」按钮"消失"）。
    /// 另外 Panel2MinSize 在构造期设大还会直接抛 InvalidOperationException。
    /// </summary>
    private void ApplySplitterDistance()
    {
        if (_split is null) return;
        try
        {
            int want = 430;
            int min = 360;
            int max = _split.Width - 420 - _split.SplitterWidth;
            if (max <= min)
            {
                // 窗口太窄：至少保证数值列可见（约占 45%）
                int fallback = Math.Max(240, (int)(_split.Width * 0.45));
                if (fallback < _split.Width - 80)
                {
                    _split.SplitterDistance = fallback;
                    _log.Info($"窗口较窄，配置面板宽度设为 {fallback}px（可用 {_split.Width}px）");
                }
                return;
            }

            _split.Panel1MinSize = min;
            _split.Panel2MinSize = 300;
            _split.SplitterDistance = Math.Clamp(want, min, max);
            _log.Info($"配置面板宽度 {_split.SplitterDistance}px（可用 {_split.Width}px）");
        }
        catch (Exception ex)
        {
            _log.Warn("设置面板宽度失败：" + ex.Message);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // 窗口被拉窄再恢复时，重新给出足够宽度给配置面板
        if (_split is not null && _split.IsHandleCreated && _split.SplitterDistance < 340)
        {
            ApplySplitterDistance();
        }
    }

    // ---------------- 界面构建 ----------------

    private void BuildUi()
    {
        Text = "杭电羽毛球自动预约 v1.0";
        MinimumSize = new Size(1080, 720);
        Size = new Size(1180, 780);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);

        var top = new Panel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(8, 6, 8, 6) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };

        Button MakeBtn(string text, EventHandler onClick, int width = 118)
        {
            var b = new Button { Text = text, Width = width, Height = 32, Margin = new Padding(3, 0, 3, 0) };
            b.Click += onClick;
            return b;
        }

        _btnRefresh = MakeBtn("刷新场地图", async (_, _) => await RefreshMapAsync());
        _btnCheckLogin = MakeBtn("检查登录", async (_, _) => await CheckLoginAsync());
        _btnTest = MakeBtn("演练一次", async (_, _) => await RunBookingAsync(dryRun: true), 100);
        _btnRunNow = MakeBtn("立即抢约", async (_, _) => await RunBookingAsync(dryRun: false));
        _btnSave = MakeBtn("保存配置", (_, _) => { SaveConfigFromUi(); _log.Info("配置已保存"); }, 100);
        _btnOpenLog = MakeBtn("打开日志", (_, _) => OpenPath(_log.CurrentFile), 100);
        _btnHelp = MakeBtn("使用说明", (_, _) => ShowHelp(), 100);

        buttons.Controls.AddRange(new Control[]
        {
            _btnRefresh, _btnCheckLogin, _btnTest, _btnRunNow, _btnSave, _btnOpenLog, _btnHelp
        });
        top.Controls.Add(buttons);

        // 状态栏
        _status = new StatusStrip();
        _lblArmed = new ToolStripStatusLabel("○ 自动预约未启用") { ForeColor = Color.Firebrick };
        _lblNext = new ToolStripStatusLabel("") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _lblState = new ToolStripStatusLabel("");
        _lblClock = new ToolStripStatusLabel("");
        _status.Items.AddRange(new ToolStripItem[] { _lblArmed, _lblNext, _lblState, _lblClock });

        // 主分割
        // 注意：SplitterDistance / Panel1MinSize / Panel2MinSize 在构造期会被布局系统重置
        // （实测 SplitterDistance 被压成 121px，导致右侧数值列塌成 1px，按钮与输入框全部看不见），
        // 且 Panel2MinSize 设大时构造期会直接抛异常，因此全部改到 OnShown 里设置。
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel1,
            SplitterWidth = 5,
        };
        _split = split;

        split.Panel1.Controls.Add(BuildConfigPanel());
        split.Panel2.Controls.Add(BuildMapPanel());

        Controls.Add(split);
        Controls.Add(top);
        Controls.Add(_status);
        MainMenuStrip = null;

        _uiTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _uiTimer.Tick += (_, _) => UpdateClock();
        _uiTimer.Start();
    }

    private Button _btnRefresh = null!;
    private Button _btnCheckLogin = null!;
    private Button _btnTest = null!;
    private Button _btnRunNow = null!;
    private Button _btnSave = null!;
    private Button _btnOpenLog = null!;
    private Button _btnHelp = null!;
    private Button _btnAutoCred = null!;
    private Label _lblTokenState = null!;

    private Control BuildConfigPanel()
    {
        var outer = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10, 8, 10, 8) };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(0),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddRow(string label, Control control, int height = 26)
        {
            var l = new Label
            {
                Text = label,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoSize = false,
                Dock = DockStyle.Fill,
                Height = height,
            };
            control.Dock = DockStyle.Fill;
            control.Height = height;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height + 4));
            layout.Controls.Add(l, 0, layout.RowStyles.Count - 1);
            layout.Controls.Add(control, 1, layout.RowStyles.Count - 1);
        }

        void AddSection(string title)
        {
            var lbl = new Label
            {
                Text = title,
                Font = new Font(Font, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 90, 160),
                Height = 28,
                TextAlign = ContentAlignment.BottomLeft,
                Dock = DockStyle.Fill,
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.Controls.Add(lbl, 0, layout.RowStyles.Count - 1);
            layout.SetColumnSpan(lbl, 2);
        }

        // --- 目标日期 ---
        AddSection("① 预约目标");
        _numDayOffset = new NumericUpDown { Minimum = 0, Maximum = 7, Value = 2, TextAlign = HorizontalAlignment.Left };
        AddRow("日期偏移", _numDayOffset);
        _chkFixedDate = new CheckBox { Text = "固定日期（优先于偏移）" };
        AddRow("", _chkFixedDate);
        _dtFixedDate = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd" };
        AddRow("固定日期", _dtFixedDate);
        _txtVenueName = new TextBox { Text = "综合馆羽毛球" };
        AddRow("场地名称", _txtVenueName);

        // --- 偏好 ---
        AddSection("② 场地与时间段偏好");
        _txtSites = new TextBox { PlaceholderText = "如 3,4,5 或 3-6，留空=任意" };
        AddRow("场地优先级", _txtSites);
        _numMaxSlots = new NumericUpDown { Minimum = 1, Maximum = 6, Value = 2 };
        _numMaxSlots.ValueChanged += (_, _) =>
        {
            if (_numMaxSlots.Value > AppConfig.MaxHoursPerBooking)
            {
                _log.Warn($"每单时段数设为 {_numMaxSlots.Value}，超过站点规则（每单最多 "
                          + $"{AppConfig.MaxHoursPerBooking} 小时 = {AppConfig.MaxHoursPerBooking} 段），"
                          + "实际提交时会被截断到上限");
            }
        };
        AddRow("每单时段数", _numMaxSlots);
        var limitHint = new Label
        {
            Text = "站点规则：每单最多 2 小时，所以这里建议保持 2。",
            ForeColor = Color.DimGray,
            Height = 20,
            Dock = DockStyle.Fill,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        layout.Controls.Add(limitHint, 1, layout.RowStyles.Count - 1);
        var hint = new Label
        {
            Text = "在上方「可约场地」网格点击单元格设定偏好，\n蓝色数字=优先级（1 最高）。",
            ForeColor = Color.DimGray,
            Height = 40,
            Dock = DockStyle.Fill,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.Controls.Add(hint, 1, layout.RowStyles.Count - 1);

        // --- 登录 ---
        AddSection("③ 登录凭据");
        var autoPanel = new FlowLayoutPanel { Height = 32, Dock = DockStyle.Fill, WrapContents = false };
        _btnAutoCred = new Button { Text = "★ 从钉钉自动获取", Width = 150, Height = 28 };
        _btnAutoCred.Click += async (_, _) => await AutoFetchCredentialAsync();
        var btnOpenDingTalk = new Button { Text = "打开钉钉", Width = 80, Height = 28 };
        btnOpenDingTalk.Click += (_, _) => OpenDingTalk();
        autoPanel.Controls.AddRange(new Control[] { _btnAutoCred, btnOpenDingTalk });
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.Controls.Add(autoPanel, 1, layout.RowStyles.Count - 1);

        _lblTokenState = new Label
        {
            Text = "尚未获取凭据",
            ForeColor = Color.DimGray,
            Height = 34,
            Dock = DockStyle.Fill,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.Controls.Add(_lblTokenState, 1, layout.RowStyles.Count - 1);

        _txtToken = new TextBox { UseSystemPasswordChar = true, PlaceholderText = "粘贴 token（Bearer 后面那一串）" };
        AddRow("token", _txtToken);
        var tokenButtons = new FlowLayoutPanel { Height = 30, Dock = DockStyle.Fill, WrapContents = false };
        var btnShow = new Button { Text = "显示", Width = 60, Height = 26 };
        btnShow.Click += (_, _) => _txtToken.UseSystemPasswordChar = !_txtToken.UseSystemPasswordChar;
        var btnPaste = new Button { Text = "从剪贴板粘贴", Width = 110, Height = 26 };
        btnPaste.Click += (_, _) =>
        {
            if (Clipboard.ContainsText())
            {
                _txtToken.Text = ExtractToken(Clipboard.GetText().Trim());
                _log.Info("已从剪贴板读取 token");
            }
        };
        var btnDiag = new Button { Text = "扫描来源", Width = 80, Height = 26 };
        btnDiag.Click += (_, _) => ShowSourceDiagnostics();
        tokenButtons.Controls.AddRange(new Control[] { btnShow, btnPaste, btnDiag });
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.Controls.Add(tokenButtons, 1, layout.RowStyles.Count - 1);

        _txtOpenId = new TextBox();
        AddRow("openid", _txtOpenId);
        _txtNickname = new TextBox();
        AddRow("昵称", _txtNickname);
        _txtPhone = new TextBox();
        AddRow("手机号", _txtPhone);

        // --- 触发 ---
        AddSection("④ 触发与重试");
        _txtTriggerTime = new TextBox { Text = "20:00:00.000" };
        AddRow("触发时刻", _txtTriggerTime);
        _numLead = new NumericUpDown { Minimum = 0, Maximum = 3000, Value = 250 };
        AddRow("提前(毫秒)", _numLead);
        _numAttempts = new NumericUpDown { Minimum = 1, Maximum = 200, Value = 12 };
        AddRow("最大尝试", _numAttempts);
        _numRetryMs = new NumericUpDown { Minimum = 0, Maximum = 5000, Value = 120 };
        AddRow("重试间隔ms", _numRetryMs);
        _chkUseServerTime = new CheckBox { Text = "用服务器时间校准时钟" };
        AddRow("", _chkUseServerTime);
        _chkAutoCreateOrder = new CheckBox { Text = "成功后自动建单" };
        AddRow("", _chkAutoCreateOrder);
        _chkLogExchange = new CheckBox { Text = "记录 HTTP 报文（排查用）" };
        AddRow("", _chkLogExchange);
        _chkSchedule = new CheckBox { Text = "启用每日自动预约" };
        _chkSchedule.CheckedChanged += (_, _) =>
        {
            // 加载配置时会改动勾选状态，此时不触发启动/停止
            if (_loading) return;
            SaveConfigFromUi();
            ApplyScheduleConfig();
        };
        AddRow("", _chkSchedule);

        _lblPreview = new Label
        {
            Dock = DockStyle.Top,
            Height = 60,
            ForeColor = Color.FromArgb(0, 100, 0),
            Text = "",
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.Controls.Add(_lblPreview, 0, layout.RowStyles.Count - 1);
        layout.SetColumnSpan(_lblPreview, 2);

        outer.Controls.Add(layout);
        return outer;
    }

    private Control BuildMapPanel()
    {
        var outer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };

        var info = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Text = "点「刷新场地图」查看可预约情况；点击可用单元格设置偏好（蓝底数字=优先级）。",
            ForeColor = Color.DimGray,
        };

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            RowHeadersWidth = 90,
            ColumnHeadersHeight = 44,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            MultiSelect = false,
            BackgroundColor = Color.White,
            EnableHeadersVisualStyles = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        };
        _grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _grid.RowHeadersDefaultCellStyle.BackColor = Color.FromArgb(245, 245, 245);
        _grid.CellClick += Grid_CellClick;
        _grid.CellPainting += Grid_CellPainting;

        var logPanel = new Panel { Dock = DockStyle.Bottom, Height = 210 };
        var logHeader = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = false,
            Text = $"运行日志　　文件：{_log.CurrentFile}",
        };
        _logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(250, 250, 250),
            Font = new Font("Consolas", 8.5F),
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Both,
        };

        logPanel.Controls.Add(_logBox);
        logPanel.Controls.Add(logHeader);

        var splitter = new Splitter { Dock = DockStyle.Bottom, Height = 4 };

        outer.Controls.Add(_grid);
        outer.Controls.Add(splitter);
        outer.Controls.Add(logPanel);
        outer.Controls.Add(info);
        return outer;
    }

    private void UpdateClock()
    {
        var serverNow = _timeSync.ServerNow;
        _lblClock.Text = $"本地 {DateTime.Now:HH:mm:ss} | 服务器 {serverNow:HH:mm:ss} (偏移 {_timeSync.OffsetMs:+0;-0;0}ms)";
        UpdateTokenState();
        UpdateScheduleBadge();
        if (_daily.IsRunning)
        {
            var span = _daily.NextTrigger - DateTime.Now;
            if (span > TimeSpan.Zero)
            {
                _lblNext.Text = $"下次自动预约：{_daily.NextTrigger:MM-dd HH:mm:ss}（{span.Days}天{span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}）";
            }
        }
    }

    private void AppendLog(string line)
    {
        RunOnUi(() =>
        {
            try
            {
                _logBox.AppendText(line + Environment.NewLine);
                if (_logBox.Lines.Length > 800)
                {
                    var keep = _logBox.Lines.Skip(_logBox.Lines.Length - 500).ToArray();
                    _logBox.Lines = keep;
                }
                _logBox.SelectionStart = _logBox.TextLength;
                _logBox.ScrollToCaret();
            }
            catch { /* 忽略 */ }
        });
    }

    private void RunOnUi(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(action); } catch { }
        }
        else
        {
            action();
        }
    }

    /// <summary>
    /// 打开文件或文件夹。
    ///
    /// ⚠️ 不能只用 <c>UseShellExecute=true</c> 直接打开 .log：
    /// 本机实测该写法抛 <c>Win32Exception: 拒绝访问</c>（.log 被关联到 UWP 应用），
    /// 而旧代码把异常吞掉了，表现就是"点了没反应"。实测结论：
    ///   记事本 + UseShellExecute=false  → 可用
    ///   显式 explorer.exe + 参数        → 可用
    /// </summary>
    private static void OpenPath(string path)
    {
        // 1) 文件：优先记事本（不依赖文件关联，一定打得开）
        if (File.Exists(path))
        {
            var sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            foreach (var editor in new[] { Path.Combine(sys32, "notepad.exe"), "notepad.exe" })
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = editor,
                        Arguments = $"\"{path}\"",
                        UseShellExecute = false,
                    });
                    return;
                }
                catch { /* 换下一种方式 */ }
            }

            // 2) 文件：交给系统默认关联（可能失败，见上）
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
                return;
            }
            catch { /* 继续降级 */ }
        }

        // 3) 兜底：用资源管理器打开目录 / 定位文件（显式传参，同样不依赖关联）
        var dir = File.Exists(path) ? Path.GetDirectoryName(path) : path;
        if (string.IsNullOrEmpty(dir)) dir = AppContext.BaseDirectory;
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var args = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{dir}\"";

        foreach (var exe in new[] { explorer, "explorer.exe" })
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                });
                return;
            }
            catch { /* 换下一种方式 */ }
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "打不开文件或目录。\n\n路径：\n" + path + "\n\n错误：" + ex.Message
                + "\n\n可以手动把上面的路径粘到资源管理器地址栏。",
                "打开失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>打开数据目录（日志与配置都在这里）。</summary>
    private void OpenDataFolder() => OpenPath(_dataDir);
    private static string ExtractToken(string raw)
    {
        // 支持整段 "Bearer xxx"、JSON 片段或裸 token
        var text = raw.Trim().Trim('"');
        if (text.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) text = text[7..].Trim();
        var m = System.Text.RegularExpressions.Regex.Match(text, @"eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+");
        return m.Success ? m.Value : text;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try
        {
            SaveConfigFromUi();
            _daily.Stop();
            _client.Dispose();
        }
        catch { }
        base.OnFormClosing(e);
    }
}
