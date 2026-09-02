using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TianshuQitanLauncher
{
    public sealed class ClientLogControl : UserControl
    {
        private const string AllModules = "全部模块";
        private const string AllLevels = "全部级别";

        private readonly ClientLogHub hub;
        private readonly DataGridView logGrid;
        private readonly ToolStripComboBox moduleFilter;
        private readonly ToolStripComboBox levelFilter;
        private readonly ToolStripTextBox keywordFilter;
        private readonly ToolStripButton autoScrollButton;
        private readonly System.Windows.Forms.Timer refreshTimer;
        private readonly List<ClientLogEntry> visibleEntries = new List<ClientLogEntry>();
        private int refreshRequested;
        private bool updatingFilters;

        public ClientLogControl(ClientLogHub hub)
        {
            if (hub == null) throw new ArgumentNullException("hub");
            this.hub = hub;
            Dock = DockStyle.Fill;

            ToolStrip tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
            tools.Items.Add(new ToolStripLabel("运行日志"));
            tools.Items.Add(new ToolStripSeparator());
            tools.Items.Add(new ToolStripLabel("模块"));
            moduleFilter = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 105 };
            moduleFilter.Items.Add(AllModules);
            moduleFilter.SelectedIndex = 0;
            moduleFilter.SelectedIndexChanged += delegate { RequestRefresh(); };
            tools.Items.Add(moduleFilter);
            tools.Items.Add(new ToolStripLabel("级别"));
            levelFilter = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 85 };
            levelFilter.Items.AddRange(new object[] { AllLevels, "信息", "警告", "错误" });
            levelFilter.SelectedIndex = 0;
            levelFilter.SelectedIndexChanged += delegate { RequestRefresh(); };
            tools.Items.Add(levelFilter);
            tools.Items.Add(new ToolStripLabel("查找"));
            keywordFilter = new ToolStripTextBox { Width = 125 };
            keywordFilter.TextChanged += delegate { RequestRefresh(); };
            tools.Items.Add(keywordFilter);
            autoScrollButton = new ToolStripButton("自动滚动") { CheckOnClick = true, Checked = true };
            tools.Items.Add(autoScrollButton);
            ToolStripButton copyButton = new ToolStripButton("复制选中");
            copyButton.Click += delegate { CopySelectedEntries(); };
            tools.Items.Add(copyButton);
            ToolStripButton clearButton = new ToolStripButton("清空日志");
            clearButton.ToolTipText = "仅清空统一运行日志的界面缓存，不影响审计日志、SQLite 或磁盘日志。";
            clearButton.Click += delegate { hub.Clear(); };
            tools.Items.Add(clearButton);

            logGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                VirtualMode = true,
                MultiSelect = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                AutoGenerateColumns = false,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.Fixed3D
            };
            AddColumn("时间", 95, DataGridViewAutoSizeColumnMode.None);
            AddColumn("级别", 55, DataGridViewAutoSizeColumnMode.None);
            AddColumn("模块", 85, DataGridViewAutoSizeColumnMode.None);
            AddColumn("状态", 105, DataGridViewAutoSizeColumnMode.None);
            AddColumn("消息", 300, DataGridViewAutoSizeColumnMode.Fill);
            logGrid.CellValueNeeded += OnCellValueNeeded;
            logGrid.CellFormatting += OnCellFormatting;
            logGrid.KeyDown += OnGridKeyDown;
            logGrid.ContextMenuStrip = CreateContextMenu();

            Controls.Add(logGrid);
            Controls.Add(tools);

            hub.EntryPublished += OnEntryPublished;
            hub.EntriesCleared += OnEntriesCleared;
            refreshTimer = new System.Windows.Forms.Timer { Interval = 250 };
            refreshTimer.Tick += delegate { FlushPendingEntries(); };
            refreshTimer.Start();
            RefreshEntries();
        }

        public ClientLogHub Hub { get { return hub; } }

        public void RefreshEntries()
        {
            if (IsDisposed) return;
            Interlocked.Exchange(ref refreshRequested, 0);
            IList<ClientLogEntry> snapshot = hub.Snapshot();
            UpdateModuleFilter(snapshot);
            string module = moduleFilter.SelectedItem as string;
            string level = levelFilter.SelectedItem as string;
            string keyword = keywordFilter.Text == null ? string.Empty : keywordFilter.Text.Trim();

            visibleEntries.Clear();
            for (int i = 0; i < snapshot.Count; i++)
            {
                ClientLogEntry entry = snapshot[i];
                if (!string.IsNullOrEmpty(module) && module != AllModules &&
                    !string.Equals(entry.Module, module, StringComparison.OrdinalIgnoreCase)) continue;
                if (!MatchesLevel(entry.Level, level)) continue;
                if (keyword.Length != 0 && !Contains(entry.Module, keyword) && !Contains(entry.State, keyword) &&
                    !Contains(entry.Message, keyword)) continue;
                visibleEntries.Add(entry);
            }

            logGrid.RowCount = visibleEntries.Count;
            logGrid.Invalidate();
            if (autoScrollButton.Checked && logGrid.RowCount > 0)
            {
                try { logGrid.FirstDisplayedScrollingRowIndex = logGrid.RowCount - 1; }
                catch (InvalidOperationException) { }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                hub.EntryPublished -= OnEntryPublished;
                hub.EntriesCleared -= OnEntriesCleared;
                refreshTimer.Stop();
                refreshTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        private void AddColumn(string name, int width, DataGridViewAutoSizeColumnMode sizeMode)
        {
            logGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = name,
                Width = width,
                AutoSizeMode = sizeMode,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        }

        private ContextMenuStrip CreateContextMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem copy = new ToolStripMenuItem("复制选中日志");
            copy.Click += delegate { CopySelectedEntries(); };
            ToolStripMenuItem clear = new ToolStripMenuItem("清空统一日志（仅界面）");
            clear.Click += delegate { hub.Clear(); };
            menu.Items.Add(copy);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(clear);
            return menu;
        }

        private void OnEntryPublished(ClientLogEntry entry)
        {
            RequestRefresh();
        }

        private void OnEntriesCleared()
        {
            RequestRefresh();
        }

        private void RequestRefresh()
        {
            if (updatingFilters || IsDisposed) return;
            Interlocked.Exchange(ref refreshRequested, 1);
        }

        private void FlushPendingEntries()
        {
            if (Interlocked.CompareExchange(ref refreshRequested, 0, 0) != 0) RefreshEntries();
        }

        private void UpdateModuleFilter(IList<ClientLogEntry> snapshot)
        {
            string selected = moduleFilter.SelectedItem as string ?? AllModules;
            string[] modules = snapshot.Select(item => item.Module).Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(item => item, StringComparer.CurrentCultureIgnoreCase).ToArray();
            bool same = moduleFilter.Items.Count == modules.Length + 1;
            if (same)
            {
                for (int i = 0; i < modules.Length; i++)
                {
                    if (!string.Equals(moduleFilter.Items[i + 1] as string, modules[i], StringComparison.Ordinal))
                    {
                        same = false;
                        break;
                    }
                }
            }
            if (same) return;

            updatingFilters = true;
            try
            {
                moduleFilter.Items.Clear();
                moduleFilter.Items.Add(AllModules);
                moduleFilter.Items.AddRange(modules.Cast<object>().ToArray());
                int selectedIndex = moduleFilter.Items.IndexOf(selected);
                moduleFilter.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
            }
            finally
            {
                updatingFilters = false;
            }
        }

        private static bool MatchesLevel(ClientLogLevel entryLevel, string selected)
        {
            if (string.IsNullOrEmpty(selected) || selected == AllLevels) return true;
            if (selected == "信息") return entryLevel == ClientLogLevel.Info;
            if (selected == "警告") return entryLevel == ClientLogLevel.Warning;
            if (selected == "错误") return entryLevel == ClientLogLevel.Error;
            return true;
        }

        private static bool Contains(string value, string keyword)
        {
            return value != null && value.IndexOf(keyword, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private void OnCellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= visibleEntries.Count) return;
            ClientLogEntry entry = visibleEntries[e.RowIndex];
            switch (e.ColumnIndex)
            {
                case 0: e.Value = entry.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff"); break;
                case 1: e.Value = LevelName(entry.Level); break;
                case 2: e.Value = entry.Module; break;
                case 3: e.Value = entry.State; break;
                case 4: e.Value = entry.Message; break;
            }
        }

        private void OnCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= visibleEntries.Count) return;
            ClientLogLevel level = visibleEntries[e.RowIndex].Level;
            if (level == ClientLogLevel.Error)
            {
                e.CellStyle.BackColor = Color.MistyRose;
                e.CellStyle.ForeColor = Color.DarkRed;
            }
            else if (level == ClientLogLevel.Warning)
            {
                e.CellStyle.BackColor = Color.LemonChiffon;
                e.CellStyle.ForeColor = Color.DarkGoldenrod;
            }
        }

        private void OnGridKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C)
            {
                CopySelectedEntries();
                e.Handled = true;
            }
        }

        private void CopySelectedEntries()
        {
            string text = BuildSelectedText();
            if (text.Length == 0) return;
            try { Clipboard.SetText(text); }
            catch (ExternalException) { }
        }

        private string BuildSelectedText()
        {
            int[] indexes = logGrid.SelectedRows.Cast<DataGridViewRow>().Select(row => row.Index)
                .Where(index => index >= 0 && index < visibleEntries.Count).OrderBy(index => index).ToArray();
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < indexes.Length; i++)
            {
                ClientLogEntry entry = visibleEntries[indexes[i]];
                if (builder.Length != 0) builder.AppendLine();
                builder.Append(entry.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff")).Append('\t')
                    .Append(LevelName(entry.Level)).Append('\t').Append(entry.Module).Append('\t')
                    .Append(entry.State).Append('\t').Append((entry.Message ?? string.Empty).Replace("\r", " ").Replace("\n", " "));
            }
            return builder.ToString();
        }

        private static string LevelName(ClientLogLevel level)
        {
            if (level == ClientLogLevel.Error) return "错误";
            if (level == ClientLogLevel.Warning) return "警告";
            return "信息";
        }
    }
}
