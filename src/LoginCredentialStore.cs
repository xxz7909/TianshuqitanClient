using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace TianshuQitanLauncher
{
    public sealed class LoginAutomationSettings
    {
        public int Version { get; set; }
        public bool Enabled { get; set; }
        public string Username { get; set; }
        public int LineNumber { get; set; }
        public int RoleSlot { get; set; }
        public string ProtectedPassword { get; set; }

        public LoginAutomationSettings()
        {
            Version = 2;
            Username = string.Empty;
            LineNumber = 1;
            RoleSlot = 1;
            ProtectedPassword = string.Empty;
        }

        [JsonIgnore]
        public bool HasSavedPassword
        {
            get { return !string.IsNullOrWhiteSpace(ProtectedPassword); }
        }

        public LoginAutomationSettings Clone()
        {
            return (LoginAutomationSettings)MemberwiseClone();
        }
    }

    public sealed class LoginCredentialStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TianshuQitanLauncher.AutoLogin.v1");
        private readonly string path;
        private readonly string mutexName;

        public LoginCredentialStore(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Credential path is required.", "path");
            this.path = Path.GetFullPath(path);
            mutexName = "Local\\TianshuQitanLauncher.Login." + HashPath(this.path);
        }

        public string PathName
        {
            get { return path; }
        }

        public LoginAutomationSettings Load()
        {
            return ExecuteLocked(delegate
            {
                if (!File.Exists(path))
                {
                    return new LoginAutomationSettings();
                }
                LoginAutomationSettings settings = JsonConvert.DeserializeObject<LoginAutomationSettings>(File.ReadAllText(path));
                if (settings == null)
                {
                    return new LoginAutomationSettings();
                }
                settings.Version = 2;
                settings.Username = settings.Username ?? string.Empty;
                settings.ProtectedPassword = settings.ProtectedPassword ?? string.Empty;
                if (settings.LineNumber < 1 || settings.LineNumber > 4) settings.LineNumber = 1;
                if (settings.RoleSlot < 1 || settings.RoleSlot > 5) settings.RoleSlot = 1;
                return settings;
            });
        }

        public void Save(LoginAutomationSettings settings, string newPassword)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            LoginAutomationSettings saved = settings.Clone();
            saved.Version = 2;
            saved.Username = (saved.Username ?? string.Empty).Trim();
            if (saved.LineNumber < 1 || saved.LineNumber > 4) throw new ArgumentOutOfRangeException("settings.LineNumber");
            if (saved.RoleSlot < 1 || saved.RoleSlot > 5) throw new ArgumentOutOfRangeException("settings.RoleSlot");
            if (newPassword != null)
            {
                saved.ProtectedPassword = Protect(newPassword);
            }
            ExecuteLocked(delegate { WriteUnlocked(saved); });
        }

        public string ReadPassword(LoginAutomationSettings settings)
        {
            if (settings == null || !settings.HasSavedPassword)
            {
                return string.Empty;
            }
            byte[] protectedBytes = null;
            byte[] clearBytes = null;
            try
            {
                protectedBytes = Convert.FromBase64String(settings.ProtectedPassword);
                clearBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(clearBytes);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("保存的密码无法由当前 Windows 用户解密。", ex);
            }
            finally
            {
                Clear(protectedBytes);
                Clear(clearBytes);
            }
        }

        public void ClearPassword(LoginAutomationSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            LoginAutomationSettings saved = settings.Clone();
            saved.ProtectedPassword = string.Empty;
            ExecuteLocked(delegate { WriteUnlocked(saved); });
        }

        private static string Protect(string password)
        {
            byte[] clearBytes = null;
            byte[] protectedBytes = null;
            try
            {
                clearBytes = Encoding.UTF8.GetBytes(password ?? string.Empty);
                protectedBytes = ProtectedData.Protect(clearBytes, Entropy, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(protectedBytes);
            }
            finally
            {
                Clear(clearBytes);
                Clear(protectedBytes);
            }
        }

        private void WriteUnlocked(LoginAutomationSettings settings)
        {
            string directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
            string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    File.Replace(temporaryPath, path, null);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        private T ExecuteLocked<T>(Func<T> action)
        {
            using (Mutex mutex = new Mutex(false, mutexName))
            {
                bool entered = false;
                try
                {
                    try
                    {
                        entered = mutex.WaitOne(TimeSpan.FromSeconds(10));
                    }
                    catch (AbandonedMutexException)
                    {
                        entered = true;
                    }
                    if (!entered) throw new TimeoutException("等待账户凭据文件锁超时。");
                    return action();
                }
                finally
                {
                    if (entered) mutex.ReleaseMutex();
                }
            }
        }

        private void ExecuteLocked(Action action)
        {
            ExecuteLocked(delegate
            {
                action();
                return true;
            });
        }

        private static string HashPath(string value)
        {
            byte[] hash;
            using (SHA256 algorithm = SHA256.Create())
            {
                hash = algorithm.ComputeHash(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
            }
            StringBuilder text = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++) text.Append(hash[i].ToString("x2"));
            return text.ToString();
        }

        private static void Clear(byte[] bytes)
        {
            if (bytes != null) Array.Clear(bytes, 0, bytes.Length);
        }
    }
}
