# Changelog（变更日志）

本文件记录本项目的改动。版本号按发布里程碑递增；编译产物固定为 x86。

---

## [未发布] 兼容性优化与健壮性修复

### 新增
- **环境探测模块 `src/Compatibility.cs`**
  - 探测本机安装的 IE 版本（跨 64 位注册表视图，兼容 Win7 x86-on-x64）。
  - 按"最高可用 IE 版本"返回最合适的 `FEATURE_BROWSER_EMULATION` 仿真值（IE11→11001 / IE10→10001 / IE9→9999 / IE8→8888）。
  - 跨 32/64 位注册表视图检测 Flash ActiveX 是否注册。
- **启动前环境自检**（`Program.cs`）：IE 版本过低或 Flash 缺失时弹一次告警，但仍允许在隔离虚拟机中强行启动。
- **新增配置项**
  - `mouseClipPollIntervalMs`：鼠标裁剪释放 / 顶部跳跃防护的轮询间隔（默认 30ms）。
  - `maxLogSizeMB`：单个日志文件轮转上限（默认 10MB，保留一份 `.1` 备份）。

### 优化
- `BrowserFeatureControl.Apply`：浏览器仿真值改为按本机 IE 版本自适应，不再写死 IE11（Win7 老 IE 也能正确渲染）。
- `MainForm.OnBrowserDocumentCompleted`：Flash 兼容修正与布局日志**仅在顶层文档**执行，避免被每个 iframe 重复触发与日志刷屏。
- `MainForm.ApplyPageCompatibilityFixes`：Flash 尺寸改用 `config.BrowserWidth/Height`，不再写死 `1005×600`，避免与视口配置不一致导致错位。
- `Logger.Write`：补充异常保护并缓存日志目录创建（磁盘满/权限不足绝不影响主流程）。
- `BrowserFeatureControl.SetFeatureInCurrentView`：补充异常保护（注册表写入失败不再中断启动）。
- `MainForm.IsCursorClipRestricted`：排除全零/空矩形，避免误判裁剪区域而误解除鼠标锁定。
- `MainForm.RegisterHotKeys`：检查 `RegisterHotKey` 返回值，热键被占用时记日志而非静默失效。
- `Compatibility.GetInstalledIeVersion`：IE 版本探测优先 `Registry64` 视图，减少一次注定失败的注册表打开与误记错误日志。
- **日志轮转**：单个日志文件超过 `maxLogSizeMB` 时自动更名 `.1` 备份，避免诊断日志 7×24 运行撑满磁盘。

### 本轮追加的性能与代码质量优化
- **30ms 高频热路径去系统调用**：`MainForm` 与 `MouseDiagnostics` 将“虚拟屏幕范围”在构造时缓存一次，移除每 30ms tick 重复 4 次 `GetSystemMetrics` 的调用（虚拟屏在会话内基本不变）。
- **DPI 文本缓存**：`MouseDiagnostics` 在 `Start` 时获取一次 DPI 文本并缓存，避免每次采样都创建 `Graphics` 对象。
- **注册表写入跳过**：`BrowserFeatureControl` 写特性值前先比对已有值，相等则跳过写入，减少启动期约 18 次注册表 I/O（值不变时降为 0）。
- **DRY 去重（新增 `Win32Util.cs`）**：将 `FormatRectangle`/`Quote`/`GetClientScreenRectangle`/`GetVirtualScreenRect`/`RectsEqual` 从 `MainForm` 与 `MouseDiagnostics` 重复实现中抽取到共享静态类，统一出口、降低维护分歧风险。

### 修复（内存/资源泄漏）
- 🔴 **真实泄漏修复**：`MainForm.mouseClipReleaseTimer` 在窗体关闭时从未释放，其底层 `GCHandle`/引用环会阻止整窗对象图被 GC 回收。已在 `OnFormClosed` 中补 `Stop()` + `Dispose()`。

### 本轮再追加的健壮性与微优化
- 🔴 **修复潜在崩溃**：`MouseDiagnostics` 增加 `disposed` 守卫，`Start/Stop/Toggle/Dump/LogClipBeforeRelease/Dispose` 在释放后调用均安全。`OnFormClosed` 已 `Dispose` 后，`OnHandleDestroyed` 再调 `Stop` 不会再触碰已释放的 `Timer`（原可能抛 `ObjectDisposedException`/`NullReferenceException`）。
- **诊断采样降 GC 压力**：`BuildSnapshot` 改为复用 `snapshotBuilder`；窗口类名/标题查询复用 `windowTextBuffer`，消除每次采样（含默认 1s 间隔）的大量临时字符串分配。
- **`Logger.Error` 日志整洁**：异常为 `null`（如热键被占用告警）时不再残留多余尾随空格。
- **`GuardAgainstTopJump` 复用时间戳**：每轮只取一次 `DateTime.Now`，避免重复系统调用。
- 🟢 **日志写盘异步化（最强的一处性能/响应性优化）**：`Logger` 改为「UI 线程入队 + 独立后台线程写盘」，把 `File.AppendAllText` 的磁盘 I/O 从 Flash 游戏所在的 UI/消息循环线程上剥离。鼠标裁剪释放、顶部跳跃纠正等高频事件触发 `Logger.Mouse` 时不再阻塞游戏线程；`Program.Main` 退出时 `Logger.Shutdown()` 会等待后台线程把残留日志落盘（超时 2s 兜底）。

### 文档
- 汉化全部源文件注释（标识符保持英文，符合 .NET 命名约定）。
- 重写 `README.md`：补充 x86 固定说明、环境自检、日志轮转、完整配置项说明表。
- 新增本 `CHANGELOG.md`。

---

## [初始版本] 基线

- 基于 .NET Framework 4.0 (x86) 的 WinForms 启动器，用 `WebBrowser`（IE 内核）承载 Flash 网页游戏。
- 注册表 `FEATURE_BROWSER_EMULATION=11001`（原写死 IE11）、鼠标裁剪释放、顶部跳跃防护、鼠标诊断日志。
- `config.ini` 驱动窗口/视口/热键等基础行为。
