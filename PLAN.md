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

必须显示五个时间窗口：

- 当天
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
Today   512k     250k     48.8%   42    $1.31
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
- 如果 cc-switch DB 没有 quota 表，则从 Codex session JSONL 的 `payload.rate_limits` 读取非敏感 quota 字段。
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
    "~/.local/bin/ccswitch-export codex --windows today,1d,7d,14d,30d --json"
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
command="/home/ubuntu/.local/bin/ccswitch-export codex --windows today,1d,7d,14d,30d --json",no-port-forwarding,no-X11-forwarding,no-agent-forwarding ssh-ed25519 AAAA...
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
    "today": {
      "total_tokens": 512000,
      "hit_tokens": 250000,
      "hit_rate": 0.488,
      "requests": 42,
      "total_cost_usd": 1.31
    },
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
- token 窗口必须包含 `today`、`1d`、`7d`、`14d`、`30d`。
- `hit_rate` 用 0 到 1 的小数，定义为 `cache_read_tokens / input_tokens`，Windows 负责格式化成百分比。
- `total_cost_usd` 用数字，不带 `$`。
- 时间使用 ISO 8601。
- Windows 展示时转成本机时区。
- 如果 quota 不可用，`available=false`，字段可为 `null`。
- 如果某个 token 窗口没有数据，返回 0，不省略字段。

## 7. Windows 配置文件

首版支持两个配置位置：

```text
.\settings.json
CodexUsageToolbar.exe 同目录\settings.json
```

如果两个都存在，优先读取当前工作目录下的 `settings.json`。命令行参数和环境变量可以覆盖配置文件。

长期路径：

```text
%LOCALAPPDATA%\CodexUsageToolbar\settings.json
```

示例：

```json
{
  "sshHost": "jiaming@192.168.32.123",
  "remoteCommand": "~/.local/bin/ccswitch-export codex --windows today,1d,7d,14d,30d --json",
  "identityFile": null,
  "port": 22,
  "pollIntervalSeconds": 60,
  "connectTimeoutSeconds": 3,
  "commandTimeoutSeconds": 5,
  "opacity": 0.95,
  "alwaysOnTop": true,
  "startHidden": false,
  "clickThrough": false,
  "startWithWindows": false,
  "accentColor": "cyan",
  "backgroundMode": "dark",
  "quotaLayout": "ring",
  "window": {
    "x": null,
    "y": null,
    "width": 390,
    "height": 190
  }
}
```

当前有效配置项：

- `sshHost`：SSH 目标，例如 `jiaming@192.168.32.123` 或 `codex-vm`。
- `remoteCommand`：Ubuntu exporter 命令。
- `identityFile`：可选 SSH key 路径。
- `port`：SSH 端口。
- `pollIntervalSeconds`：自动刷新间隔，程序限制在 5 到 3600 秒。
- `connectTimeoutSeconds`：SSH 连接超时，程序限制在 1 到 60 秒。
- `commandTimeoutSeconds`：远程命令总超时，程序限制在 1 到 120 秒。
- `opacity`：窗口透明度，程序限制在 0.35 到 1.0。
- `alwaysOnTop`：悬浮窗是否置顶。
- `startHidden`：启动时只进托盘，不显示悬浮窗。
- `clickThrough`：悬浮窗是否点击穿透，也可在托盘右键菜单切换。
- `startWithWindows`：是否写入 HKCU Run 开机启动，也可在托盘右键菜单切换。
- `accentColor`：主色调，可选 `cyan`、`blue`、`green`、`purple`、`amber`，也可在托盘右键菜单切换。
- `backgroundMode`：背景模式，可选 `dark`、`light`，也可在悬浮窗标题栏按钮切换。
- `quotaLayout`：额度展示布局，可选 `ring`、`bar`，也可在悬浮窗标题栏按钮切换。
- `window.x/y/width/height`：窗口初始位置和尺寸；`x/y` 为 `null` 时自动放到屏幕右上角。

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
Today   512k     250k     48.8%   42    $1.31
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

MVP 运行依赖：

- Windows OpenSSH client。

如果 GitHub Actions 发布 `self-contained` 单文件包，最终用户机器不需要额外安装 .NET runtime。

开发 / 调试依赖：

- .NET 8 SDK。
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
- Node.js / npm / pnpm / yarn。
- Visual Studio 完整 IDE，除非后续确实需要调试 WinForms 设计器。
- 本地安装 Inno Setup、WiX、MSIX Packaging Tool 等打包工具。

### 12.2 本地环境边界

本地开发尽量保持轻量：

- 必须：编辑器 + Git。
- 可选：.NET 8 SDK，用于 `dotnet run`、`dotnet test`、`dotnet build`。
- 不要求：Visual Studio、Node.js、Electron/Tauri toolchain、Windows installer toolchain。
- 不要求：本地生成 release zip、installer、签名包。

本地常用命令只保留：

```powershell
dotnet run --project src/CodexUsageToolbar -- --once
dotnet test
```

发布验证和最终打包放到 GitHub Actions。

### 12.3 资源目标

目标值：

- 常驻内存：低于 60 MB。
- 空闲 CPU：接近 0%。
- 刷新时 CPU spike：短暂。
- 正常刷新耗时：小于 1 秒；超时上限 5 秒。
- 本地磁盘写入：只写 settings、last-good、小日志。

### 12.4 UI 约束

MVP 不做动画。

MVP 不做图表。

MVP 不做多窗口复杂布局。

MVP 不做多 provider tab。

只做一个 Codex 小窗。

## 13. 开发阶段

### Phase 1 — JSON contract 验证

状态：已完成。

目标：确认 Ubuntu exporter 可以稳定返回 Windows 所需字段。

交付：

- `ccswitch-export codex --windows today,1d,7d,14d,30d --json`
- `scripts/ccswitch-export`
- `scripts/install-ubuntu-exporter.sh`
- 手工 SSH 可拿到 JSON。
- JSON 包含 quota、today/1d/7d/14d/30d token、hit、hit rate、request、cost。
- 只包含 Codex。

验收：

先在 Ubuntu VM 本地验证 exporter：

```bash
bash scripts/check-p1-exporter.sh
```

如果 exporter 不在 `~/.local/bin/ccswitch-export`，传入实际路径：

```bash
bash scripts/check-p1-exporter.sh /actual/path/to/ccswitch-export
```

安装仓库内 exporter：

```bash
bash scripts/install-ubuntu-exporter.sh
```

Ubuntu 本地通过后，再从 Windows 验证 SSH 通道：

```powershell
ssh codex-vm "~/.local/bin/ccswitch-export codex --windows today,1d,7d,14d,30d --json"
```

输出能被 `jq` 或 `ConvertFrom-Json` 正常解析。

仓库提供轻量验证脚本：

```powershell
.\scripts\Validate-P1Exporter.ps1
```

脚本只调用 Windows `ssh.exe` 和 PowerShell `ConvertFrom-Json`，不安装依赖；验证通过时输出 `P1 exporter contract OK`。

如果还没有配置 `codex-vm` SSH alias，可以直接传 VM 地址：

```powershell
.\scripts\Validate-P1Exporter.ps1 -SshHost ubuntu@192.168.x.x -IdentityFile "$HOME\.ssh\codex_usage_vm_ed25519"
```

如果 Ubuntu 提示符是 `jiaming@jiaming-VM-Ubuntu:~$`，可以先尝试：

```powershell
.\scripts\Validate-P1Exporter.ps1 -SshHost jiaming@jiaming-VM-Ubuntu
```

如果 SSH 已通但提示 `~/.local/bin/ccswitch-export` 不存在，先运行诊断：

```powershell
.\scripts\Validate-P1Exporter.ps1 -SshHost jiaming@192.168.32.123 -CheckOnly
```

然后根据实际 exporter 路径改用 `-RemoteCommand`，或先在 Ubuntu 侧创建 `~/.local/bin/ccswitch-export`。

### Phase 2 — Windows console prototype

状态：已完成。

目标：先不做 UI，只做拉取、解析、格式化。

交付：

```powershell
CodexUsageToolbar.exe --once
```

输出：

```text
5h left 19%, reset 17:00
Week left 77%, reset Tue 00:00
Today 512k tokens, hit 250k, hit 48.8%, req 42, cost $1.31
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

状态：已完成。

目标：实现最小可用桌面悬浮显示。

交付：

- 托盘图标。
- compact 悬浮窗。
- 60 秒自动刷新。
- 手动 refresh。
- last-good 缓存。
- VM 离线时显示 stale。
- 深色科技感 compact UI。
- token 横向 bar。
- cache hit rate 圆环。

启动时可用参数或环境变量指定 SSH 目标：

```powershell
.\CodexUsageToolbar.exe --ssh-host jiaming@192.168.32.123
```

或：

```powershell
$env:CODEX_USAGE_SSH_HOST = "jiaming@192.168.32.123"
.\CodexUsageToolbar.exe
```

验收：

- 能长时间悬浮在桌面。
- 不影响终端 SSH/Codex 使用。
- VM 关机后不崩溃。
- VM 恢复后自动恢复刷新。

### Phase 4 — Expanded view + 设置

状态：已完成。

目标：补齐完整数据表和基本配置。

交付：

- expanded token table。
- 设置文件打开入口。
- 透明度。
- 窗口位置保存。
- AlwaysOnTop 开关。
- 点击穿透开关。
- 点击穿透托盘开关。
- 开机启动托盘开关。
- compact / expanded 切换按钮。
- resize 后布局自适应。
- 悬浮窗边缘拖动调整大小，右下角提供 resize grip 提示。

验收：

- today/1d/7d/14d/30d 全部显示。
- 位置和模式重启后保持。
- 设置变更后可生效。

### Phase 5 — GitHub Actions 打包与自启动

目标：形成日常使用版本。

交付：

- GitHub Actions 生成 zip 包。
- README。
- 示例 `settings.json`。
- release artifact。
- 可选开机启动。

本地不做正式打包。本地只做开发运行、单元测试和必要的手工验证。

自启动方式优先使用：

```text
%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup
```

或 HKCU Run key。

## 14. GitHub Actions 打包方案

状态：已落地，见 `.github/workflows/build.yml`。

### 14.1 目标

打包只在 GitHub Actions 中完成，避免本地安装重型环境和打包工具。

Actions 负责：

- restore。
- build。
- test。
- publish Windows x64。
- 生成 zip。
- 上传 artifact。
- tag / release 时附加到 GitHub Release。

本地负责：

- 修改代码。
- 可选运行 `dotnet run` 和 `dotnet test`。
- 不负责生成正式发布包。

### 14.2 推荐 workflow

路径：

```text
.github/workflows/build.yml
```

触发：

- `push` 到 `main`。
- `pull_request`。
- `workflow_dispatch` 手动打包。
- `v*` tag 发布。

建议 workflow：

```yaml
name: build

on:
  push:
    branches: [main]
    tags: ['v*']
  pull_request:
  workflow_dispatch:

permissions:
  contents: write

jobs:
  windows:
    runs-on: windows-latest

    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Restore
        run: dotnet restore

      - name: Build
        run: dotnet build --configuration Release --no-restore

      - name: Test
        run: dotnet test --configuration Release --no-build

      - name: Publish win-x64
        run: >
          dotnet publish src/CodexUsageToolbar/CodexUsageToolbar.csproj
          --configuration Release
          --runtime win-x64
          --self-contained true
          --output artifacts/publish/win-x64
          /p:PublishSingleFile=true
          /p:EnableCompressionInSingleFile=true
          /p:IncludeNativeLibrariesForSelfExtract=true
          /p:PublishTrimmed=false

      - name: Add sample files
        shell: pwsh
        run: |
          Copy-Item README.md artifacts/publish/win-x64/ -ErrorAction SilentlyContinue
          Copy-Item examples/settings.example.json artifacts/publish/win-x64/settings.example.json -ErrorAction SilentlyContinue

      - name: Pack zip
        shell: pwsh
        run: |
          New-Item -ItemType Directory -Force artifacts/dist | Out-Null
          Compress-Archive -Path artifacts/publish/win-x64/* -DestinationPath artifacts/dist/CodexUsageToolbar-win-x64.zip -Force

      - uses: actions/upload-artifact@v4
        with:
          name: CodexUsageToolbar-win-x64
          path: artifacts/dist/CodexUsageToolbar-win-x64.zip

      - uses: softprops/action-gh-release@v2
        if: startsWith(github.ref, 'refs/tags/v')
        with:
          files: artifacts/dist/CodexUsageToolbar-win-x64.zip
```

说明：

- `--self-contained true` 让最终 zip 不依赖目标机器安装 .NET runtime，代价是包体更大。
- CI 使用 `NuGet.CI.config` 访问 `nuget.org` 获取 Windows runtime packs；本地 `NuGet.config` 仍清空外部源，保持普通 build 轻量。
- `PublishTrimmed=false` 是 WinForms 首版的保守选择，避免反射、资源、控件初始化被裁剪影响。
- 不生成 installer，首版只发布 zip，降低维护成本。
- 不在 workflow 里引入 Node、Electron、Tauri、WiX、MSIX。

### 14.3 产物结构

zip 解压后建议结构：

```text
CodexUsageToolbar-win-x64/
  CodexUsageToolbar.exe
  settings.example.json
  README.md
  LICENSE
```

运行后用户数据仍写入：

```text
%LOCALAPPDATA%\CodexUsageToolbar\
```

发布包里不包含用户私有配置、SSH key、last-good 缓存或日志。

### 14.4 版本策略

- 普通 push / PR：只验证 build 和 test，并上传临时 artifact。
- 手动 `workflow_dispatch`：可生成测试 zip。
- tag `v0.1.0`：生成 GitHub Release 附件。
- 版本号后续从 csproj 的 `Version` / `AssemblyVersion` 统一管理。

### 14.5 本地与 CI 职责边界

本地不要安装专门打包环境。

本地可以只做：

```powershell
dotnet run --project src/CodexUsageToolbar -- --once
dotnet test
```

CI 必须做：

```powershell
dotnet publish ... --runtime win-x64 --self-contained true
Compress-Archive ...
```

这样可以保证正式产物可复现，也避免开发机因为缺少某个 Windows 打包工具而阻塞。

### 14.6 下载验证

push 到 GitHub 后：

1. 打开 GitHub 仓库的 `Actions` 页面。
2. 进入最新的 `build` workflow run。
3. 下载 `CodexUsageToolbar-win-x64` artifact。
4. 解压 `CodexUsageToolbar-win-x64.zip`。
5. 在 Windows PowerShell 中运行：

```powershell
.\CodexUsageToolbar.exe --once --ssh-host jiaming@192.168.32.123
```

如果使用 `codex-vm` SSH alias：

```powershell
.\CodexUsageToolbar.exe --once --ssh-host codex-vm
```

## 15. 验收标准

MVP 完成标准：

- Windows 桌面显示 Codex 5h 剩余额度。
- Windows 桌面显示 Codex weekly 剩余额度。
- 显示 5h / weekly 重置时间。
- 显示本工具最后刷新时间。
- 显示 Codex today / 1d / 7d / 14d / 30d token 消耗。
- 显示命中、命中率、请求数、总成本。
- 所有 token/cost 数据只统计 Codex。
- SSH 失败不崩溃。
- exporter 返回异常不崩溃。
- VM 离线时显示 stale + last-good。
- Windows 端不读取任何 credential。
- 无 Electron / WebView。

## 16. 风险与规避

### 16.1 cc-switch schema 变化

风险：cc-switch 升级后内部数据库 schema 改变。

规避：Windows 不直接读 cc-switch.db，只依赖 Ubuntu exporter 的稳定 JSON contract。

### 16.2 quota 不可用

风险：cc-switch 暂时无法查询 Codex 5h / weekly quota。

规避：token 表继续显示；quota 区域显示 `--`；状态显示 `QUOTA_UNAVAILABLE`。

### 16.3 token 数据延迟

风险：cc-switch usage sync 可能落后于实际 Codex 使用。

规避：同时显示 `last refresh` 和 `cc_switch_last_event_at`。

### 16.4 cost 与官方账单不完全一致

风险：cc-switch 的 cost 是 estimated cost，可能与官方最终账单不同。

规避：UI 显示 `Cost`，expanded view 中标记 `Estimated by cc-switch`。

### 16.5 SSH 卡住

风险：SSH 命令阻塞导致 UI 卡顿。

规避：刷新在后台 task 执行；5 秒 command timeout；UI 线程只接收结果。

### 16.6 CI 打包失败

风险：GitHub Actions 的 Windows runner、.NET SDK 或 publish 参数变化导致 release 包生成失败。

规避：workflow 固定 `actions/setup-dotnet@v4` 和 `8.0.x`；PR 必跑 build/test；tag release 前先用 `workflow_dispatch` 验证一次。

### 16.7 self-contained 包体偏大

风险：self-contained 单文件 zip 比 framework-dependent 包明显更大。

规避：首版接受包体换取免安装；如果后续确实需要减小体积，再增加 framework-dependent artifact，但不要让本地打包链路变复杂。

## 17. 最终推荐结论

Windows 端实现为：

```text
.NET 8 WinForms + Windows ssh.exe + System.Text.Json + last-good JSON cache + GitHub Actions publish
```

数据流：

```text
cc-switch 3.16.1 on Ubuntu
        ↓
ccswitch-export codex --windows today,1d,7d,14d,30d --json
        ↓ SSH
Windows CodexUsageToolbar.exe
        ↓
tray + floating compact/expanded window
```

这个方案的关键点是：Windows 足够轻，只做展示；复杂的数据读取、Codex 过滤、token/cost/hit rate 计算全部留在 Ubuntu 的 cc-switch/exporter 侧。

打包策略是：本地尽量只保留编辑、运行和测试能力；正式 Windows zip 由 GitHub Actions 在 `windows-latest` 上生成。

## 18. 资料依据

- cc-switch README：Usage dashboard 支持 spending、requests、tokens、request logs 和 custom pricing。
- cc-switch usage manual：Usage 页面支持时间范围、token trend、cache hit tokens、cost、request logs、App Type 过滤。
- cc-switch v3.15.0 release notes：Usage Dashboard Hero 支持 cache-normalized real total tokens 和 cache hit rate。
- cc-switch v3.16.1 release notes：Codex OAuth preservation、Codex native balance / Coding Plan credential lookup 修复。
