# Codex Usage Toolbar for Windows — PLAN

## 0. 目标

做一个尽量轻量的 Windows 桌面工具，用于显示 Hyper-V Ubuntu VM 中 cc-switch 已采集到的 Codex 用量数据。

工具只负责展示，不负责采集、解析 Codex 原始日志，也不读取任何 Codex/cc-switch 凭据。

## 1. 显示范围

### 1.1 额度显示

Windows 悬浮工具必须显示：

- 5h 额度剩余
- 5h 额度刷新 / 重置时间
- 每周额度剩余
- 每周额度刷新 / 重置时间
- 本工具最后一次成功刷新时间
- 数据是否过期 / stale

建议 UI 文案：

```text
Codex
5h   剩 19% · 重置 17:00
Week 剩 77% · 重置 Tue 00:00
↻    14:23
```

### 1.2 Token 显示

只统计 `Codex`，不混入 Claude、Gemini、OpenCode 或其他 app。

必须显示四个时间窗口：

- 1 天
- 7 天
- 14 天
- 30 天

每个窗口显示：

- token 消耗
- 命中
- 命中率
- 总请求数
- 总成本

建议表格：

```text
Range   Tokens   Hit      Hit%    Req   Cost
1d      649k     320k     49.3%   64    $1.82
7d      4.6M     2.4M     52.1%   390   $13.92
14d     8.9M     4.8M     53.9%   812   $27.40
30d     18.2M    10.1M    55.5%   1690  $61.05
```

字段含义：

- `Tokens`：cc-switch 提供的 cache-normalized / real total tokens，或者 exporter 输出的 `real_total_tokens`。
- `Hit`：cache hit tokens，优先使用 cc-switch 已计算字段；如果 exporter 只给原始字段，则使用 `cache_read_tokens`。
- `Hit%`：优先使用 cc-switch 已计算的命中率；Windows 端不重新定义命中率公式。
- `Req`：Codex 请求总数。
- `Cost`：cc-switch 计算出的 estimated total cost，显示为 USD。

## 2. 关键设计原则

### 2.1 Windows 端保持 dumb client

Windows 工具不做以下事情：

- 不解析 Codex JSONL session log。
- 不读取 `~/.codex/auth.json`。
- 不读取 `~/.cc-switch/cc-switch.db`。
- 不通过 SMB / scp 把 cc-switch 数据库复制到 Windows。
- 不重新实现 cc-switch 的 token / cost / quota 计算逻辑。
- 不运行本地路由。
- 不依赖 Electron / Chromium WebView。

Windows 只执行一个远程命令，拿到 JSON，然后展示。

### 2.2 Ubuntu / cc-switch 是数据源

Ubuntu VM 中的 `ccswitch-export` 负责：

- 从 cc-switch 3.16.1 读取 Codex quota / balance。
- 从 cc-switch 3.16.1 读取 Codex token / cost / cache hit / request count。
- 只输出 Codex 数据。
- 输出脱敏后的稳定 JSON。

Windows 不理解 cc-switch 内部 schema。后续 cc-switch 升级导致 schema 变化时，只改 Ubuntu 侧 exporter。

### 2.3 轻量化优先级

优先级从高到低：

1. 低常驻资源占用。
2. 低依赖数量。
3. 启动快。
4. UI 足够清晰。
5. 后续可维护。
6. 视觉效果。

因此 MVP 不做图表、不做历史曲线、不做 WebView、不做 SQLite、本地不做后台服务。

## 3. 推荐 Windows 技术栈

### 3.1 UI 框架

推荐：`.NET 8 + WinForms`。

原因：

- 比 Electron/Tauri 更轻。
- `NotifyIcon` 托盘支持成熟。
- 无需嵌入浏览器。
- 悬浮窗、AlwaysOnTop、透明度、拖动、点击穿透都可以用 Win32 API 实现。
- 配合 Windows 内置 `ssh.exe`，可以避免 SSH.NET 依赖。

不推荐首版使用：

- Electron：过重。
- Tauri：更轻但工程链路更复杂，收益不明显。
- WPF：可行，但对于纯状态小窗比 WinForms 稍重。
- PowerShell GUI：原型可以，长期维护不够稳。
- AutoHotkey：极轻，但 JSON、错误状态、托盘/设置维护会变差。

### 3.2 运行形态

单进程桌面程序：

```text
CodexUsageToolbar.exe
```

功能：

- 托盘图标。
- 悬浮小窗。
- 可选 toolbar 模式。
- 定时 SSH 拉取 JSON。
- 本地 last-known-good 缓存。
- 配置文件。
- 最小日志。

不安装 Windows Service。

## 4. Windows 端架构

```text
CodexUsageToolbar
  ├─ Program.cs
  ├─ AppContext.cs
  ├─ PollingService.cs
  ├─ SshCommandClient.cs
  ├─ UsageJsonParser.cs
  ├─ UsageState.cs
  ├─ SettingsStore.cs
  ├─ LastGoodStore.cs
  ├─ FloatingWindow.cs
  ├─ TrayController.cs
  ├─ Formatters.cs
  └─ NativeWindowInterop.cs
```

### 4.1 模块职责

`SshCommandClient`

- 调用 Windows 内置 OpenSSH。
- 执行远程 exporter 命令。
- 设置 timeout。
- 捕获 stdout / stderr / exit code。
- 不解析业务数据。

`UsageJsonParser`

- 解析 exporter 输出 JSON。
- 校验 `app == "codex"`。
- 校验 schema version。
- 将 JSON 映射为内部 view model。

`PollingService`

- 定时刷新。
- 管理成功 / 失败 / stale 状态。
- 避免并发刷新。
- 失败时保留 last-known-good。

`FloatingWindow`

- 显示 compact / expanded UI。
- AlwaysOnTop。
- 可拖动。
- 可调透明度。
- 可锁定位置。
- 可选点击穿透。

`TrayController`

- 托盘图标。
- 右键菜单。
- tooltip。
- 手动刷新。
- 显示 / 隐藏悬浮窗。
- 退出。

`SettingsStore`

- 读取 / 写入配置。
- 首版可以只用 JSON 配置文件，不做复杂设置页。

## 5. SSH 数据通道

### 5.1 推荐调用方式

Windows 调用：

```powershell
ssh -o BatchMode=yes `
    -o ConnectTimeout=3 `
    -o ServerAliveInterval=10 `
    codex-vm `
    "~/.local/bin/ccswitch-export codex --windows 1d,7d,14d,30d --json"
```

建议在 Windows `%USERPROFILE%\.ssh\config` 里配置：

```sshconfig
Host codex-vm
  HostName 192.168.x.x
  User ubuntu
  IdentityFile ~/.ssh/codex_usage_vm_ed25519
  IdentitiesOnly yes
```

Windows 程序只保存 `Host alias` 和远程命令，不保存密码。

### 5.2 安全加固

Ubuntu `authorized_keys` 可限制该 key 只能执行 exporter：

```text
command="/home/ubuntu/.local/bin/ccswitch-export codex --windows 1d,7d,14d,30d --json",no-port-forwarding,no-X11-forwarding,no-agent-forwarding ssh-ed25519 AAAA...
```

如果使用 forced command，Windows 端实际只需要：

```powershell
ssh codex-vm
```

### 5.3 Timeout

建议：

- SSH connect timeout：3 秒。
- 命令总 timeout：5 秒。
- 正常轮询间隔：60 秒。
- 展开窗口时：30 秒。
- 连续失败后退避：120 秒、300 秒。

## 6. Ubuntu exporter JSON contract

Windows 端只依赖这个 contract。字段名必须稳定。

```json
{
  "schema_version": "1.0",
  "app": "codex",
  "source": "cc-switch",
  "cc_switch": {
    "version": "3.16.1"
  },
  "collected_at": "2026-06-26T14:23:10+09:00",
  "quota": {
    "five_hour": {
      "remaining_percent": 19.0,
      "used_percent": 81.0,
      "reset_at": "2026-06-26T17:00:00+09:00",
      "available": true
    },
    "weekly": {
      "remaining_percent": 77.0,
      "used_percent": 23.0,
      "reset_at": "2026-06-30T00:00:00+09:00",
      "available": true
    }
  },
  "tokens": {
    "1d": {
      "total_tokens": 649000,
      "hit_tokens": 320000,
      "hit_rate": 0.493,
      "requests": 64,
      "total_cost_usd": 1.82
    },
    "7d": {
      "total_tokens": 4600000,
      "hit_tokens": 2400000,
      "hit_rate": 0.521,
      "requests": 390,
      "total_cost_usd": 13.92
    },
    "14d": {
      "total_tokens": 8900000,
      "hit_tokens": 4800000,
      "hit_rate": 0.539,
      "requests": 812,
      "total_cost_usd": 27.40
    },
    "30d": {
      "total_tokens": 18200000,
      "hit_tokens": 10100000,
      "hit_rate": 0.555,
      "requests": 1690,
      "total_cost_usd": 61.05
    }
  },
  "refresh": {
    "last_success_at": "2026-06-26T14:23:10+09:00",
    "cc_switch_last_event_at": "2026-06-26T14:20:03+09:00",
    "stale": false
  },
  "errors": []
}
```

### 6.1 Contract 规则

- `app` 必须是 `codex`。
- token 窗口必须包含 `1d`、`7d`、`14d`、`30d`。
- `hit_rate` 用 0 到 1 的小数，Windows 负责格式化成百分比。
- `total_cost_usd` 用数字，不带 `$`。
- 时间使用 ISO 8601。
- Windows 展示时转成本机时区。
- 如果 quota 不可用，`available=false`，字段可为 `null`。
- 如果某个 token 窗口没有数据，返回 0，不省略字段。

## 7. Windows 配置文件

路径：

```text
%LOCALAPPDATA%\CodexUsageToolbar\settings.json
```

示例：

```json
{
  "sshHost": "codex-vm",
  "remoteCommand": "~/.local/bin/ccswitch-export codex --windows 1d,7d,14d,30d --json",
  "pollIntervalSeconds": 60,
  "expandedPollIntervalSeconds": 30,
  "commandTimeoutSeconds": 5,
  "staleAfterSeconds": 300,
  "opacity": 0.92,
  "alwaysOnTop": true,
  "clickThrough": false,
  "startMinimizedToTray": true,
  "window": {
    "x": 1600,
    "y": 80,
    "width": 340,
    "height": 180,
    "mode": "compact"
  },
  "thresholds": {
    "fiveHourRemainingWarnPercent": 15,
    "weeklyRemainingWarnPercent": 20
  }
}
```

## 8. 本地缓存与日志

### 8.1 Last-known-good

路径：

```text
%LOCALAPPDATA%\CodexUsageToolbar\last-good.json
```

用途：

- VM 离线时继续显示最后一次成功数据。
- UI 显示 `STALE`。
- 不把失败误显示成 0。

### 8.2 日志

路径：

```text
%LOCALAPPDATA%\CodexUsageToolbar\logs\app.log
```

只记录：

- 时间。
- SSH exit code。
- 刷新耗时。
- 错误类型。
- 是否使用 last-good。

不记录：

- 原始 Codex prompt。
- request body。
- response body。
- auth token。
- provider key。
- 完整 cc-switch 数据库内容。

日志大小限制：

- 单文件 256 KB。
- 最多 3 个滚动文件。

## 9. UI 设计

### 9.1 Compact 悬浮窗

默认样式：

```text
┌────────────────────────────┐
│ Codex          ↻ 14:23     │
│ 5h    剩 19%   重置 17:00  │
│ Week  剩 77%   重置 Tue    │
│ Tok   1d 649k  7d 4.6M     │
└────────────────────────────┘
```

适合长期悬浮。

### 9.2 Expanded 悬浮窗

点击 compact 窗口后展开：

```text
Codex Usage                         ↻ 14:23
5h    剩 19%     重置 17:00
Week  剩 77%     重置 Tue 00:00

Range   Tokens   Hit      Hit%    Req   Cost
1d      649k     320k     49.3%   64    $1.82
7d      4.6M     2.4M     52.1%   390   $13.92
14d     8.9M     4.8M     53.9%   812   $27.40
30d     18.2M    10.1M    55.5%   1690  $61.05

Source: cc-switch 3.16.1 · Event: 14:20
```

### 9.3 Tray 菜单

右键菜单：

```text
Codex Usage Toolbar
-------------------
Show / Hide
Refresh now
Compact / Expanded
Click-through: On / Off
Always on top: On / Off
Open settings file
Open logs folder
Exit
```

### 9.4 Tooltip

托盘 hover：

```text
Codex 5h left 19%, weekly left 77%
1d 649k tokens, 64 req, $1.82
Last refresh 14:23
```

## 10. 刷新与状态机

### 10.1 状态

```text
OK
STALE
VM_OFFLINE
SSH_FAILED
EXPORTER_FAILED
INVALID_JSON
SCHEMA_UNSUPPORTED
QUOTA_UNAVAILABLE
TOKEN_EMPTY
```

### 10.2 状态处理

`OK`

- 正常显示。
- 保存 last-good。

`STALE`

- 显示 last-good。
- 标记 `stale`。
- 不清空数字。

`VM_OFFLINE` / `SSH_FAILED`

- 显示 last-good。
- tooltip 显示 SSH 失败。
- 下次刷新退避。

`EXPORTER_FAILED`

- 显示 last-good。
- 展开窗口显示 exporter stderr 摘要。

`INVALID_JSON`

- 显示 last-good。
- 记录日志。

`SCHEMA_UNSUPPORTED`

- 显示 last-good。
- 提示 exporter / Windows contract 不匹配。

`QUOTA_UNAVAILABLE`

- token 正常显示。
- quota 区域显示 `--`。

`TOKEN_EMPTY`

- quota 正常显示。
- token 表显示 0 或 `No Codex usage`。

## 11. 数字格式化规则

### 11.1 Token

```text
0 - 999          932
1,000 - 999,999 649k
>= 1,000,000     4.6M
>= 1,000,000,000 1.2B
```

### 11.2 命中率

```text
0.493 -> 49.3%
null  -> --
```

### 11.3 成本

```text
< $1       $0.23
>= $1      $13.92
>= $100    $128
null       --
```

### 11.4 时间

- 今日：`HH:mm`。
- 非今日：`MM-dd HH:mm`。
- 周重置时间可显示星期：`Tue 00:00`。

## 12. 轻量化约束

### 12.1 依赖约束

MVP 依赖：

- .NET 8 runtime。
- Windows OpenSSH client。
- System.Text.Json。
- WinForms。

不引入：

- Electron。
- Chromium。
- SQLite client。
- SSH.NET，除非 Windows 内置 ssh.exe 不满足需求。
- Chart library。
- Web server。
- Local background service。

### 12.2 资源目标

目标值：

- 常驻内存：低于 60 MB。
- 空闲 CPU：接近 0%。
- 刷新时 CPU spike：短暂。
- 正常刷新耗时：小于 1 秒；超时上限 5 秒。
- 本地磁盘写入：只写 settings、last-good、小日志。

### 12.3 UI 约束

MVP 不做动画。

MVP 不做图表。

MVP 不做多窗口复杂布局。

MVP 不做多 provider tab。

只做一个 Codex 小窗。

## 13. 开发阶段

### Phase 1 — JSON contract 验证

目标：确认 Ubuntu exporter 可以稳定返回 Windows 所需字段。

交付：

- `ccswitch-export codex --windows 1d,7d,14d,30d --json`
- 手工 SSH 可拿到 JSON。
- JSON 包含 quota、1d/7d/14d/30d token、hit、hit rate、request、cost。
- 只包含 Codex。

验收：

```powershell
ssh codex-vm "~/.local/bin/ccswitch-export codex --windows 1d,7d,14d,30d --json"
```

输出能被 `jq` 或 `ConvertFrom-Json` 正常解析。

### Phase 2 — Windows console prototype

目标：先不做 UI，只做拉取、解析、格式化。

交付：

```powershell
CodexUsageToolbar.exe --once
```

输出：

```text
5h left 19%, reset 17:00
Week left 77%, reset Tue 00:00
1d 649k tokens, hit 320k, hit 49.3%, req 64, cost $1.82
7d 4.6M tokens, hit 2.4M, hit 52.1%, req 390, cost $13.92
14d ...
30d ...
Last refresh 14:23
```

验收：

- SSH 成功时输出正常。
- SSH 失败时 exit code 非 0。
- JSON 错误时能给出明确错误。

### Phase 3 — Tray + compact floating window

目标：实现最小可用桌面悬浮显示。

交付：

- 托盘图标。
- compact 悬浮窗。
- 60 秒自动刷新。
- 手动 refresh。
- last-good 缓存。
- VM 离线时显示 stale。

验收：

- 能长时间悬浮在桌面。
- 不影响终端 SSH/Codex 使用。
- VM 关机后不崩溃。
- VM 恢复后自动恢复刷新。

### Phase 4 — Expanded view + 设置

目标：补齐完整数据表和基本配置。

交付：

- expanded token table。
- 设置文件打开入口。
- 透明度。
- 窗口位置保存。
- AlwaysOnTop 开关。
- 点击穿透开关。

验收：

- 1d/7d/14d/30d 全部显示。
- 位置和模式重启后保持。
- 设置变更后可生效。

### Phase 5 — 打包与自启动

目标：形成日常使用版本。

交付：

- zip 包。
- README。
- 示例 `settings.json`。
- 可选开机启动。

自启动方式优先使用：

```text
%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup
```

或 HKCU Run key。

## 14. 验收标准

MVP 完成标准：

- Windows 桌面显示 Codex 5h 剩余额度。
- Windows 桌面显示 Codex weekly 剩余额度。
- 显示 5h / weekly 重置时间。
- 显示本工具最后刷新时间。
- 显示 Codex 1d / 7d / 14d / 30d token 消耗。
- 显示命中、命中率、请求数、总成本。
- 所有 token/cost 数据只统计 Codex。
- SSH 失败不崩溃。
- exporter 返回异常不崩溃。
- VM 离线时显示 stale + last-good。
- Windows 端不读取任何 credential。
- 无 Electron / WebView。

## 15. 风险与规避

### 15.1 cc-switch schema 变化

风险：cc-switch 升级后内部数据库 schema 改变。

规避：Windows 不直接读 cc-switch.db，只依赖 Ubuntu exporter 的稳定 JSON contract。

### 15.2 quota 不可用

风险：cc-switch 暂时无法查询 Codex 5h / weekly quota。

规避：token 表继续显示；quota 区域显示 `--`；状态显示 `QUOTA_UNAVAILABLE`。

### 15.3 token 数据延迟

风险：cc-switch usage sync 可能落后于实际 Codex 使用。

规避：同时显示 `last refresh` 和 `cc_switch_last_event_at`。

### 15.4 cost 与官方账单不完全一致

风险：cc-switch 的 cost 是 estimated cost，可能与官方最终账单不同。

规避：UI 显示 `Cost`，expanded view 中标记 `Estimated by cc-switch`。

### 15.5 SSH 卡住

风险：SSH 命令阻塞导致 UI 卡顿。

规避：刷新在后台 task 执行；5 秒 command timeout；UI 线程只接收结果。

## 16. 最终推荐结论

Windows 端实现为：

```text
.NET 8 WinForms + Windows ssh.exe + System.Text.Json + last-good JSON cache
```

数据流：

```text
cc-switch 3.16.1 on Ubuntu
        ↓
ccswitch-export codex --windows 1d,7d,14d,30d --json
        ↓ SSH
Windows CodexUsageToolbar.exe
        ↓
tray + floating compact/expanded window
```

这个方案的关键点是：Windows 足够轻，只做展示；复杂的数据读取、Codex 过滤、token/cost/hit rate 计算全部留在 Ubuntu 的 cc-switch/exporter 侧。

## 17. 资料依据

- cc-switch README：Usage dashboard 支持 spending、requests、tokens、request logs 和 custom pricing。
- cc-switch usage manual：Usage 页面支持时间范围、token trend、cache hit tokens、cost、request logs、App Type 过滤。
- cc-switch v3.15.0 release notes：Usage Dashboard Hero 支持 cache-normalized real total tokens 和 cache hit rate。
- cc-switch v3.16.1 release notes：Codex OAuth preservation、Codex native balance / Coding Plan credential lookup 修复。
