using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace TianshuQitanLauncher
{
    // 鼠标诊断器：按固定间隔采集光标、裁剪区域、窗口与 DPI 等信息并写入日志，
    // 用于排查老 Flash 游戏导致的鼠标错位、被锁死等问题。
    internal sealed class MouseDiagnostics : IDisposable
    {
        private readonly Form form;
        private readonly WebBrowser browser;
        private readonly Timer timer;
        private bool running;
        // 是否已释放：用于保证 Stop/Start/Dump/Dispose 在释放后调用时安全（避免对已 Dispose 的 Timer 再次操作）
        private bool disposed;
        private string lastClipBeforeRelease;
        // 虚拟屏幕范围缓存（构造时获取一次），避免每次采样 tick 重复 4 次 GetSystemMetrics
        private readonly NativeMethods.RECT cachedVirtualScreen = Win32Util.GetVirtualScreenRect();
        // DPI 文本缓存（进程生命周期内不变），仅在 Start 时获取一次
        private string cachedDpiText;
        // 复用的字符串缓冲区，避免诊断采样每次分配大量临时字符串（所有调用均在 UI 线程，安全）
        private readonly StringBuilder windowTextBuffer = new StringBuilder(256);
        private readonly StringBuilder snapshotBuilder = new StringBuilder(512);

        public MouseDiagnostics(Form form, WebBrowser browser, int intervalMs)
        {
            this.form = form;
            this.browser = browser;

            timer = new Timer();
            timer.Interval = intervalMs;
            timer.Tick += OnTimerTick;
        }

        // 启动采样（仅启动一次），并立即打一份快照
        public void Start()
        {
            if (disposed || running)
            {
                return;
            }

            running = true;
            timer.Start();
            cachedDpiText = GetDpiText();
            Dump("started");
        }

        // 停止采样
        public void Stop()
        {
            if (disposed || !running)
            {
                return;
            }

            timer.Stop();
            running = false;
            Logger.Mouse("stopped");
        }

        // 切换采样开关
        public void Toggle()
        {
            if (disposed)
            {
                return;
            }

            if (running)
            {
                Stop();
            }
            else
            {
                Start();
            }
        }

        // 按指定原因打一份当前快照
        public void Dump(string reason)
        {
            if (disposed)
            {
                return;
            }

            Logger.Mouse(reason + " " + BuildSnapshot());
        }

        // 记录“释放前的裁剪区域”；若与虚拟屏幕一致（即无裁剪）则忽略，且只在变化时记录，避免刷屏
        public void LogClipBeforeRelease()
        {
            if (disposed)
            {
                return;
            }

            NativeMethods.RECT clip;
            if (!NativeMethods.GetClipCursor(out clip))
            {
                return;
            }

            if (!Win32Util.IsClipRestricted(clip, cachedVirtualScreen))
            {
                lastClipBeforeRelease = null;
                return;
            }

            string text = "clip-before-release clip=" + Win32Util.FormatRectangle(clip) + " virtual=" + Win32Util.FormatRectangle(cachedVirtualScreen);
            if (text != lastClipBeforeRelease)
            {
                Logger.Mouse(text);
                lastClipBeforeRelease = text;
            }
        }

        // 释放定时器与事件订阅（幂等：多次调用安全，不会在已释放后再次触碰 Timer）
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            timer.Stop();
            timer.Tick -= OnTimerTick;
            timer.Dispose();
        }

        // 定时器触发时打一份采样快照
        private void OnTimerTick(object sender, EventArgs e)
        {
            Dump("sample");
        }

        // 组装一份完整快照：光标位置、是否在窗口/浏览器内、裁剪状态、各窗口矩形、DPI 等。
        // 复用 snapshotBuilder，避免每次采样都新建大字符串（默认 1s 一次，用户把间隔调小后收益更明显）。
        private string BuildSnapshot()
        {
            NativeMethods.POINT cursor;
            NativeMethods.GetCursorPos(out cursor);

            NativeMethods.RECT clip;
            bool hasClip = NativeMethods.GetClipCursor(out clip);

            Point cursorPoint = new Point(cursor.X, cursor.Y);
            Rectangle formClientScreen = Win32Util.GetClientScreenRectangle(form);
            Rectangle browserScreen = Win32Util.GetClientScreenRectangle(browser);
            Screen screen = Screen.FromPoint(cursorPoint);
            IntPtr hwndUnderCursor = NativeMethods.WindowFromPoint(cursor);
            IntPtr foreground = NativeMethods.GetForegroundWindow();

            StringBuilder sb = snapshotBuilder;
            sb.Length = 0;
            sb.Append("cursor=").Append(cursor)
              .Append(" inFormClient=").Append(formClientScreen.Contains(cursorPoint))
              .Append(" inBrowser=").Append(browserScreen.Contains(cursorPoint))
              .Append(" clip=").Append(hasClip ? Win32Util.FormatRectangle(clip) : "unavailable")
              .Append(" clipRestricted=").Append(hasClip && Win32Util.IsClipRestricted(clip, cachedVirtualScreen))
              .Append(" virtual=").Append(Win32Util.FormatRectangle(cachedVirtualScreen))
              .Append(" screenBounds=").Append(Win32Util.FormatRectangle(screen.Bounds))
              .Append(" screenWorkingArea=").Append(Win32Util.FormatRectangle(screen.WorkingArea))
              .Append(" formBounds=").Append(Win32Util.FormatRectangle(form.Bounds))
              .Append(" formClientScreen=").Append(Win32Util.FormatRectangle(formClientScreen))
              .Append(" browserScreen=").Append(Win32Util.FormatRectangle(browserScreen))
              .Append(" formState=").Append(form.WindowState)
              .Append(" formBorder=").Append(form.FormBorderStyle)
              .Append(" topMost=").Append(form.TopMost)
              .Append(" focused=").Append(form.Focused)
              .Append(" browserFocused=").Append(browser.Focused)
              .Append(" dpi=").Append(cachedDpiText)
              .Append(" hwndUnder=").Append(FormatWindow(hwndUnderCursor))
              .Append(" foreground=").Append(FormatWindow(foreground));

            return sb.ToString();
        }

        // 获取窗口的 DPI（X、Y）
        private string GetDpiText()
        {
            try
            {
                using (Graphics graphics = Graphics.FromHwnd(form.Handle))
                {
                    return ((int)graphics.DpiX) + "x" + ((int)graphics.DpiY);
                }
            }
            catch
            {
                return "unavailable";
            }
        }

        // 格式化窗口信息：句柄、类名、标题、矩形
        private string FormatWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return "0x0";
            }

            NativeMethods.RECT rect;
            string rectText = NativeMethods.GetWindowRect(hwnd, out rect) ? Win32Util.FormatRectangle(rect) : "unavailable";

            return "0x" + hwnd.ToString("X") +
                " class=" + Win32Util.Quote(GetClassName(hwnd)) +
                " title=" + Win32Util.Quote(GetWindowText(hwnd)) +
                " rect=" + rectText;
        }

        // 获取窗口类名（复用 windowTextBuffer，避免每次分配）
        private string GetClassName(IntPtr hwnd)
        {
            windowTextBuffer.Length = 0;
            // 传 Capacity-1 而非 Capacity：留出 1 字符给 API 写入的结尾 NUL，
            // 否则文本恰好等于缓冲长度时缺 NUL 会导致后续出现不可读乱码（静默截断问题）。
            int length = NativeMethods.GetClassName(hwnd, windowTextBuffer, windowTextBuffer.Capacity - 1);
            return length > 0 ? windowTextBuffer.ToString() : "";
        }

        // 获取窗口标题文本（复用 windowTextBuffer，避免每次分配）
        private string GetWindowText(IntPtr hwnd)
        {
            windowTextBuffer.Length = 0;
            // 留 1 字符给结尾 NUL（见 GetClassName 说明），避免超长标题被静默截断为乱码
            int length = NativeMethods.GetWindowText(hwnd, windowTextBuffer, windowTextBuffer.Capacity - 1);
            return length > 0 ? windowTextBuffer.ToString() : "";
        }
    }
}
