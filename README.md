# Tianshu Qitan Launcher

Windows IE WebBrowser shell for the Flash game entry:

```text
http://v5.t.imop.com/
```

## Requirements

- Windows
- IE WebBrowser control
- Flash ActiveX plugin registered in the system
- Prefer an isolated VM for old IE/Flash content

## Build

```powershell
.\build.ps1
```

Output:

```text
bin\Release\TianshuQitanLauncher.exe
```

## Run

```powershell
.\run.ps1
```

## Window Mode

The launcher starts as a normal bordered Windows window with a title bar. It fills the current screen working area, so it does not cover the Windows taskbar.

## Diagnostics

Navigation events are written to:

```text
bin\Release\data\instances\<instance-id>\logs\launcher.log
```

Mouse diagnostics are written to:

```text
bin\Release\data\instances\<instance-id>\logs\mouse-diagnostics.log
```

Hotkeys:

- `F8`: write one mouse diagnostic snapshot immediately
- `F9`: toggle automatic mouse diagnostic sampling

## Hotkeys

- `F11`: fit working area / restore window
- `Esc`: restore window
- `F5` / `Ctrl+F5`: refresh; if automatic login is enabled or was active, restart the full login flow after loading
- `Ctrl+R`: same refresh-and-resume behavior
- `Alt+Left`: back
- `Alt+Right`: forward
- `Ctrl+F8`: start/stop the selected atomic operation recording
- `Ctrl+F9`: insert an atomic operation step marker
- `Ctrl+F10`: force a non-destructive Flash repaint if the game display is corrupted
- `Ctrl+Shift+F12`: emergency bypass for packet mutation rules

`F11`, `Esc`, `F5`, and `Ctrl+F5` are registered as Win32 hotkeys by the currently active launcher window, so they still work when Flash owns keyboard focus without controlling another running account. Refresh releases any stale Flash mouse capture before navigation. Right-click messages inside the Flash area are blocked because the legacy ActiveX context menu can leave the game in a modal/frozen input state; right-clicks in the protocol workbench are unaffected.

`flashWindowMode=window` avoids the incomplete repaints caused by the page's windowless `opaque` Flash mode. Use `page` only when diagnosing compatibility with the server-provided mode. The repaint workaround now invalidates every native browser/Flash child window in four delayed passes after load, activation, resize, and splitter movement, so black stale regions are repainted without moving the mouse or reloading the game session.

## Atomic operation recording

Open the `原子操作` workbench tab, create or select a reusable template, enable the recorder, and press `Ctrl+F8` immediately before and after the manual game action. Use `Ctrl+F9` to place semantic step markers while Flash has focus.

Operation templates are stored per account at `data/accounts/<account-id>/operation-templates.json`, with a `.bak` recovery copy and a cross-process write lock. Existing templates from the shared source catalog or old published `protocols/*/operations.json` location seed the account catalog, so replacing or rebuilding the client no longer resets the template list and different accounts cannot overwrite each other.

Runs of the same template can be selected together for ordered frame comparison and dynamic-byte masks. Frame roles, field meanings, results, notes, and optional screenshot attachments are stored with the session. `导出分析包` writes a standalone `*.tsqop.sqlite` containing only the selected runs and included connections; account files and launcher credentials are never copied.

Use `会话命名` to append or replace a semantic suffix while preserving the timestamp prefix, for example `session-20260831-112228-317-除暴安良.sqlite`. The current live SQLite writer is safely checkpointed, closed, renamed, and reopened without ending capture. Session-name cells can also be edited directly; `Ctrl+C` copies the file name, `Ctrl+Shift+C` copies the full path, and the row context menu exposes both copy actions.

抓包 SQLite 按客户端实例保存在 `data/instances/<instance-id>/sessions/`。实际完整路径、文件命名规则、当前路径复制方式，以及“清空显示缓存”与数据库保存之间的关系见 [数据包 SQLite 保存路径说明](docs/capture-sqlite-path.md)。

“数据包”表格支持 `Ctrl+单击` 离散多选、`Shift+单击` 范围多选、`Ctrl+A` 全选和 `Ctrl+C` 复制选中行。右键菜单可以分别复制选中行、原始/有效 Hex、原始/有效 Hex/ASCII，也可以“清空选中数据包（仅界面）”或“清空全部显示缓存（保留 SQLite）”；两种清空操作都不会删除数据库中的抓包记录，也不会停止继续抓包。

## Auto login

Open the `自动登录` workbench tab, enter an account and password, select `一线` through `四线` and a role slot from 1 through 5, then choose `保存配置` or `保存并登录`. Enabling automatic login repeats the full flow after the next page load.

`刷新并登录` saves the current selections, completely refreshes the game page, and starts login again after the top-level document is ready. F5, Ctrl+F5, and Ctrl+R preserve an enabled or currently active automatic-login session across refreshes; an explicit `停止` still cancels a pending restart.

The password field is masked and the saved password is encrypted with Windows DPAPI for the current Windows user. The v5 wire protocol itself sends `CS_USER_PASS` credentials in plaintext and reuses a Base64 login ticket, so new captures redact the account, password, and ticket before SQLite storage, UI display, analysis bundles, or PCAPNG export. Existing session files are not rewritten automatically.

The selected line is resolved from each live `SC_GAMESERVER_LIST (0x00C9)` response instead of hard-coding the recorded ports. The selected role is resolved from each live `SC_ROLE_INFO_LIST (0x000C)` response; completion is confirmed by `SC_ROLE_START_POINT (0x0016)`. See [the login protocol notes](docs/login-protocol.md), [the role-selection protocol notes](docs/role-selection-protocol.md), [the heartbeat protocol notes](docs/heartbeat-protocol.md), [the bounty automation protocol notes](docs/bounty-protocol.md), [the guild donation protocol notes](docs/donation-protocol.md), [the run-loop protocol notes](docs/run-loop-protocol.md), and [the map-teleport protocol notes](docs/map-teleport-protocol.md) for the confirmed layouts and state sequences.

## TypeScript 无头网页客户端

根目录的 [`headlessclient`](headlessclient/README.md) 是不依赖 Flash 界面的第三方协议客户端原型。浏览器页面负责输入和状态展示，本机 Node 网关负责游戏原生 TCP；它已实现入口服认证、按实时列表选择一至四线、票据登录、角色槽位选择、进图和 `0x0053 → 0x0042` 心跳应答。账号密码不落盘，网关只监听 `127.0.0.1`。目前不包含地图渲染和完整玩法 UI。

## 地图传送基础功能

工作台“地图传送”页提供录制确认的 42 张地图及对应蟠龙图腾。可选择传送点 action 316/172 直传，也可用旧任务链接 `0x00B5` 瞬移到目标地图图腾；输入 X/Y 后还能自动到图腾、开启飞行列表并以 action 169 飞到指定坐标。所有流程使用当前连接的实时序号和服务端动态确认串，只有收到目标地图的 `SC_MAP_INFO (0x0055)` 才算成功。代码可调用 `TeleportTo`、`TeleportViaTotem` 和 `FlyTo`，详见 `docs/map-teleport-protocol.md`。

供其他功能衔接的高层函数是 `MapTeleportAutomationCoordinator.TeleportTo(int mapId)` 和 `TeleportTo(string mapNameOrId)`；已持有自动化锁的状态机可以直接使用 `TianshuMapTeleportProtocol.BuildSelectMap`、`BuildTeleportToMap` 和 `TianshuMapTeleportCatalog`。完整接口、协议字段及 42 张地图目录见 [地图传送基础功能](docs/map-teleport-protocol.md)。

## 自动跑环

工作台“自动跑环”页可设置本次 1–6 轮，每轮 20 环，最多 120 环；服务端提示次数用尽时停止。交付第 20 环并收到任务移除确认后，程序清空旧任务缓存，自动返回柳先元重新接取下一轮。程序从实时任务数据解析提交道具、NPC 对话或战斗任务、地图、NPC、坐标及进度；战斗环持续走步遇怪，每 4 秒一键恢复 HP/MP，进度完成后返回 NPC 交付。

所有请求使用当前游戏连接的实时序号，传送确认使用服务端本次随机 token。未录制的地图入口或无法确认的 NPC ID 会安全停止，不发送猜测报文。详见 [自动跑环协议与状态机](docs/run-loop-protocol.md)。

## 自动登山爬塔

工作台“登山爬塔”页会按录制库完成三项登山任务：飞行到三界关高攀攀接取任务，进入皇城皇宫和通天塔，通过通天塔传送人直达无间境十层交付，再到元荒境五层交付；随后从时雨山脚按走步帧经过半天坡进入清溪云涧交付，最后返回皇城皇宫。开始和停止按钮会与其他发包自动化互斥；同一客户端运行期间再次启动会跳过已由服务端确认完成的任务。

任务接取/交付使用已确认的 `0x0017 + 0x0018` 两阶段序列，飞行确认 token 和请求序号均取实时连接；塔层、山路只发送录制中验证过的 `0x00C1` 走步帧和 `0x0016` 进图帧。背包不足、地图不符或等待超时时会安全停止，不会自动丢弃物品。详见 [登山爬塔协议与状态机](docs/mountain-climb-protocol.md)。

## 自动捐献上古神器碎片

角色站在帮会捐献官附近后，打开工作台的“自动捐献”页并点击“开始自动捐献”。程序会按道具名称 `上古神器碎片(一等)` 和物品 ID `115000140` 自动查询实时背包格，无需用户指定位置；每次成功且确认背包扣减后继续，直到服务端次数耗尽、碎片耗尽或达到安全上限。

NPC 功能 ID、背包格和请求序号都从当前会话动态取得，不会重放录制值。自动捐献与自动除暴互斥，防止两个流程同时竞争请求序号。协议证据和停止条件见 [帮派捐献协议与自动化状态机](docs/donation-protocol.md)。

## 多开与多账户

工作台的“多开管理”页可以保存多个账户的账号、DPAPI 加密密码、线路、角色和自动登录开关。可重复启动选中账户，也可一次启动全部尚未运行的账户；密码不会出现在账户清单或子进程命令行中。

每个客户端使用独立的抓包 SQLite、日志、协议/规则/Lua 副本、BGM 缓存和动态音频代理端口；原子操作模板按账户持久保存。窗口标题显示账户、PID 和实例 ID；全局快捷键只属于当前激活窗口，后台客户端不会改动鼠标状态。完整目录结构、迁移规则和启动参数见 [多开与多账户运行模型](docs/multi-account.md)。

## Config

Edit `config.ini`:

```ini
[launcher]
gameUrl=http://v5.t.imop.com/
startFullScreen=false
startFitToWorkArea=true
width=1280
height=720
browserViewportMode=fixed
browserWidth=1005
browserHeight=600
browserAlign=topLeft
fixFlashPosition=true
flashWindowMode=window
flashRepaintWorkaround=true
flashRepaintDelayMs=150
topMost=false
scriptErrorsSuppressed=true
releaseMouseClip=true
mouseTopJumpGuardEnabled=false
mouseTopJumpThresholdPixels=120
mouseTopJumpReturnOffsetPixels=8
mouseDiagnosticsEnabled=true
mouseDiagnosticsIntervalMs=1000
audioFilterEnabled=true
audioFilterProxyPort=0
ffmpegPath=ffmpeg
audioFilterGraph=highpass=f=25,lowpass=f=19000,afftdn=nr=6:nf=-50:tn=1:gs=6,adeclick=t=3,alimiter=limit=0.97
audioBitrateKbps=0
audioMp3Quality=2
audioCacheDirectory=data/audio-cache
audioSoundHost=resource.t.imop.com
audioSoundPathPrefix=/sound
```

## 实时 BGM 降噪

启动器会透明接管游戏进程的 HTTP 音频请求，把原始音频通过 FFmpeg 管道实时执行降噪、去爆音和频段过滤，再以 Flash 可播放的 MP3 流返回给游戏。除了配置的 `resource.t.imop.com/sound` 路径，还会根据响应的 `Content-Type: audio/*`、文件扩展名和 `Content-Disposition` 自动识别音频，因此无扩展名或服务端改变路径时也能命中。页面地址、Cookie、Flash 同源关系和游戏登录 Socket 都不会改变。

- 工具栏的 `BGM降噪 ON / BGM降噪 旁路` 可随时切换；旁路不会中断游戏。
- 支持 WinINet/Flash 使用 `ConnectEx` 以及系统 HTTP 代理（例如 Clash `127.0.0.1:7890`）的情况；现有代理连接会先转入降噪代理，HTTPS `CONNECT` 请求原样隧道转发。
- 按钮上的数字是“音频命中/实际过滤”，悬停还会显示全部代理请求、缓存命中和最近一次音频 URL。
- 首次播放边下载边处理，原始字节原样保存在 `data/instances/<instance-id>/audio-cache/original`，处理结果写入该实例的 `audio-cache`；后续播放直接读取降噪缓存。原始文件可直接交给 `ffplay`/`ffmpeg` 判断噪声来自服务器还是播放链路。
- FFmpeg 不可用或转码启动失败时会自动返回原始音频，不阻断登录和游戏资源。
- 默认使用方案 3 的标准过滤链：`highpass=f=25,lowpass=f=19000,afftdn=nr=6:nf=-50:tn=1:gs=6,adeclick=t=3,alimiter=limit=0.97`。
- 不再假定源文件是 128 kbps，也不再强制 44.1 kHz/双声道。`audioBitrateKbps=0` 时保留源采样率和声道，并使用 `audioMp3Quality=2` 的高质量 VBR；只有显式把 `audioBitrateKbps` 改为 32–320 时才固定输出码率。
- 当前机器需要能从 PATH 执行 `ffmpeg.exe`；也可把 `ffmpegPath` 配置为绝对路径。
