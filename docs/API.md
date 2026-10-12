# 杭电场馆预约系统 —— 接口契约（逆向 + 实测记录）

来源：`https://sportmeta.hdu.edu.cn/book/dingtalk/` 前端 bundle 静态分析
（`index.64358918.js`、`pages-book-soloBook.219e7e55.js`、`pages-boot-boot.4b0507ca.js`）
＋ 2026-10-08 实机请求验证。应用版本 `__APP_VERSION__ = 1.0.27`，`appName = gymbook`。

## 1. 基础约定

| 项 | 值 |
| --- | --- |
| API 根 | `https://sportmeta.hdu.edu.cn/book` |
| 前端路由 base | `/book/dingtalk/`（uni-app H5，hash 路由） |
| 鉴权 | 请求头 `Authorization: Bearer <token>` |
| 内容类型 | `content-type: application/json` |
| token 存放 | 浏览器 `localStorage.token`；钉钉内为 `<profile>\Default\Local Storage\leveldb` |
| token 有效期 | **短期票据**，实测仅数小时（JWT `exp`）；每次进入页面时由 `dingtalk_login` 重新下发 |
| 场地类型 | `badminton`（羽毛球）、`tennis`（网球）为前端开放项 |

前端请求封装（bundle 内 `a29a` 模块）关键逻辑：

```js
var u = "https://sportmeta.hdu.edu.cn/gymbook";        // 默认（未命中前缀时）
var a = {
  "/client": { host: "https://sportmeta.hdu.edu.cn/book", stripPrefix: false },
  "/server": { host: "https://sportmeta.hdu.edu.cn/book", stripPrefix: false }
};
header: { "content-type": "application/json", Authorization: c ? "Bearer " + c : "" }
```

## 2. 登录

前端采用钉钉免登：`dd.runtime.permission.requestAuthCode({corpId:"ding8224358e11e0debf35c2f4657eb6378f"})`
拿到 `authCode` 后调用：

```
POST /client/dingtalk_login      body: {"authCode": "<code>"}
→ {"status": 200, "data": {avatar, nickname, phone, user_type, openid, proc_uid, code, token}}
```

返回的 `token` 即后续 Bearer 凭据，`openid` 提交预约时必填。
（实测：无效 authCode 返回 `{"status": -1, "msg": "业务异常"}`。）

另有微信链路：`GET /client/get_openid?code=`、`POST /client/getuser_info`、`POST /client/add_user`。

## 3. 查询类接口（实测无需 token）

| 接口 | 方法 | 请求体 | 响应 |
| --- | --- | --- | --- |
| `/client/post_server_time` | GET | — | `{"data": 1791463245385}`（毫秒时间戳，与本地时钟一致） |
| `/client/choose_venue` | POST | `{"venue_type":"badminton"}` | `{"venue_list":[{"venue_name":"综合馆羽毛球","venue_introduce":"12片场地","venue_image_url":"..."}]}` |
| `/client/post_venue_info` | POST | `{"venue_type":"badminton","venue_name":"综合馆羽毛球","date":"2026-10-10"}` | `{"time_list":[...14 项...],"days_can_appoint":3,"base_price":0.0,"rest_list":[...]}` |
| `/client/post_site_situation` | POST | `{"date":"2026-10-10","venue_name":"综合馆羽毛球"}` | `{"site_situation":[{"site_id":1,"book_time_list":[...],"oci_time_list":[...]}],"extra_list":{...}}` |
| `/client/show_notice` | POST | `{}` | `{"message":"2026-2027-1学期下沙校区体育场馆开放安排：..."}` |

`time_list`（14 段，下标即 `time_list` 下标）：

```
0=08:00  1=09:00  2=10:00  3=11:00  4=11:40  5=13:20  6=14:00
7=15:00  8=16:00  9=17:00 10=18:00 11=19:00 12=20:00 13=21:00
```

错误响应（实测）：

| 场景 | 响应 |
| --- | --- |
| `choose_venue` 缺 `venue_type` | `401 {"status":"error","message":"场馆类型不存在"}` |
| `post_venue_info` 日期格式错误 | `500 {"status":"error","message":"无法解析日期，请使用类似 '2025-04-09' 的格式"}` |
| `post_venue_info` 场地名不存在 | `404 {"status":"error","message":"Venue not found"}` |
| `post_site_situation` 缺参数 | `400 {"status":"error","message":"缺少必要参数"}` |
| `post_site_situation` 日期格式错 | `500 time data '20261010' does not match format '%Y-%m-%d'`（Flask/Python 后端） |

## 4. 提交预约（需要 token）

```
POST /client/creat_book_info
{
  "orderData": {
    "openid": "<openid>",
    "nickname": "<昵称>",
    "phone": "<手机号>",
    "date": "2026-10-10",
    "venue_name": "综合馆羽毛球",
    "venue_type": "badminton",
    "site_id": 3,
    "total_price": 0,
    "time_list": [10, 11],
    "start_time": "18:00",
    "end_time": "20:00"
  }
}
```

响应语义（来自前端 `submitBookInfo` 分支逻辑）：

| HTTP | 含义 | 前端处理 |
| --- | --- | --- |
| 200 且 `conflict_times` 为空 | **预约成功** | 进入 `creat_order` |
| 200 且 `conflict_times` 非空、`available_times` 非空 | 部分冲突 | 用 `available_times` 拆分连续段重新提交 |
| 200 且 `conflict_times` 非空、`available_times` 为空 | 全部被占 | 提示 `message` 并重置 |
| 201 | 被拒绝 | 提示 `message` |
| 401 | 未登录/token 无效 | 实测：`{"error":"未登录或缺少 token"}`、`{"error":"无效 token"}` |

成功后建单：

```
POST /client/creat_order      body: 同 creat_book_info 的 orderData 包装
POST /client/cancel_book      body: {"openid": "<openid>"}
```

### 实测记录（真实 token 下的鉴权与结构验证）

用真实账号 token 提交一个**已被占用**的时段，服务器返回：

```
HTTP 500 {"status":"error","message":"local variable 'self_book' referenced before assignment"}
```

说明：
- 请求已通过 `Bearer` 鉴权与结构校验，进入了后端冲突判定逻辑（`self_book` 是"自己是否已约"的判断）；
- 该 500 是服务端自身的未初始化变量缺陷（出现在冲突分支），不影响我们：我们提交的都是**当前可约**的时段；
- 该次请求**未产生任何预约**。


## 5. 预约窗口（实测）

以 2026-10-08（周四）为 T：

| 日期 | 偏移 | 可约时段数 |
| --- | --- | --- |
| 2026-10-07 | T-1 | 0（已过期） |
| 2026-10-08 | T+0 | 44 |
| 2026-10-09 | T+1 | 30 |
| 2026-10-10 | T+2 | 57 |
| 2026-10-11 | T+3 | 0（尚未开放） |

`post_venue_info` 返回 `days_can_appoint = 3`；`oci_time_list` 在 T+3 及以后仍有值（表示场馆开放时段），
但 `book_time_list` 为空（表示"可预约"尚未放出）。**结论：每天 20:00 只放出 T+2，T+0/T+1 是余量。**

## 6. 订单查询类接口（需要 token，只读）

| 接口 | 方法 | 请求体 | 响应 |
| --- | --- | --- | --- |
| `/client/post_order_info` | POST | `{"openid":"<openid>"}` | `{"order_info":[{"start_time":"13:20","end_time":"14:00","venue_name":"综合馆羽毛球","order_num":"17905968378971","total_price":"0.00","order_date":"2026-09-30","site_id":"4","creat_time":"2026-09-28 20:00","verified":true,"with_friends":true}]}` |
| `/client/get_playing_partner` | POST | `{"openid":"<openid>"}` | `{"playing_partner":[]}` |
| `/client/cancel_order` | POST | 见前端 | 取消订单 |
| `/client/friends_out_of_order` / `add_playing_partner` / `remove_playing_partner` | POST | — | 球友管理 |

`post_order_info` 是**只读**接口，适合用来校验 token 是否有效以及归属哪个账号
（`openid` 不匹配会返回空数组，因此必须用 token 对应的 openid 查询）。

## 7. 从本地存储提取 token（自动化登录的关键）

钉钉 PC 版是基于 Chromium 的容器，H5 页面的 `localStorage` 落在：

```
%LOCALAPPDATA%\DingTalk_133\Default\Local Storage\leveldb\
```

其中键 `_https://sportmeta.hdu.edu.cn\0token` 的值即站点 JWT。
LevelDB 的 SSTable/WAL 数据块大多未压缩，JWT 作为完整可见 ASCII 保留在文件里，
因此按字节扫描 `eyJ...` 三段式字符串即可提取（参考 `Core\CredentialExtractor.cs`）。
Edge/Chrome 的同名目录同样适用。

## 8. 未纳入本次实现的接口（备查）

- 团队预约：`/client/show_team_venue/`、`/client/choose_team_venue/`、`/client/team_post_site_situation/`、
  `/client/team_book_pay` 等
- 管理端：`/server/approval_team_book`、`/server/team_book_list_by_scope`、`/server/team_urgent_list` 等
- 其他：`/client/post_oci_ctrl`（时段控制）、`/client/download_file`、`/client/get_behavior_data/`
