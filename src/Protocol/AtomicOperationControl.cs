using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace TianshuQitanLauncher.Protocol
{
    public sealed class AtomicOperationControl : UserControl
    {
        private readonly ProtocolWorkbenchService service;
        private readonly ComboBox definitionBox;
        private readonly ToolStripButton enabledButton;
        private readonly ToolStripButton startStopButton;
        private readonly DataGridView runGrid;
        private readonly DataGridView stepGrid;
        private readonly DataGridView connectionGrid;
        private readonly DataGridView frameGrid;
        private readonly DataGridView comparisonGrid;
        private readonly ComboBox outcomeBox;
        private readonly TextBox actualResultBox;
        private readonly TextBox runNotesBox;
        private readonly CheckBox includeNoiseBox;
        private readonly ComboBox frameRoleBox;
        private readonly TextBox frameMeaningBox;
        private readonly CheckBox frameConfirmedBox;
        private readonly ComboBox fieldBox;
        private readonly TextBox fieldSemanticNameBox;
        private readonly TextBox fieldMeaningBox;
        private readonly ComboBox fieldDynamicBox;
        private readonly CheckBox fieldConfirmedBox;
        private IList<AtomicOperationDefinition> definitions = new List<AtomicOperationDefinition>();
        private IList<AtomicOperationRun> runs = new List<AtomicOperationRun>();
        private IList<OperationStep> steps = new List<OperationStep>();
        private IList<OperationConnectionCandidate> connectionCandidates = new List<OperationConnectionCandidate>();
        private IList<OperationFrameSample> frames = new List<OperationFrameSample>();
        private OperationComparison currentComparison;
        private IList<FieldSemanticAnnotation> currentFieldAnnotations = new List<FieldSemanticAnnotation>();
        private bool loadingConnections;
        private bool restoringSessionName;

        public AtomicOperationControl(ProtocolWorkbenchService service)
        {
            if (service == null) throw new ArgumentNullException("service");
            this.service = service;
            Dock = DockStyle.Fill;

            ToolStrip tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
            enabledButton = new ToolStripButton { CheckOnClick = true };
            enabledButton.Click += delegate { service.SetOperationRecorderEnabled(enabledButton.Checked, true); };
            definitionBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
            definitionBox.SelectedIndexChanged += OnDefinitionSelected;
            ToolStripControlHost definitionHost = new ToolStripControlHost(definitionBox);
            ToolStripButton newDefinition = new ToolStripButton("新建模板");
            newDefinition.Click += delegate { EditDefinition(null); };
            ToolStripButton editDefinition = new ToolStripButton("编辑模板");
            editDefinition.Click += delegate { EditDefinition(definitionBox.SelectedItem as AtomicOperationDefinition); };
            ToolStripButton renameSession = new ToolStripButton("会话命名");
            renameSession.Click += delegate { RenameSelectedSession(); };
            ToolStripButton copySession = new ToolStripButton("复制库名");
            copySession.Click += delegate { CopySelectedSession(false); };
            startStopButton = new ToolStripButton();
            startStopButton.ToolTipText = "Ctrl+F8";
            startStopButton.Click += delegate { ToggleRecording(); };
            ToolStripButton markStep = new ToolStripButton("插入步骤");
            markStep.ToolTipText = "Ctrl+F9";
            markStep.Click += delegate { MarkStep(); };
            ToolStripButton discard = new ToolStripButton("废弃");
            discard.Click += delegate { DiscardRecording(); };
            ToolStripButton refresh = new ToolStripButton("刷新");
            refresh.Click += delegate { RefreshAll(); };
            ToolStripButton compare = new ToolStripButton("比较所选");
            compare.Click += delegate { CompareSelected(); };
            ToolStripButton export = new ToolStripButton("导出分析包");
            export.Click += delegate { ExportSelected(); };
            tools.Items.Add(enabledButton);
            tools.Items.Add(new ToolStripLabel("模板"));
            tools.Items.Add(definitionHost);
            tools.Items.Add(newDefinition);
            tools.Items.Add(editDefinition);
            tools.Items.Add(renameSession);
            tools.Items.Add(copySession);
            tools.Items.Add(new ToolStripSeparator());
            tools.Items.Add(startStopButton);
            tools.Items.Add(markStep);
            tools.Items.Add(discard);
            tools.Items.Add(refresh);
            tools.Items.Add(compare);
            tools.Items.Add(export);

            runGrid = CreateGrid(true);
            AddColumn(runGrid, "开始", 130);
            AddColumn(runGrid, "状态", 75);
            AddColumn(runGrid, "结果", 65);
            AddColumn(runGrid, "时长", 70);
            runGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "会话库（可编辑/复制）", Width = 210, ReadOnly = false });
            runGrid.SelectionChanged += delegate { LoadSelectedRun(); };
            runGrid.CellEndEdit += OnSessionDatabaseCellEndEdit;
            runGrid.KeyDown += OnRunGridKeyDown;
            ContextMenuStrip sessionMenu = new ContextMenuStrip();
            sessionMenu.Items.Add("按语义重命名", null, delegate { RenameSelectedSession(); });
            sessionMenu.Items.Add("复制文件名", null, delegate { CopySelectedSession(false); });
            sessionMenu.Items.Add("复制完整路径", null, delegate { CopySelectedSession(true); });
            runGrid.ContextMenuStrip = sessionMenu;

            TabControl details = new TabControl { Dock = DockStyle.Fill };
            stepGrid = CreateGrid(false);
            AddColumn(stepGrid, "#", 35);
            AddColumn(stepGrid, "时间", 100);
            AddColumn(stepGrid, "步骤", 130);
            AddColumn(stepGrid, "说明", 260);
            stepGrid.CellDoubleClick += delegate { EditSelectedStep(); };
            outcomeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            outcomeBox.Items.AddRange(Enum.GetValues(typeof(OperationOutcome)).Cast<object>().ToArray());
            actualResultBox = new TextBox { Dock = DockStyle.Fill };
            runNotesBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, Height = 54, ScrollBars = ScrollBars.Vertical };
            details.TabPages.Add(new TabPage("过程/说明") { Controls = { CreateRunDetails() } });

            connectionGrid = CreateGrid(false);
            DataGridViewCheckBoxColumn included = new DataGridViewCheckBoxColumn { HeaderText = "纳入", Width = 45 };
            connectionGrid.Columns.Add(included);
            AddColumn(connectionGrid, "ID", 45);
            AddColumn(connectionGrid, "类型", 75);
            AddColumn(connectionGrid, "自动", 45);
            AddColumn(connectionGrid, "远端", 165);
            AddColumn(connectionGrid, "状态", 90);
            connectionGrid.CellValueChanged += OnConnectionCellValueChanged;
            connectionGrid.CurrentCellDirtyStateChanged += delegate
            {
                if (connectionGrid.IsCurrentCellDirty) connectionGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            details.TabPages.Add(new TabPage("连接") { Controls = { connectionGrid } });

            frameGrid = CreateGrid(false);
            AddColumn(frameGrid, "#", 40);
            AddColumn(frameGrid, "方向", 45);
            AddColumn(frameGrid, "Opcode", 75);
            AddColumn(frameGrid, "名称", 120);
            AddColumn(frameGrid, "长度", 55);
            AddColumn(frameGrid, "角色", 75);
            AddColumn(frameGrid, "含义", 180);
            AddColumn(frameGrid, "Hex", 220);
            frameGrid.SelectionChanged += delegate { LoadFrameEditor(); };
            includeNoiseBox = new CheckBox { Text = "显示噪声", AutoSize = true, Dock = DockStyle.Left };
            includeNoiseBox.CheckedChanged += delegate { LoadFrames(); };
            frameRoleBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            frameRoleBox.Items.AddRange(Enum.GetValues(typeof(FrameSemanticRole)).Cast<object>().ToArray());
            frameMeaningBox = new TextBox { Dock = DockStyle.Fill };
            frameConfirmedBox = new CheckBox { Text = "已确认", AutoSize = true };
            fieldBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            fieldBox.SelectedIndexChanged += delegate { LoadFieldEditor(); };
            fieldSemanticNameBox = new TextBox { Dock = DockStyle.Fill };
            fieldMeaningBox = new TextBox { Dock = DockStyle.Fill };
            fieldDynamicBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            fieldDynamicBox.Items.AddRange(new object[] { "Unknown", "Stable", "Dynamic", "Sequence", "Timestamp", "SessionIdentifier" });
            fieldConfirmedBox = new CheckBox { Text = "字段已确认", AutoSize = true };
            details.TabPages.Add(new TabPage("数据包语义") { Controls = { CreateFrameDetails() } });

            comparisonGrid = CreateGrid(false);
            AddColumn(comparisonGrid, "#", 40);
            AddColumn(comparisonGrid, "方向", 45);
            AddColumn(comparisonGrid, "Opcode", 75);
            AddColumn(comparisonGrid, "长度", 55);
            AddColumn(comparisonGrid, "出现", 60);
            AddColumn(comparisonGrid, "分类", 80);
            AddColumn(comparisonGrid, "动态候选", 280);
            details.TabPages.Add(new TabPage("样本差异") { Controls = { comparisonGrid } });

            SplitContainer split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 310
            };
            split.Panel1.Controls.Add(runGrid);
            split.Panel2.Controls.Add(details);
            Controls.Add(split);
            Controls.Add(tools);
            tools.Dock = DockStyle.Top;

            service.OperationRecordingStateChanged += OnRecordingStateChanged;
            service.OperationRunChanged += OnRunChanged;
            service.OperationStepAdded += OnStepAdded;
            service.SessionDatabaseRenamed += OnSessionDatabaseRenamed;
            RefreshDefinitions();
            ApplyRecorderState(service.OperationRecorder.State);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                service.OperationRecordingStateChanged -= OnRecordingStateChanged;
                service.OperationRunChanged -= OnRunChanged;
                service.OperationStepAdded -= OnStepAdded;
                service.SessionDatabaseRenamed -= OnSessionDatabaseRenamed;
            }
            base.Dispose(disposing);
        }

        private Control CreateRunDetails()
        {
            Button save = new Button { Text = "保存操作说明", Dock = DockStyle.Right, Width = 120 };
            save.Click += delegate { SaveRunNotes(); };
            Button editStep = new Button { Text = "编辑所选步骤", Dock = DockStyle.Left, Width = 120 };
            editStep.Click += delegate { EditSelectedStep(); };
            Button attach = new Button { Text = "附加截图/文件", Dock = DockStyle.Left, Width = 120 };
            attach.Click += delegate { AttachToSelectedStep(); };
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            buttons.Controls.Add(editStep);
            buttons.Controls.Add(attach);
            buttons.Controls.Add(save);
            TableLayoutPanel table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5 };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            table.Controls.Add(stepGrid, 0, 0);
            table.SetColumnSpan(stepGrid, 2);
            table.Controls.Add(new Label { Text = "结果", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 1);
            table.Controls.Add(outcomeBox, 1, 1);
            table.Controls.Add(new Label { Text = "实际表现", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 2);
            table.Controls.Add(actualResultBox, 1, 2);
            table.Controls.Add(new Label { Text = "补充说明", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 3);
            table.Controls.Add(runNotesBox, 1, 3);
            table.Controls.Add(buttons, 0, 4);
            table.SetColumnSpan(buttons, 2);
            return table;
        }

        private Control CreateFrameDetails()
        {
            Button saveFrame = new Button { Text = "保存包注释", Dock = DockStyle.Fill };
            saveFrame.Click += delegate { SaveFrameAnnotation(); };
            Button saveField = new Button { Text = "保存字段注释", Dock = DockStyle.Fill };
            saveField.Click += delegate { SaveFieldAnnotation(); };
            TableLayoutPanel editor = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 125, ColumnCount = 6, RowCount = 4 };
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            editor.Controls.Add(includeNoiseBox, 0, 0);
            editor.Controls.Add(new Label { Text = "包角色", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
            editor.Controls.Add(frameRoleBox, 1, 1);
            editor.Controls.Add(new Label { Text = "包含义", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 2, 1);
            editor.Controls.Add(frameMeaningBox, 3, 1);
            editor.Controls.Add(frameConfirmedBox, 4, 1);
            editor.Controls.Add(saveFrame, 5, 1);
            editor.Controls.Add(new Label { Text = "字段", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
            editor.Controls.Add(fieldBox, 1, 2);
            editor.Controls.Add(new Label { Text = "语义名", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 2, 2);
            editor.Controls.Add(fieldSemanticNameBox, 3, 2);
            editor.Controls.Add(fieldDynamicBox, 4, 2);
            editor.Controls.Add(fieldConfirmedBox, 5, 2);
            editor.Controls.Add(new Label { Text = "字段含义", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 3);
            editor.Controls.Add(fieldMeaningBox, 1, 3);
            editor.SetColumnSpan(fieldMeaningBox, 4);
            editor.Controls.Add(saveField, 5, 3);
            Panel panel = new Panel { Dock = DockStyle.Fill };
            panel.Controls.Add(frameGrid);
            panel.Controls.Add(editor);
            return panel;
        }

        private void RefreshAll()
        {
            RefreshDefinitions();
            LoadRuns();
        }

        private void RefreshDefinitions()
        {
            string selectedId = (definitionBox.SelectedItem as AtomicOperationDefinition) == null
                ? null : ((AtomicOperationDefinition)definitionBox.SelectedItem).Id;
            definitions = service.Operations.GetDefinitions();
            definitionBox.BeginUpdate();
            definitionBox.Items.Clear();
            foreach (AtomicOperationDefinition definition in definitions) definitionBox.Items.Add(definition);
            definitionBox.EndUpdate();
            AtomicOperationDefinition selected = definitions.FirstOrDefault(item => item.Id == selectedId) ?? definitions.FirstOrDefault();
            if (selected != null) definitionBox.SelectedItem = selected;
        }

        private void OnDefinitionSelected(object sender, EventArgs e)
        {
            AtomicOperationDefinition definition = definitionBox.SelectedItem as AtomicOperationDefinition;
            if (definition == null) return;
            try
            {
                if (service.OperationRecorder.State != OperationRecordingState.Recording)
                    service.SelectOperationDefinition(definition);
                LoadRuns();
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private void EditDefinition(AtomicOperationDefinition definition)
        {
            AtomicOperationDefinition value = definition == null ? new AtomicOperationDefinition() : definition.Clone();
            using (OperationDefinitionDialog dialog = new OperationDefinitionDialog(value))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    service.Operations.SaveDefinition(value);
                    RefreshDefinitions();
                    for (int i = 0; i < definitionBox.Items.Count; i++)
                    {
                        AtomicOperationDefinition item = (AtomicOperationDefinition)definitionBox.Items[i];
                        if (item.Id == value.Id) { definitionBox.SelectedIndex = i; break; }
                    }
                }
                catch (Exception ex) { ShowError(ex); }
            }
        }

        private void ToggleRecording()
        {
            try
            {
                if (service.OperationRecorder.State == OperationRecordingState.Disabled)
                    service.SetOperationRecorderEnabled(true, true);
                if (service.OperationRecorder.State == OperationRecordingState.Recording)
                    service.StopAtomicOperation(OperationOutcome.Unknown, null, "Stopped from the operation workbench.");
                else
                {
                    AtomicOperationDefinition definition = definitionBox.SelectedItem as AtomicOperationDefinition;
                    if (definition == null) throw new InvalidOperationException("请先新建并选择原子操作模板。");
                    service.SelectOperationDefinition(definition);
                    service.StartAtomicOperation();
                }
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private void MarkStep()
        {
            try { service.MarkAtomicOperationStep(null, null); }
            catch (Exception ex) { ShowError(ex); }
        }

        private void DiscardRecording()
        {
            if (service.OperationRecorder.State != OperationRecordingState.Recording) return;
            if (MessageBox.Show(this, "将本次样本标记为废弃？原始数据仍会保留。", "废弃录制", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try { service.DiscardAtomicOperation("Discarded by operator."); }
            catch (Exception ex) { ShowError(ex); }
        }

        private void LoadRuns()
        {
            AtomicOperationDefinition definition = definitionBox.SelectedItem as AtomicOperationDefinition;
            runs = definition == null ? new List<AtomicOperationRun>() : service.Operations.GetRuns(definition.Id);
            runGrid.Rows.Clear();
            foreach (AtomicOperationRun run in runs)
            {
                TimeSpan duration = (run.StoppedUtc ?? DateTime.UtcNow) - run.StartedUtc;
                int index = runGrid.Rows.Add(
                    run.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), run.Status, run.Outcome,
                    duration.TotalSeconds.ToString("0.000") + "s", Path.GetFileName(run.SourceDatabasePath));
                runGrid.Rows[index].Tag = run;
            }
        }

        private AtomicOperationRun SelectedRun
        {
            get { return runGrid.CurrentRow == null ? null : runGrid.CurrentRow.Tag as AtomicOperationRun; }
        }

        private void RenameSelectedSession()
        {
            AtomicOperationRun run = SelectedRun;
            if (run == null)
            {
                MessageBox.Show(this, "请先选择一条操作样本。", "会话库命名", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string initial = SqliteCaptureStore.ExtractSemanticDatabaseName(run.SourceDatabasePath);
            using (SessionNameDialog dialog = new SessionNameDialog(initial))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                RenameSession(run, dialog.SemanticName);
            }
        }

        private void RenameSession(AtomicOperationRun run, string semanticName)
        {
            try
            {
                SessionDatabaseRenameResult result = service.RenameSessionDatabase(run.SourceDatabasePath, semanticName);
                MessageBox.Show(
                    this,
                    "会话库已重命名为：" + Path.GetFileName(result.NewPath),
                    "会话库命名",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                ShowError(ex);
                LoadRuns();
            }
        }

        private void OnSessionDatabaseCellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            if (restoringSessionName || e.RowIndex < 0 || e.ColumnIndex != 4) return;
            DataGridViewRow row = runGrid.Rows[e.RowIndex];
            AtomicOperationRun run = row.Tag as AtomicOperationRun;
            if (run == null) return;
            string entered = Convert.ToString(row.Cells[4].Value) ?? string.Empty;
            string original = Path.GetFileName(run.SourceDatabasePath);
            if (string.Equals(entered.Trim(), original, StringComparison.OrdinalIgnoreCase)) return;
            string semanticName = SqliteCaptureStore.ExtractSemanticDatabaseName(entered);
            if (string.IsNullOrWhiteSpace(semanticName))
            {
                restoringSessionName = true;
                row.Cells[4].Value = original;
                restoringSessionName = false;
                MessageBox.Show(this, "请输入语义名称，例如：除暴安良-完成。", "会话库命名", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            BeginInvoke(new MethodInvoker(delegate { RenameSession(run, semanticName); }));
        }

        private void OnRunGridKeyDown(object sender, KeyEventArgs e)
        {
            if (!e.Control || e.KeyCode != Keys.C || runGrid.IsCurrentCellInEditMode) return;
            CopySelectedSession(e.Shift);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private void OnSessionDatabaseRenamed(SessionDatabaseRenameResult result)
        {
            RunOnUi(delegate { RefreshAll(); });
        }

        private void CopySelectedSession(bool fullPath)
        {
            AtomicOperationRun run = SelectedRun;
            if (run == null) return;
            try
            {
                Clipboard.SetText(fullPath ? run.SourceDatabasePath : Path.GetFileName(run.SourceDatabasePath));
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private void LoadSelectedRun()
        {
            AtomicOperationRun run = SelectedRun;
            steps = run == null ? new List<OperationStep>() : service.Operations.GetSteps(run);
            stepGrid.Rows.Clear();
            foreach (OperationStep step in steps)
            {
                int index = stepGrid.Rows.Add(step.Sequence, step.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff"), step.Name, step.Description);
                stepGrid.Rows[index].Tag = step;
            }
            if (run == null)
            {
                connectionGrid.Rows.Clear();
                frameGrid.Rows.Clear();
                return;
            }
            outcomeBox.SelectedItem = run.Outcome;
            actualResultBox.Text = run.ActualResult ?? string.Empty;
            runNotesBox.Text = run.Notes ?? string.Empty;
            LoadConnections();
            LoadFrames();
        }

        private void LoadConnections()
        {
            AtomicOperationRun run = SelectedRun;
            loadingConnections = true;
            try
            {
                connectionCandidates = run == null ? new List<OperationConnectionCandidate>() : service.Operations.GetConnectionCandidates(run);
                connectionGrid.Rows.Clear();
                foreach (OperationConnectionCandidate candidate in connectionCandidates)
                {
                    ConnectionSession connection = candidate.Connection;
                    int index = connectionGrid.Rows.Add(candidate.Included, connection.Id, connection.Kind,
                        candidate.Automatic ? "是" : "否", connection.RemoteEndPoint, connection.State);
                    connectionGrid.Rows[index].Tag = candidate;
                }
            }
            finally { loadingConnections = false; }
        }

        private void OnConnectionCellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (loadingConnections || e.RowIndex < 0 || e.ColumnIndex != 0) return;
            AtomicOperationRun run = SelectedRun;
            OperationConnectionCandidate candidate = connectionGrid.Rows[e.RowIndex].Tag as OperationConnectionCandidate;
            if (run == null || candidate == null) return;
            try
            {
                bool included = Convert.ToBoolean(connectionGrid.Rows[e.RowIndex].Cells[0].Value);
                service.Operations.SetConnectionIncluded(run, candidate.Connection.Id, included);
                candidate.Included = included;
                LoadFrames();
            }
            catch (Exception ex) { ShowError(ex); LoadConnections(); }
        }

        private void LoadFrames()
        {
            AtomicOperationRun run = SelectedRun;
            frames = run == null ? new List<OperationFrameSample>() : service.Operations.GetFrames(run, includeNoiseBox.Checked);
            frameGrid.Rows.Clear();
            foreach (OperationFrameSample frame in frames)
            {
                string hex = HexCodec.Format(frame.Bytes);
                if (hex.Length > 100) hex = hex.Substring(0, 100) + "…";
                int index = frameGrid.Rows.Add(frame.Sequence,
                    frame.Direction == TrafficDirection.ClientToServer ? "C→S" : "S→C",
                    frame.Opcode.HasValue ? "0x" + frame.Opcode.Value.ToString("X4") : "raw",
                    frame.Name, frame.Bytes.Length, frame.Role, frame.Meaning, hex);
                frameGrid.Rows[index].Tag = frame;
                if (frame.Role == FrameSemanticRole.Noise) frameGrid.Rows[index].DefaultCellStyle.ForeColor = Color.Gray;
            }
        }

        private OperationFrameSample SelectedFrame
        {
            get { return frameGrid.CurrentRow == null ? null : frameGrid.CurrentRow.Tag as OperationFrameSample; }
        }

        private void LoadFrameEditor()
        {
            OperationFrameSample frame = SelectedFrame;
            frameRoleBox.SelectedItem = frame == null ? FrameSemanticRole.Unknown : frame.Role;
            frameMeaningBox.Text = frame == null ? string.Empty : frame.Meaning ?? string.Empty;
            frameConfirmedBox.Checked = frame != null && frame.Confirmed;
            fieldBox.Items.Clear();
            if (frame != null)
            {
                AtomicOperationRun run = SelectedRun;
                currentFieldAnnotations = run == null ? new List<FieldSemanticAnnotation>() : service.Operations.GetFieldAnnotations(run, frame.FrameId);
                foreach (DecodedField field in frame.Fields) fieldBox.Items.Add(field);
                fieldBox.DisplayMember = "Name";
                if (fieldBox.Items.Count > 0) fieldBox.SelectedIndex = 0;
            }
        }

        private void LoadFieldEditor()
        {
            DecodedField field = fieldBox.SelectedItem as DecodedField;
            FieldSemanticAnnotation annotation = field == null ? null : currentFieldAnnotations.FirstOrDefault(
                item => item.FieldName == field.Name && item.Offset == field.Offset);
            fieldSemanticNameBox.Text = annotation == null ? (field == null ? string.Empty : field.Name) : annotation.SemanticName;
            fieldMeaningBox.Text = annotation == null ? string.Empty : annotation.Meaning;
            string dynamicKind = annotation == null ? "Unknown" : annotation.DynamicKind;
            fieldDynamicBox.SelectedItem = fieldDynamicBox.Items.Cast<object>().FirstOrDefault(
                item => string.Equals(item.ToString(), dynamicKind, StringComparison.OrdinalIgnoreCase));
            if (fieldDynamicBox.SelectedIndex < 0) fieldDynamicBox.SelectedIndex = 0;
            fieldConfirmedBox.Checked = annotation != null && annotation.Confirmed;
        }

        private void SaveRunNotes()
        {
            AtomicOperationRun run = SelectedRun;
            if (run == null) return;
            try
            {
                run.Outcome = outcomeBox.SelectedItem == null ? OperationOutcome.Unknown : (OperationOutcome)outcomeBox.SelectedItem;
                run.ActualResult = actualResultBox.Text;
                run.Notes = runNotesBox.Text;
                service.Operations.SaveRun(run);
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private void EditSelectedStep()
        {
            AtomicOperationRun run = SelectedRun;
            OperationStep step = stepGrid.CurrentRow == null ? null : stepGrid.CurrentRow.Tag as OperationStep;
            if (run == null || step == null) return;
            using (TextEditDialog dialog = new TextEditDialog("编辑步骤", step.Name, step.Description))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                step.Name = dialog.TitleText;
                step.Description = dialog.DescriptionText;
                service.Operations.SaveStep(run, step);
                LoadSelectedRun();
            }
        }

        private void AttachToSelectedStep()
        {
            AtomicOperationRun run = SelectedRun;
            OperationStep step = stepGrid.CurrentRow == null ? null : stepGrid.CurrentRow.Tag as OperationStep;
            if (run == null || step == null) return;
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Filter = "图片 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    service.Operations.SaveAttachment(run, new OperationAttachment
                    {
                        StepId = step.Id,
                        FileName = Path.GetFileName(dialog.FileName),
                        MediaType = GuessMediaType(dialog.FileName),
                        Bytes = File.ReadAllBytes(dialog.FileName)
                    });
                }
                catch (Exception ex) { ShowError(ex); }
            }
        }

        private void SaveFrameAnnotation()
        {
            AtomicOperationRun run = SelectedRun;
            OperationFrameSample frame = SelectedFrame;
            if (run == null || frame == null) return;
            try
            {
                service.Operations.SaveFrameAnnotation(run, new FrameSemanticAnnotation
                {
                    FrameId = frame.FrameId,
                    Role = frameRoleBox.SelectedItem == null ? FrameSemanticRole.Unknown : (FrameSemanticRole)frameRoleBox.SelectedItem,
                    Meaning = frameMeaningBox.Text,
                    Confirmed = frameConfirmedBox.Checked
                });
                LoadFrames();
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private void SaveFieldAnnotation()
        {
            AtomicOperationRun run = SelectedRun;
            OperationFrameSample frame = SelectedFrame;
            DecodedField field = fieldBox.SelectedItem as DecodedField;
            if (run == null || frame == null || field == null) return;
            try
            {
                service.Operations.SaveFieldAnnotation(run, new FieldSemanticAnnotation
                {
                    FrameId = frame.FrameId,
                    FieldName = field.Name,
                    Offset = field.Offset,
                    Length = field.Length,
                    SemanticName = fieldSemanticNameBox.Text,
                    Meaning = fieldMeaningBox.Text,
                    ValueType = field.Type,
                    DynamicKind = fieldDynamicBox.SelectedItem == null ? "Unknown" : fieldDynamicBox.SelectedItem.ToString(),
                    Confirmed = fieldConfirmedBox.Checked
                });
                currentFieldAnnotations = service.Operations.GetFieldAnnotations(run, frame.FrameId);
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private IList<AtomicOperationRun> GetSelectedRuns()
        {
            return runGrid.SelectedRows.Cast<DataGridViewRow>()
                .Select(row => row.Tag as AtomicOperationRun).Where(run => run != null && run.Status != OperationRunStatus.Discarded)
                .OrderBy(run => run.StartedUtc).ToList();
        }

        private void CompareSelected()
        {
            IList<AtomicOperationRun> selected = GetSelectedRuns();
            if (selected.Count < 2)
            {
                MessageBox.Show(this, "请按住 Ctrl 选择至少两个同模板样本。", "样本比较", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                currentComparison = service.CompareOperations(selected);
                comparisonGrid.Rows.Clear();
                foreach (OperationComparisonFrame frame in currentComparison.Frames)
                {
                    comparisonGrid.Rows.Add(frame.Sequence,
                        frame.Direction == TrafficDirection.ClientToServer ? "C→S" : "S→C",
                        frame.Opcode.HasValue ? "0x" + frame.Opcode.Value.ToString("X4") : "raw",
                        frame.Length, frame.PresentRunCount + "/" + selected.Count, frame.Presence, frame.CandidateKinds);
                }
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private void ExportSelected()
        {
            IList<AtomicOperationRun> selected = GetSelectedRuns();
            if (selected.Count == 0 && SelectedRun != null) selected = new List<AtomicOperationRun> { SelectedRun };
            if (selected.Count == 0) return;
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Filter = "天书原子操作分析包 (*.tsqop.sqlite)|*.tsqop.sqlite";
                dialog.FileName = "operation-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".tsqop.sqlite";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    OperationComparison comparison = currentComparison != null &&
                        currentComparison.RunIds.OrderBy(id => id).SequenceEqual(selected.Select(run => run.Id).OrderBy(id => id))
                        ? currentComparison : null;
                    service.ExportOperationBundle(selected, comparison, dialog.FileName);
                }
                catch (Exception ex) { ShowError(ex); }
            }
        }

        private void OnRecordingStateChanged(OperationRecordingState state)
        {
            RunOnUi(delegate { ApplyRecorderState(state); });
        }

        private void OnRunChanged(AtomicOperationRun run)
        {
            RunOnUi(delegate { LoadRuns(); });
        }

        private void OnStepAdded(OperationStep step)
        {
            RunOnUi(delegate { LoadSelectedRun(); });
        }

        private void ApplyRecorderState(OperationRecordingState state)
        {
            enabledButton.Checked = state != OperationRecordingState.Disabled;
            enabledButton.Text = state == OperationRecordingState.Disabled ? "原子录制 OFF" :
                (state == OperationRecordingState.Ready ? "原子录制 READY" : "原子录制 REC");
            enabledButton.BackColor = state == OperationRecordingState.Recording ? Color.Red :
                (state == OperationRecordingState.Ready ? Color.Khaki : SystemColors.Control);
            enabledButton.ForeColor = state == OperationRecordingState.Recording ? Color.White : Color.Black;
            startStopButton.Text = state == OperationRecordingState.Recording ? "停止" : "开始";
            definitionBox.Enabled = state != OperationRecordingState.Recording;
        }

        private void RunOnUi(MethodInvoker action)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }

        private static DataGridView CreateGrid(bool multiSelect)
        {
            return new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = multiSelect,
                AutoGenerateColumns = false,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText,
                BackgroundColor = Color.White
            };
        }

        private static void AddColumn(DataGridView grid, string text, int width)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = text, Width = width, ReadOnly = true });
        }

        private static string GuessMediaType(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".png") return "image/png";
            if (extension == ".jpg" || extension == ".jpeg") return "image/jpeg";
            if (extension == ".bmp") return "image/bmp";
            return "application/octet-stream";
        }

        private void ShowError(Exception ex)
        {
            MessageBox.Show(this, ex.Message, "原子操作", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    internal sealed class SessionNameDialog : Form
    {
        private readonly TextBox nameBox;

        public SessionNameDialog(string currentSemanticName)
        {
            Text = "会话库语义命名";
            Width = 460;
            Height = 155;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            Label help = new Label
            {
                Dock = DockStyle.Top,
                Height = 42,
                Padding = new Padding(8, 8, 8, 0),
                Text = "输入语义名称，例如：登录-一线-角色1。时间前缀和 .sqlite 会自动保留。"
            };
            nameBox = new TextBox { Dock = DockStyle.Top, Text = currentSemanticName ?? string.Empty };
            Panel input = new Panel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(8, 2, 8, 2) };
            input.Controls.Add(nameBox);
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, FlowDirection = FlowDirection.RightToLeft };
            Button ok = new Button { Text = "确定", DialogResult = DialogResult.OK };
            Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            Controls.Add(buttons);
            Controls.Add(input);
            Controls.Add(help);
            AcceptButton = ok;
            CancelButton = cancel;
            Shown += delegate { nameBox.Focus(); nameBox.SelectAll(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (DialogResult == DialogResult.OK && string.IsNullOrWhiteSpace(nameBox.Text))
                {
                    MessageBox.Show(this, "语义名称不能为空。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    e.Cancel = true;
                }
            };
        }

        public string SemanticName { get { return nameBox.Text.Trim(); } }
    }

    internal sealed class OperationDefinitionDialog : Form
    {
        private readonly AtomicOperationDefinition definition;
        private readonly TextBox nameBox = new TextBox();
        private readonly TextBox moduleBox = new TextBox();
        private readonly TextBox purposeBox = new TextBox();
        private readonly TextBox preconditionsBox = new TextBox();
        private readonly TextBox expectedBox = new TextBox();
        private readonly TextBox tagsBox = new TextBox();

        public OperationDefinitionDialog(AtomicOperationDefinition definition)
        {
            this.definition = definition;
            Text = "原子操作模板";
            Width = 520;
            Height = 430;
            StartPosition = FormStartPosition.CenterParent;
            TableLayoutPanel table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 7, Padding = new Padding(8) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddRow(table, 0, "名称*", nameBox, definition.Name);
            AddRow(table, 1, "模块", moduleBox, definition.Module);
            AddRow(table, 2, "目的", purposeBox, definition.Purpose);
            AddRow(table, 3, "前置条件", preconditionsBox, definition.Preconditions);
            AddRow(table, 4, "期望结果", expectedBox, definition.ExpectedResult);
            AddRow(table, 5, "标签(逗号)", tagsBox, string.Join(",", (definition.Tags ?? new List<string>()).ToArray()));
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            Button ok = new Button { Text = "保存", DialogResult = DialogResult.OK };
            Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            table.Controls.Add(buttons, 0, 6);
            table.SetColumnSpan(buttons, 2);
            Controls.Add(table);
            AcceptButton = ok;
            CancelButton = cancel;
            FormClosing += OnFormClosing;
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (DialogResult != DialogResult.OK) return;
            if (string.IsNullOrWhiteSpace(nameBox.Text))
            {
                MessageBox.Show(this, "模板名称不能为空。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
                return;
            }
            definition.Name = nameBox.Text.Trim();
            definition.Module = moduleBox.Text.Trim();
            definition.Purpose = purposeBox.Text.Trim();
            definition.Preconditions = preconditionsBox.Text.Trim();
            definition.ExpectedResult = expectedBox.Text.Trim();
            definition.Tags = tagsBox.Text.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim()).Where(value => value.Length > 0).ToList();
        }

        private static void AddRow(TableLayoutPanel table, int row, string label, TextBox box, string value)
        {
            table.RowStyles.Add(new RowStyle(SizeType.Percent, row < 2 || row == 5 ? 10 : 20));
            table.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            box.Dock = DockStyle.Fill;
            box.Text = value ?? string.Empty;
            box.Multiline = row >= 2 && row <= 4;
            table.Controls.Add(box, 1, row);
        }
    }

    internal sealed class TextEditDialog : Form
    {
        private readonly TextBox titleBox;
        private readonly TextBox descriptionBox;

        public TextEditDialog(string caption, string title, string description)
        {
            Text = caption;
            Width = 440;
            Height = 260;
            StartPosition = FormStartPosition.CenterParent;
            titleBox = new TextBox { Dock = DockStyle.Top, Text = title ?? string.Empty };
            descriptionBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, Text = description ?? string.Empty };
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, FlowDirection = FlowDirection.RightToLeft };
            Button ok = new Button { Text = "保存", DialogResult = DialogResult.OK };
            Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            Controls.Add(descriptionBox);
            Controls.Add(titleBox);
            Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        public string TitleText { get { return titleBox.Text.Trim(); } }
        public string DescriptionText { get { return descriptionBox.Text; } }
    }
}
