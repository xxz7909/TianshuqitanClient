using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace TianshuQitanLauncher.Protocol
{
    public sealed class ProtocolWorkbenchControl : UserControl
    {
        private const int MaximumUiChunks = 250000;
        private const int MaximumUiFrames = 50000;
        private readonly ProtocolWorkbenchService service;
        private readonly AudioFilterProxy audioFilterProxy;
        private readonly LoginAutomationCoordinator loginAutomation;
        private readonly BountyAutomationCoordinator bountyAutomation;
        private readonly DonationAutomationCoordinator donationAutomation;
        private readonly RunLoopAutomationCoordinator runLoopAutomation;
        private readonly MountainClimbAutomationCoordinator mountainClimbAutomation;
        private readonly MapTeleportAutomationCoordinator mapTeleportAutomation;
        private readonly NpcCatalogHarvesterCoordinator npcCatalogHarvester;
        private readonly ClientLogHub clientLogHub;
        private readonly List<TransportChunk> chunks = new List<TransportChunk>();
        private readonly List<ProtocolFrame> frames = new List<ProtocolFrame>();
        private readonly List<StateTransition> confirmedTransitions = new List<StateTransition>();
        private readonly Dictionary<long, ConnectionSession> connections = new Dictionary<long, ConnectionSession>();
        private readonly ToolStripButton activeButton;
        private readonly ToolStripButton operationButton;
        private readonly ToolStripButton operationStartStopButton;
        private readonly ToolStripButton audioFilterButton;
        private readonly ToolStripTextBox databaseLabel;
        private readonly DataGridView packetGrid;
        private readonly RichTextBox originalHex;
        private readonly RichTextBox effectiveHex;
        private readonly DataGridView connectionGrid;
        private readonly DataGridView frameGrid;
        private readonly TreeView fieldTree;
        private readonly DataGridView stateGrid;
        private readonly DataGridView ruleGrid;
        private readonly BindingList<PacketRule> ruleBinding;
        private readonly RichTextBox protocolEditor;
        private readonly RichTextBox luaEditor;
        private readonly DataGridView resultGrid;
        private readonly RichTextBox eventLog;
        private readonly ClientLogControl clientLogControl;
        private readonly SplitContainer logSplitContainer;
        private readonly ToolStripButton logPaneButton;
        private readonly TabControl featureTabs;
        private readonly TabControl captureTabs;
        private readonly Timer refreshTimer;
        private int expandedLogHeight = 200;
        private bool logSplitterInitialized;

        public ProtocolWorkbenchControl(ProtocolWorkbenchService service)
            : this(service, null, null, null, null, null)
        {
        }

        public ProtocolWorkbenchControl(ProtocolWorkbenchService service, LoginAutomationCoordinator loginAutomation)
            : this(service, loginAutomation, null, null, null, null)
        {
        }

        public ProtocolWorkbenchControl(ProtocolWorkbenchService service, LoginAutomationCoordinator loginAutomation, AudioFilterProxy audioFilterProxy)
            : this(service, loginAutomation, audioFilterProxy, null, null, null)
        {
        }

        public ProtocolWorkbenchControl(ProtocolWorkbenchService service, LoginAutomationCoordinator loginAutomation,
            AudioFilterProxy audioFilterProxy, BountyAutomationCoordinator bountyAutomation)
            : this(service, loginAutomation, audioFilterProxy, bountyAutomation, null, null)
        {
        }

        public ProtocolWorkbenchControl(ProtocolWorkbenchService service, LoginAutomationCoordinator loginAutomation,
            AudioFilterProxy audioFilterProxy, BountyAutomationCoordinator bountyAutomation,
            MultiAccountManager multiAccountManager)
            : this(service, loginAutomation, audioFilterProxy, bountyAutomation, null, multiAccountManager)
        {
        }

        public ProtocolWorkbenchControl(ProtocolWorkbenchService service, LoginAutomationCoordinator loginAutomation,
            AudioFilterProxy audioFilterProxy, BountyAutomationCoordinator bountyAutomation,
            DonationAutomationCoordinator donationAutomation, MultiAccountManager multiAccountManager)
            : this(service, loginAutomation, audioFilterProxy, bountyAutomation, donationAutomation, null, multiAccountManager)
        {
        }

        public ProtocolWorkbenchControl(ProtocolWorkbenchService service, LoginAutomationCoordinator loginAutomation,
            AudioFilterProxy audioFilterProxy, BountyAutomationCoordinator bountyAutomation,
            DonationAutomationCoordinator donationAutomation, RunLoopAutomationCoordinator runLoopAutomation,
            MultiAccountManager multiAccountManager)
            : this(service, loginAutomation, audioFilterProxy, bountyAutomation, donationAutomation, runLoopAutomation,
                null, multiAccountManager)
        {
        }

        public ProtocolWorkbenchControl(ProtocolWorkbenchService service, LoginAutomationCoordinator loginAutomation,
            AudioFilterProxy audioFilterProxy, BountyAutomationCoordinator bountyAutomation,
            DonationAutomationCoordinator donationAutomation, RunLoopAutomationCoordinator runLoopAutomation,
            MountainClimbAutomationCoordinator mountainClimbAutomation, MultiAccountManager multiAccountManager)
            : this(service, loginAutomation, audioFilterProxy, bountyAutomation, donationAutomation, runLoopAutomation,
                mountainClimbAutomation, null, multiAccountManager)
        {
        }

        public ProtocolWorkbenchControl(ProtocolWorkbenchService service, LoginAutomationCoordinator loginAutomation,
            AudioFilterProxy audioFilterProxy, BountyAutomationCoordinator bountyAutomation,
            DonationAutomationCoordinator donationAutomation, RunLoopAutomationCoordinator runLoopAutomation,
            MountainClimbAutomationCoordinator mountainClimbAutomation,
            MapTeleportAutomationCoordinator mapTeleportAutomation, MultiAccountManager multiAccountManager)
            : this(service, loginAutomation, audioFilterProxy, bountyAutomation, donationAutomation, runLoopAutomation,
                mountainClimbAutomation, mapTeleportAutomation, multiAccountManager, new ClientLogHub())
        {
        }

        public ProtocolWorkbenchControl(ProtocolWorkbenchService service, LoginAutomationCoordinator loginAutomation,
            AudioFilterProxy audioFilterProxy, BountyAutomationCoordinator bountyAutomation,
            DonationAutomationCoordinator donationAutomation, RunLoopAutomationCoordinator runLoopAutomation,
            MountainClimbAutomationCoordinator mountainClimbAutomation,
            MapTeleportAutomationCoordinator mapTeleportAutomation,
            NpcCatalogHarvesterCoordinator npcCatalogHarvester, MultiAccountManager multiAccountManager)
            : this(service, loginAutomation, audioFilterProxy, bountyAutomation, donationAutomation, runLoopAutomation,
                mountainClimbAutomation, mapTeleportAutomation, npcCatalogHarvester, multiAccountManager, new ClientLogHub())
        {
        }

        public ProtocolWorkbenchControl(ProtocolWorkbenchService service, LoginAutomationCoordinator loginAutomation,
            AudioFilterProxy audioFilterProxy, BountyAutomationCoordinator bountyAutomation,
            DonationAutomationCoordinator donationAutomation, RunLoopAutomationCoordinator runLoopAutomation,
            MountainClimbAutomationCoordinator mountainClimbAutomation,
            MapTeleportAutomationCoordinator mapTeleportAutomation, MultiAccountManager multiAccountManager,
            ClientLogHub clientLogHub)
            : this(service, loginAutomation, audioFilterProxy, bountyAutomation, donationAutomation, runLoopAutomation,
                mountainClimbAutomation, mapTeleportAutomation, null, multiAccountManager, clientLogHub)
        {
        }

        public ProtocolWorkbenchControl(ProtocolWorkbenchService service, LoginAutomationCoordinator loginAutomation,
            AudioFilterProxy audioFilterProxy, BountyAutomationCoordinator bountyAutomation,
            DonationAutomationCoordinator donationAutomation, RunLoopAutomationCoordinator runLoopAutomation,
            MountainClimbAutomationCoordinator mountainClimbAutomation,
            MapTeleportAutomationCoordinator mapTeleportAutomation,
            NpcCatalogHarvesterCoordinator npcCatalogHarvester, MultiAccountManager multiAccountManager,
            ClientLogHub clientLogHub)
        {
            if (service == null) throw new ArgumentNullException("service");
            if (clientLogHub == null) throw new ArgumentNullException("clientLogHub");
            this.service = service;
            this.audioFilterProxy = audioFilterProxy;
            this.loginAutomation = loginAutomation;
            this.bountyAutomation = bountyAutomation;
            this.donationAutomation = donationAutomation;
            this.runLoopAutomation = runLoopAutomation;
            this.mountainClimbAutomation = mountainClimbAutomation;
            this.mapTeleportAutomation = mapTeleportAutomation;
            this.npcCatalogHarvester = npcCatalogHarvester;
            this.clientLogHub = clientLogHub;
            Dock = DockStyle.Fill;
            BackColor = SystemColors.Control;

            ToolStrip toolStrip = new ToolStrip();
            toolStrip.GripStyle = ToolStripGripStyle.Hidden;
            activeButton = new ToolStripButton();
            activeButton.CheckOnClick = true;
            activeButton.Checked = service.ActiveMode;
            activeButton.Click += OnActiveClick;
            ApplyActiveAppearance(service.ActiveMode);
            if (audioFilterProxy != null)
            {
                audioFilterButton = new ToolStripButton();
                audioFilterButton.CheckOnClick = true;
                audioFilterButton.Checked = audioFilterProxy.FilteringEnabled;
                audioFilterButton.Click += delegate { audioFilterProxy.FilteringEnabled = audioFilterButton.Checked; };
                ApplyAudioFilterAppearance();
            }

            ToolStripButton bypassButton = new ToolStripButton("紧急旁路");
            bypassButton.ToolTipText = "Ctrl+Shift+F12";
            bypassButton.Click += delegate { service.EmergencyBypass(); };
            ToolStripButton markerButton = new ToolStripButton("标记");
            markerButton.Click += delegate { service.AddMarker("Manual marker " + DateTime.Now.ToString("HH:mm:ss.fff"), null); };
            operationButton = new ToolStripButton { CheckOnClick = true };
            operationButton.Click += delegate { service.SetOperationRecorderEnabled(operationButton.Checked, true); };
            operationStartStopButton = new ToolStripButton();
            operationStartStopButton.ToolTipText = "Ctrl+F8";
            operationStartStopButton.Click += delegate
            {
                try { service.ToggleAtomicOperationRecording(); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "原子操作", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            };
            ToolStripButton operationStepButton = new ToolStripButton("步骤");
            operationStepButton.ToolTipText = "Ctrl+F9";
            operationStepButton.Click += delegate
            {
                try { service.MarkAtomicOperationStep(null, null); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "原子操作", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            };
            ApplyOperationAppearance(service.OperationRecorder.State);
            ToolStripButton reloadButton = new ToolStripButton("重载");
            reloadButton.Click += delegate { service.ReloadDefinitions(); LoadEditors(); };
            ToolStripButton runButton = new ToolStripButton("运行 main");
            runButton.Click += delegate { service.RunScenario("main"); };
            ToolStripButton replayButton = new ToolStripButton("离线回放");
            replayButton.Click += delegate
            {
                try
                {
                    OfflineReplayResult result = service.ReplayCurrentSession();
                    eventLog.AppendText("Replay signatures: " + string.Join(", ", result.SignatureCounts.OrderByDescending(item => item.Value).Take(20).Select(item => item.Key + "×" + item.Value).ToArray()) + Environment.NewLine);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Replay failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            ToolStripButton exportButton = new ToolStripButton("导出 PCAPNG");
            exportButton.Click += OnExportClick;
            ToolStripButton clearDisplayCacheButton = new ToolStripButton("清空显示缓存");
            clearDisplayCacheButton.ToolTipText = "释放数据包、协议帧和状态记录的界面内存；不会删除 SQLite 抓包记录，也不会停止抓包。";
            clearDisplayCacheButton.Click += delegate { ClearDisplayCache(); };
            logPaneButton = new ToolStripButton("日志") { CheckOnClick = true, Checked = true };
            logPaneButton.ToolTipText = "显示或折叠固定在工作台底部的统一运行日志。";
            logPaneButton.Click += delegate { SetLogPaneVisible(logPaneButton.Checked); };
            ToolStripButton renameDatabaseButton = new ToolStripButton("会话命名");
            renameDatabaseButton.Click += delegate { RenameCurrentDatabase(); };
            ToolStripButton copyDatabaseButton = new ToolStripButton("复制路径");
            copyDatabaseButton.Click += delegate { CopyCurrentDatabasePath(); };
            databaseLabel = new ToolStripTextBox
            {
                Text = Path.GetFileName(service.DatabasePath),
                ReadOnly = true,
                AutoSize = false,
                Width = 230,
                BorderStyle = BorderStyle.FixedSingle
            };
            databaseLabel.ToolTipText = service.DatabasePath;

            toolStrip.Items.Add(activeButton);
            if (audioFilterButton != null) toolStrip.Items.Add(audioFilterButton);
            toolStrip.Items.Add(bypassButton);
            toolStrip.Items.Add(markerButton);
            toolStrip.Items.Add(new ToolStripSeparator());
            toolStrip.Items.Add(operationButton);
            toolStrip.Items.Add(operationStartStopButton);
            toolStrip.Items.Add(operationStepButton);
            toolStrip.Items.Add(new ToolStripSeparator());
            toolStrip.Items.Add(reloadButton);
            toolStrip.Items.Add(runButton);
            toolStrip.Items.Add(replayButton);
            toolStrip.Items.Add(exportButton);
            toolStrip.Items.Add(clearDisplayCacheButton);
            toolStrip.Items.Add(logPaneButton);
            toolStrip.Items.Add(new ToolStripSeparator());
            toolStrip.Items.Add(renameDatabaseButton);
            toolStrip.Items.Add(copyDatabaseButton);
            toolStrip.Items.Add(databaseLabel);

            featureTabs = new TabControl
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                SizeMode = TabSizeMode.Fixed,
                ItemSize = new Size(86, 26)
            };

            captureTabs = new TabControl
            {
                Dock = DockStyle.Fill,
                Multiline = false,
                SizeMode = TabSizeMode.Normal
            };

            packetGrid = CreateReadOnlyGrid();
            packetGrid.MultiSelect = true;
            packetGrid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
            packetGrid.VirtualMode = true;
            packetGrid.CellValueNeeded += OnPacketCellValueNeeded;
            packetGrid.SelectionChanged += OnPacketSelectionChanged;
            packetGrid.CellMouseDown += OnPacketCellMouseDown;
            packetGrid.KeyDown += OnPacketGridKeyDown;
            AddColumn(packetGrid, "时间", 100);
            AddColumn(packetGrid, "连接", 55);
            AddColumn(packetGrid, "方向", 45);
            AddColumn(packetGrid, "操作", 65);
            AddColumn(packetGrid, "类型", 70);
            AddColumn(packetGrid, "动作", 65);
            AddColumn(packetGrid, "长度", 55);
            AddColumn(packetGrid, "Hex/ASCII", 250);
            ConfigurePacketContextMenu();
            originalHex = CreateHexBox();
            effectiveHex = CreateHexBox();
            TabPage packetPage = new TabPage("数据包");
            packetPage.Controls.Add(CreatePacketLayout());

            connectionGrid = CreateReadOnlyGrid();
            AddColumn(connectionGrid, "ID", 45);
            AddColumn(connectionGrid, "Socket", 75);
            AddColumn(connectionGrid, "类型", 75);
            AddColumn(connectionGrid, "状态", 85);
            AddColumn(connectionGrid, "本地", 130);
            AddColumn(connectionGrid, "远端", 150);
            AddColumn(connectionGrid, "C→S", 70);
            AddColumn(connectionGrid, "S→C", 70);
            AddColumn(connectionGrid, "错误", 65);
            TabPage connectionPage = new TabPage("连接");
            connectionPage.Controls.Add(connectionGrid);

            frameGrid = CreateReadOnlyGrid();
            frameGrid.MultiSelect = true;
            frameGrid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
            AddColumn(frameGrid, "时间", 100);
            AddColumn(frameGrid, "连接", 55);
            AddColumn(frameGrid, "方向", 45);
            AddColumn(frameGrid, "Opcode", 75);
            AddColumn(frameGrid, "长度", 55);
            AddColumn(frameGrid, "签名", 180);
            frameGrid.SelectionChanged += OnFrameSelectionChanged;
            fieldTree = new TreeView { Dock = DockStyle.Fill };
            TabPage framePage = new TabPage("协议/字段");
            framePage.Controls.Add(CreateFrameLayout());

            captureTabs.TabPages.Add(packetPage);
            captureTabs.TabPages.Add(connectionPage);
            captureTabs.TabPages.Add(framePage);
            TabPage capturePage = new TabPage("抓包");
            capturePage.Controls.Add(captureTabs);

            stateGrid = CreateReadOnlyGrid();
            AddColumn(stateGrid, "时间", 100);
            AddColumn(stateGrid, "连接", 55);
            AddColumn(stateGrid, "From", 110);
            AddColumn(stateGrid, "To", 110);
            AddColumn(stateGrid, "触发", 180);
            AddColumn(stateGrid, "确认", 55);
            TabPage statePage = new TabPage("状态图");
            statePage.Controls.Add(stateGrid);

            AtomicOperationControl atomicOperationControl = new AtomicOperationControl(service);
            TabPage operationPage = new TabPage("原子操作");
            operationPage.Controls.Add(atomicOperationControl);

            TabPage loginPage = null;
            if (loginAutomation != null)
            {
                LoginAutomationControl loginControl = new LoginAutomationControl(loginAutomation);
                loginPage = new TabPage("自动登录");
                loginPage.Controls.Add(loginControl);
            }

            TabPage multiAccountPage = null;
            if (multiAccountManager != null)
            {
                MultiAccountControl multiAccountControl = new MultiAccountControl(multiAccountManager, clientLogHub);
                multiAccountPage = new TabPage("多开管理");
                multiAccountPage.Controls.Add(multiAccountControl);
            }

            TabPage bountyPage = null;
            if (bountyAutomation != null)
            {
                BountyAutomationControl bountyControl = new BountyAutomationControl(bountyAutomation);
                bountyPage = new TabPage("自动除暴");
                bountyPage.Controls.Add(bountyControl);
            }

            TabPage donationPage = null;
            if (donationAutomation != null)
            {
                DonationAutomationControl donationControl = new DonationAutomationControl(donationAutomation);
                donationPage = new TabPage("自动捐献");
                donationPage.Controls.Add(donationControl);
            }

            TabPage runLoopPage = null;
            if (runLoopAutomation != null)
            {
                RunLoopAutomationControl runLoopControl = new RunLoopAutomationControl(runLoopAutomation);
                runLoopPage = new TabPage("自动跑环");
                runLoopPage.Controls.Add(runLoopControl);
            }

            TabPage mountainClimbPage = null;
            if (mountainClimbAutomation != null)
            {
                MountainClimbAutomationControl mountainClimbControl = new MountainClimbAutomationControl(mountainClimbAutomation);
                mountainClimbPage = new TabPage("登山爬塔");
                mountainClimbPage.Controls.Add(mountainClimbControl);
            }

            TabPage mapTeleportPage = null;
            if (mapTeleportAutomation != null)
            {
                MapTeleportAutomationControl mapTeleportControl = new MapTeleportAutomationControl(mapTeleportAutomation);
                mapTeleportPage = new TabPage("地图传送");
                mapTeleportPage.Controls.Add(mapTeleportControl);
            }

            TabPage npcCatalogPage = null;
            if (npcCatalogHarvester != null)
            {
                NpcCatalogHarvesterControl npcCatalogControl = new NpcCatalogHarvesterControl(npcCatalogHarvester);
                npcCatalogPage = new TabPage("NPC 目录采集");
                npcCatalogPage.Controls.Add(npcCatalogControl);
            }

            ruleBinding = new BindingList<PacketRule>(new List<PacketRule>(service.Rules.Rules));
            ruleGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoGenerateColumns = true,
                DataSource = ruleBinding,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = true,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells
            };
            TabPage rulePage = new TabPage("规则");
            rulePage.Controls.Add(CreateRuleLayout());

            protocolEditor = CreateEditor();
            TabPage definitionPage = new TabPage("协议定义");
            definitionPage.Controls.Add(CreateEditorLayout(protocolEditor, OnSaveProtocol));

            luaEditor = CreateEditor();
            TabPage luaPage = new TabPage("Lua 场景");
            luaPage.Controls.Add(CreateEditorLayout(luaEditor, OnSaveLua));

            resultGrid = CreateReadOnlyGrid();
            AddColumn(resultGrid, "场景", 120);
            AddColumn(resultGrid, "结果", 70);
            AddColumn(resultGrid, "开始", 130);
            AddColumn(resultGrid, "耗时(ms)", 75);
            AddColumn(resultGrid, "消息", 260);
            TabPage resultPage = new TabPage("测试结果");
            resultPage.Controls.Add(resultGrid);

            eventLog = CreateEditor();
            eventLog.ReadOnly = true;
            eventLog.BackColor = Color.White;
            TabPage logPage = new TabPage("审计日志");
            logPage.Controls.Add(eventLog);

            featureTabs.TabPages.Add(capturePage);
            featureTabs.TabPages.Add(statePage);
            if (multiAccountPage != null) featureTabs.TabPages.Add(multiAccountPage);
            if (loginPage != null) featureTabs.TabPages.Add(loginPage);
            if (bountyPage != null) featureTabs.TabPages.Add(bountyPage);
            if (donationPage != null) featureTabs.TabPages.Add(donationPage);
            if (runLoopPage != null) featureTabs.TabPages.Add(runLoopPage);
            if (mountainClimbPage != null) featureTabs.TabPages.Add(mountainClimbPage);
            if (mapTeleportPage != null) featureTabs.TabPages.Add(mapTeleportPage);
            if (npcCatalogPage != null) featureTabs.TabPages.Add(npcCatalogPage);
            featureTabs.TabPages.Add(operationPage);
            featureTabs.TabPages.Add(rulePage);
            featureTabs.TabPages.Add(definitionPage);
            featureTabs.TabPages.Add(luaPage);
            featureTabs.TabPages.Add(resultPage);
            featureTabs.TabPages.Add(logPage);

            clientLogControl = new ClientLogControl(clientLogHub);
            logSplitContainer = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                FixedPanel = FixedPanel.Panel2,
                Panel1MinSize = 120,
                Panel2MinSize = 120,
                SplitterWidth = 5
            };
            logSplitContainer.Panel1.Controls.Add(featureTabs);
            logSplitContainer.Panel2.Controls.Add(clientLogControl);
            logSplitContainer.SizeChanged += OnLogSplitSizeChanged;

            Controls.Add(logSplitContainer);
            Controls.Add(toolStrip);
            toolStrip.Dock = DockStyle.Top;

            service.ConnectionChanged += OnConnectionChanged;
            service.ChunkCaptured += OnChunkCaptured;
            service.FrameCaptured += OnFrameCaptured;
            service.StateChanged += OnStateChanged;
            service.EventRaised += OnEventRaised;
            service.ScenarioCompleted += OnScenarioCompleted;
            service.ActiveModeChanged += OnActiveModeChanged;
            service.OperationRecordingStateChanged += OnOperationRecordingStateChanged;
            service.SessionDatabaseRenamed += OnSessionDatabaseRenamed;
            SubscribeClientLogSources();

            refreshTimer = new Timer();
            refreshTimer.Interval = 500;
            refreshTimer.Tick += OnRefreshTimer;
            refreshTimer.Start();
            LoadEditors();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                refreshTimer.Stop();
                refreshTimer.Dispose();
                service.ConnectionChanged -= OnConnectionChanged;
                service.ChunkCaptured -= OnChunkCaptured;
                service.FrameCaptured -= OnFrameCaptured;
                service.StateChanged -= OnStateChanged;
                service.EventRaised -= OnEventRaised;
                service.ScenarioCompleted -= OnScenarioCompleted;
                service.ActiveModeChanged -= OnActiveModeChanged;
                service.OperationRecordingStateChanged -= OnOperationRecordingStateChanged;
                service.SessionDatabaseRenamed -= OnSessionDatabaseRenamed;
                UnsubscribeClientLogSources();
                logSplitContainer.SizeChanged -= OnLogSplitSizeChanged;
            }
            base.Dispose(disposing);
        }

        private void SubscribeClientLogSources()
        {
            if (loginAutomation != null) loginAutomation.StatusChanged += OnLoginAutomationStatusChanged;
            if (bountyAutomation != null) bountyAutomation.StatusChanged += OnBountyAutomationStatusChanged;
            if (donationAutomation != null) donationAutomation.StatusChanged += OnDonationAutomationStatusChanged;
            if (runLoopAutomation != null) runLoopAutomation.StatusChanged += OnRunLoopAutomationStatusChanged;
            if (mountainClimbAutomation != null) mountainClimbAutomation.StatusChanged += OnMountainClimbAutomationStatusChanged;
            if (mapTeleportAutomation != null) mapTeleportAutomation.StatusChanged += OnMapTeleportAutomationStatusChanged;
            if (npcCatalogHarvester != null) npcCatalogHarvester.StatusChanged += OnNpcCatalogHarvesterStatusChanged;
            if (audioFilterProxy != null)
            {
                audioFilterProxy.StatusChanged += OnAudioFilterStatusChanged;
                clientLogHub.Publish("BGM", ClientLogLevel.Info,
                    audioFilterProxy.FilteringEnabled ? "启用" : "旁路", audioFilterProxy.LastStatus);
            }
        }

        private void UnsubscribeClientLogSources()
        {
            if (loginAutomation != null) loginAutomation.StatusChanged -= OnLoginAutomationStatusChanged;
            if (bountyAutomation != null) bountyAutomation.StatusChanged -= OnBountyAutomationStatusChanged;
            if (donationAutomation != null) donationAutomation.StatusChanged -= OnDonationAutomationStatusChanged;
            if (runLoopAutomation != null) runLoopAutomation.StatusChanged -= OnRunLoopAutomationStatusChanged;
            if (mountainClimbAutomation != null) mountainClimbAutomation.StatusChanged -= OnMountainClimbAutomationStatusChanged;
            if (mapTeleportAutomation != null) mapTeleportAutomation.StatusChanged -= OnMapTeleportAutomationStatusChanged;
            if (npcCatalogHarvester != null) npcCatalogHarvester.StatusChanged -= OnNpcCatalogHarvesterStatusChanged;
            if (audioFilterProxy != null) audioFilterProxy.StatusChanged -= OnAudioFilterStatusChanged;
        }

        private void OnLoginAutomationStatusChanged(LoginAutomationState state, string message)
        {
            PublishAutomationLog("自动登录", state.ToString(), message);
        }

        private void OnBountyAutomationStatusChanged(BountyAutomationState state, string message)
        {
            PublishAutomationLog("自动除暴", state.ToString(), message);
        }

        private void OnDonationAutomationStatusChanged(DonationAutomationState state, string message)
        {
            PublishAutomationLog("自动捐献", state.ToString(), message);
        }

        private void OnRunLoopAutomationStatusChanged(RunLoopAutomationState state, string message)
        {
            PublishAutomationLog("自动跑环", state.ToString(), message);
        }

        private void OnMountainClimbAutomationStatusChanged(MountainClimbAutomationState state, string message)
        {
            PublishAutomationLog("登山爬塔", state.ToString(), message);
        }

        private void OnMapTeleportAutomationStatusChanged(MapTeleportAutomationState state, string message)
        {
            PublishAutomationLog("地图传送", state.ToString(), message);
        }

        private void OnNpcCatalogHarvesterStatusChanged(NpcCatalogHarvesterState state, string message)
        {
            PublishAutomationLog("NPC目录采集", state.ToString(), message);
        }

        private void OnAudioFilterStatusChanged(string message)
        {
            clientLogHub.Publish("BGM", ClientLogLevel.Info,
                audioFilterProxy != null && audioFilterProxy.FilteringEnabled ? "启用" : "旁路", message);
        }

        private void PublishAutomationLog(string module, string state, string message)
        {
            ClientLogLevel level = string.Equals(state, "Failed", StringComparison.OrdinalIgnoreCase)
                ? ClientLogLevel.Error
                : (string.Equals(state, "Stopped", StringComparison.OrdinalIgnoreCase)
                    ? ClientLogLevel.Warning
                    : ClientLogLevel.Info);
            clientLogHub.Publish(module, level, state, message);
        }

        private void OnLogSplitSizeChanged(object sender, EventArgs e)
        {
            if (logSplitterInitialized || logSplitContainer.Panel2Collapsed) return;
            int available = logSplitContainer.ClientSize.Height - logSplitContainer.SplitterWidth;
            if (available < logSplitContainer.Panel1MinSize + logSplitContainer.Panel2MinSize) return;
            int desiredHeight = Math.Min(expandedLogHeight, available - logSplitContainer.Panel1MinSize);
            logSplitContainer.SplitterDistance = available - Math.Max(logSplitContainer.Panel2MinSize, desiredHeight);
            logSplitterInitialized = true;
        }

        private void SetLogPaneVisible(bool visible)
        {
            if (visible)
            {
                logSplitContainer.Panel2Collapsed = false;
                logSplitterInitialized = false;
                OnLogSplitSizeChanged(logSplitContainer, EventArgs.Empty);
            }
            else
            {
                if (!logSplitContainer.Panel2Collapsed && logSplitContainer.Panel2.Height >= logSplitContainer.Panel2MinSize)
                    expandedLogHeight = logSplitContainer.Panel2.Height;
                logSplitContainer.Panel2Collapsed = true;
            }
            logPaneButton.Checked = visible;
        }

        private Control CreatePacketLayout()
        {
            SplitContainer details = new SplitContainer();
            details.Dock = DockStyle.Fill;
            details.Orientation = Orientation.Vertical;
            details.SplitterDistance = 420;
            details.Panel1.Controls.Add(WrapWithLabel("Original", originalHex));
            details.Panel2.Controls.Add(WrapWithLabel("Effective", effectiveHex));

            SplitContainer layout = new SplitContainer();
            layout.Dock = DockStyle.Fill;
            layout.Orientation = Orientation.Horizontal;
            layout.SplitterDistance = 310;
            layout.Panel1.Controls.Add(packetGrid);
            layout.Panel2.Controls.Add(details);
            return layout;
        }

        private Control CreateFrameLayout()
        {
            SplitContainer layout = new SplitContainer();
            layout.Dock = DockStyle.Fill;
            layout.Orientation = Orientation.Horizontal;
            layout.SplitterDistance = 300;
            layout.Panel1.Controls.Add(frameGrid);
            layout.Panel2.Controls.Add(fieldTree);
            return layout;
        }

        private Control CreateRuleLayout()
        {
            ToolStrip tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
            ToolStripButton add = new ToolStripButton("新增");
            add.Click += delegate
            {
                ruleBinding.Add(new PacketRule
                {
                    ConnectionKind = ConnectionKind.Game,
                    Priority = ruleBinding.Count * 10,
                    Name = "Rule " + (ruleBinding.Count + 1)
                });
            };
            ToolStripButton remove = new ToolStripButton("删除所选");
            remove.Click += delegate
            {
                PacketRule selectedRule = ruleGrid.CurrentRow == null ? null : ruleGrid.CurrentRow.DataBoundItem as PacketRule;
                if (selectedRule != null)
                {
                    ruleBinding.Remove(selectedRule);
                }
            };
            ToolStripButton save = new ToolStripButton("保存并重载");
            save.Click += delegate
            {
                ruleGrid.EndEdit();
                service.Rules.Save(ruleBinding.ToList());
                service.ReloadDefinitions();
            };
            tools.Items.Add(add);
            tools.Items.Add(remove);
            tools.Items.Add(save);

            Panel panel = new Panel { Dock = DockStyle.Fill };
            panel.Controls.Add(ruleGrid);
            panel.Controls.Add(tools);
            tools.Dock = DockStyle.Top;
            return panel;
        }

        private Control CreateEditorLayout(RichTextBox editor, EventHandler saveHandler)
        {
            ToolStrip tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
            ToolStripButton save = new ToolStripButton("保存并重载");
            save.Click += saveHandler;
            tools.Items.Add(save);
            Panel panel = new Panel { Dock = DockStyle.Fill };
            panel.Controls.Add(editor);
            panel.Controls.Add(tools);
            tools.Dock = DockStyle.Top;
            return panel;
        }

        private void OnActiveClick(object sender, EventArgs e)
        {
            service.SetActiveMode(activeButton.Checked, true);
        }

        private void OnOperationRecordingStateChanged(OperationRecordingState state)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<OperationRecordingState>(OnOperationRecordingStateChanged), state);
                return;
            }
            ApplyOperationAppearance(state);
        }

        private void ApplyOperationAppearance(OperationRecordingState state)
        {
            operationButton.Checked = state != OperationRecordingState.Disabled;
            operationButton.Text = state == OperationRecordingState.Disabled ? "原子 OFF" :
                (state == OperationRecordingState.Ready ? "原子 READY" : "原子 REC");
            operationButton.BackColor = state == OperationRecordingState.Recording ? Color.Red :
                (state == OperationRecordingState.Ready ? Color.Khaki : SystemColors.Control);
            operationButton.ForeColor = state == OperationRecordingState.Recording ? Color.White : Color.Black;
            operationStartStopButton.Text = state == OperationRecordingState.Recording ? "停止" : "开始";
        }

        private void OnExportClick(object sender, EventArgs e)
        {
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Filter = "PCAPNG capture (*.pcapng)|*.pcapng";
                dialog.FileName = "tianshu-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".pcapng";
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }
                try
                {
                    service.ExportPcapng(dialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ClearDisplayCache()
        {
            int chunkCount = chunks.Count;
            int frameCount = frames.Count;
            int transitionCount = confirmedTransitions.Count;

            packetGrid.SuspendLayout();
            frameGrid.SuspendLayout();
            stateGrid.SuspendLayout();
            try
            {
                packetGrid.ClearSelection();
                packetGrid.CurrentCell = null;
                chunks.Clear();
                frames.Clear();
                confirmedTransitions.Clear();

                packetGrid.RowCount = 0;
                frameGrid.Rows.Clear();
                stateGrid.Rows.Clear();
                originalHex.Clear();
                effectiveHex.Clear();
                fieldTree.Nodes.Clear();
            }
            finally
            {
                packetGrid.ResumeLayout();
                frameGrid.ResumeLayout();
                stateGrid.ResumeLayout();
            }

            GC.Collect(2, GCCollectionMode.Optimized);
            service.ReportEngineEvent(
                "Workbench",
                "INFO",
                "UI display cache cleared: chunks=" + chunkCount +
                ", frames=" + frameCount + ", transitions=" + transitionCount +
                ". SQLite capture data was preserved.",
                null);
        }

        private void ConfigurePacketContextMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem copyRows = new ToolStripMenuItem("复制选中行");
            copyRows.ShortcutKeyDisplayString = "Ctrl+C";
            copyRows.Click += delegate { CopySelectedPacketRows(); };
            ToolStripMenuItem copyEffectiveHex = new ToolStripMenuItem("复制有效数据 Hex");
            copyEffectiveHex.Click += delegate { CopySelectedPacketBytes(false, false); };
            ToolStripMenuItem copyOriginalHex = new ToolStripMenuItem("复制原始数据 Hex");
            copyOriginalHex.Click += delegate { CopySelectedPacketBytes(true, false); };
            ToolStripMenuItem copyEffectiveHexAscii = new ToolStripMenuItem("复制有效数据 Hex/ASCII");
            copyEffectiveHexAscii.Click += delegate { CopySelectedPacketBytes(false, true); };
            ToolStripMenuItem copyOriginalHexAscii = new ToolStripMenuItem("复制原始数据 Hex/ASCII");
            copyOriginalHexAscii.Click += delegate { CopySelectedPacketBytes(true, true); };
            ToolStripMenuItem selectAll = new ToolStripMenuItem("全选");
            selectAll.ShortcutKeyDisplayString = "Ctrl+A";
            selectAll.Click += delegate { SelectAllPackets(); };
            ToolStripMenuItem clearSelected = new ToolStripMenuItem("清空选中数据包（仅界面）");
            clearSelected.Click += delegate { ClearSelectedPackets(); };
            ToolStripMenuItem clearAll = new ToolStripMenuItem("清空全部显示缓存（保留 SQLite）");
            clearAll.Click += delegate { ClearDisplayCache(); };

            menu.Items.Add(copyRows);
            menu.Items.Add(copyEffectiveHex);
            menu.Items.Add(copyOriginalHex);
            menu.Items.Add(copyEffectiveHexAscii);
            menu.Items.Add(copyOriginalHexAscii);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(selectAll);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(clearSelected);
            menu.Items.Add(clearAll);
            menu.Opening += delegate
            {
                int selectedCount = GetSelectedPacketIndexes().Count;
                copyRows.Enabled = selectedCount > 0;
                copyEffectiveHex.Enabled = selectedCount > 0;
                copyOriginalHex.Enabled = selectedCount > 0;
                copyEffectiveHexAscii.Enabled = selectedCount > 0;
                copyOriginalHexAscii.Enabled = selectedCount > 0;
                clearSelected.Enabled = selectedCount > 0;
                copyRows.Text = selectedCount > 1 ? "复制选中行（" + selectedCount + " 条）" : "复制选中行";
                clearSelected.Text = selectedCount > 1
                    ? "清空选中数据包（" + selectedCount + " 条，仅界面）"
                    : "清空选中数据包（仅界面）";
                selectAll.Enabled = chunks.Count > 0;
                clearAll.Enabled = chunks.Count > 0 || frames.Count > 0 || confirmedTransitions.Count > 0;
            };
            packetGrid.ContextMenuStrip = menu;
        }

        private void OnPacketCellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.RowIndex >= packetGrid.RowCount) return;
            DataGridViewRow row = packetGrid.Rows[e.RowIndex];
            bool keepExistingSelection = row.Selected;
            if (!keepExistingSelection)
            {
                if ((ModifierKeys & (Keys.Control | Keys.Shift)) == Keys.None)
                    packetGrid.ClearSelection();
                row.Selected = true;
            }
            int columnIndex = e.ColumnIndex >= 0 ? e.ColumnIndex : 0;
            packetGrid.CurrentCell = row.Cells[columnIndex];
        }

        private void OnPacketGridKeyDown(object sender, KeyEventArgs e)
        {
            if (!e.Control) return;
            if (e.KeyCode == Keys.C)
            {
                CopySelectedPacketRows();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.A)
            {
                SelectAllPackets();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private IList<int> GetSelectedPacketIndexes()
        {
            return packetGrid.SelectedRows.Cast<DataGridViewRow>()
                .Select(row => row.Index)
                .Where(index => index >= 0 && index < chunks.Count)
                .Distinct()
                .OrderBy(index => index)
                .ToList();
        }

        private void SelectAllPackets()
        {
            if (packetGrid.RowCount == 0) return;
            packetGrid.SelectAll();
        }

        private void CopySelectedPacketRows()
        {
            IList<int> indexes = GetSelectedPacketIndexes();
            if (indexes.Count == 0) return;
            StringBuilder text = new StringBuilder();
            text.Append("时间\t连接\t方向\t操作\t类型\t动作\t长度\tEffective Hex\tASCII");
            for (int i = 0; i < indexes.Count; i++)
            {
                TransportChunk chunk = chunks[indexes[i]];
                ConnectionSession connection;
                connections.TryGetValue(chunk.ConnectionId, out connection);
                text.AppendLine();
                text.Append(chunk.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff")).Append('\t')
                    .Append(chunk.ConnectionId).Append('\t')
                    .Append(chunk.Direction == TrafficDirection.ClientToServer ? "C→S" : "S→C").Append('\t')
                    .Append(chunk.Operation).Append('\t')
                    .Append(connection == null ? ConnectionKind.Unknown : connection.Kind).Append('\t')
                    .Append(chunk.RuleAction).Append('\t')
                    .Append(chunk.Length).Append('\t')
                    .Append(HexCodec.Format(chunk.EffectiveBytes)).Append('\t')
                    .Append(HexCodec.FormatAscii(chunk.EffectiveBytes));
            }
            SetPacketClipboardText(text.ToString());
        }

        private void CopySelectedPacketBytes(bool original, bool includeAscii)
        {
            IList<int> indexes = GetSelectedPacketIndexes();
            if (indexes.Count == 0) return;
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < indexes.Count; i++)
            {
                if (i > 0) text.AppendLine();
                byte[] bytes = original ? chunks[indexes[i]].OriginalBytes : chunks[indexes[i]].EffectiveBytes;
                text.Append(HexCodec.Format(bytes));
                if (includeAscii) text.Append("  ").Append(HexCodec.FormatAscii(bytes));
            }
            SetPacketClipboardText(text.ToString());
        }

        private void SetPacketClipboardText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "复制数据包", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ClearSelectedPackets()
        {
            IList<int> indexes = GetSelectedPacketIndexes();
            if (indexes.Count == 0) return;
            packetGrid.SuspendLayout();
            try
            {
                packetGrid.ClearSelection();
                packetGrid.CurrentCell = null;
                for (int i = indexes.Count - 1; i >= 0; i--) chunks.RemoveAt(indexes[i]);
                packetGrid.RowCount = chunks.Count;
                packetGrid.Invalidate();
                originalHex.Clear();
                effectiveHex.Clear();
            }
            finally
            {
                packetGrid.ResumeLayout();
            }
            GC.Collect(2, GCCollectionMode.Optimized);
            service.ReportEngineEvent(
                "Workbench",
                "INFO",
                "Selected UI packet rows cleared: chunks=" + indexes.Count + ". SQLite capture data was preserved.",
                null);
        }

        private void OnPacketCellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= chunks.Count)
            {
                return;
            }
            TransportChunk chunk = chunks[e.RowIndex];
            ConnectionSession connection;
            connections.TryGetValue(chunk.ConnectionId, out connection);
            switch (e.ColumnIndex)
            {
                case 0: e.Value = chunk.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff"); break;
                case 1: e.Value = chunk.ConnectionId; break;
                case 2: e.Value = chunk.Direction == TrafficDirection.ClientToServer ? "C→S" : "S→C"; break;
                case 3: e.Value = chunk.Operation; break;
                case 4: e.Value = connection == null ? ConnectionKind.Unknown : connection.Kind; break;
                case 5: e.Value = chunk.RuleAction; break;
                case 6: e.Value = chunk.Length; break;
                case 7:
                    string hex = HexCodec.Format(chunk.EffectiveBytes);
                    if (hex.Length > 96) hex = hex.Substring(0, 96) + "…";
                    e.Value = hex + "  " + HexCodec.FormatAscii(chunk.EffectiveBytes);
                    break;
            }
        }

        private void OnPacketSelectionChanged(object sender, EventArgs e)
        {
            if (packetGrid.CurrentCell == null || packetGrid.CurrentCell.RowIndex >= chunks.Count)
            {
                return;
            }
            TransportChunk chunk = chunks[packetGrid.CurrentCell.RowIndex];
            originalHex.Text = BuildHexDump(chunk.OriginalBytes);
            effectiveHex.Text = BuildHexDump(chunk.EffectiveBytes);
        }

        private void OnFrameSelectionChanged(object sender, EventArgs e)
        {
            if (frameGrid.CurrentRow == null || frameGrid.CurrentRow.Tag == null)
            {
                return;
            }
            ProtocolFrame frame = (ProtocolFrame)frameGrid.CurrentRow.Tag;
            fieldTree.BeginUpdate();
            fieldTree.Nodes.Clear();
            TreeNode root = fieldTree.Nodes.Add(frame.Signature + "  " + HexCodec.Format(frame.Bytes));
            for (int i = 0; i < frame.Fields.Count; i++)
            {
                DecodedField field = frame.Fields[i];
                root.Nodes.Add(field.Name + " = " + field.DisplayValue + "  [" + field.Offset + ":" + field.Length + "]");
            }
            root.Expand();
            fieldTree.EndUpdate();
        }

        private void OnConnectionChanged(ConnectionSession connection)
        {
            SafeInvoke(delegate { connections[connection.Id] = connection; });
        }

        private void OnChunkCaptured(TransportChunk chunk)
        {
            SafeInvoke(delegate
            {
                if (chunks.Count >= MaximumUiChunks)
                {
                    chunks.RemoveRange(0, Math.Min(1000, chunks.Count));
                }
                chunks.Add(chunk);
                packetGrid.RowCount = chunks.Count;
                packetGrid.InvalidateRow(chunks.Count - 1);
            });
        }

        private void OnFrameCaptured(ProtocolFrame frame)
        {
            SafeInvoke(delegate
            {
                if (frames.Count >= MaximumUiFrames)
                {
                    frames.RemoveAt(0);
                    if (frameGrid.Rows.Count > 0) frameGrid.Rows.RemoveAt(0);
                }
                frames.Add(frame);
                int index = frameGrid.Rows.Add(
                    frame.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff"),
                    frame.ConnectionId,
                    frame.Direction == TrafficDirection.ClientToServer ? "C→S" : "S→C",
                    frame.Opcode.HasValue ? "0x" + frame.Opcode.Value.ToString("X") : "",
                    frame.Bytes.Length,
                    frame.Signature);
                frameGrid.Rows[index].Tag = frame;
            });
        }

        private void OnStateChanged(StateTransition transition)
        {
            SafeInvoke(delegate
            {
                confirmedTransitions.Add(transition);
            });
        }

        private void OnEventRaised(WorkbenchEvent workbenchEvent)
        {
            SafeInvoke(delegate
            {
                eventLog.AppendText(string.Format(
                    "{0:HH:mm:ss.fff} [{1}] [{2}] {3}{4}",
                    workbenchEvent.TimestampUtc.ToLocalTime(),
                    workbenchEvent.Level,
                    workbenchEvent.Category,
                    workbenchEvent.Message,
                    Environment.NewLine));
                if (eventLog.TextLength > 1024 * 1024)
                {
                    eventLog.Select(0, eventLog.TextLength - 512 * 1024);
                    eventLog.SelectedText = string.Empty;
                }
            });
        }

        private void OnScenarioCompleted(ScenarioResult result)
        {
            SafeInvoke(delegate
            {
                resultGrid.Rows.Add(result.Name, result.Status, result.StartedUtc.ToLocalTime(),
                    (result.FinishedUtc - result.StartedUtc).TotalMilliseconds.ToString("F0"), result.Message);
            });
        }

        private void OnActiveModeChanged(bool value)
        {
            SafeInvoke(delegate
            {
                activeButton.Checked = value;
                ApplyActiveAppearance(value);
            });
        }

        private void OnRefreshTimer(object sender, EventArgs e)
        {
            ApplyAudioFilterAppearance();
            connectionGrid.Rows.Clear();
            foreach (ConnectionSession connection in connections.Values.OrderBy(item => item.Id))
            {
                connectionGrid.Rows.Add(
                    connection.Id,
                    "0x" + connection.SocketHandle.ToString("X"),
                    connection.Kind,
                    connection.State,
                    connection.LocalEndPoint,
                    connection.RemoteEndPoint,
                    connection.ClientStreamOffset,
                    connection.ServerStreamOffset,
                    connection.LastError);
            }

            stateGrid.SuspendLayout();
            stateGrid.Rows.Clear();
            for (int i = 0; i < confirmedTransitions.Count; i++)
            {
                StateTransition transition = confirmedTransitions[i];
                stateGrid.Rows.Add(
                    transition.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff"),
                    transition.ConnectionId,
                    transition.FromState,
                    transition.ToState,
                    transition.Trigger,
                    true);
            }
            IList<ObservedTransition> observed = service.States.GetObservedTransitions();
            for (int i = 0; i < observed.Count; i++)
            {
                ObservedTransition transition = observed[i];
                stateGrid.Rows.Add("", "", transition.FromSignature, transition.ToSignature,
                    "observed ×" + transition.Count, false);
            }
            stateGrid.ResumeLayout();
        }

        private void ApplyAudioFilterAppearance()
        {
            if (audioFilterButton == null || audioFilterProxy == null) return;
            bool enabled = audioFilterProxy.FilteringEnabled;
            audioFilterButton.Checked = enabled;
            audioFilterButton.Text = !audioFilterProxy.FfmpegAvailable
                ? "BGM降噪 不可用"
                : (enabled
                    ? "BGM降噪 ON " + audioFilterProxy.SoundRequests + "/" + audioFilterProxy.FilteredRequests
                    : "BGM降噪 旁路");
            audioFilterButton.BackColor = !audioFilterProxy.FfmpegAvailable
                ? Color.MistyRose
                : (enabled ? Color.LightGreen : SystemColors.Control);
            audioFilterButton.ToolTipText = audioFilterProxy.LastStatus + Environment.NewLine +
                "方案 3：" + audioFilterProxy.OutputEncodingDescription + Environment.NewLine +
                "代理请求 " + audioFilterProxy.ProxyRequests + "，音频命中 " + audioFilterProxy.SoundRequests +
                "，已过滤 " + audioFilterProxy.FilteredRequests + "，缓存命中 " + audioFilterProxy.CacheHits;
        }

        private void OnSaveProtocol(object sender, EventArgs e)
        {
            SaveEditor(service.Profile.Resolve(service.Profile.ProtocolPath), protocolEditor.Text, "Protocol saved. Restart to activate framing changes.");
        }

        private void RenameCurrentDatabase()
        {
            string currentPath = service.DatabasePath;
            string currentSemanticName = SqliteCaptureStore.ExtractSemanticDatabaseName(currentPath);
            using (SessionNameDialog dialog = new SessionNameDialog(currentSemanticName))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    SessionDatabaseRenameResult result = service.RenameSessionDatabase(currentPath, dialog.SemanticName);
                    databaseLabel.Text = Path.GetFileName(result.NewPath);
                    databaseLabel.ToolTipText = result.NewPath;
                    databaseLabel.SelectAll();
                    MessageBox.Show(
                        this,
                        "当前会话库已重命名为：" + Path.GetFileName(result.NewPath),
                        "会话库命名",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "会话库命名", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void OnSessionDatabaseRenamed(SessionDatabaseRenameResult result)
        {
            SafeInvoke(delegate
            {
                databaseLabel.Text = Path.GetFileName(result.NewPath);
                databaseLabel.ToolTipText = result.NewPath;
            });
        }

        private void CopyCurrentDatabasePath()
        {
            try
            {
                Clipboard.SetText(service.DatabasePath);
                databaseLabel.SelectAll();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "复制路径", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnSaveLua(object sender, EventArgs e)
        {
            SaveEditor(service.Profile.Resolve(service.Profile.PacketScriptPath), luaEditor.Text, "Lua script saved and reloaded.");
            service.ReloadDefinitions();
        }

        private void SaveEditor(string path, string text, string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, text, new UTF8Encoding(false));
                service.AddMarker(message, null);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LoadEditors()
        {
            protocolEditor.Text = ReadText(service.Profile.Resolve(service.Profile.ProtocolPath));
            luaEditor.Text = ReadText(service.Profile.Resolve(service.Profile.PacketScriptPath));
        }

        private void ApplyActiveAppearance(bool active)
        {
            activeButton.Text = active ? "ACTIVE" : "RECORD";
            activeButton.BackColor = active ? Color.Firebrick : SystemColors.Control;
            activeButton.ForeColor = active ? Color.White : SystemColors.ControlText;
            activeButton.Font = new Font(activeButton.Font, FontStyle.Bold);
        }

        private void SafeInvoke(Action action)
        {
            if (IsDisposed || Disposing)
            {
                return;
            }
            if (InvokeRequired)
            {
                try { BeginInvoke(action); } catch (InvalidOperationException) { }
            }
            else
            {
                action();
            }
        }

        private static DataGridView CreateReadOnlyGrid()
        {
            return new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells
            };
        }

        private static RichTextBox CreateHexBox()
        {
            return new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Font = new Font("Consolas", 9f),
                WordWrap = false,
                BackColor = Color.White
            };
        }

        private static RichTextBox CreateEditor()
        {
            return new RichTextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9f),
                WordWrap = false,
                AcceptsTab = true
            };
        }

        private static Control WrapWithLabel(string labelText, Control child)
        {
            Panel panel = new Panel { Dock = DockStyle.Fill };
            Label label = new Label { Text = labelText, Dock = DockStyle.Top, Height = 20 };
            panel.Controls.Add(child);
            panel.Controls.Add(label);
            return panel;
        }

        private static void AddColumn(DataGridView grid, string name, int width)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable });
        }

        private static string BuildHexDump(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }
            StringBuilder text = new StringBuilder();
            for (int offset = 0; offset < bytes.Length; offset += 16)
            {
                int count = Math.Min(16, bytes.Length - offset);
                text.Append(offset.ToString("X8")).Append("  ");
                for (int i = 0; i < 16; i++)
                {
                    text.Append(i < count ? bytes[offset + i].ToString("X2") : "  ").Append(' ');
                }
                text.Append(" ");
                for (int i = 0; i < count; i++)
                {
                    byte value = bytes[offset + i];
                    text.Append(value >= 32 && value <= 126 ? (char)value : '.');
                }
                text.AppendLine();
            }
            return text.ToString();
        }

        private static string ReadText(string path)
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
    }
}
