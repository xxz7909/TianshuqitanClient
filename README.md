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

## Auto login

Open the `自动登录` workbench tab, enter an account and password, select `一线` through `四线` and a role slot from 1 through 5, then choose `保存配置` or `保存并登录`. Enabling automatic login repeats the full flow after the next page load.

`刷新并登录` saves the current selections, completely refreshes the game page, and starts login again after the top-level document is ready. F5, Ctrl+F5, and Ctrl+R preserve an enabled or currently active automatic-login session across refreshes; an explicit `停止` still cancels a pending restart.

The password field is masked and the saved password is encrypted with Windows DPAPI for the current Windows user. The v5 wire protocol itself sends `CS_USER_PASS` credentials in plaintext and reuses a Base64 login ticket, so new captures redact the account, password, and ticket before SQLite storage, UI display, analysis bundles, or PCAPNG export. Existing session files are not rewritten automatically.

The selected line is resolved from each live `SC_GAMESERVER_LIST (0x00C9)` response instead of hard-coding the recorded ports. The selected role is resolved from each live `SC_ROLE_INFO_LIST (0x000C)` response; completion is confirmed by `SC_ROLE_START_POINT (0x0016)`. See [the login protocol notes](docs/login-protocol.md), [the role-selection protocol notes](docs/role-selection-protocol.md), and [the bounty automation protocol notes](docs/bounty-protocol.md) for the confirmed layouts and state sequences.

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
