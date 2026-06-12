using System;
using System.IO;
using System.Windows.Forms;

namespace TianshuQitanLauncher
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            TrySetDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            BrowserFeatureControl.Apply();

            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");
            LauncherConfig config = LauncherConfig.Load(configPath);

            Application.Run(new MainForm(config));
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
