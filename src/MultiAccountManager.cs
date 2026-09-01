using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace TianshuQitanLauncher
{
    public sealed class LauncherAccountProfile
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }

        public LauncherAccountProfile Clone()
        {
            return (LauncherAccountProfile)MemberwiseClone();
        }
    }

    public sealed class LauncherAccountCatalog
    {
        public int Version { get; set; }
        public IList<LauncherAccountProfile> Accounts { get; set; }

        public LauncherAccountCatalog()
        {
            Version = 1;
            Accounts = new List<LauncherAccountProfile>();
        }
    }

    public sealed class AccountProfileStore
    {
        public const string DefaultAccountId = "default";
        private readonly string rootDirectory;
        private readonly string catalogPath;
        private readonly string mutexName;

        public AccountProfileStore(string applicationBaseDirectory)
        {
            if (string.IsNullOrWhiteSpace(applicationBaseDirectory)) throw new ArgumentException("Base directory is required.", "applicationBaseDirectory");
            rootDirectory = Path.Combine(Path.GetFullPath(applicationBaseDirectory), "data", "accounts");
            catalogPath = Path.Combine(rootDirectory, "accounts.json");
            mutexName = "Local\\TianshuQitanLauncher.Accounts." + StablePathHash(rootDirectory);
        }

        public string RootDirectory { get { return rootDirectory; } }

        public void EnsureInitialized(string legacyCredentialPath)
        {
            ExecuteLocked(delegate
            {
                Directory.CreateDirectory(rootDirectory);
                LauncherAccountCatalog catalog = ReadCatalogUnlocked();
                if (FindAccount(catalog.Accounts, DefaultAccountId) == null)
                {
                    DateTime now = DateTime.UtcNow;
                    catalog.Accounts.Add(new LauncherAccountProfile
                    {
                        Id = DefaultAccountId,
                        DisplayName = "默认账户",
                        CreatedUtc = now,
                        UpdatedUtc = now
                    });
                    WriteCatalogUnlocked(catalog);
                }
                string target = GetCredentialPath(DefaultAccountId);
                if (!File.Exists(target) && !string.IsNullOrWhiteSpace(legacyCredentialPath) && File.Exists(legacyCredentialPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(legacyCredentialPath, target, false);
                }
                return 0;
            });
        }

        public IList<LauncherAccountProfile> LoadAccounts()
        {
            return ExecuteLocked(delegate
            {
                LauncherAccountCatalog catalog = ReadCatalogUnlocked();
                List<LauncherAccountProfile> result = new List<LauncherAccountProfile>();
                for (int i = 0; i < catalog.Accounts.Count; i++) result.Add(catalog.Accounts[i].Clone());
                result.Sort(delegate(LauncherAccountProfile left, LauncherAccountProfile right)
                {
                    if (left.Id == DefaultAccountId && right.Id != DefaultAccountId) return -1;
                    if (right.Id == DefaultAccountId && left.Id != DefaultAccountId) return 1;
                    return StringComparer.CurrentCultureIgnoreCase.Compare(left.DisplayName, right.DisplayName);
                });
                return (IList<LauncherAccountProfile>)result;
            });
        }

        public LauncherAccountProfile GetAccount(string accountId)
        {
            ValidateAccountId(accountId);
            return ExecuteLocked(delegate
            {
                LauncherAccountProfile profile = FindAccount(ReadCatalogUnlocked().Accounts, accountId);
                return profile == null ? null : profile.Clone();
            });
        }

        public LauncherAccountProfile CreateAccount(string displayName)
        {
            string normalizedName = NormalizeDisplayName(displayName);
            return ExecuteLocked(delegate
            {
                LauncherAccountCatalog catalog = ReadCatalogUnlocked();
                DateTime now = DateTime.UtcNow;
                LauncherAccountProfile account = new LauncherAccountProfile
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DisplayName = normalizedName,
                    CreatedUtc = now,
                    UpdatedUtc = now
                };
                catalog.Accounts.Add(account);
                WriteCatalogUnlocked(catalog);
                Directory.CreateDirectory(GetAccountDirectory(account.Id));
                return account.Clone();
            });
        }

        public void SaveAccount(LauncherAccountProfile account)
        {
            if (account == null) throw new ArgumentNullException("account");
            ValidateAccountId(account.Id);
            string normalizedName = NormalizeDisplayName(account.DisplayName);
            ExecuteLocked(delegate
            {
                LauncherAccountCatalog catalog = ReadCatalogUnlocked();
                LauncherAccountProfile saved = FindAccount(catalog.Accounts, account.Id);
                if (saved == null) throw new InvalidOperationException("账户不存在：" + account.Id);
                saved.DisplayName = normalizedName;
                saved.UpdatedUtc = DateTime.UtcNow;
                WriteCatalogUnlocked(catalog);
                return 0;
            });
        }

        public void DeleteAccount(string accountId)
        {
            ValidateAccountId(accountId);
            if (accountId == DefaultAccountId) throw new InvalidOperationException("默认账户不能删除。");
            ExecuteLocked(delegate
            {
                LauncherAccountCatalog catalog = ReadCatalogUnlocked();
                LauncherAccountProfile account = FindAccount(catalog.Accounts, accountId);
                if (account == null) return 0;
                catalog.Accounts.Remove(account);
                WriteCatalogUnlocked(catalog);
                string accountDirectory = GetAccountDirectory(accountId);
                if (Directory.Exists(accountDirectory)) Directory.Delete(accountDirectory, true);
                return 0;
            });
        }

        public string GetAccountDirectory(string accountId)
        {
            ValidateAccountId(accountId);
            return Path.Combine(rootDirectory, accountId);
        }

        public string GetCredentialPath(string accountId)
        {
            return Path.Combine(GetAccountDirectory(accountId), "login.json");
        }

        private LauncherAccountCatalog ReadCatalogUnlocked()
        {
            if (!File.Exists(catalogPath)) return new LauncherAccountCatalog();
            LauncherAccountCatalog catalog = JsonConvert.DeserializeObject<LauncherAccountCatalog>(File.ReadAllText(catalogPath));
            if (catalog == null) catalog = new LauncherAccountCatalog();
            if (catalog.Accounts == null) catalog.Accounts = new List<LauncherAccountProfile>();
            catalog.Version = 1;
            return catalog;
        }

        private void WriteCatalogUnlocked(LauncherAccountCatalog catalog)
        {
            Directory.CreateDirectory(rootDirectory);
            string temporaryPath = catalogPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(catalog, Formatting.Indented), new UTF8Encoding(false));
            try
            {
                if (File.Exists(catalogPath)) File.Replace(temporaryPath, catalogPath, null);
                else File.Move(temporaryPath, catalogPath);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        private T ExecuteLocked<T>(Func<T> callback)
        {
            using (Mutex mutex = new Mutex(false, mutexName))
            {
                bool acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(10000); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new TimeoutException("等待多账户配置锁超时。");
                    return callback();
                }
                finally
                {
                    if (acquired) mutex.ReleaseMutex();
                }
            }
        }

        private static LauncherAccountProfile FindAccount(IList<LauncherAccountProfile> accounts, string id)
        {
            if (accounts == null) return null;
            for (int i = 0; i < accounts.Count; i++)
                if (string.Equals(accounts[i].Id, id, StringComparison.OrdinalIgnoreCase)) return accounts[i];
            return null;
        }

        private static string NormalizeDisplayName(string value)
        {
            string result = (value ?? string.Empty).Trim();
            if (result.Length == 0) throw new ArgumentException("账户名称不能为空。", "displayName");
            if (result.Length > 80) result = result.Substring(0, 80);
            return result;
        }

        internal static void ValidateAccountId(string value)
        {
            if (string.Equals(value, DefaultAccountId, StringComparison.OrdinalIgnoreCase)) return;
            Guid parsed;
            if (string.IsNullOrWhiteSpace(value) || !Guid.TryParseExact(value, "N", out parsed))
                throw new ArgumentException("无效的账户 ID。", "accountId");
        }

        private static string StablePathHash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
                StringBuilder text = new StringBuilder(16);
                for (int i = 0; i < 8; i++) text.Append(hash[i].ToString("X2"));
                return text.ToString();
            }
        }
    }

    public sealed class ClientInstanceContext
    {
        public string ApplicationBaseDirectory { get; private set; }
        public string AccountId { get; private set; }
        public string AccountDisplayName { get; private set; }
        public string InstanceId { get; private set; }
        public string InstanceRoot { get; private set; }
        public string CredentialPath { get; private set; }
        public int WindowSlot { get; private set; }
        public bool ManagedLaunch { get; private set; }
        public string LaunchProfilePath { get; set; }

        private ClientInstanceContext()
        {
        }

        public static ClientInstanceContext Parse(string[] args, string baseDirectory, AccountProfileStore store)
        {
            if (store == null) throw new ArgumentNullException("store");
            string accountId = ReadArgument(args, "--account") ?? AccountProfileStore.DefaultAccountId;
            AccountProfileStore.ValidateAccountId(accountId);
            LauncherAccountProfile account = store.GetAccount(accountId);
            if (account == null) throw new InvalidOperationException("找不到启动账户：" + accountId);

            string instanceId = ReadArgument(args, "--instance");
            bool managed = !string.IsNullOrWhiteSpace(instanceId);
            Guid instanceGuid;
            if (string.IsNullOrWhiteSpace(instanceId))
                instanceId = Guid.NewGuid().ToString("N");
            else if (!Guid.TryParseExact(instanceId, "N", out instanceGuid))
                throw new ArgumentException("无效的实例 ID。");

            int windowSlot = 0;
            string slotText = ReadArgument(args, "--window-slot");
            if (!Int32.TryParse(slotText, out windowSlot) || windowSlot < 0 || windowSlot > 31)
                windowSlot = Math.Abs(instanceId.GetHashCode()) % 8;

            string fullBase = Path.GetFullPath(baseDirectory);
            string root = Path.Combine(fullBase, "data", "instances", instanceId);
            Directory.CreateDirectory(root);
            return new ClientInstanceContext
            {
                ApplicationBaseDirectory = fullBase,
                AccountId = account.Id,
                AccountDisplayName = account.DisplayName,
                InstanceId = instanceId,
                InstanceRoot = root,
                CredentialPath = store.GetCredentialPath(account.Id),
                WindowSlot = windowSlot,
                ManagedLaunch = managed
            };
        }

        public string ShortInstanceId
        {
            get { return InstanceId.Length <= 8 ? InstanceId : InstanceId.Substring(0, 8); }
        }

        private static string ReadArgument(string[] args, string name)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
    }

    public sealed class ClientInstanceRecord
    {
        public int Version { get; set; }
        public string InstanceId { get; set; }
        public string AccountId { get; set; }
        public string AccountDisplayName { get; set; }
        public int ProcessId { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime? EndedUtc { get; set; }
        public string ExecutablePath { get; set; }
        public string InstanceRoot { get; set; }
    }

    public sealed class ClientInstanceLease : IDisposable
    {
        private readonly string recordPath;
        private readonly ClientInstanceRecord record;
        private bool disposed;

        public ClientInstanceLease(ClientInstanceContext context)
        {
            if (context == null) throw new ArgumentNullException("context");
            recordPath = Path.Combine(context.InstanceRoot, "instance.json");
            record = new ClientInstanceRecord
            {
                Version = 1,
                InstanceId = context.InstanceId,
                AccountId = context.AccountId,
                AccountDisplayName = context.AccountDisplayName,
                ProcessId = Process.GetCurrentProcess().Id,
                StartedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime(),
                ExecutablePath = Application.ExecutablePath,
                InstanceRoot = context.InstanceRoot
            };
            WriteRecord();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            record.EndedUtc = DateTime.UtcNow;
            try { WriteRecord(); } catch { }
        }

        private void WriteRecord()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(recordPath));
            string temporaryPath = recordPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(record, Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(recordPath)) File.Replace(temporaryPath, recordPath, null);
            else File.Move(temporaryPath, recordPath);
        }
    }

    public sealed class AccountRuntimeSummary
    {
        public LauncherAccountProfile Account { get; set; }
        public LoginAutomationSettings Login { get; set; }
        public int RunningInstances { get; set; }
    }

    public sealed class MultiAccountManager
    {
        private readonly string baseDirectory;
        private readonly string executablePath;
        private readonly string profilePath;
        private readonly AccountProfileStore store;
        private readonly ClientInstanceContext currentContext;

        public MultiAccountManager(string baseDirectory, string executablePath, string profilePath,
            AccountProfileStore store, ClientInstanceContext currentContext)
        {
            this.baseDirectory = Path.GetFullPath(baseDirectory);
            this.executablePath = Path.GetFullPath(executablePath);
            this.profilePath = profilePath;
            this.store = store;
            this.currentContext = currentContext;
        }

        public AccountProfileStore Store { get { return store; } }
        public ClientInstanceContext CurrentContext { get { return currentContext; } }

        public IList<AccountRuntimeSummary> GetAccountSummaries()
        {
            IList<ClientInstanceRecord> running = GetRunningInstances();
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < running.Count; i++)
            {
                int count;
                counts.TryGetValue(running[i].AccountId, out count);
                counts[running[i].AccountId] = count + 1;
            }
            IList<LauncherAccountProfile> accounts = store.LoadAccounts();
            List<AccountRuntimeSummary> result = new List<AccountRuntimeSummary>();
            for (int i = 0; i < accounts.Count; i++)
            {
                int count;
                counts.TryGetValue(accounts[i].Id, out count);
                result.Add(new AccountRuntimeSummary
                {
                    Account = accounts[i],
                    Login = new LoginCredentialStore(store.GetCredentialPath(accounts[i].Id)).Load(),
                    RunningInstances = count
                });
            }
            return result;
        }

        public Process Launch(string accountId)
        {
            return Launch(accountId, GetRunningInstances().Count % 8);
        }

        private Process Launch(string accountId, int slot)
        {
            LauncherAccountProfile account = store.GetAccount(accountId);
            if (account == null) throw new InvalidOperationException("账户不存在。");
            string instanceId = Guid.NewGuid().ToString("N");
            StringBuilder arguments = new StringBuilder();
            arguments.Append("--account ").Append(account.Id);
            arguments.Append(" --instance ").Append(instanceId);
            arguments.Append(" --window-slot ").Append(slot);
            if (!string.IsNullOrWhiteSpace(profilePath))
                arguments.Append(" --profile ").Append(QuoteArgument(profilePath));
            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments.ToString(),
                WorkingDirectory = Path.GetDirectoryName(executablePath),
                UseShellExecute = false,
                CreateNoWindow = false
            };
            return Process.Start(start);
        }

        public int LaunchAllNotRunning()
        {
            IList<AccountRuntimeSummary> accounts = GetAccountSummaries();
            int nextSlot = GetRunningInstances().Count % 8;
            int launched = 0;
            for (int i = 0; i < accounts.Count; i++)
            {
                if (accounts[i].RunningInstances > 0) continue;
                Launch(accounts[i].Account.Id, nextSlot);
                nextSlot = (nextSlot + 1) % 8;
                launched++;
            }
            return launched;
        }

        public void DeleteAccount(string accountId)
        {
            if (string.Equals(accountId, currentContext.AccountId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("不能删除当前客户端正在使用的账户。");
            IList<ClientInstanceRecord> running = GetRunningInstances();
            for (int i = 0; i < running.Count; i++)
                if (string.Equals(running[i].AccountId, accountId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("该账户仍有运行中的客户端，不能删除。");
            store.DeleteAccount(accountId);
        }

        public IList<ClientInstanceRecord> GetRunningInstances()
        {
            List<ClientInstanceRecord> result = new List<ClientInstanceRecord>();
            string instancesRoot = Path.Combine(baseDirectory, "data", "instances");
            if (!Directory.Exists(instancesRoot)) return result;
            string[] records = Directory.GetFiles(instancesRoot, "instance.json", SearchOption.AllDirectories);
            for (int i = 0; i < records.Length; i++)
            {
                try
                {
                    ClientInstanceRecord record = JsonConvert.DeserializeObject<ClientInstanceRecord>(File.ReadAllText(records[i]));
                    if (record == null || record.EndedUtc.HasValue || !IsProcessRecordAlive(record)) continue;
                    result.Add(record);
                }
                catch
                {
                }
            }
            return result;
        }

        public void OpenAccountDirectory(string accountId)
        {
            string directory = store.GetAccountDirectory(accountId);
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = QuoteArgument(directory), UseShellExecute = true });
        }

        private static bool IsProcessRecordAlive(ClientInstanceRecord record)
        {
            try
            {
                Process process = Process.GetProcessById(record.ProcessId);
                return !process.HasExited && Math.Abs((process.StartTime.ToUniversalTime() - record.StartedUtc).TotalSeconds) < 5;
            }
            catch
            {
                return false;
            }
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }
    }

    public sealed class MultiAccountControl : UserControl
    {
        private readonly MultiAccountManager manager;
        private readonly DataGridView grid;
        private readonly TextBox displayNameBox;
        private readonly TextBox usernameBox;
        private readonly TextBox passwordBox;
        private readonly CheckBox autoLoginBox;
        private readonly ComboBox lineBox;
        private readonly ComboBox roleBox;
        private readonly Label statusLabel;
        private readonly System.Windows.Forms.Timer refreshTimer;
        private string selectedAccountId;
        private bool suppressSelectionChanged;

        public MultiAccountControl(MultiAccountManager manager)
        {
            if (manager == null) throw new ArgumentNullException("manager");
            this.manager = manager;
            Dock = DockStyle.Fill;
            Padding = new Padding(6);

            grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            grid.Columns.Add("Name", "名称");
            grid.Columns.Add("Username", "账号");
            grid.Columns.Add("Line", "线路");
            grid.Columns.Add("Role", "角色");
            grid.Columns.Add("Auto", "自动登录");
            grid.Columns.Add("Running", "运行实例");
            grid.SelectionChanged += OnSelectionChanged;

            displayNameBox = new TextBox { Width = 120 };
            usernameBox = new TextBox { Width = 150, MaxLength = 128 };
            passwordBox = new TextBox { Width = 130, MaxLength = 256, UseSystemPasswordChar = true };
            autoLoginBox = new CheckBox { Text = "自动登录", AutoSize = true };
            lineBox = new ComboBox { Width = 70, DropDownStyle = ComboBoxStyle.DropDownList };
            lineBox.Items.AddRange(new object[] { "一线", "二线", "三线", "四线" });
            roleBox = new ComboBox { Width = 85, DropDownStyle = ComboBoxStyle.DropDownList };
            roleBox.Items.AddRange(new object[] { "角色1", "角色2", "角色3", "角色4", "角色5" });

            FlowLayoutPanel editor = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
            editor.Controls.Add(LabelFor("名称"));
            editor.Controls.Add(displayNameBox);
            editor.Controls.Add(LabelFor("账号"));
            editor.Controls.Add(usernameBox);
            editor.Controls.Add(LabelFor("密码"));
            editor.Controls.Add(passwordBox);
            editor.Controls.Add(lineBox);
            editor.Controls.Add(roleBox);
            editor.Controls.Add(autoLoginBox);

            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
            Button newButton = new Button { Text = "新建", AutoSize = true };
            Button saveButton = new Button { Text = "保存账户", AutoSize = true };
            Button deleteButton = new Button { Text = "删除", AutoSize = true };
            Button launchButton = new Button { Text = "启动选中账户", AutoSize = true };
            Button launchAllButton = new Button { Text = "启动全部未运行", AutoSize = true };
            Button folderButton = new Button { Text = "账户目录", AutoSize = true };
            newButton.Click += delegate { ClearEditor(); };
            saveButton.Click += delegate { SaveEditor(); };
            deleteButton.Click += OnDelete;
            launchButton.Click += OnLaunch;
            launchAllButton.Click += OnLaunchAll;
            folderButton.Click += OnOpenFolder;
            buttons.Controls.Add(newButton);
            buttons.Controls.Add(saveButton);
            buttons.Controls.Add(deleteButton);
            buttons.Controls.Add(launchButton);
            buttons.Controls.Add(launchAllButton);
            buttons.Controls.Add(folderButton);

            statusLabel = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ForeColor = Color.DarkSlateBlue,
                Text = "当前实例：" + manager.CurrentContext.AccountDisplayName + " / " + manager.CurrentContext.ShortInstanceId
            };
            Label note = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ForeColor = Color.DimGray,
                Text = "每个客户端使用独立 SQLite、日志、规则副本、音频缓存和动态代理端口；密码按账户使用 Windows DPAPI 加密。密码留空保存表示保留原密码。"
            };

            TableLayoutPanel top = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 1,
                RowCount = 4,
                GrowStyle = TableLayoutPanelGrowStyle.AddRows
            };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.Controls.Add(note, 0, 0);
            top.Controls.Add(editor, 0, 1);
            top.Controls.Add(buttons, 0, 2);
            top.Controls.Add(statusLabel, 0, 3);
            Controls.Add(grid);
            Controls.Add(top);

            refreshTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            refreshTimer.Tick += delegate { RefreshGrid(); };
            refreshTimer.Start();
            selectedAccountId = manager.CurrentContext.AccountId;
            RefreshGrid();
            LoadSelectedEditor();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                refreshTimer.Stop();
                refreshTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        private void RefreshGrid()
        {
            if (IsDisposed) return;
            string keep = selectedAccountId;
            IList<AccountRuntimeSummary> accounts;
            try { accounts = manager.GetAccountSummaries(); }
            catch (Exception ex) { SetStatus(ex.Message, true); return; }
            suppressSelectionChanged = true;
            try
            {
                grid.Rows.Clear();
                int selectIndex = -1;
                for (int i = 0; i < accounts.Count; i++)
                {
                    AccountRuntimeSummary item = accounts[i];
                    int rowIndex = grid.Rows.Add(
                        item.Account.DisplayName,
                        item.Login.Username,
                        LineName(item.Login.LineNumber),
                        "第" + item.Login.RoleSlot + "个",
                        item.Login.Enabled ? "是" : "否",
                        item.RunningInstances);
                    grid.Rows[rowIndex].Tag = item;
                    if (item.Account.Id == keep) selectIndex = rowIndex;
                }
                grid.ClearSelection();
                if (selectIndex >= 0)
                {
                    grid.Rows[selectIndex].Selected = true;
                    grid.CurrentCell = grid.Rows[selectIndex].Cells[0];
                }
                else
                {
                    grid.CurrentCell = null;
                }
            }
            finally
            {
                suppressSelectionChanged = false;
            }
        }

        private void OnSelectionChanged(object sender, EventArgs e)
        {
            if (suppressSelectionChanged) return;
            LoadSelectedEditor();
        }

        private void LoadSelectedEditor()
        {
            if (grid.SelectedRows.Count == 0) return;
            AccountRuntimeSummary item = grid.SelectedRows[0].Tag as AccountRuntimeSummary;
            if (item == null) return;
            selectedAccountId = item.Account.Id;
            displayNameBox.Text = item.Account.DisplayName;
            usernameBox.Text = item.Login.Username;
            passwordBox.Text = string.Empty;
            autoLoginBox.Checked = item.Login.Enabled;
            lineBox.SelectedIndex = Math.Max(0, Math.Min(3, item.Login.LineNumber - 1));
            roleBox.SelectedIndex = Math.Max(0, Math.Min(4, item.Login.RoleSlot - 1));
        }

        private void ClearEditor()
        {
            suppressSelectionChanged = true;
            try
            {
                grid.ClearSelection();
                grid.CurrentCell = null;
            }
            finally
            {
                suppressSelectionChanged = false;
            }
            selectedAccountId = null;
            displayNameBox.Text = string.Empty;
            usernameBox.Text = string.Empty;
            passwordBox.Text = string.Empty;
            autoLoginBox.Checked = true;
            lineBox.SelectedIndex = 0;
            roleBox.SelectedIndex = 0;
            displayNameBox.Focus();
            SetStatus("填写新账户后点击“保存账户”。", false);
        }

        private LauncherAccountProfile SaveEditor()
        {
            try
            {
                LauncherAccountProfile account = selectedAccountId == null
                    ? manager.Store.CreateAccount(displayNameBox.Text)
                    : manager.Store.GetAccount(selectedAccountId);
                account.DisplayName = displayNameBox.Text;
                manager.Store.SaveAccount(account);

                LoginCredentialStore credentials = new LoginCredentialStore(manager.Store.GetCredentialPath(account.Id));
                LoginAutomationSettings login = credentials.Load();
                login.Enabled = autoLoginBox.Checked;
                login.Username = usernameBox.Text.Trim();
                login.LineNumber = lineBox.SelectedIndex < 0 ? 1 : lineBox.SelectedIndex + 1;
                login.RoleSlot = roleBox.SelectedIndex < 0 ? 1 : roleBox.SelectedIndex + 1;
                credentials.Save(login, passwordBox.Text.Length == 0 ? null : passwordBox.Text);
                passwordBox.Text = string.Empty;
                selectedAccountId = account.Id;
                RefreshGrid();
                SetStatus("账户已保存；密码未写入账户目录明文。", false);
                return account;
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, true);
                return null;
            }
        }

        private void OnDelete(object sender, EventArgs e)
        {
            if (selectedAccountId == null) return;
            if (MessageBox.Show(this, "删除账户会删除其加密凭据，确定继续吗？", "多账户管理",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                manager.DeleteAccount(selectedAccountId);
                selectedAccountId = null;
                ClearEditor();
                RefreshGrid();
                SetStatus("账户及其加密凭据已删除，无法恢复。", false);
            }
            catch (Exception ex) { SetStatus(ex.Message, true); }
        }

        private void OnLaunch(object sender, EventArgs e)
        {
            LauncherAccountProfile account = SaveEditor();
            if (account == null) return;
            try
            {
                Process process = manager.Launch(account.Id);
                SetStatus("已启动“" + account.DisplayName + "”，PID " + process.Id + "。", false);
                RefreshGrid();
            }
            catch (Exception ex) { SetStatus(ex.Message, true); }
        }

        private void OnLaunchAll(object sender, EventArgs e)
        {
            try
            {
                int count = manager.LaunchAllNotRunning();
                SetStatus(count == 0 ? "所有账户均已有运行实例。" : "已启动 " + count + " 个账户客户端。", false);
                RefreshGrid();
            }
            catch (Exception ex) { SetStatus(ex.Message, true); }
        }

        private void OnOpenFolder(object sender, EventArgs e)
        {
            if (selectedAccountId == null) return;
            try { manager.OpenAccountDirectory(selectedAccountId); }
            catch (Exception ex) { SetStatus(ex.Message, true); }
        }

        private void SetStatus(string text, bool error)
        {
            statusLabel.ForeColor = error ? Color.DarkRed : Color.DarkSlateBlue;
            statusLabel.Text = text;
        }

        private static Label LabelFor(string text)
        {
            return new Label { Text = text, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        }

        private static string LineName(int lineNumber)
        {
            string[] names = { "一线", "二线", "三线", "四线" };
            return lineNumber >= 1 && lineNumber <= names.Length ? names[lineNumber - 1] : lineNumber + "线";
        }
    }
}
