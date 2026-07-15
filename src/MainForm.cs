using System;
using System.Drawing;
using System.Windows.Forms;

namespace TianshuQitanLauncher
{
    // 主窗口：承载 IE WebBrowser 控件加载游戏页面，并处理窗口自适应、热键、鼠标兼容等逻辑
    internal sealed class MainForm : Form
    {
        // 各类全局热键的注册 ID
        private const int HotKeyFullScreen = 1001;
        private const int HotKeyExitFullScreen = 1002;
        private const int HotKeyRefresh = 1003;
        private const int HotKeyMouseDiagnosticsDump = 1004;
        private const int HotKeyMouseDiagnosticsToggle = 1005;

        private readonly LauncherConfig config;
        private readonly WebBrowser browser;
        // 30ms 定时器：用于解除鼠标裁剪、执行顶部跳跃防护
        private readonly Timer mouseClipReleaseTimer;
        private readonly MouseDiagnostics mouseDiagnostics;
        // 窗口布局模式：Normal=普通带边框窗口；WorkArea=铺满工作区；FullScreen=无边框全屏
        private enum LayoutMode
        {
            Normal,
            WorkArea,
            FullScreen,
        }

        // 记录进入 WorkArea/FullScreen 前的普通窗口位置，便于还原
        private Rectangle previousBounds;
        // 顶部跳跃防护用的上一次“在浏览器内”的光标位置与时间（null 表示尚未记录过，取代原先 MinValue 哨兵）
        private Point lastBrowserCursorPoint;
        private DateTime? lastBrowserCursorAt;
        // 当前布局模式（取代原先语义被污染的 isFitToWorkArea 布尔：它无法区分“工作区”与“全屏”）
        private LayoutMode layoutMode = LayoutMode.Normal;
        // 虚拟屏幕范围（多显示器拼接范围），构造时缓存，避免每 30ms 高频检测重复 4 次 GetSystemMetrics
        private readonly NativeMethods.RECT virtualScreen;

        public MainForm(LauncherConfig config)
        {
            this.config = config;
            this.virtualScreen = Win32Util.GetVirtualScreenRect();

            Text = "Tianshu Qitan";
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = FormWindowState.Normal;
            ClientSize = new Size(config.Width, config.Height);
            TopMost = config.TopMost;
            KeyPreview = true;

            browser = new WebBrowser();
            browser.Dock = DockStyle.None;
            browser.Location = Point.Empty;
            browser.ScriptErrorsSuppressed = config.ScriptErrorsSuppressed;
            browser.AllowWebBrowserDrop = false;
            browser.IsWebBrowserContextMenuEnabled = false;
            browser.WebBrowserShortcutsEnabled = true;
            browser.Navigating += OnBrowserNavigating;
            browser.Navigated += OnBrowserNavigated;
            browser.DocumentCompleted += OnBrowserDocumentCompleted;

            Controls.Add(browser);
            ApplyBrowserBounds();

            mouseClipReleaseTimer = new Timer();
            mouseClipReleaseTimer.Interval = config.MouseClipPollIntervalMs;
            mouseClipReleaseTimer.Tick += OnMouseClipReleaseTimerTick;
            mouseDiagnostics = new MouseDiagnostics(this, browser, config.MouseDiagnosticsIntervalMs);

            Load += OnLoad;
            KeyDown += OnKeyDown;
        }

        // 窗口句柄创建后注册全局热键（此时 Handle 才有效）
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterHotKeys();
        }

        // 窗口句柄销毁时停止诊断并注销热键，避免资源泄漏
        protected override void OnHandleDestroyed(EventArgs e)
        {
            mouseDiagnostics.Stop();
            UnregisterHotKeys();
            base.OnHandleDestroyed(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // 释放鼠标裁剪定时器：其底层 GCHandle/NativeWindow 及 Tick 委托对窗体的引用，
            // 若不释放会阻止整棵对象图（MainForm/browser/config）被 GC 回收，造成内存泄漏。
            if (mouseClipReleaseTimer != null)
            {
                mouseClipReleaseTimer.Stop();
                mouseClipReleaseTimer.Dispose();
                mouseClipReleaseTimer = null;
            }

            mouseDiagnostics.Dispose();
            base.OnFormClosed(e);
        }

        // 客户端尺寸变化（如拖动边框）时重新布局浏览器视口
        protected override void OnClientSizeChanged(EventArgs e)
        {
            base.OnClientSizeChanged(e);

            if (browser != null)
            {
                ApplyBrowserBounds();
            }
        }

        // 转发 Win32 热键消息到对应处理函数
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();

                if (id == HotKeyFullScreen)
                {
                    ToggleFitToWorkArea();
                    return;
                }

                if (id == HotKeyExitFullScreen)
                {
                    // 已是普通窗口时 RestoreWindowBounds 内部会直接返回，无需额外判断
                    RestoreWindowBounds();
                    return;
                }

                if (id == HotKeyRefresh)
                {
                    RefreshBrowser();
                    return;
                }

                if (id == HotKeyMouseDiagnosticsDump)
                {
                    mouseDiagnostics.Dump("manual-f8");
                    return;
                }

                if (id == HotKeyMouseDiagnosticsToggle)
                {
                    mouseDiagnostics.Toggle();
                    return;
                }
            }

            base.WndProc(ref m);
        }

        // 窗口加载完成：按需适配工作区、启动定时器与诊断，并开始导航到游戏
        private void OnLoad(object sender, EventArgs e)
        {
            // 启动布局：startFullScreen 优先（真正无边框全屏，覆盖任务栏），否则按需适配工作区
            if (config.StartFullScreen)
            {
                FitToFullScreen();
            }
            else if (config.StartFitToWorkArea)
            {
                FitToWorkArea();
            }

            if (config.ReleaseMouseClip || config.MouseTopJumpGuardEnabled)
            {
                mouseClipReleaseTimer.Start();
            }

            if (config.MouseDiagnosticsEnabled)
            {
                mouseDiagnostics.Start();
            }

            browser.Navigate(config.GameUrl);
        }

        private void OnBrowserNavigating(object sender, WebBrowserNavigatingEventArgs e)
        {
            Logger.Info("Navigating: " + e.Url);
        }

        private void OnBrowserNavigated(object sender, WebBrowserNavigatedEventArgs e)
        {
            Logger.Info("Navigated: " + e.Url);
            UpdateTitle();
        }

        // 文档加载完成：仅在顶层文档时做兼容修正与布局记录（子框架也会触发此事件，避免重复改写与日志刷屏）
        private void OnBrowserDocumentCompleted(object sender, WebBrowserDocumentCompletedEventArgs e)
        {
            Logger.Info("DocumentCompleted: " + e.Url);

            if (browser.Document != null && browser.Document.Window != null && browser.Document.Window.Parent == null)
            {
                try
                {
                    ApplyPageCompatibilityFixes();
                }
                catch (Exception ex)
                {
                    Logger.Error("ApplyPageCompatibilityFixes failed", ex);
                }

                LogDocumentLayout();
            }

            UpdateTitle();
        }

        // 根据页面标题更新窗口标题
        private void UpdateTitle()
        {
            string title = browser.DocumentTitle;
            Text = string.IsNullOrWhiteSpace(title) ? "Tianshu Qitan" : "Tianshu Qitan - " + title;
        }

        // 键盘快捷键：F11/Esc/F5 对应的全屏切换、退出全屏、刷新都由全局热键在 WndProc 中处理
        // （这样即使 Flash/WebBrowser 抢占了键盘焦点也能生效），这里不再重复处理以免同一按键被双重触发
        // （Toggle 类操作两次会相互抵消）。仅保留全局热键未覆盖的快捷键：Ctrl+R 刷新、Alt+Left/Right 前进/后退。
        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.R)
            {
                RefreshBrowser();
                e.Handled = true;
                return;
            }

            if (e.Alt && e.KeyCode == Keys.Left && browser.CanGoBack)
            {
                browser.GoBack();
                e.Handled = true;
                return;
            }

            if (e.Alt && e.KeyCode == Keys.Right && browser.CanGoForward)
            {
                browser.GoForward();
            }
        }

        // F11 切换：普通窗口 -> 适配工作区；已处于工作区/全屏 -> 还原为普通窗口
        private void ToggleFitToWorkArea()
        {
            if (layoutMode == LayoutMode.Normal)
            {
                FitToWorkArea();
            }
            else
            {
                RestoreWindowBounds();
            }
        }

        // 将窗口铺满屏幕工作区（不遮挡任务栏）。仅在从普通窗口切入时记录还原位置。
        private void FitToWorkArea()
        {
            if (layoutMode == LayoutMode.WorkArea)
            {
                return;
            }

            if (layoutMode == LayoutMode.Normal)
            {
                previousBounds = Bounds;
            }

            SuspendLayout();
            FormBorderStyle = FormBorderStyle.Sizable;
            TopMost = config.TopMost;
            WindowState = FormWindowState.Normal;
            Bounds = Screen.FromControl(this).WorkingArea;
            ApplyBrowserBounds();
            ResumeLayout();

            layoutMode = LayoutMode.WorkArea;
        }

        // 将窗口真正全屏（无边框覆盖整个屏幕，含任务栏）。仅在从普通窗口切入时记录还原位置。
        private void FitToFullScreen()
        {
            if (layoutMode == LayoutMode.FullScreen)
            {
                return;
            }

            if (layoutMode == LayoutMode.Normal)
            {
                previousBounds = Bounds;
            }

            SuspendLayout();
            FormBorderStyle = FormBorderStyle.None;
            TopMost = config.TopMost;
            WindowState = FormWindowState.Normal;
            Bounds = Screen.FromControl(this).Bounds;
            ApplyBrowserBounds();
            ResumeLayout();

            layoutMode = LayoutMode.FullScreen;
        }

        // 还原到进入工作区/全屏之前的普通窗口位置；已是普通窗口则直接返回
        private void RestoreWindowBounds()
        {
            if (layoutMode == LayoutMode.Normal)
            {
                return;
            }

            SuspendLayout();
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.Sizable;
            TopMost = config.TopMost;
            Bounds = previousBounds;
            ApplyBrowserBounds();
            ResumeLayout();

            layoutMode = LayoutMode.Normal;
        }

        // 每 30ms 触发：先执行顶部跳跃防护，再在确有裁剪时解除鼠标裁剪
        private void OnMouseClipReleaseTimerTick(object sender, EventArgs e)
        {
            if (config.MouseTopJumpGuardEnabled)
            {
                GuardAgainstTopJump();
            }

            if (!config.ReleaseMouseClip)
            {
                return;
            }

            // 优化：仅在检测到“鼠标被限制在某矩形内（而非整个虚拟屏幕）”时才解除裁剪，
            // 避免每 30ms 无条件调用 ClipCursor(IntPtr.Zero) 造成的无谓系统调用。
            NativeMethods.RECT clip;
            if (NativeMethods.GetClipCursor(out clip) && Win32Util.IsClipRestricted(clip, virtualScreen))
            {
                if (config.MouseDiagnosticsEnabled)
                {
                    mouseDiagnostics.LogClipBeforeRelease();
                }

                NativeMethods.ClipCursor(IntPtr.Zero);
            }
        }

        // 鼠标“顶部跳跃”防护：当光标刚在浏览器内、随后异常向上跳到窗口顶部上方时，将其拉回 Flash 区域
        private void GuardAgainstTopJump()
        {
            Rectangle browserScreen = GetBrowserScreenRectangle();

            // 浏览器视口过小（宽/高 < 3px）时，下方 Clamp 的 min 会大于 max，纠正坐标会越界且无意义，
            // 这种极端情况直接跳过；正常情况下配置下限（高>=240/宽>=320）不会触发。
            if (browserScreen.Width < 3 || browserScreen.Height < 3)
            {
                return;
            }

            Point cursor = Cursor.Position;
            DateTime now = DateTime.Now;

            // 光标当前在浏览器内，仅记录位置后返回
            if (browserScreen.Contains(cursor))
            {
                lastBrowserCursorPoint = cursor;
                lastBrowserCursorAt = now;
                return;
            }

            if (lastBrowserCursorAt == null)
            {
                return;
            }

            TimeSpan elapsed = now - lastBrowserCursorAt;
            bool recentlyInBrowser = elapsed.TotalMilliseconds <= 500;
            bool inThisWindowTop = Bounds.Contains(cursor) && cursor.Y < browserScreen.Top;
            int upwardJump = lastBrowserCursorPoint.Y - cursor.Y;

            if (!recentlyInBrowser || !inThisWindowTop || upwardJump < config.MouseTopJumpThresholdPixels)
            {
                return;
            }

            Point previousBrowserPoint = lastBrowserCursorPoint;
            Point target = new Point(
                Win32Util.Clamp(cursor.X, browserScreen.Left + 1, browserScreen.Right - 2),
                Win32Util.Clamp(browserScreen.Top + config.MouseTopJumpReturnOffsetPixels, browserScreen.Top + 1, browserScreen.Bottom - 2));

            Cursor.Position = target;
            lastBrowserCursorPoint = target;
            lastBrowserCursorAt = now;

            Logger.Mouse(
                "top-jump-corrected cursor=" + cursor.X + "," + cursor.Y +
                " lastBrowser=" + previousBrowserPoint.X + "," + previousBrowserPoint.Y +
                " target=" + target.X + "," + target.Y +
                " browserScreen=" + Win32Util.FormatRectangle(browserScreen) +
                " formBounds=" + Win32Util.FormatRectangle(Bounds) +
                " upwardJump=" + upwardJump);
        }

        // 获取浏览器在屏幕坐标系下的矩形
        private Rectangle GetBrowserScreenRectangle()
        {
            return Win32Util.GetClientScreenRectangle(browser);
        }

        // 根据配置计算并设置浏览器视口的大小与位置（fill 填满 / fixed 固定尺寸并按对齐方式定位）
        private void ApplyBrowserBounds()
        {
            if (browser == null)
            {
                return;
            }

            Rectangle bounds;

            if (string.Equals(config.BrowserViewportMode, "fill", StringComparison.OrdinalIgnoreCase))
            {
                bounds = ClientRectangle;
            }
            else
            {
                int width = Math.Min(config.BrowserWidth, ClientSize.Width);
                int height = Math.Min(config.BrowserHeight, ClientSize.Height);
                int left = 0;
                int top = 0;

                if (string.Equals(config.BrowserAlign, "center", StringComparison.OrdinalIgnoreCase))
                {
                    left = Math.Max(0, (ClientSize.Width - width) / 2);
                    top = Math.Max(0, (ClientSize.Height - height) / 2);
                }

                bounds = new Rectangle(left, top, width, height);
            }

            browser.Bounds = bounds;
            BackColor = Color.Black;
        }

        // 文档加载完成后对老页面做兼容修正：去除边距、隐藏滚动条，并把 Flash 元素钉在左上角
        private void ApplyPageCompatibilityFixes()
        {
            if (!config.FixFlashPosition || browser.Document == null)
            {
                return;
            }

            if (browser.Document.Body != null)
            {
                browser.Document.Body.Style = "padding:0;margin:0;overflow:hidden;background:#000;";
            }

            HtmlElement flashContent = browser.Document.GetElementById("flashcontent");
            if (flashContent == null)
            {
                return;
            }

            // 使用配置中的浏览器视口尺寸，而非写死 1005x600，避免与 config.ini 不一致导致错位
            flashContent.Style = "position:absolute;top:0px;left:0px;margin:0px;width:" + config.BrowserWidth + "px;height:" + config.BrowserHeight + "px;";

            Logger.Info("Applied flashcontent top-left compatibility style");
        }

        // 记录文档与关键元素（object/embed/flashcontent/clientcontent）的布局信息，便于定位错位问题
        private void LogDocumentLayout()
        {
            if (browser.Document == null)
            {
                return;
            }

            Logger.Mouse(
                "document-layout browserClient=" + Win32Util.FormatRectangle(new Rectangle(Point.Empty, browser.ClientSize)) +
                " body=" + FormatElement(browser.Document.Body));

            LogElementsByTagName("object");
            LogElementsByTagName("embed");
            LogElementById("flashcontent");
            LogElementById("clientcontent");
        }

        private void LogElementsByTagName(string tagName)
        {
            HtmlElementCollection elements = browser.Document.GetElementsByTagName(tagName);

            for (int i = 0; i < elements.Count; i++)
            {
                Logger.Mouse("document-" + tagName + "[" + i + "] " + FormatElement(elements[i]));
            }
        }

        private void LogElementById(string id)
        {
            HtmlElement element = browser.Document.GetElementById(id);
            Logger.Mouse("document-id#" + id + " " + FormatElement(element));
        }

        // 将元素的关键布局属性格式化为可读字符串
        private static string FormatElement(HtmlElement element)
        {
            if (element == null)
            {
                return "missing";
            }

            return
                "tag=" + element.TagName +
                " id=" + Win32Util.Quote(element.GetAttribute("id")) +
                " name=" + Win32Util.Quote(element.GetAttribute("name")) +
                " class=" + Win32Util.Quote(element.GetAttribute("className")) +
                " widthAttr=" + Win32Util.Quote(element.GetAttribute("width")) +
                " heightAttr=" + Win32Util.Quote(element.GetAttribute("height")) +
                " offset=" + Win32Util.FormatRectangle(element.OffsetRectangle) +
                " client=" + Win32Util.FormatRectangle(element.ClientRectangle) +
                " style=" + Win32Util.Quote(element.GetAttribute("style"));
        }

        // 完整刷新浏览器（忽略缓存）
        private void RefreshBrowser()
        {
            browser.Refresh(WebBrowserRefreshOption.Completely);
        }

        // 注册全局热键：F11 适配/还原、Esc 还原、F5 刷新、F8 诊断快照、F9 诊断开关
        private void RegisterHotKeys()
        {
            RegisterHotKeyOrLog(HotKeyFullScreen, Keys.F11, "FitToWorkArea");
            RegisterHotKeyOrLog(HotKeyExitFullScreen, Keys.Escape, "ExitFullScreen");
            RegisterHotKeyOrLog(HotKeyRefresh, Keys.F5, "Refresh");
            RegisterHotKeyOrLog(HotKeyMouseDiagnosticsDump, Keys.F8, "MouseDiagnosticsDump");
            RegisterHotKeyOrLog(HotKeyMouseDiagnosticsToggle, Keys.F9, "MouseDiagnosticsToggle");
        }

        // 注册单个全局热键；失败（如被其他程序占用）时记录日志，不中断启动
        private void RegisterHotKeyOrLog(int id, Keys key, string name)
        {
            if (!NativeMethods.RegisterHotKey(Handle, id, 0, (uint)key))
            {
                Logger.Error("热键注册失败（可能被其他程序占用）：" + name, null);
            }
        }

        // 注销全部全局热键
        private void UnregisterHotKeys()
        {
            NativeMethods.UnregisterHotKey(Handle, HotKeyFullScreen);
            NativeMethods.UnregisterHotKey(Handle, HotKeyExitFullScreen);
            NativeMethods.UnregisterHotKey(Handle, HotKeyRefresh);
            NativeMethods.UnregisterHotKey(Handle, HotKeyMouseDiagnosticsDump);
            NativeMethods.UnregisterHotKey(Handle, HotKeyMouseDiagnosticsToggle);
        }

    }
}
