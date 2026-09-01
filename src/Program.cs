using System;
using System.IO;
using System.Windows.Forms;
using TianshuQitanLauncher.Protocol;

namespace TianshuQitanLauncher
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            TrySetDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string configPath = Path.Combine(baseDirectory, "config.ini");
            LauncherConfig config = LauncherConfig.Load(configPath);
            AccountProfileStore accountStore = new AccountProfileStore(baseDirectory);
            accountStore.EnsureInitialized(Path.Combine(baseDirectory, "data", "secrets", "auto-login.json"));
            ClientInstanceContext instanceContext = ClientInstanceContext.Parse(args, baseDirectory, accountStore);
            Logger.Configure(Path.Combine(instanceContext.InstanceRoot, "logs"));

            BrowserFeatureControl.Apply();

            WorkbenchProfile sourceProfile = LoadWorkbenchProfile(args);
            instanceContext.LaunchProfilePath = sourceProfile.SourcePath;
            WorkbenchProfile runtimeProfile = sourceProfile.CreateIsolatedRuntime(
                instanceContext.InstanceRoot,
                instanceContext.ShortInstanceId,
                Path.Combine(accountStore.GetAccountDirectory(instanceContext.AccountId), "operation-templates.json"));
            using (ClientInstanceLease lease = new ClientInstanceLease(instanceContext))
            {
                Application.Run(new MainForm(config, runtimeProfile, instanceContext, accountStore));
            }
        }

        private static WorkbenchProfile LoadWorkbenchProfile(string[] args)
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string profilePath = null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--profile", StringComparison.OrdinalIgnoreCase))
                {
                    profilePath = args[i + 1];
                    break;
                }
            }
            if (string.IsNullOrWhiteSpace(profilePath))
            {
                profilePath = Environment.GetEnvironmentVariable("TSQ_PROFILE");
            }
            if (string.IsNullOrWhiteSpace(profilePath))
            {
                return WorkbenchProfile.LoadDefault(baseDirectory);
            }
            if (!Path.IsPathRooted(profilePath))
            {
                profilePath = Path.Combine(baseDirectory, profilePath);
            }
            return WorkbenchProfile.Load(Path.GetFullPath(profilePath), baseDirectory);
        }

        private static void TrySetDpiAware()
        {
            try
            {
                NativeMethods.SetProcessDPIAware();
            }
            catch
            {
            }
        }
    }
}
