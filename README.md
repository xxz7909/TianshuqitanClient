# Tianshu Qitan Launcher（天书奇谈启动器）

一个 Windows 桌面外壳程序，用 `System.Windows.Forms.WebBrowser`（IE 内核）把老 Flash 网页游戏《天书奇谈》封装成独立客户端窗口运行：

```text
http://v5.t.imop.com/
```

目标是在**装了旧版 IE（IE8~IE11）与 Flash ActiveX** 的老 Windows（含 Win7）上，让这个早已过时的 Flash 游戏仍能勉强可玩。

## 环境要求

- Windows（建议装了旧版 IE + Flash ActiveX 的隔离虚拟机）
- 进程固定为 **x86**：`WebBrowser` 控件在进程内承载与宿主同位的 IE，Flash 作为 ActiveX 也必须是同位的 32 位版本。32 位 Flash ActiveX 最易获取且最可靠，因此本项目固定编译为 x86，请勿改成 x64/AnyCPU。
- 系统需注册 Flash ActiveX 插件（游戏依赖它渲染）
- 建议：为安全考虑，在隔离虚拟机中运行这类老 IE/Flash 内容

## 构建

```powershell
.\build.ps1
```

产物：

```text
bin\Release\TianshuQitanLauncher.exe
```

## 运行

```powershell
.\run.ps1
```

## 启动前环境自检

程序启动时会探测本机环境：

- 探测真实安装的 **IE 版本**，按"最高可用"选择 `FEATURE_BROWSER_EMULATION` 仿真值（IE11→11001 / IE10→10001 / IE9→9999 / IE8→8888），而不是写死 IE11，从而让 Win7 老 IE 也能正确渲染页面。
- 检测 **Flash ActiveX** 是否注册（跨 32/64 位注册表视图）。
- 若 IE 版本过低或 Flash 缺失，会弹一次告警，但仍允许在隔离虚拟机等环境中强行启动。

## 窗口模式

启动为带标题栏的普通 Windows 窗口，默认填满当前屏幕工作区（不遮挡任务栏）。

## 诊断与日志

导航事件写入：

```text
bin\Release\logs\launcher.log
```

鼠标诊断写入：

```text
bin\Release\logs\mouse-diagnostics.log
```

日志轮转：单个日志文件超过 `maxLogSizeMB`（默认 10MB）时自动更名 `.1` 作为备份（仅保留一份），避免长期运行撑满磁盘。

热键：

- `F8`：立即写一份鼠标诊断快照
- `F9`：切换自动鼠标诊断采样
- `F11`：适配工作区 / 还原窗口
- `Esc`：还原窗口
- `F5` / `Ctrl+R`：刷新
- `Alt+Left` / `Alt+Right`：后退 / 前进

`F11`、`Esc`、`F5`、`F8`、`F9` 以 Win32 全局热键注册，即使 Flash 抢占了键盘焦点仍可用；注册失败（被其他程序占用）时只记录日志，不中断启动。

## 配置

编辑 `config.ini`（可执行文件同目录）：

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
topMost=false
scriptErrorsSuppressed=true
releaseMouseClip=true
# 鼠标裁剪释放 / 顶部跳跃防护的轮询间隔（毫秒，10~1000）
mouseClipPollIntervalMs=30
mouseDiagnosticsEnabled=true
mouseDiagnosticsIntervalMs=1000
mouseTopJumpGuardEnabled=false
mouseTopJumpThresholdPixels=120
mouseTopJumpReturnOffsetPixels=8
# 单个日志文件达到此大小（MB）后自动轮转（1~200），保留一份 .1 备份
maxLogSizeMB=10
```

### 配置项说明

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| `gameUrl` | `http://v5.t.imop.com/` | 游戏页面地址 |
| `startFullScreen` | `false` | 启动即全屏 |
| `startFitToWorkArea` | `true` | 启动即填满工作区（不遮挡任务栏） |
| `width` / `height` | `1280` / `720` | 窗口初始尺寸 |
| `browserViewportMode` | `fixed` | 浏览器视口模式：`fill`=填满窗口，`fixed`=固定尺寸 |
| `browserWidth` / `browserHeight` | `1005` / `600` | `fixed` 模式下的视口尺寸；同时用于强制定位 Flash 元素 |
| `browserAlign` | `topLeft` | `fixed` 模式下对齐方式：`topLeft` / `center` |
| `fixFlashPosition` | `true` | 强制把 Flash 元素（`#flashcontent`）钉在左上角、去边距、黑底 |
| `topMost` | `false` | 窗口是否置顶 |
| `scriptErrorsSuppressed` | `true` | 是否抑制脚本错误弹窗 |
| `releaseMouseClip` | `true` | 定时解除鼠标裁剪，防止游戏把鼠标锁死在窗口内 |
| `mouseClipPollIntervalMs` | `30` | 鼠标裁剪释放 / 顶部跳跃防护的轮询间隔（毫秒） |
| `mouseDiagnosticsEnabled` | `true` | 是否启用鼠标诊断采样 |
| `mouseDiagnosticsIntervalMs` | `1000` | 鼠标诊断采样间隔（毫秒） |
| `mouseTopJumpGuardEnabled` | `false` | 是否启用"鼠标顶部跳跃"防护 |
| `mouseTopJumpThresholdPixels` | `120` | 触发顶部跳跃防护的向上位移阈值（像素） |
| `mouseTopJumpReturnOffsetPixels` | `8` | 纠正后鼠标回落到 Flash 区域顶部的偏移（像素） |
| `maxLogSizeMB` | `10` | 单个日志文件轮转上限（MB），避免长期运行撑满磁盘 |
