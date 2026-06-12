using System;
using System.Drawing;
using System.Windows.Forms;

namespace TianshuQitanLauncher
{
    internal sealed class MainForm : Form
    {
        private const int HotKeyFullScreen = 1001;
        private const int HotKeyExitFullScreen = 1002;
        private const int HotKeyRefresh = 1003;
        private const int HotKeyMouseDiagnosticsDump = 1004;
        private const int HotKeyMouseDiagnosticsToggle = 1005;

        private readonly LauncherConfig config;
        private readonly WebBrowser browser;
        private readonly Timer mouseClipReleaseTimer;
        private readonly MouseDiagnostics mouseDiagnostics;
        private Rectangle previousBounds;
        private Point lastBrowserCursorPoint;
        private DateTime lastBrowserCursorAt;
        private bool isFitToWorkArea;

        public MainForm(LauncherConfig config)
        {
            this.config = config;

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
            mouseClipReleaseTimer.Interval = 30;
            mouseClipReleaseTimer.Tick += OnMouseClipReleaseTimerTick;
            mouseDiagnostics = new MouseDiagnostics(this, browser, config.MouseDiagnosticsIntervalMs);

            Load += OnLoad;
            KeyDown += OnKeyDown;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterHotKeys();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            mouseDiagnostics.Stop();
            UnregisterHotKeys();
            base.OnHandleDestroyed(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            mouseDiagnostics.Dispose();
            base.OnFormClosed(e);
        }

        protected override void OnClientSizeChanged(EventArgs e)
        {
            base.OnClientSizeChanged(e);

            if (browser != null)
            {
                ApplyBrowserBounds();
            }
        }

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
                    if (isFitToWorkArea)
                    {
                        RestoreWindowBounds();
                    }
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

        private void OnLoad(object sender, EventArgs e)
        {
            if (config.StartFitToWorkArea || config.StartFullScreen)
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

        private void OnBrowserDocumentCompleted(object sender, WebBrowserDocumentCompletedEventArgs e)
        {
            Logger.Info("DocumentCompleted: " + e.Url);
            ApplyPageCompatibilityFixes();
            LogDocumentLayout();
            UpdateTitle();
        }

        private void UpdateTitle()
        {
            string title = browser.DocumentTitle;
            Text = string.IsNullOrWhiteSpace(title) ? "Tianshu Qitan" : "Tianshu Qitan - " + title;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F11)
            {
                ToggleFitToWorkArea();
                e.Handled = true;
                return;
            }

            if (e.KeyCode == Keys.Escape && isFitToWorkArea)
            {
                RestoreWindowBounds();
                e.Handled = true;
                return;
            }

            if (e.KeyCode == Keys.F5 || (e.Control && e.KeyCode == Keys.R))
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
                e.Handled = true;
            }
        }

        private void ToggleFitToWorkArea()
        {
            if (isFitToWorkArea)
            {
                RestoreWindowBounds();
            }
            else
            {
                FitToWorkArea();
            }
        }

        private void FitToWorkArea()
        {
            if (isFitToWorkArea)
            {
                return;
            }

            previousBounds = Bounds;

            SuspendLayout();
            FormBorderStyle = FormBorderStyle.Sizable;
            TopMost = config.TopMost;
            WindowState = FormWindowState.Normal;
            Bounds = Screen.FromControl(this).WorkingArea;
            ApplyBrowserBounds();
            ResumeLayout();

            isFitToWorkArea = true;
        }

        private void RestoreWindowBounds()
        {
            if (!isFitToWorkArea)
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

            isFitToWorkArea = false;
        }

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

            if (config.MouseDiagnosticsEnabled)
            {
                mouseDiagnostics.LogClipBeforeRelease();
            }

            NativeMethods.ClipCursor(IntPtr.Zero);
        }

        private void GuardAgainstTopJump()
        {
            Rectangle browserScreen = GetBrowserScreenRectangle();
            Point cursor = Cursor.Position;

            if (browserScreen.Contains(cursor))
            {
                lastBrowserCursorPoint = cursor;
                lastBrowserCursorAt = DateTime.Now;
                return;
            }

            if (lastBrowserCursorAt == DateTime.MinValue)
            {
                return;
            }

            TimeSpan elapsed = DateTime.Now - lastBrowserCursorAt;
            bool recentlyInBrowser = elapsed.TotalMilliseconds <= 500;
            bool inThisWindowTop = Bounds.Contains(cursor) && cursor.Y < browserScreen.Top;
            int upwardJump = lastBrowserCursorPoint.Y - cursor.Y;

            if (!recentlyInBrowser || !inThisWindowTop || upwardJump < config.MouseTopJumpThresholdPixels)
            {
                return;
            }

            Point previousBrowserPoint = lastBrowserCursorPoint;
            Point target = new Point(
                Clamp(cursor.X, browserScreen.Left + 1, browserScreen.Right - 2),
                Clamp(browserScreen.Top + config.MouseTopJumpReturnOffsetPixels, browserScreen.Top + 1, browserScreen.Bottom - 2));

            Cursor.Position = target;
            lastBrowserCursorPoint = target;
            lastBrowserCursorAt = DateTime.Now;

            Logger.Mouse(
                "top-jump-corrected cursor=" + cursor.X + "," + cursor.Y +
                " lastBrowser=" + previousBrowserPoint.X + "," + previousBrowserPoint.Y +
                " target=" + target.X + "," + target.Y +
                " browserScreen=" + FormatRectangle(browserScreen) +
                " formBounds=" + FormatRectangle(Bounds) +
                " upwardJump=" + upwardJump);
        }

        private Rectangle GetBrowserScreenRectangle()
        {
            return new Rectangle(browser.PointToScreen(Point.Empty), browser.ClientSize);
        }

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

            flashContent.Style = "position:absolute;top:0px;left:0px;margin:0px;width:1005px;height:600px;";

            Logger.Info("Applied flashcontent top-left compatibility style");
        }

        private void LogDocumentLayout()
        {
            if (browser.Document == null)
            {
                return;
            }

            Logger.Mouse(
                "document-layout browserClient=" + FormatRectangle(new Rectangle(Point.Empty, browser.ClientSize)) +
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

        private static string FormatElement(HtmlElement element)
        {
            if (element == null)
            {
                return "missing";
            }

            return
                "tag=" + element.TagName +
                " id=" + Quote(element.GetAttribute("id")) +
                " name=" + Quote(element.GetAttribute("name")) +
                " class=" + Quote(element.GetAttribute("className")) +
                " widthAttr=" + Quote(element.GetAttribute("width")) +
                " heightAttr=" + Quote(element.GetAttribute("height")) +
                " offset=" + FormatRectangle(element.OffsetRectangle) +
                " client=" + FormatRectangle(element.ClientRectangle) +
                " style=" + Quote(element.GetAttribute("style"));
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            if (value > max)
            {
                return max;
            }

            return value;
        }

        private static string FormatRectangle(Rectangle rectangle)
        {
            return rectangle.Left + "," + rectangle.Top + "," + rectangle.Width + "x" + rectangle.Height;
        }

        private static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            return "\"" + value.Replace("\"", "'") + "\"";
        }

        private void RefreshBrowser()
        {
            browser.Refresh(WebBrowserRefreshOption.Completely);
        }

        private void RegisterHotKeys()
        {
            NativeMethods.RegisterHotKey(Handle, HotKeyFullScreen, 0, (uint)Keys.F11);
            NativeMethods.RegisterHotKey(Handle, HotKeyExitFullScreen, 0, (uint)Keys.Escape);
            NativeMethods.RegisterHotKey(Handle, HotKeyRefresh, 0, (uint)Keys.F5);
            NativeMethods.RegisterHotKey(Handle, HotKeyMouseDiagnosticsDump, 0, (uint)Keys.F8);
            NativeMethods.RegisterHotKey(Handle, HotKeyMouseDiagnosticsToggle, 0, (uint)Keys.F9);
        }

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
