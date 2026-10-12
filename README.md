# 杭电羽毛球场自动预约（HduCourtBooker）

面向杭州电子科技大学场馆预约系统（`https://sportmeta.hdu.edu.cn/book/dingtalk/`）的 Windows 桌面工具。
可预先设定**场地**与**时间段**偏好，在每天 **20:00** 预约开放的瞬间自动完成"后天"场地的预约。

> 已还原并实测的真实接口：`choose_venue` / `post_venue_info` / `post_site_situation` /
> `creat_book_info` / `creat_order` / `post_server_time`，场地为「综合馆羽毛球」共 12 片，
> 每天可预约 T+0 ~ T+2（即 20:00 开放后天的场地）。

---

## 〇、选哪个版本

| 目录 | 体积 | 需要 .NET | 说明 |
| --- | --- | --- | --- |
| **`publish-lite\`** | **约 0.3 MB** | 需要 .NET 8 桌面运行时 | **推荐**。体积小、启动快、不易被安全软件误拦 |
| `publish\` | 约 63 MB | 不需要 | 自包含单文件，拷到任何 Windows 机器都能跑，但大且有时被拦 |

两个版本**共用同一份数据**（`%LOCALAPPDATA%\HduCourtBooker\`），配置与日志互通，随便换着用。

检查是否已装 .NET 8 桌面运行时：

```powershell
dotnet --list-runtimes | Select-String WindowsDesktop
```

需要看到 `Microsoft.WindowsDesktop.App 8.x`。若没有，装一下
[.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0)（约 55 MB，一次即可）。

## 一、快速开始

1. 双击 `publish-lite\HduCourtBooker.exe`
   （若双击没反应，改用同目录的 **`启动.bat`**，它会保留窗口显示返回码与排查路径）
2. 点「★ 从钉钉自动获取」→ 自动填入 token 与 openid，并**自动开启每日自动预约**
3. 确认状态栏显示 **● 自动预约已启用（下次 …）**
4. 点「刷新场地图」确认能读到 12 片场地与可约时段（白色=可约，红色=已被约，灰色=未开放）
5. 在网格上点击想约的时段（可多选，数字越小优先级越高），填写「场地优先级」（如 `3,4,5`）
6. 点「演练一次」确认目标日期与时段正确 → 点「保存配置」

之后只要软件保持运行、网络畅通，每天 20:00 会自动抢后天的场地，抢到后弹窗提示并写入日志。

### 数据放在哪

所有数据都在 **`%LOCALAPPDATA%\HduCourtBooker\`**（`config.json` + `logs\`），
不再写在程序目录，因此**不需要管理员权限**。首次运行会自动把旧的 `程序目录\data` 迁移过来。
若该目录恰好不可写，程序会退回"exe 旁 data"或"我的文档"，启动日志第一行会写明用了哪个。

## 二、获取 token（已自动化，无需手动查找）

### 推荐：软件自动获取

1. 打开**钉钉**，从工作台进入一次「场馆预约」页面（进入即可，不用预约）
2. 打开 `HduCourtBooker.exe`，点 **「★ 从钉钉自动获取」**

软件会读取钉钉本地存储里的登录凭据，自动填入 token / openid，并调用只读接口
`post_client/post_order_info` 校验归属（会显示你的历史订单条数，可确认是自己的账号）。

命令行方式（可选）：

```powershell
publish\HduCourtBooker.SelfTest.exe --extract-credentials
```

### 手动获取（备用）

**软件里点「token 在哪」按钮**会弹出完整指引（可复制）。按可靠性排序：

#### ① 钉钉本地存储（软件已自动化，失败时手工确认）

```
%LOCALAPPDATA%\DingTalk_133\Default\Local Storage\leveldb\
```

- 把这个路径粘到资源管理器地址栏即可打开；token 是 `eyJ` 开头的三段式字符串，
  藏在 `.ldb` / `.log` 文件里，用记事本打开搜 `eyJ` 也能看到。
- **目录名随版本变化**（如 `DingTalk_133`）；找不到就找同级任意 `DingTalk*` 目录。
  软件已改为自动枚举所有 `DingTalk*`，点「扫描来源」可看每个路径是否存在。
- 前提：先在钉钉里打开过一次「场馆预约」页面，本地才会有这份数据。

#### ② 软件自己的配置文件（上次成功获取的 token 会一直留着）

```
<软件目录>\data\config.json      →  "TokenProtected" 字段就是 token（明文）
```

- 换机器、重装软件但想沿用旧 token 时，直接改这个字段即可。
- 另外 `data\logs\exchange-*.log` 记录每次请求的鉴权头（token 已脱敏，只能用于排查）。

#### ③ 电脑浏览器（仅当曾在浏览器里登录成功过）

```
Edge  : %LOCALAPPDATA%\Microsoft\Edge\User Data\Default\Local Storage\leveldb\
Chrome: %LOCALAPPDATA%\Google\Chrome\User Data\Default\Local Storage\leveldb\
```

或在浏览器里 `F12` → Application → Local Storage → `https://sportmeta.hdu.edu.cn` → 复制 `token`。
注意：该站点在浏览器里通常登不进去，这条路只在极少数情况下可用。

#### ④ 抓包（能拿到请求头即可）

用 Fiddler / Reqable 等工具让钉钉走代理，在任意 `/book/client/` 请求的请求头里找：

```
Authorization: Bearer eyJhbGciOi...
```

`Bearer` 后面那一串（到行尾）就是 token。手机端同理：手机 WiFi 设置 HTTP 代理指向电脑并安装证书。

#### ⑤ 从日志里捞

在钉钉日志目录里搜 `eyJ`：`%APPDATA%\DingTalk\log\`、`%LOCALAPPDATA%\DingTalk_133\Default\Cache\`。
找到多条时，**用 `exp` 字段最大（最晚过期）的那条**。

> 拿到后粘进主界面 token 输入框 → 点「检查登录」→ 状态栏显示「token 有效，剩余 X 小时」即成功。

### 关于有效期（重要）

该 token 是**短期票据**，实测只有**数小时**有效期（服务端下发）。
软件在标题区实时显示剩余时间；到期后：

1. 在**钉钉**里重新打开一次「场馆预约」页面（会拿到新 token）
2. 回软件点一次「★ 从钉钉自动获取」

> **抢约当天务必在 20:00 之前刷新一次 token**；否则软件会在日志里直接提示
> "token 已过期"，而不是白白等到 20:00 才发现。

### 存储方式

token 以**明文**保存在 `data\config.json` 的 `TokenProtected` 字段，便于查看、备份与手工替换
（按用户要求取消了 DPAPI 加密）。配置文件里若残留早期版本的 `dpapi:` 密文，
软件会提示重新获取一次，保存后即变为明文。

⚠️ 明文意味着：**不要把 `data\config.json` 发给别人或上传到公开仓库**，
它等同于你的登录凭据。

## 三、抢约机制

| 环节 | 做法 |
| --- | --- |
| 时钟对齐 | 调用 `post_server_time` 多次采样，取往返最小的样本算出本机偏移 |
| 提前量 | 默认提前 **250 ms** 发起请求（`提前(毫秒)` 可调），补偿网络往返 |
| 保活连接 | 预热阶段先建 TLS 长连接并拉取时间表，避免握手落在关键路径 |
| 触发精度 | 最后一小段用自旋等待，判据用 `Stopwatch` 高精度时间源，误差通常 < 5 ms |
| 冲突重试 | 被抢时按 `重试间隔ms` 重试，最多 `最大尝试` 次；每轮都重新拉最新可约时段 |
| 被限流退避 | 收到 HTTP 403 时按 200→400→800→1600→3000 ms 指数退避，不再猛冲 |
| 兜底 | 偏好时段抢不到时，退化为该场地任意可用时段，保证成功率 |
| 成功后 | 可选自动调用 `creat_order` 建单 |

### 防并发护栏（三条，缺一不可）

1. **单一调度入口**：界面只通过 `ApplyScheduleConfig()` 启停自动预约；`DailyScheduler.Start()`
   内部判断"已在运行且配置未变就忽略"，重复调用不会产生第二条循环。
2. **抢约互斥锁**：`BookingScheduler.RunOnceAsync` 用 `SemaphoreSlim` 串行化，同一时刻只有一个
   抢约流程，后续调用立即返回 `Busy` 并被忽略。
3. **请求闸门 + 最小间隔**：`GymbookClient` 内部所有 HTTP 请求串行且间隔 ≥ 25ms；
   2 秒内超过 40 个请求会写警告日志。

> 状态栏会显著显示 **● 自动预约已启用（下次 …）** 或 **○ 自动预约未启用**，跑之前先看这一行。

## 事故复盘：2026-10-09 20:00 抢约失败

**现象**：25 个"▶ 触发预约"在同一毫秒打出，716 次 `creat_book_info` 全部返回 403/400，一单未成。

**根因**（三条叠加）：

1. **调度循环被重复创建**：`DailyScheduler.Start()` 在 12:28、16:44、17:14、17:15 被多次触发
   （17:15:19 一秒内就出现 8 个"时钟校准"），最终累积 25 条循环，全部在 19:59:59.756 同时开抢。
   每条循环自带 5 次时钟校准 + 最多 15 次重试 → 瞬间几百个请求 → 服务器判定异常流量返回 403。
2. **自旋等待死循环**：`WaitUntilAsync` 原来用 `DateTime.Now`（精度约 15.6ms）做判据，
   最后一刻 `remain` 永远算不出 ≤0，于是持续自旋把 CPU 打满，进一步拖垮了在途请求。
3. **无并发闸门**：客户端没有任何串行/限速措施，上层重复调用就等量放大成请求风暴。

**修复**：见上一节三条护栏 + `Timing.WaitUntilAsync` 改用 `Stopwatch`；
并新增 52 项单元测试（含"等待不得死循环"）与 31 项集成测试
（含"并发 20 次只有 1 次执行"、"重复 Start 25 次只产生一条循环"）锁住这些行为。
原始日志已归档到 `logs-incident-20261009\`。

## 四、配置项（`data\config.json`）

| 字段 | 说明 |
| --- | --- |
| `DayOffset` | 目标日期偏移，默认 `2` = 后天（T+2） |
| `UseFixedDate` / `FixedDate` | 固定某个日期预约，优先于偏移 |
| `VenueName` | 场地名，默认「综合馆羽毛球」 |
| `SitePreference` | 场地优先级，如 `[3,4,5]`；空 = 1→12 |
| `SlotPreference` | 时间段优先级（下标数组），如 `[[10,11]]` = 18:00-20:00 |
| `MaxSlotsPerBooking` | 单次预约的连续时段数上限 |
| `TriggerTime` | 触发时刻，默认 `20:00:00.000` |
| `LeadMilliseconds` | 提前发起毫秒数 |
| `MaxAttempts` / `RetryIntervalMs` | 重试次数与间隔 |
| `ScheduleEnabled` | 是否启用每日自动预约 |
| `AutoCreateOrder` | 成功后是否自动建单 |

时间段下标与时间的对应（14 段，注意 **11:40 / 13:20 是 40 分钟段**）：

```
0=08:00  1=09:00  2=10:00  3=11:00  4=11:40  5=13:20  6=14:00
7=15:00  8=16:00  9=17:00 10=18:00 11=19:00 12=20:00 13=21:00
```

所以 **19:00-21:00 = 下标 `[11,12]`**（这是默认的第一优先时段）。

### 场馆开放时间与预约规则

来自站点公告（`/client/show_notice`）与接口能力：

| 项目 | 内容 |
| --- | --- |
| 综合馆(羽毛球馆) | 周一至周五 8:00-21:00；周六 8:00-20:00；周日 8:00-17:00 |
| 闭馆 | 9月15-19日闭馆；10月1-5日国庆期间所有场馆关闭；遇检修/竞赛暂停开放 |
| **每单时长上限** | **最多 2 小时**（所以 `MaxSlotsPerBooking` 默认 2；填更大只会被截断） |
| 可提前预约 | 最多约到后天（T+0 ~ T+2），每天 **20:00 放出后天** |
| 周日注意 | 周日只开放到 17:00，晚上时段不存在，需相应调整偏好 |

## 五、自检与排错

`publish\HduCourtBooker.SelfTest.exe` 会打印各接口的真实返回：

```text
[1] 服务器时间与时钟校准     [2] 场地列表 choose_venue
[3] 时间表 post_venue_info   [4] 场地图 post_site_situation
[5] 抢约规划                [6] token 校验（需 --token）
```

带 token 校验：`HduCourtBooker.SelfTest.exe --token <你的token> --openid <你的openid>`

一键运行并保存输出：`powershell -ExecutionPolicy Bypass -File hdu-booking\verify.ps1`

日志位于 `data\logs\booker-yyyyMMdd.log`，界面里点「打开日志」可直接查看。
自检第 1 步就会打印时钟偏移；若偏移达到秒级说明本机时钟不准，但软件会自动补偿
（实测样例：偏移 +2221 ms、往返 4 ms）。

**常见问题**

| 现象 | 处理 |
| --- | --- |
| 自检开头即 `无法连接场馆服务器` | 确认在校园网或 VPN 内；检查系统时间是否准确 |
| 大量 `HTTP 403` | 被服务器限流。确认没有同时开多个软件实例/多次点「立即抢约」；新版已自动指数退避 |
| `未登录或 token 失效` | token 过期，重新获取 |
| 状态栏显示 `○ 自动预约未启用` | 勾选「启用每日自动预约」并保存；否则 20:00 不会自动抢 |
| 刷新场地图为空 | 场地名需与 `choose_venue` 返回一致（默认「综合馆羽毛球」） |
| 目标日期全空 | 该馆当天 20:00 才放出后天，更远的日期尚未开放；用自检的第 [6] 段可看清 |
| 抢不到 | 提前 `LeadMilliseconds`（如 300~400 ms）、增加 `MaxAttempts`、扩大场地/时段偏好 |

## 六、测试与验证

```powershell
powershell -ExecutionPolicy Bypass -File hdu-booking\build.ps1   # 测试 + 发布
```

| 层次 | 位置 | 覆盖内容 |
| --- | --- | --- |
| 离线单元测试（30 项） | `src\HduCourtBooker.Tests` | 选场规划、场地/时段优先级、退化策略、日期与触发时刻计算、请求体 JSON 字段、响应解析 |
| 端到端集成测试（20 项） | `src\HduCourtBooker.IntegrationTest` | 起本地仿真服务端，用真实 HTTP 往返验证：时钟校准、完整抢约与建单、冲突重试、**触发精度**、401 处理 |
| 联网自检 | `src\HduCourtBooker.SelfTest` | 对真实站点逐接口校验契约、预约窗口（T+3 应未开放）、`--token` 鉴权校验 |

触发精度实测：目标 `21:30:56.434` → 实际首次提交 `21:30:56.438`，**偏差 4 ms**。

> 关键回归：`TimeSpan.TryParse("21:11:43.443")` 会把 `21:` 解析成 **21 天**，导致触发时刻静默错位。
> 现已改为 `TryParseExact` 严格解析（`Core\ScheduleMath.cs`），并有专门单元测试守住。

## 七、目录结构

```
hdu-booking/
├─ build.ps1                  # 一键构建 + 测试 + 发布
├─ verify.ps1                 # 本机联网自检
├─ publish/                   # 交付产物：主程序 + 自检工具
├─ src/
│  ├─ HduCourtBooker/         # WinForms 主程序
│  │  ├─ Api/                 # 接口模型 + HTTP 客户端
│  │  ├─ Core/                # 配置、日志、时间同步、选场规划、抢约调度、每日调度
│  │  └─ MainForm*.cs         # 界面
│  ├─ HduCourtBooker.SelfTest/        # 联网自检（真实接口）
│  ├─ HduCourtBooker.Tests/           # 离线单元测试
│  └─ HduCourtBooker.IntegrationTest/ # 端到端集成测试（本地仿真服务端）
└─ docs/API.md                # 接口契约与逆向记录
```

## 八、合规提示

本工具仅用于个人自用，减少手动抢场的重复操作。自动化访问第三方系统可能与场馆/学校的使用规定冲突，
请自行确认并承担相应后果；请勿用于批量占场、代抢等用途。程序已内置重试间隔，请勿改到极高频率以免给服务器造成压力。
