using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace TianshuQitanLauncher.Protocol
{
    public sealed class WorkbenchProfile
    {
        public string Name { get; set; }
        public string GameUrl { get; set; }
        public bool ActiveMode { get; set; }
        public string CaptureDirectory { get; set; }
        public string ProtocolPath { get; set; }
        public string RulesPath { get; set; }
        public string PacketScriptPath { get; set; }
        public string OperationsPath { get; set; }
        public bool OperationRecorderEnabled { get; set; }
        public IList<int> ProxyPorts { get; set; }
        public IList<int> PolicyPorts { get; set; }
        public int ScriptTimeoutMs { get; set; }
        public int CaptureQueueCapacity { get; set; }

        [JsonIgnore]
        public string BaseDirectory { get; private set; }

        [JsonIgnore]
        public string SourcePath { get; private set; }

        public WorkbenchProfile()
        {
            Name = "v5";
            GameUrl = "http://v5.t.imop.com/";
            ActiveMode = true;
            CaptureDirectory = "data/sessions";
            ProtocolPath = "protocols/v5/protocol.json";
            RulesPath = "protocols/v5/rules.json";
            PacketScriptPath = "protocols/v5/scripts/packet.lua";
            OperationsPath = "data/settings/operation-templates.json";
            OperationRecorderEnabled = false;
            ProxyPorts = new List<int> { 7890 };
            PolicyPorts = new List<int> { 843 };
            ScriptTimeoutMs = 50;
            CaptureQueueCapacity = 65536;
        }

        public static WorkbenchProfile LoadDefault(string baseDirectory)
        {
            return Load(Path.Combine(baseDirectory, "profiles", "v5.json"), baseDirectory);
        }

        public static WorkbenchProfile Load(string path, string baseDirectory)
        {
            WorkbenchProfile profile;
            if (!File.Exists(path))
            {
                profile = new WorkbenchProfile();
            }
            else
            {
                profile = JsonConvert.DeserializeObject<WorkbenchProfile>(File.ReadAllText(path)) ?? new WorkbenchProfile();
            }

            profile.BaseDirectory = baseDirectory;
            profile.SourcePath = path;
            profile.ScriptTimeoutMs = Math.Max(1, Math.Min(5000, profile.ScriptTimeoutMs));
            profile.CaptureQueueCapacity = Math.Max(1024, profile.CaptureQueueCapacity);
            return profile;
        }

        public void Save()
        {
            if (string.IsNullOrWhiteSpace(SourcePath))
            {
                throw new InvalidOperationException("The profile does not have a source path.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(SourcePath));
            File.WriteAllText(SourcePath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }

        public WorkbenchProfile CreateIsolatedRuntime(string instanceRoot, string instanceLabel)
        {
            return CreateIsolatedRuntime(instanceRoot, instanceLabel, null);
        }

        public WorkbenchProfile CreateIsolatedRuntime(string instanceRoot, string instanceLabel,
            string persistentOperationsPath)
        {
            if (string.IsNullOrWhiteSpace(instanceRoot)) throw new ArgumentException("Instance root is required.", "instanceRoot");
            string root = Path.GetFullPath(instanceRoot);
            string configurationRoot = Path.Combine(root, "configuration");
            Directory.CreateDirectory(configurationRoot);

            WorkbenchProfile clone = new WorkbenchProfile
            {
                Name = Name + "-" + (string.IsNullOrWhiteSpace(instanceLabel) ? "instance" : instanceLabel),
                GameUrl = GameUrl,
                ActiveMode = ActiveMode,
                OperationRecorderEnabled = OperationRecorderEnabled,
                ScriptTimeoutMs = ScriptTimeoutMs,
                CaptureQueueCapacity = CaptureQueueCapacity,
                ProxyPorts = new List<int>(ProxyPorts ?? new List<int>()),
                PolicyPorts = new List<int>(PolicyPorts ?? new List<int>())
            };
            clone.BaseDirectory = BaseDirectory;
            clone.SourcePath = Path.Combine(configurationRoot, "runtime-profile.json");
            clone.CaptureDirectory = Path.Combine(root, "sessions");
            clone.OperationsPath = string.IsNullOrWhiteSpace(persistentOperationsPath)
                ? Path.Combine(root, "settings", "operation-templates.json")
                : Path.GetFullPath(persistentOperationsPath);
            clone.ProtocolPath = CopyRuntimeFile(Resolve(ProtocolPath), Path.Combine(configurationRoot, "protocol.json"));
            clone.RulesPath = CopyRuntimeFile(Resolve(RulesPath), Path.Combine(configurationRoot, "rules.json"));
            clone.PacketScriptPath = CopyRuntimeFile(Resolve(PacketScriptPath), Path.Combine(configurationRoot, "packet.lua"));

            string sourceOperations = Resolve(OperationsPath);
            if (File.Exists(sourceOperations) && !File.Exists(clone.OperationsPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(clone.OperationsPath));
                try
                {
                    File.Copy(sourceOperations, clone.OperationsPath, false);
                }
                catch (IOException)
                {
                    if (!File.Exists(clone.OperationsPath)) throw;
                }
            }
            clone.Save();
            return clone;
        }

        public string Resolve(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }
            return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(BaseDirectory, path));
        }

        public bool IsProxyPort(int port)
        {
            return ContainsPort(ProxyPorts, port);
        }

        public bool IsPolicyPort(int port)
        {
            return ContainsPort(PolicyPorts, port);
        }

        private static bool ContainsPort(IList<int> ports, int port)
        {
            if (ports == null)
            {
                return false;
            }
            for (int i = 0; i < ports.Count; i++)
            {
                if (ports[i] == port)
                {
                    return true;
                }
            }
            return false;
        }

        private static string CopyRuntimeFile(string sourcePath, string targetPath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return sourcePath;
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
            File.Copy(sourcePath, targetPath, true);
            return targetPath;
        }
    }
}
