using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace TianshuQitanLauncher
{
    internal sealed class MouseDiagnostics : IDisposable
    {
        private readonly Form form;
        private readonly WebBrowser browser;
        private readonly Timer timer;
        private bool running;
        private string lastClipBeforeRelease;

        public MouseDiagnostics(Form form, WebBrowser browser, int intervalMs)
        {
            this.form = form;
            this.browser = browser;

            timer = new Timer();
            timer.Interval = intervalMs;
            timer.Tick += OnTimerTick;
        }

        public void Start()
        {
            if (running)
            {
                return;
            }

            running = true;
            timer.Start();
            Dump("started");
        }

        public void Stop()
        {
            if (!running)
            {
                return;
            }

            timer.Stop();
            running = false;
            Logger.Mouse("stopped");
        }

        public void Toggle()
        {
            if (running)
            {
                Stop();
            }
            else
            {
                Start();
            }
        }

        public void Dump(string reason)
        {
            Logger.Mouse(reason + " " + BuildSnapshot());
        }

        public void LogClipBeforeRelease()
        {
            NativeMethods.RECT clip;
            if (!NativeMethods.GetClipCursor(out clip))
            {
                return;
            }

            NativeMethods.RECT virtualScreen = GetVirtualScreenRect();
            if (RectsEqual(clip, virtualScreen))
            {
                lastClipBeforeRelease = null;
                return;
            }

            string text = "clip-before-release clip=" + clip + " virtual=" + virtualScreen;
            if (text != lastClipBeforeRelease)
            {
                Logger.Mouse(text);
                lastClipBeforeRelease = text;
            }
        }

        public void Dispose()
        {
            timer.Stop();
            timer.Tick -= OnTimerTick;
            timer.Dispose();
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            Dump("sample");
        }

        private string BuildSnapshot()
        {
            NativeMethods.POINT cursor;
            NativeMethods.GetCursorPos(out cursor);

            NativeMethods.RECT clip;
            bool hasClip = NativeMethods.GetClipCursor(out clip);
            NativeMethods.RECT virtualScreen = GetVirtualScreenRect();

            Point cursorPoint = new Point(cursor.X, cursor.Y);
            Rectangle formClientScreen = GetClientScreenRectangle(form);
            Rectangle browserScreen = GetClientScreenRectangle(browser);
            Screen screen = Screen.FromPoint(cursorPoint);
            IntPtr hwndUnderCursor = NativeMethods.WindowFromPoint(cursor);
            IntPtr foreground = NativeMethods.GetForegroundWindow();

            return
                "cursor=" + cursor +
                " inFormClient=" + formClientScreen.Contains(cursorPoint) +
                " inBrowser=" + browserScreen.Contains(cursorPoint) +
                " clip=" + (hasClip ? clip.ToString() : "unavailable") +
                " clipRestricted=" + (hasClip && !RectsEqual(clip, virtualScreen)) +
                " virtual=" + virtualScreen +
                " screenBounds=" + FormatRectangle(screen.Bounds) +
                " screenWorkingArea=" + FormatRectangle(screen.WorkingArea) +
                " formBounds=" + FormatRectangle(form.Bounds) +
                " formClientScreen=" + FormatRectangle(formClientScreen) +
                " browserScreen=" + FormatRectangle(browserScreen) +
                " formState=" + form.WindowState +
                " formBorder=" + form.FormBorderStyle +
                " topMost=" + form.TopMost +
                " focused=" + form.Focused +
                " browserFocused=" + browser.Focused +
                " dpi=" + GetDpiText() +
                " hwndUnder=" + FormatWindow(hwndUnderCursor) +
                " foreground=" + FormatWindow(foreground);
        }

        private static Rectangle GetClientScreenRectangle(Control control)
        {
            Point topLeft = control.PointToScreen(Point.Empty);
            return new Rectangle(topLeft, control.ClientSize);
        }

        private static NativeMethods.RECT GetVirtualScreenRect()
        {
            NativeMethods.RECT rect = new NativeMethods.RECT();
            rect.Left = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
            rect.Top = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
            rect.Right = rect.Left + NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
            rect.Bottom = rect.Top + NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
            return rect;
        }

        private static bool RectsEqual(NativeMethods.RECT left, NativeMethods.RECT right)
        {
            return left.Left == right.Left &&
                left.Top == right.Top &&
                left.Right == right.Right &&
                left.Bottom == right.Bottom;
        }

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

        private static string FormatRectangle(Rectangle rectangle)
        {
            return rectangle.Left + "," + rectangle.Top + "," + rectangle.Width + "x" + rectangle.Height;
        }

        private static string FormatWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return "0x0";
            }

            NativeMethods.RECT rect;
            string rectText = NativeMethods.GetWindowRect(hwnd, out rect) ? rect.ToString() : "unavailable";

            return "0x" + hwnd.ToString("X") +
                " class=" + Quote(GetClassName(hwnd)) +
                " title=" + Quote(GetWindowText(hwnd)) +
                " rect=" + rectText;
        }

        private static string GetClassName(IntPtr hwnd)
        {
            StringBuilder builder = new StringBuilder(256);
            int length = NativeMethods.GetClassName(hwnd, builder, builder.Capacity);
            return length > 0 ? builder.ToString() : "";
        }

        private static string GetWindowText(IntPtr hwnd)
        {
            StringBuilder builder = new StringBuilder(256);
            int length = NativeMethods.GetWindowText(hwnd, builder, builder.Capacity);
            return length > 0 ? builder.ToString() : "";
        }

        private static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            return "\"" + value.Replace("\"", "'") + "\"";
        }
    }
}
