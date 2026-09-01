using System;
using System.Drawing;
using System.Windows.Forms;
using TianshuQitanLauncher.Protocol;

namespace TianshuQitanLauncher
{
    internal sealed class MainForm : Form, IMessageFilter
    {
        private const int HotKeyFullScreen = 1001;
        private const int HotKeyExitFullScreen = 1002;
        private const int HotKeyRefresh = 1003;
        private const int HotKeyMouseDiagnosticsDump = 1004;
        private const int HotKeyMouseDiagnosticsToggle = 1005;
        private const int HotKeyEmergencyBypass = 1006;
        private const int HotKeyAtomicToggle = 1007;
        private const int HotKeyAtomicStep = 1008;
        private const int HotKeyRepairDisplay = 1009;
        private const int HotKeyHardRefresh = 1010;

        private readonly LauncherConfig config;
        private readonly WebBrowser browser;
        private readonly SplitContainer mainSplit;
        private readonly Panel gameHostPanel;
        private readonly Timer mouseClipReleaseTimer;
        private readonly Timer flashRepaintTimer;
        private readonly MouseDiagnostics mouseDiagnostics;
        private readonly ClientInstanceContext instanceContext;
        private readonly MultiAccountManager multiAccountManager;
        private readonly WorkbenchProfile workbenchProfile;
        private readonly ProtocolWorkbenchService workbenchService;
        private readonly ProtocolWorkbenchControl workbenchControl;
        private readonly LoginAutomationCoordinator loginAutomation;
        private readonly BountyAutomationCoordinator bountyAutomation;
        private readonly AudioFilterProxy audioFilterProxy;
        private Rectangle previousBounds;
        private Point lastBrowserCursorPoint;
        private DateTime lastBrowserCursorAt;
        private bool isFitToWorkArea;
        private bool messageFilterInstalled;
        private bool hotKeysRegistered;
        private int flashRepaintPass;

        public MainForm(LauncherConfig config)
            : this(config, null, null, null)
        {
        }

        public MainForm(LauncherConfig config, WorkbenchProfile selectedProfile)
            : this(config, selectedProfile, null, null)
        {
        }

        public MainForm(LauncherConfig config, WorkbenchProfile selectedProfile,
            ClientInstanceContext instanceContext, AccountProfileStore accountStore)
        {
            this.config = config;
            this.instanceContext = instanceContext;

            Text = "Tianshu Qitan";
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = instanceContext != null && instanceContext.ManagedLaunch
                ? FormStartPosition.Manual
                : FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = FormWindowState.Normal;
            ClientSize = new Size(config.Width, config.Height);
            if (instanceContext != null && instanceContext.ManagedLaunch)
            {
                ApplyManagedWindowPosition();
            }
            TopMost = config.TopMost;
            KeyPreview = true;

            mainSplit = new SplitContainer();
            mainSplit.Dock = DockStyle.Fill;
            mainSplit.Size = ClientSize;
            mainSplit.Orientation = Orientation.Vertical;
            mainSplit.FixedPanel = FixedPanel.Panel1;
            mainSplit.Panel1MinSize = 320;
            mainSplit.Panel2MinSize = 220;
            mainSplit.SplitterWidth = 5;
            mainSplit.SplitterMoved += OnMainSplitterMoved;

            gameHostPanel = new Panel();
            gameHostPanel.Dock = DockStyle.Fill;
            gameHostPanel.BackColor = Color.Black;
            mainSplit.Panel1.Controls.Add(gameHostPanel);

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

            gameHostPanel.Controls.Add(browser);

            WorkbenchProfile loadedProfile = null;
            ProtocolWorkbenchService loadedService = null;
            ProtocolWorkbenchControl loadedControl = null;
            LoginAutomationCoordinator loadedLoginAutomation = null;
            BountyAutomationCoordinator loadedBountyAutomation = null;
            AudioFilterProxy loadedAudioFilterProxy = null;
            MultiAccountManager loadedMultiAccountManager = null;
            try
            {
                AudioFilterProxyOptions audioOptions = new AudioFilterProxyOptions
                {
                    ListenPort = instanceContext == null ? config.AudioFilterProxyPort : 0,
                    FfmpegPath = config.FfmpegPath,
                    FilterGraph = config.AudioFilterGraph,
                    BitrateKbps = config.AudioBitrateKbps,
                    Mp3Quality = config.AudioMp3Quality,
                    CacheDirectory = instanceContext == null
                        ? ResolveLauncherPath(config.AudioCacheDirectory)
                        : System.IO.Path.Combine(instanceContext.InstanceRoot, "audio-cache"),
                    SoundHost = config.AudioSoundHost,
                    SoundPathPrefix = config.AudioSoundPathPrefix
                };
                loadedAudioFilterProxy = new AudioFilterProxy(audioOptions);
                loadedAudioFilterProxy.FilteringEnabled = config.AudioFilterEnabled;
                loadedAudioFilterProxy.Start();
            }
            catch (Exception ex)
            {
                if (loadedAudioFilterProxy != null) loadedAudioFilterProxy.Dispose();
                loadedAudioFilterProxy = null;
                Logger.Error("Cannot initialize BGM filter proxy", ex);
            }
            try
            {
                loadedProfile = selectedProfile ?? WorkbenchProfile.LoadDefault(AppDomain.CurrentDomain.BaseDirectory);
                loadedService = new ProtocolWorkbenchService(loadedProfile);
                WinsockCaptureEngine captureEngine = new WinsockCaptureEngine(
                    loadedService,
                    loadedProfile,
                    loadedAudioFilterProxy == null ? 0 : loadedAudioFilterProxy.Port);
                loadedService.AttachCaptureEngine(captureEngine);
                LoginCredentialStore credentialStore = new LoginCredentialStore(
                    instanceContext == null
                        ? System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "secrets", "auto-login.json")
                        : instanceContext.CredentialPath);
                loadedLoginAutomation = new LoginAutomationCoordinator(browser, loadedService, credentialStore);
                loadedBountyAutomation = new BountyAutomationCoordinator(loadedService);
                if (instanceContext != null && accountStore != null)
                {
                    loadedMultiAccountManager = new MultiAccountManager(
                        AppDomain.CurrentDomain.BaseDirectory,
                        Application.ExecutablePath,
                        instanceContext.LaunchProfilePath,
                        accountStore,
                        instanceContext);
                }
                loadedControl = new ProtocolWorkbenchControl(
                    loadedService, loadedLoginAutomation, loadedAudioFilterProxy, loadedBountyAutomation,
                    loadedMultiAccountManager);
                mainSplit.Panel2.Controls.Add(loadedControl);
            }
            catch (Exception ex)
            {
                if (loadedBountyAutomation != null) loadedBountyAutomation.Dispose();
                if (loadedLoginAutomation != null) loadedLoginAutomation.Dispose();
                Logger.Error("Cannot initialize protocol workbench", ex);
                Label errorLabel = new Label();
                errorLabel.Dock = DockStyle.Fill;
                errorLabel.TextAlign = ContentAlignment.MiddleCenter;
                errorLabel.ForeColor = Color.DarkRed;
                errorLabel.Text = "Protocol workbench unavailable:" + Environment.NewLine + ex.Message;
                mainSplit.Panel2.Controls.Add(errorLabel);
            }
            workbenchProfile = loadedProfile;
            workbenchService = loadedService;
            workbenchControl = loadedControl;
            loginAutomation = loadedLoginAutomation;
            bountyAutomation = loadedBountyAutomation;
            audioFilterProxy = loadedAudioFilterProxy;
            multiAccountManager = loadedMultiAccountManager;

            flashRepaintTimer = new Timer();
            flashRepaintTimer.Interval = config.FlashRepaintDelayMs;
            flashRepaintTimer.Tick += OnFlashRepaintTimerTick;

            Controls.Add(mainSplit);
            ApplyWorkbenchLayout();
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
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            mouseDiagnostics.Stop();
            flashRepaintTimer.Stop();
            UnregisterHotKeys();
            base.OnHandleDestroyed(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (messageFilterInstalled)
            {
                Application.RemoveMessageFilter(this);
                messageFilterInstalled = false;
            }
            flashRepaintTimer.Dispose();
            mouseDiagnostics.Dispose();
            if (loginAutomation != null)
            {
                loginAutomation.Dispose();
            }
            if (bountyAutomation != null)
            {
                bountyAutomation.Dispose();
            }
            if (workbenchService != null)
            {
                workbenchService.Dispose();
            }
            if (audioFilterProxy != null)
            {
                audioFilterProxy.Dispose();
            }
            base.OnFormClosed(e);
        }

        private static string ResolveLauncherPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "audio-cache");
            }
            return System.IO.Path.IsPathRooted(path)
                ? path
                : System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path));
        }

        private void ApplyManagedWindowPosition()
        {
            if (instanceContext == null) return;
            Rectangle workingArea = Screen.PrimaryScreen.WorkingArea;
            int cascade = (instanceContext.WindowSlot % 8) * 28;
            Location = new Point(workingArea.Left + cascade, workingArea.Top + cascade);
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            RegisterHotKeys();
            ScheduleFlashRepaint();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            UnregisterHotKeys();
            base.OnDeactivate(e);
        }

        protected override void OnClientSizeChanged(EventArgs e)
        {
            base.OnClientSizeChanged(e);

            if (browser != null)
            {
                ApplyWorkbenchLayout();
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

                if (id == HotKeyHardRefresh)
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

                if (id == HotKeyEmergencyBypass)
                {
                    if (workbenchService != null)
                    {
                        workbenchService.EmergencyBypass();
                    }
                    return;
                }

                if (id == HotKeyAtomicToggle)
                {
                    HandleAtomicToggle();
                    return;
                }

                if (id == HotKeyAtomicStep)
                {
                    HandleAtomicStep();
                    return;
                }

                if (id == HotKeyRepairDisplay)
                {
                    ForceFlashRepaint("manual-hotkey");
                    return;
                }
            }

            base.WndProc(ref m);
        }

        private void OnLoad(object sender, EventArgs e)
        {
            if (!messageFilterInstalled)
            {
                Application.AddMessageFilter(this);
                messageFilterInstalled = true;
            }

            if ((instanceContext == null || !instanceContext.ManagedLaunch) &&
                (config.StartFitToWorkArea || config.StartFullScreen))
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

            if (workbenchService != null)
            {
                try
                {
                    workbenchService.StartCapture();
                }
                catch (Exception ex)
                {
                    Logger.Error("Cannot start Winsock capture", ex);
                    workbenchService.ReportEngineEvent("Capture", "ERROR", ex.Message, null);
                }
            }

            string gameUrl = workbenchProfile != null && !string.IsNullOrWhiteSpace(workbenchProfile.GameUrl)
                ? workbenchProfile.GameUrl
                : config.GameUrl;
            browser.Navigate(gameUrl);
        }

        private void OnBrowserNavigating(object sender, WebBrowserNavigatingEventArgs e)
        {
            ReleaseFlashMouseState("navigation");
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
            if (browser.Url != null && e.Url == browser.Url && browser.ReadyState == WebBrowserReadyState.Complete)
            {
                ReleaseFlashMouseState("document-ready");
                if (loginAutomation != null)
                {
                    loginAutomation.NotifyDocumentReady();
                }
            }
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (!IsRightMouseMessage(m.Msg) || !IsBrowserMessageTarget(m.HWnd))
            {
                return false;
            }

            if (m.Msg == NativeMethods.WM_RBUTTONDOWN ||
                m.Msg == NativeMethods.WM_RBUTTONDBLCLK ||
                m.Msg == NativeMethods.WM_CONTEXTMENU)
            {
                ReleaseFlashMouseState("right-click-blocked");
                ScheduleFlashRepaint();
                Logger.Info("Blocked right-click inside Flash to prevent the ActiveX context-menu freeze");
            }

            return true;
        }

        private static bool IsRightMouseMessage(int message)
        {
            return message == NativeMethods.WM_RBUTTONDOWN ||
                message == NativeMethods.WM_RBUTTONUP ||
                message == NativeMethods.WM_RBUTTONDBLCLK ||
                message == NativeMethods.WM_CONTEXTMENU;
        }

        private bool IsBrowserMessageTarget(IntPtr target)
        {
            if (browser == null || browser.IsDisposed || !browser.IsHandleCreated)
            {
                return false;
            }

            if (target == browser.Handle || (target != IntPtr.Zero && NativeMethods.IsChild(browser.Handle, target)))
            {
                return true;
            }

            NativeMethods.POINT cursor;
            if (!NativeMethods.GetCursorPos(out cursor))
            {
                return false;
            }

            IntPtr cursorWindow = NativeMethods.WindowFromPoint(cursor);
            return cursorWindow == browser.Handle ||
                (cursorWindow != IntPtr.Zero && NativeMethods.IsChild(browser.Handle, cursorWindow));
        }

        private void UpdateTitle()
        {
            string title = browser.DocumentTitle;
            string pageTitle = string.IsNullOrWhiteSpace(title) ? "Tianshu Qitan" : "Tianshu Qitan - " + title;
            if (instanceContext == null)
            {
                Text = pageTitle;
                return;
            }
            Text = "[" + instanceContext.AccountDisplayName + " | PID " +
                System.Diagnostics.Process.GetCurrentProcess().Id + " | " + instanceContext.ShortInstanceId + "] " + pageTitle;
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

            if (e.Control && e.Shift && e.KeyCode == Keys.F12)
            {
                if (workbenchService != null)
                {
                    workbenchService.EmergencyBypass();
                }
                e.Handled = true;
                return;
            }

            if (e.Control && e.KeyCode == Keys.F8)
            {
                HandleAtomicToggle();
                e.Handled = true;
                return;
            }

            if (e.Control && e.KeyCode == Keys.F9)
            {
                HandleAtomicStep();
                e.Handled = true;
                return;
            }

            if (e.Control && e.KeyCode == Keys.F10)
            {
                ForceFlashRepaint("manual-key");
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
            if (instanceContext != null && instanceContext.ManagedLaunch &&
                Form.ActiveForm != this && !ContainsFocus)
            {
                return;
            }

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
                bounds = gameHostPanel.ClientRectangle;
            }
            else
            {
                int width = Math.Min(config.BrowserWidth, gameHostPanel.ClientSize.Width);
                int height = Math.Min(config.BrowserHeight, gameHostPanel.ClientSize.Height);
                int left = 0;
                int top = 0;

                if (string.Equals(config.BrowserAlign, "center", StringComparison.OrdinalIgnoreCase))
                {
                    left = Math.Max(0, (gameHostPanel.ClientSize.Width - width) / 2);
                    top = Math.Max(0, (gameHostPanel.ClientSize.Height - height) / 2);
                }

                bounds = new Rectangle(left, top, width, height);
            }

            bool boundsChanged = browser.Bounds != bounds;
            browser.Bounds = bounds;
            gameHostPanel.BackColor = Color.Black;

            if (boundsChanged)
            {
                ScheduleFlashRepaint();
            }
        }

        private void OnMainSplitterMoved(object sender, SplitterEventArgs e)
        {
            ApplyBrowserBounds();
            ScheduleFlashRepaint();
        }

        private void ApplyWorkbenchLayout()
        {
            if (mainSplit == null || mainSplit.Width <= 0)
            {
                return;
            }
            int desired = Math.Min(config.BrowserWidth, Math.Max(mainSplit.Panel1MinSize, mainSplit.Width - mainSplit.Panel2MinSize - mainSplit.SplitterWidth));
            int maximum = Math.Max(mainSplit.Panel1MinSize, mainSplit.Width - mainSplit.Panel2MinSize - mainSplit.SplitterWidth);
            mainSplit.SplitterDistance = Math.Min(desired, maximum);
        }

        private void ApplyPageCompatibilityFixes()
        {
            if (browser.Document == null)
            {
                return;
            }

            if (config.FixFlashPosition)
            {
                if (browser.Document.Body != null)
                {
                    browser.Document.Body.Style = "padding:0;margin:0;overflow:hidden;background:#000;";
                }

                HtmlElement flashContent = browser.Document.GetElementById("flashcontent");
                if (flashContent != null)
                {
                    flashContent.Style = "position:absolute;top:0px;left:0px;margin:0px;width:1005px;height:600px;";
                    Logger.Info("Applied flashcontent top-left compatibility style");
                }
            }

            ApplyFlashWindowMode();
            ScheduleFlashRepaint();
        }

        private void ApplyFlashWindowMode()
        {
            if (string.Equals(config.FlashWindowMode, "page", StringComparison.OrdinalIgnoreCase) ||
                browser.Document == null)
            {
                return;
            }

            HtmlElement flash = browser.Document.GetElementById("Loading");
            if (flash == null)
            {
                return;
            }

            try
            {
                bool changed = false;
                if (string.Equals(flash.TagName, "EMBED", StringComparison.OrdinalIgnoreCase))
                {
                    string currentMode = flash.GetAttribute("wmode");
                    if (!string.Equals(currentMode, config.FlashWindowMode, StringComparison.OrdinalIgnoreCase))
                    {
                        flash.SetAttribute("wmode", config.FlashWindowMode);
                        changed = true;
                    }
                }
                else if (string.Equals(flash.TagName, "OBJECT", StringComparison.OrdinalIgnoreCase))
                {
                    HtmlElement modeParam = null;
                    HtmlElementCollection parameters = flash.GetElementsByTagName("param");
                    for (int i = 0; i < parameters.Count; i++)
                    {
                        if (string.Equals(parameters[i].GetAttribute("name"), "wmode", StringComparison.OrdinalIgnoreCase))
                        {
                            modeParam = parameters[i];
                            break;
                        }
                    }

                    if (modeParam == null)
                    {
                        modeParam = browser.Document.CreateElement("param");
                        modeParam.SetAttribute("name", "wmode");
                        modeParam.SetAttribute("value", config.FlashWindowMode);
                        flash.AppendChild(modeParam);
                        changed = true;
                    }
                    else if (!string.Equals(modeParam.GetAttribute("value"), config.FlashWindowMode, StringComparison.OrdinalIgnoreCase))
                    {
                        modeParam.SetAttribute("value", config.FlashWindowMode);
                        changed = true;
                    }
                }

                if (!changed)
                {
                    return;
                }

                // WMODE is read when Flash is instantiated. Replacing the element once applies
                // the new mode while the page is still loading, before the user can start work.
                flash.OuterHtml = flash.OuterHtml;
                Logger.Info("Changed Flash window mode to " + config.FlashWindowMode + " to prevent black repaint artifacts");
            }
            catch (Exception ex)
            {
                Logger.Error("Cannot apply Flash window mode " + config.FlashWindowMode, ex);
            }
        }

        private void ScheduleFlashRepaint()
        {
            if (!config.FlashRepaintWorkaround || flashRepaintTimer == null || IsDisposed)
            {
                return;
            }

            flashRepaintTimer.Stop();
            flashRepaintPass = 0;
            flashRepaintTimer.Interval = Math.Max(25, config.FlashRepaintDelayMs);
            flashRepaintTimer.Start();
        }

        private void OnFlashRepaintTimerTick(object sender, EventArgs e)
        {
            flashRepaintTimer.Stop();
            flashRepaintPass++;
            ForceFlashRepaint("automatic-pass-" + flashRepaintPass);

            // Flash creates and paints its native child window asynchronously. Repainting at
            // several progressively wider intervals repairs stale black regions without a reload.
            if (flashRepaintPass < 4 && !IsDisposed)
            {
                flashRepaintTimer.Interval = flashRepaintPass == 1 ? 200 :
                    flashRepaintPass == 2 ? 500 : 1000;
                flashRepaintTimer.Start();
            }
        }

        private void ForceFlashRepaint(string reason)
        {
            if (browser == null || browser.IsDisposed || !browser.IsHandleCreated)
            {
                return;
            }

            uint childFlags = NativeMethods.RDW_INVALIDATE |
                NativeMethods.RDW_INTERNALPAINT |
                NativeMethods.RDW_ERASE |
                NativeMethods.RDW_FRAME |
                NativeMethods.RDW_UPDATENOW;
            int childCount = 0;

            NativeMethods.EnumWindowsProc repaintChild = delegate(IntPtr child, IntPtr data)
            {
                childCount++;
                NativeMethods.InvalidateRect(child, IntPtr.Zero, true);
                NativeMethods.RedrawWindow(child, IntPtr.Zero, IntPtr.Zero, childFlags);
                NativeMethods.UpdateWindow(child);
                return true;
            };

            browser.Invalidate(true);
            NativeMethods.EnumChildWindows(browser.Handle, repaintChild, IntPtr.Zero);
            NativeMethods.RedrawWindow(
                browser.Handle,
                IntPtr.Zero,
                IntPtr.Zero,
                NativeMethods.RDW_INVALIDATE | NativeMethods.RDW_INTERNALPAINT |
                NativeMethods.RDW_ERASE | NativeMethods.RDW_FRAME |
                NativeMethods.RDW_ALLCHILDREN | NativeMethods.RDW_UPDATENOW);
            browser.Update();
            Logger.Info("Forced Flash repaint: " + reason + ", nativeChildren=" + childCount);
        }

        private void ReleaseFlashMouseState(string reason)
        {
            IntPtr capture = NativeMethods.GetCapture();
            bool browserCapture = capture != IntPtr.Zero && IsBrowserMessageTarget(capture);

            if (browser != null && !browser.IsDisposed && browser.IsHandleCreated)
            {
                NativeMethods.PostMessage(browser.Handle, NativeMethods.WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
                NativeMethods.EnumWindowsProc cancelChild = delegate(IntPtr child, IntPtr data)
                {
                    NativeMethods.PostMessage(child, NativeMethods.WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
                    return true;
                };
                NativeMethods.EnumChildWindows(browser.Handle, cancelChild, IntPtr.Zero);
            }

            NativeMethods.ReleaseCapture();
            NativeMethods.ClipCursor(IntPtr.Zero);
            Cursor.Clip = Rectangle.Empty;
            Cursor.Current = Cursors.Default;
            NativeMethods.SetCursor(Cursors.Default.Handle);

            NativeMethods.CURSORINFO cursorInfo = new NativeMethods.CURSORINFO();
            cursorInfo.Size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.CURSORINFO));
            bool restoredHiddenCursor = NativeMethods.GetCursorInfo(ref cursorInfo) &&
                (cursorInfo.Flags & NativeMethods.CURSOR_SHOWING) == 0;
            if (restoredHiddenCursor)
            {
                // ShowCursor is reference counted. Only rebalance it when Windows confirms
                // that the cursor is hidden, and stop as soon as it becomes visible.
                for (int attempt = 0; attempt < 8 && NativeMethods.ShowCursor(true) < 0; attempt++)
                {
                }
            }

            Logger.Info(
                "Released Flash mouse state: " + reason +
                ", browserCapture=" + browserCapture +
                ", restoredHiddenCursor=" + restoredHiddenCursor);
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
                " wmodeAttr=" + Quote(element.GetAttribute("wmode")) +
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
            ReleaseFlashMouseState("before-refresh");
            if (loginAutomation != null)
            {
                loginAutomation.RefreshPage(false);
                return;
            }
            browser.Refresh(WebBrowserRefreshOption.Completely);
        }

        private void RegisterHotKeys()
        {
            if (hotKeysRegistered || !IsHandleCreated) return;
            NativeMethods.RegisterHotKey(Handle, HotKeyFullScreen, 0, (uint)Keys.F11);
            NativeMethods.RegisterHotKey(Handle, HotKeyExitFullScreen, 0, (uint)Keys.Escape);
            NativeMethods.RegisterHotKey(Handle, HotKeyRefresh, 0, (uint)Keys.F5);
            NativeMethods.RegisterHotKey(Handle, HotKeyHardRefresh, NativeMethods.MOD_CONTROL, (uint)Keys.F5);
            NativeMethods.RegisterHotKey(Handle, HotKeyMouseDiagnosticsDump, 0, (uint)Keys.F8);
            NativeMethods.RegisterHotKey(Handle, HotKeyMouseDiagnosticsToggle, 0, (uint)Keys.F9);
            NativeMethods.RegisterHotKey(
                Handle,
                HotKeyEmergencyBypass,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT,
                (uint)Keys.F12);
            NativeMethods.RegisterHotKey(Handle, HotKeyAtomicToggle, NativeMethods.MOD_CONTROL, (uint)Keys.F8);
            NativeMethods.RegisterHotKey(Handle, HotKeyAtomicStep, NativeMethods.MOD_CONTROL, (uint)Keys.F9);
            NativeMethods.RegisterHotKey(Handle, HotKeyRepairDisplay, NativeMethods.MOD_CONTROL, (uint)Keys.F10);
            hotKeysRegistered = true;
        }

        private void UnregisterHotKeys()
        {
            if (!hotKeysRegistered || !IsHandleCreated) return;
            NativeMethods.UnregisterHotKey(Handle, HotKeyFullScreen);
            NativeMethods.UnregisterHotKey(Handle, HotKeyExitFullScreen);
            NativeMethods.UnregisterHotKey(Handle, HotKeyRefresh);
            NativeMethods.UnregisterHotKey(Handle, HotKeyHardRefresh);
            NativeMethods.UnregisterHotKey(Handle, HotKeyMouseDiagnosticsDump);
            NativeMethods.UnregisterHotKey(Handle, HotKeyMouseDiagnosticsToggle);
            NativeMethods.UnregisterHotKey(Handle, HotKeyEmergencyBypass);
            NativeMethods.UnregisterHotKey(Handle, HotKeyAtomicToggle);
            NativeMethods.UnregisterHotKey(Handle, HotKeyAtomicStep);
            NativeMethods.UnregisterHotKey(Handle, HotKeyRepairDisplay);
            hotKeysRegistered = false;
        }

        private void HandleAtomicToggle()
        {
            if (workbenchService == null) return;
            try { workbenchService.ToggleAtomicOperationRecording(); }
            catch (Exception ex)
            {
                Logger.Error("Cannot toggle atomic operation recording", ex);
                workbenchService.ReportEngineEvent("AtomicOperation", "ERROR", ex.Message, null);
            }
        }

        private void HandleAtomicStep()
        {
            if (workbenchService == null) return;
            try { workbenchService.MarkAtomicOperationStep(null, null); }
            catch (Exception ex)
            {
                Logger.Error("Cannot mark atomic operation step", ex);
                workbenchService.ReportEngineEvent("AtomicOperation", "ERROR", ex.Message, null);
            }
        }
    }
}
