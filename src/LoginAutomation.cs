using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using TianshuQitanLauncher.Protocol;

namespace TianshuQitanLauncher
{
    public enum LoginAutomationState
    {
        Inactive = 0,
        Scheduled = 1,
        FillingCredentials = 2,
        AwaitingCredentialPacket = 3,
        AwaitingServerList = 4,
        SelectingLine = 5,
        AwaitingGameTicket = 6,
        AwaitingRoleList = 7,
        SelectingRole = 8,
        AwaitingRoleSelection = 9,
        AwaitingWorldEntry = 10,
        Completed = 11,
        Stopped = 12,
        Failed = 13
    }

    public sealed class LoginAutomationCoordinator : IDisposable
    {
        private readonly object syncRoot = new object();
        private readonly WebBrowser browser;
        private readonly ProtocolWorkbenchService service;
        private readonly LoginCredentialStore credentialStore;
        private readonly FlashLoginInputDriver inputDriver;
        private System.Threading.Timer timer;
        private LoginAutomationSettings settings;
        private LoginAutomationState state;
        private string activePassword;
        private int generation;
        private int submitAttempts;
        private int selectedRoleId;
        private bool restartAfterRefresh;
        private bool disposed;

        public event Action<LoginAutomationState, string> StatusChanged;

        public LoginAutomationCoordinator(WebBrowser browser, ProtocolWorkbenchService service, LoginCredentialStore credentialStore)
        {
            if (browser == null) throw new ArgumentNullException("browser");
            if (service == null) throw new ArgumentNullException("service");
            if (credentialStore == null) throw new ArgumentNullException("credentialStore");
            this.browser = browser;
            this.service = service;
            this.credentialStore = credentialStore;
            inputDriver = new FlashLoginInputDriver(browser);
            settings = credentialStore.Load();
            state = LoginAutomationState.Inactive;
            service.FrameCaptured += OnFrameCaptured;
        }

        public LoginAutomationSettings Settings
        {
            get
            {
                lock (syncRoot)
                {
                    return settings.Clone();
                }
            }
        }

        public LoginAutomationState State
        {
            get
            {
                lock (syncRoot) return state;
            }
        }

        public void SaveConfiguration(bool enabled, string username, int lineNumber, int roleSlot, string newPassword)
        {
            LoginAutomationSettings updated;
            lock (syncRoot)
            {
                updated = settings.Clone();
            }
            updated.Enabled = enabled;
            updated.Username = (username ?? string.Empty).Trim();
            updated.LineNumber = lineNumber;
            updated.RoleSlot = roleSlot;
            credentialStore.Save(updated, newPassword);
            lock (syncRoot)
            {
                settings = credentialStore.Load();
            }
            Publish(LoginAutomationState.Inactive, "自动登录配置已保存；密码仅由当前 Windows 用户解密。", false);
        }

        public void ClearSavedPassword()
        {
            LoginAutomationSettings current;
            lock (syncRoot) current = settings.Clone();
            credentialStore.ClearPassword(current);
            lock (syncRoot) settings = credentialStore.Load();
            Publish(LoginAutomationState.Inactive, "已清除保存的密码。", false);
        }

        public void NotifyDocumentReady()
        {
            LoginAutomationSettings current;
            LoginAutomationState currentState;
            bool refreshRestart;
            lock (syncRoot)
            {
                current = settings.Clone();
                currentState = state;
                refreshRestart = restartAfterRefresh;
                if (refreshRestart) restartAfterRefresh = false;
            }
            if (!refreshRestart && (!current.Enabled || IsRunning(currentState)))
            {
                return;
            }
            StartCore(6000);
        }

        public void StartNow()
        {
            StartCore(250);
        }

        public void RefreshPage(bool forceLogin)
        {
            int currentGeneration;
            bool shouldRestart;
            lock (syncRoot)
            {
                if (disposed) return;
                shouldRestart = forceLogin || settings.Enabled || IsRunning(state) || state == LoginAutomationState.Completed;
                generation++;
                currentGeneration = generation;
                state = LoginAutomationState.Inactive;
                activePassword = null;
                selectedRoleId = 0;
                restartAfterRefresh = shouldRestart;
                DisposeTimerLocked();
            }
            PublishForGeneration(currentGeneration, LoginAutomationState.Inactive,
                shouldRestart ? "正在刷新页面；加载完成后将重新自动登录。" : "正在刷新页面。", false);
            try
            {
                browser.Refresh(WebBrowserRefreshOption.Completely);
            }
            catch (Exception ex)
            {
                lock (syncRoot)
                {
                    if (generation != currentGeneration) return;
                    restartAfterRefresh = false;
                    state = LoginAutomationState.Failed;
                }
                PublishForGeneration(currentGeneration, LoginAutomationState.Failed, "刷新页面失败：" + ex.Message, true);
            }
        }

        public void Stop()
        {
            int currentGeneration;
            lock (syncRoot)
            {
                generation++;
                currentGeneration = generation;
                state = LoginAutomationState.Stopped;
                activePassword = null;
                selectedRoleId = 0;
                restartAfterRefresh = false;
                DisposeTimerLocked();
            }
            PublishForGeneration(currentGeneration, LoginAutomationState.Stopped, "自动登录已停止。", false);
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                if (disposed) return;
                disposed = true;
                generation++;
                activePassword = null;
                restartAfterRefresh = false;
                DisposeTimerLocked();
            }
            service.FrameCaptured -= OnFrameCaptured;
        }

        private void StartCore(int delayMs)
        {
            LoginAutomationSettings loaded;
            string password;
            try
            {
                loaded = credentialStore.Load();
                password = credentialStore.ReadPassword(loaded);
            }
            catch (Exception ex)
            {
                Fail("无法读取自动登录密码：" + ex.Message);
                return;
            }
            if (string.IsNullOrWhiteSpace(loaded.Username))
            {
                Fail("请先配置账号。", false);
                return;
            }
            if (string.IsNullOrEmpty(password))
            {
                Fail("请先输入并保存密码。", false);
                return;
            }

            int currentGeneration;
            lock (syncRoot)
            {
                if (disposed) return;
                generation++;
                currentGeneration = generation;
                settings = loaded;
                activePassword = password;
                submitAttempts = 0;
                selectedRoleId = 0;
                restartAfterRefresh = false;
                state = LoginAutomationState.Scheduled;
                ScheduleLocked(currentGeneration, delayMs, SubmitCredentials);
            }
            PublishForGeneration(currentGeneration, LoginAutomationState.Scheduled,
                delayMs > 1000 ? "页面已就绪，等待登录界面加载。" : "准备执行自动登录。", false);
        }

        private void SubmitCredentials(int expectedGeneration)
        {
            LoginAutomationSettings current;
            string password;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration, LoginAutomationState.Scheduled)) return;
                state = LoginAutomationState.FillingCredentials;
                current = settings.Clone();
                password = activePassword;
                submitAttempts++;
            }
            PublishForGeneration(expectedGeneration, LoginAutomationState.FillingCredentials,
                "正在填写账号和密码（内容不会写入日志）。", false);

            string error;
            bool submitted = inputDriver.TrySubmitCredentials(current.Username, password, out error);
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration, LoginAutomationState.FillingCredentials)) return;
                if (!submitted)
                {
                    if (submitAttempts >= 3)
                    {
                        state = LoginAutomationState.Failed;
                        activePassword = null;
                        DisposeTimerLocked();
                    }
                    else
                    {
                        state = LoginAutomationState.Scheduled;
                        ScheduleLocked(expectedGeneration, 2500, SubmitCredentials);
                    }
                }
                else
                {
                    state = LoginAutomationState.AwaitingCredentialPacket;
                    ScheduleLocked(expectedGeneration, 8000, CredentialPacketTimeout);
                }
            }
            if (!submitted)
            {
                if (submitAttempts >= 3)
                    PublishForGeneration(expectedGeneration, LoginAutomationState.Failed, "无法操作登录界面：" + error, true);
                else
                    PublishForGeneration(expectedGeneration, LoginAutomationState.Scheduled, "登录界面尚未可用，稍后重试。", false);
            }
            else
            {
                PublishForGeneration(expectedGeneration, LoginAutomationState.AwaitingCredentialPacket,
                    "登录已提交，等待入口服确认。", false);
            }
        }

        private void CredentialPacketTimeout(int expectedGeneration)
        {
            bool retry;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration, LoginAutomationState.AwaitingCredentialPacket)) return;
                retry = submitAttempts < 3;
                state = retry ? LoginAutomationState.Scheduled : LoginAutomationState.Failed;
                if (retry)
                    ScheduleLocked(expectedGeneration, 1500, SubmitCredentials);
                else
                {
                    activePassword = null;
                    DisposeTimerLocked();
                }
            }
            PublishForGeneration(expectedGeneration, retry ? LoginAutomationState.Scheduled : LoginAutomationState.Failed,
                retry ? "尚未观察到登录报文，重新尝试。" : "入口服未收到登录报文，自动登录超时。", !retry);
        }

        private void OnFrameCaptured(ProtocolFrame frame)
        {
            if (frame == null || !frame.Opcode.HasValue) return;
            int opcode = (int)frame.Opcode.Value;
            int currentGeneration;
            LoginAutomationState currentState;
            lock (syncRoot)
            {
                if (disposed) return;
                currentGeneration = generation;
                currentState = state;
            }

            if (frame.Direction == TrafficDirection.ClientToServer &&
                opcode == TianshuLoginProtocol.ClientUserPassword &&
                (currentState == LoginAutomationState.AwaitingCredentialPacket || currentState == LoginAutomationState.FillingCredentials))
            {
                lock (syncRoot)
                {
                    if (generation != currentGeneration ||
                        (state != LoginAutomationState.AwaitingCredentialPacket && state != LoginAutomationState.FillingCredentials)) return;
                    state = LoginAutomationState.AwaitingServerList;
                    activePassword = null;
                    ScheduleLocked(currentGeneration, 20000, ServerListTimeout);
                }
                PublishForGeneration(currentGeneration, LoginAutomationState.AwaitingServerList,
                    "入口服已收到凭据；抓包副本已脱敏，等待线路列表。", false);
                return;
            }

            if (frame.Direction == TrafficDirection.ServerToClient &&
                opcode == TianshuLoginProtocol.ServerGameServerList &&
                currentState == LoginAutomationState.AwaitingServerList)
            {
                HandleServerList(currentGeneration, frame.Bytes);
                return;
            }

            if (frame.Direction == TrafficDirection.ClientToServer &&
                opcode == TianshuLoginProtocol.ClientUserToken2 &&
                (currentState == LoginAutomationState.SelectingLine || currentState == LoginAutomationState.AwaitingGameTicket))
            {
                lock (syncRoot)
                {
                    if (generation != currentGeneration) return;
                    state = LoginAutomationState.AwaitingRoleList;
                    activePassword = null;
                    ScheduleLocked(currentGeneration, 30000, RoleListTimeout);
                }
                PublishForGeneration(currentGeneration, LoginAutomationState.AwaitingRoleList,
                    "指定线路已校验登录票据，等待角色列表。", false);
                return;
            }

            if (frame.Direction == TrafficDirection.ServerToClient &&
                opcode == TianshuLoginProtocol.ServerRoleInfoList &&
                currentState == LoginAutomationState.AwaitingRoleList)
            {
                HandleRoleList(currentGeneration, frame.Bytes);
                return;
            }

            if (frame.Direction == TrafficDirection.ClientToServer &&
                opcode == TianshuLoginProtocol.ClientSelectRole &&
                (currentState == LoginAutomationState.SelectingRole || currentState == LoginAutomationState.AwaitingRoleSelection))
            {
                int characterId;
                if (!TianshuLoginProtocol.TryParseSelectRole(frame.Bytes, out characterId))
                {
                    FailForGeneration(currentGeneration, "选择角色请求格式不符合已确认的协议结构。");
                    return;
                }
                bool matched;
                lock (syncRoot)
                {
                    if (generation != currentGeneration ||
                        (state != LoginAutomationState.SelectingRole && state != LoginAutomationState.AwaitingRoleSelection)) return;
                    matched = selectedRoleId != 0 && characterId == selectedRoleId;
                    if (!matched)
                    {
                        state = LoginAutomationState.Failed;
                        selectedRoleId = 0;
                        DisposeTimerLocked();
                    }
                    else
                    {
                        state = LoginAutomationState.AwaitingWorldEntry;
                        ScheduleLocked(currentGeneration, 60000, WorldEntryTimeout);
                    }
                }
                if (!matched)
                {
                    PublishForGeneration(currentGeneration, LoginAutomationState.Failed,
                        "客户端提交的角色与配置槽位不一致，自动登录已停止。", true);
                }
                else
                {
                    PublishForGeneration(currentGeneration, LoginAutomationState.AwaitingWorldEntry,
                        "已提交配置的角色，等待地图初始化。", false);
                }
                return;
            }

            if (frame.Direction == TrafficDirection.ServerToClient &&
                opcode == TianshuLoginProtocol.ServerRoleStartPoint &&
                currentState == LoginAutomationState.AwaitingWorldEntry)
            {
                lock (syncRoot)
                {
                    if (!IsCurrentLocked(currentGeneration, LoginAutomationState.AwaitingWorldEntry)) return;
                    state = LoginAutomationState.Completed;
                    selectedRoleId = 0;
                    DisposeTimerLocked();
                }
                PublishForGeneration(currentGeneration, LoginAutomationState.Completed,
                    "角色与地图起点已确认，自动登录完成。", false);
            }
        }

        private void HandleServerList(int expectedGeneration, byte[] bytes)
        {
            IList<TianshuGameServer> servers;
            string error;
            if (!TianshuLoginProtocol.TryParseGameServerList(bytes, out servers, out error))
            {
                FailForGeneration(expectedGeneration, "线路列表解析失败：" + error);
                return;
            }
            LoginAutomationSettings current;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration, LoginAutomationState.AwaitingServerList)) return;
                current = settings.Clone();
            }
            TianshuGameServer selected = TianshuLoginProtocol.FindPhysicalLine(servers, current.LineNumber);
            if (selected == null)
            {
                FailForGeneration(expectedGeneration, "服务器未返回配置的" + TianshuLoginProtocol.GetChineseLineMarker(current.LineNumber) + "。");
                return;
            }
            int packetIndex = selected.PacketIndex;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration, LoginAutomationState.AwaitingServerList)) return;
                state = LoginAutomationState.SelectingLine;
                ScheduleLocked(expectedGeneration, 500, delegate(int value) { SelectLine(value, packetIndex); });
            }
            PublishForGeneration(expectedGeneration, LoginAutomationState.SelectingLine,
                "线路列表已解析，准备选择" + TianshuLoginProtocol.GetChineseLineMarker(current.LineNumber) + "。", false);
        }

        private void SelectLine(int expectedGeneration, int packetIndex)
        {
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration, LoginAutomationState.SelectingLine)) return;
            }
            string error;
            if (!inputDriver.TrySelectServerRow(packetIndex, out error))
            {
                FailForGeneration(expectedGeneration, "无法点击目标线路：" + error);
                return;
            }
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration, LoginAutomationState.SelectingLine)) return;
                state = LoginAutomationState.AwaitingGameTicket;
                ScheduleLocked(expectedGeneration, 15000, GameTicketTimeout);
            }
            PublishForGeneration(expectedGeneration, LoginAutomationState.AwaitingGameTicket,
                "已选择目标线路，等待游戏服票据校验。", false);
        }

        private void HandleRoleList(int expectedGeneration, byte[] bytes)
        {
            IList<TianshuRoleSummary> roles;
            string error;
            if (!TianshuLoginProtocol.TryParseRoleInfoList(bytes, out roles, out error))
            {
                FailForGeneration(expectedGeneration, "角色列表解析失败：" + error);
                return;
            }

            LoginAutomationSettings current;
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration, LoginAutomationState.AwaitingRoleList)) return;
                current = settings.Clone();
            }
            int roleIndex = current.RoleSlot - 1;
            if (roleIndex < 0 || roleIndex >= roles.Count)
            {
                FailForGeneration(expectedGeneration,
                    "账号只返回 " + roles.Count + " 个角色，无法选择配置的第 " + current.RoleSlot + " 个角色。");
                return;
            }
            TianshuRoleSummary selected = roles[roleIndex];
            if (selected.IsPendingDeletion)
            {
                FailForGeneration(expectedGeneration,
                    "配置的第 " + current.RoleSlot + " 个角色处于删除/恢复状态，不能自动进入游戏。");
                return;
            }

            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration, LoginAutomationState.AwaitingRoleList)) return;
                selectedRoleId = selected.CharacterId;
                state = LoginAutomationState.SelectingRole;
                ScheduleLocked(expectedGeneration, 1200,
                    delegate(int value) { SelectRole(value, current.RoleSlot); });
            }
            PublishForGeneration(expectedGeneration, LoginAutomationState.SelectingRole,
                "角色列表已解析，准备选择第 " + current.RoleSlot + " 个角色。", false);
        }

        private void SelectRole(int expectedGeneration, int roleSlot)
        {
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration, LoginAutomationState.SelectingRole)) return;
                state = LoginAutomationState.AwaitingRoleSelection;
                ScheduleLocked(expectedGeneration, 180000, RoleSelectionTimeout);
            }

            string error;
            if (!inputDriver.TrySelectRoleSlot(roleSlot, out error))
            {
                FailForGeneration(expectedGeneration, "无法操作角色选择界面：" + error,
                    LoginAutomationState.AwaitingRoleSelection);
                return;
            }
            lock (syncRoot)
            {
                if (!IsCurrentLocked(expectedGeneration, LoginAutomationState.AwaitingRoleSelection)) return;
            }
            PublishForGeneration(expectedGeneration, LoginAutomationState.AwaitingRoleSelection,
                "已选择第 " + roleSlot + " 个角色并点击进入游戏，等待客户端加载资源。", false);
        }

        private void ServerListTimeout(int expectedGeneration)
        {
            FailForGeneration(expectedGeneration, "入口服未返回线路列表，自动登录超时。", LoginAutomationState.AwaitingServerList);
        }

        private void GameTicketTimeout(int expectedGeneration)
        {
            FailForGeneration(expectedGeneration, "游戏服未观察到票据登录报文，自动登录超时。", LoginAutomationState.AwaitingGameTicket);
        }

        private void RoleListTimeout(int expectedGeneration)
        {
            FailForGeneration(expectedGeneration, "游戏服未返回角色列表，自动登录超时。", LoginAutomationState.AwaitingRoleList);
        }

        private void RoleSelectionTimeout(int expectedGeneration)
        {
            FailForGeneration(expectedGeneration, "客户端加载资源后仍未提交角色选择，自动登录超时。",
                LoginAutomationState.AwaitingRoleSelection);
        }

        private void WorldEntryTimeout(int expectedGeneration)
        {
            FailForGeneration(expectedGeneration, "角色已选择，但服务器未返回地图起点，自动登录超时。",
                LoginAutomationState.AwaitingWorldEntry);
        }

        private void Fail(string message)
        {
            Fail(message, true);
        }

        private void Fail(string message, bool auditAsError)
        {
            int currentGeneration;
            lock (syncRoot)
            {
                generation++;
                currentGeneration = generation;
                state = LoginAutomationState.Failed;
                activePassword = null;
                selectedRoleId = 0;
                restartAfterRefresh = false;
                DisposeTimerLocked();
            }
            PublishForGeneration(currentGeneration, LoginAutomationState.Failed, message, auditAsError);
        }

        private void FailForGeneration(int expectedGeneration, string message)
        {
            FailForGeneration(expectedGeneration, message, null);
        }

        private void FailForGeneration(int expectedGeneration, string message, LoginAutomationState? requiredState)
        {
            lock (syncRoot)
            {
                if (generation != expectedGeneration || (requiredState.HasValue && state != requiredState.Value)) return;
                state = LoginAutomationState.Failed;
                activePassword = null;
                selectedRoleId = 0;
                restartAfterRefresh = false;
                DisposeTimerLocked();
            }
            PublishForGeneration(expectedGeneration, LoginAutomationState.Failed, message, true);
        }

        private void Publish(LoginAutomationState newState, string message, bool error)
        {
            int currentGeneration;
            lock (syncRoot) currentGeneration = generation;
            PublishForGeneration(currentGeneration, newState, message, error);
        }

        private void PublishForGeneration(int expectedGeneration, LoginAutomationState newState, string message, bool error)
        {
            lock (syncRoot)
            {
                if (disposed || generation != expectedGeneration) return;
            }
            service.ReportEngineEvent("AutoLogin", error ? "ERROR" : "INFO", message, null);
            Action<LoginAutomationState, string> handler = StatusChanged;
            if (handler != null) handler(newState, message);
        }

        private void ScheduleLocked(int expectedGeneration, int delayMs, Action<int> callback)
        {
            DisposeTimerLocked();
            timer = new System.Threading.Timer(delegate
            {
                try
                {
                    if (browser.IsDisposed || !browser.IsHandleCreated) return;
                    browser.BeginInvoke(new Action(delegate { callback(expectedGeneration); }));
                }
                catch (ObjectDisposedException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }, null, delayMs, Timeout.Infinite);
        }

        private void DisposeTimerLocked()
        {
            if (timer == null) return;
            timer.Dispose();
            timer = null;
        }

        private bool IsCurrentLocked(int expectedGeneration, LoginAutomationState requiredState)
        {
            return !disposed && generation == expectedGeneration && state == requiredState;
        }

        private static bool IsRunning(LoginAutomationState value)
        {
            return value >= LoginAutomationState.Scheduled && value <= LoginAutomationState.AwaitingWorldEntry;
        }
    }

    internal sealed class FlashLoginInputDriver
    {
        private static readonly Point UsernamePoint = new Point(448, 279);
        private static readonly Point PasswordPoint = new Point(448, 306);
        private static readonly Point LoginButtonPoint = new Point(400, 372);
        private static readonly Point EnterGameButtonPoint = new Point(412, 526);
        private readonly WebBrowser browser;

        public FlashLoginInputDriver(WebBrowser browser)
        {
            this.browser = browser;
        }

        public bool TrySubmitCredentials(string username, string password, out string error)
        {
            error = null;
            if (!CanUsePoint(LoginButtonPoint))
            {
                error = "游戏视口尺寸不足或尚未创建。";
                return false;
            }
            IntPtr usernameWindow;
            if (!TryClick(UsernamePoint, out usernameWindow, out error)) return false;
            ReplaceFocusedText(usernameWindow, username ?? string.Empty);
            IntPtr passwordWindow;
            if (!TryClick(PasswordPoint, out passwordWindow, out error)) return false;
            ReplaceFocusedText(passwordWindow, password ?? string.Empty);
            IntPtr ignored;
            return TryClick(LoginButtonPoint, out ignored, out error);
        }

        public bool TrySelectServerRow(int packetIndex, out string error)
        {
            Point point = GetServerRowPoint(packetIndex);
            if (!CanUsePoint(point))
            {
                error = "目标线路按钮不在游戏视口内。";
                return false;
            }
            IntPtr ignored;
            return TryClick(point, out ignored, out error);
        }

        public bool TrySelectRoleSlot(int roleSlot, out string error)
        {
            if (roleSlot < 1 || roleSlot > 5)
            {
                error = "角色槽位必须在 1 到 5 之间。";
                return false;
            }
            Point rolePoint = GetRoleRowPoint(roleSlot);
            if (!CanUsePoint(rolePoint) || !CanUsePoint(EnterGameButtonPoint))
            {
                error = "角色或进入游戏按钮不在游戏视口内。";
                return false;
            }
            IntPtr ignored;
            if (!TryClick(rolePoint, out ignored, out error)) return false;
            return TryClick(EnterGameButtonPoint, out ignored, out error);
        }

        public static Point GetServerRowPoint(int packetIndex)
        {
            return new Point(400, 208 + packetIndex * 32);
        }

        public static Point GetRoleRowPoint(int roleSlot)
        {
            return new Point(431, 164 + (roleSlot - 1) * 60);
        }

        private bool CanUsePoint(Point point)
        {
            return !browser.IsDisposed && browser.IsHandleCreated && browser.ClientSize.Width > point.X && browser.ClientSize.Height > point.Y;
        }

        private bool TryClick(Point browserPoint, out IntPtr target, out string error)
        {
            target = IntPtr.Zero;
            error = null;
            if (!CanUsePoint(browserPoint))
            {
                error = "游戏视口尚未就绪。";
                return false;
            }
            Form form = browser.FindForm();
            if (form != null && form.IsHandleCreated) NativeMethods.SetForegroundWindow(form.Handle);
            Point screenPoint = browser.PointToScreen(browserPoint);
            NativeMethods.POINT nativePoint = new NativeMethods.POINT { X = screenPoint.X, Y = screenPoint.Y };
            target = NativeMethods.WindowFromPoint(nativePoint);
            if (target == IntPtr.Zero)
            {
                error = "找不到 Flash 输入窗口。";
                return false;
            }
            NativeMethods.SetFocus(target);
            NativeMethods.POINT clientPoint = nativePoint;
            if (!NativeMethods.ScreenToClient(target, ref clientPoint))
            {
                error = "无法换算 Flash 控件坐标。";
                return false;
            }
            IntPtr location = MakeLParam(clientPoint.X, clientPoint.Y);
            NativeMethods.SendMessage(target, NativeMethods.WM_LBUTTONDOWN, new IntPtr(NativeMethods.MK_LBUTTON), location);
            NativeMethods.SendMessage(target, NativeMethods.WM_LBUTTONUP, IntPtr.Zero, location);
            return true;
        }

        private static void ReplaceFocusedText(IntPtr target, string value)
        {
            NativeMethods.SendMessage(target, NativeMethods.WM_KEYDOWN, new IntPtr(NativeMethods.VK_CONTROL), IntPtr.Zero);
            NativeMethods.SendMessage(target, NativeMethods.WM_KEYDOWN, new IntPtr(NativeMethods.VK_A), IntPtr.Zero);
            NativeMethods.SendMessage(target, NativeMethods.WM_KEYUP, new IntPtr(NativeMethods.VK_A), IntPtr.Zero);
            NativeMethods.SendMessage(target, NativeMethods.WM_KEYUP, new IntPtr(NativeMethods.VK_CONTROL), IntPtr.Zero);
            NativeMethods.SendMessage(target, NativeMethods.WM_KEYDOWN, new IntPtr(NativeMethods.VK_BACK), IntPtr.Zero);
            NativeMethods.SendMessage(target, NativeMethods.WM_KEYUP, new IntPtr(NativeMethods.VK_BACK), IntPtr.Zero);
            for (int i = 0; i < value.Length; i++)
            {
                NativeMethods.SendMessage(target, NativeMethods.WM_CHAR, new IntPtr(value[i]), IntPtr.Zero);
            }
        }

        private static IntPtr MakeLParam(int x, int y)
        {
            return new IntPtr(((y & 0xFFFF) << 16) | (x & 0xFFFF));
        }
    }

    public sealed class LoginAutomationControl : UserControl
    {
        private readonly LoginAutomationCoordinator automation;
        private readonly CheckBox enabledBox;
        private readonly TextBox usernameBox;
        private readonly TextBox passwordBox;
        private readonly ComboBox lineBox;
        private readonly ComboBox roleBox;
        private readonly Label passwordStatus;
        private readonly Label statusLabel;

        public LoginAutomationControl(LoginAutomationCoordinator automation)
        {
            if (automation == null) throw new ArgumentNullException("automation");
            this.automation = automation;
            Dock = DockStyle.Fill;
            Padding = new Padding(12);

            TableLayoutPanel table = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 8
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            enabledBox = new CheckBox { Text = "启用页面加载后自动登录", AutoSize = true };
            usernameBox = new TextBox { Dock = DockStyle.Fill, MaxLength = 128 };
            passwordBox = new TextBox { Dock = DockStyle.Fill, MaxLength = 256, UseSystemPasswordChar = true };
            lineBox = new ComboBox { Dock = DockStyle.Left, Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
            lineBox.Items.AddRange(new object[] { "一线", "二线", "三线", "四线" });
            roleBox = new ComboBox { Dock = DockStyle.Left, Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
            roleBox.Items.AddRange(new object[] { "第1个角色", "第2个角色", "第3个角色", "第4个角色", "第5个角色" });
            passwordStatus = new Label { AutoSize = true, ForeColor = Color.DimGray };
            statusLabel = new Label { AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = Color.DarkSlateBlue };

            FlowLayoutPanel buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            Button saveButton = new Button { Text = "保存配置", AutoSize = true };
            Button loginButton = new Button { Text = "保存并登录", AutoSize = true };
            Button refreshButton = new Button { Text = "刷新并登录", AutoSize = true };
            Button stopButton = new Button { Text = "停止", AutoSize = true };
            Button clearButton = new Button { Text = "清除密码", AutoSize = true };
            saveButton.Click += delegate { Save(false); };
            loginButton.Click += delegate { Save(true); };
            refreshButton.Click += delegate { if (Save(false)) automation.RefreshPage(true); };
            stopButton.Click += delegate { automation.Stop(); };
            clearButton.Click += OnClearPassword;
            buttons.Controls.Add(saveButton);
            buttons.Controls.Add(loginButton);
            buttons.Controls.Add(refreshButton);
            buttons.Controls.Add(stopButton);
            buttons.Controls.Add(clearButton);

            table.Controls.Add(enabledBox, 1, 0);
            table.Controls.Add(new Label { Text = "账号", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            table.Controls.Add(usernameBox, 1, 1);
            table.Controls.Add(new Label { Text = "密码", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
            table.Controls.Add(passwordBox, 1, 2);
            table.Controls.Add(new Label { Text = "", AutoSize = true }, 0, 3);
            table.Controls.Add(passwordStatus, 1, 3);
            table.Controls.Add(new Label { Text = "线路", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 4);
            table.Controls.Add(lineBox, 1, 4);
            table.Controls.Add(new Label { Text = "角色", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 5);
            table.Controls.Add(roleBox, 1, 5);
            table.Controls.Add(new Label { Text = "", AutoSize = true }, 0, 6);
            table.Controls.Add(buttons, 1, 6);
            table.Controls.Add(new Label { Text = "状态", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 7);
            table.Controls.Add(statusLabel, 1, 7);

            Label securityNote = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                MaximumSize = new Size(560, 0),
                ForeColor = Color.DimGray,
                Text = "角色按服务器列表中的界面顺序选择。密码框始终遮蔽显示；留空保存表示保留旧密码。本地密码使用 Windows DPAPI 加密，登录抓包中的账号和密码会在入库前脱敏。"
            };
            Controls.Add(securityNote);
            Controls.Add(table);
            securityNote.BringToFront();

            automation.StatusChanged += OnStatusChanged;
            LoadSettings();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) automation.StatusChanged -= OnStatusChanged;
            base.Dispose(disposing);
        }

        private void LoadSettings()
        {
            LoginAutomationSettings current = automation.Settings;
            enabledBox.Checked = current.Enabled;
            usernameBox.Text = current.Username;
            lineBox.SelectedIndex = Math.Max(0, Math.Min(3, current.LineNumber - 1));
            roleBox.SelectedIndex = Math.Max(0, Math.Min(4, current.RoleSlot - 1));
            passwordBox.Text = string.Empty;
            passwordStatus.Text = current.HasSavedPassword ? "已保存加密密码；留空不会覆盖。" : "尚未保存密码。";
            statusLabel.Text = "未运行";
        }

        private bool Save(bool start)
        {
            try
            {
                string newPassword = passwordBox.Text.Length == 0 ? null : passwordBox.Text;
                automation.SaveConfiguration(enabledBox.Checked, usernameBox.Text, lineBox.SelectedIndex + 1,
                    roleBox.SelectedIndex + 1, newPassword);
                passwordBox.Text = string.Empty;
                passwordStatus.Text = automation.Settings.HasSavedPassword ? "已保存加密密码；留空不会覆盖。" : "尚未保存密码。";
                if (start) automation.StartNow();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "自动登录", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private void OnClearPassword(object sender, EventArgs e)
        {
            if (MessageBox.Show(this, "确定清除当前 Windows 用户保存的登录密码吗？", "自动登录",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                automation.ClearSavedPassword();
                passwordBox.Text = string.Empty;
                passwordStatus.Text = "尚未保存密码。";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "自动登录", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnStatusChanged(LoginAutomationState newState, string message)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<LoginAutomationState, string>(OnStatusChanged), newState, message);
                return;
            }
            statusLabel.ForeColor = newState == LoginAutomationState.Failed ? Color.DarkRed :
                newState == LoginAutomationState.Completed ? Color.DarkGreen : Color.DarkSlateBlue;
            statusLabel.Text = message;
        }
    }
}
