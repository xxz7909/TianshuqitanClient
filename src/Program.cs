using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace TianshuQitanLauncher
{
    // 应用程序入口
    internal static class Program
    {
        // 入口方法。注意 WebBrowser 控件要求运行在 STA（单线程单元）线程上
        [STAThread]
        private static int Main(string[] args)
        {
            // 让进程感知系统 DPI，避免高分屏下窗口与网页被拉伸模糊
            TrySetDpiAware();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 单实例：用命名互斥体防止多开多个游戏窗口（避免重复占用 Flash/IE 资源）
            bool createdNew;
            using (Mutex mutex = new Mutex(true, SingleInstanceMutexName, out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show(
                        "Tianshu Qitan 启动器已经在运行中，请勿重复打开。",
                        "Tianshu Qitan",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return 0;
                }

                // 向注册表写入 IE 特性控制，强制 WebBrowser 以兼容模式运行老 Flash 页面
                BrowserFeatureControl.Apply();

                // 启动前环境自检：IE 版本过低或 Flash 缺失时给出明确告警，
                // 但仍允许在隔离虚拟机等“特殊但可用”的环境中强行启动。
                WarnIfEnvironmentIncompatible();

                // 解析命令行覆盖项（--config= / --url= / --help）
                string configPath;
                string overrideUrl;
                ParseCommandLine(args, out configPath, out overrideUrl);

                // 从指定路径读取 config.ini（文件缺失时使用内置默认值）
                LauncherConfig config = LauncherConfig.Load(configPath);
                if (overrideUrl != null)
                {
                    config.OverrideGameUrl(overrideUrl);
                }

                // 把日志轮转上限交给日志器，避免诊断日志长期运行撑满磁盘
                Logger.SetMaxLogSizeBytes(config.MaxLogSizeBytes);

                // 启动主窗口的消息循环；退出时（含异常路径）确保残留日志落盘
                try
                {
                    Application.Run(new MainForm(config));
                }
                finally
                {
                    Logger.Shutdown();
                }
            }

            return 0;
        }

        // 旧式 DPI 感知 API；失败（如系统不支持）则静默忽略，不影响核心功能
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

        // 环境兼容性自检：IE 版本不足或 Flash 未注册时弹出一次性告警，但仍继续启动
        private static void WarnIfEnvironmentIncompatible()
        {
            int ieVersion = Compatibility.GetInstalledIeVersion();
            bool flashInstalled = Compatibility.IsFlashInstalled();

            string message = null;

            if (ieVersion == 0)
            {
                message = "未检测到 Internet Explorer，WebBrowser 控件可能无法运行。\n建议：在已安装 IE 的隔离虚拟机中使用本程序。";
            }
            else if (ieVersion < 8)
            {
                message = "当前 IE 版本（" + ieVersion + "）过低，游戏页面可能无法正常加载。\n建议：升级到 IE9 及以上，或在隔离虚拟机中运行。";
            }

            if (!flashInstalled)
            {
                message = (message == null ? "" : message + "\n\n")
                    + "未检测到 Flash Player（ActiveX）。游戏依赖 Flash 渲染，\n请在对应系统手动安装 Flash ActiveX，或改用支持 Flash 的隔离环境（如虚拟机）。";
            }

            if (message != null)
            {
                MessageBox.Show(message, "环境检测警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // 单实例互斥体名称；用于防止同一会话内多开多个游戏窗口（会话级，不跨用户/终端会话）
        private const string SingleInstanceMutexName = "TianshuQitanLauncher.SingleInstance.v1";

        // 解析命令行参数：
        //   --config=<path>  指定配置 ini 路径（默认取 exe 同目录的 config.ini）
        //   --url=<url>      临时覆盖游戏地址（不改动配置文件，重启后失效）
        //   --help / -? / /? 弹出用法说明
        private static void ParseCommandLine(string[] args, out string configPath, out string overrideUrl)
        {
            configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");
            overrideUrl = null;

            foreach (string arg in args)
            {
                if (arg.StartsWith("--config=", StringComparison.OrdinalIgnoreCase))
                {
                    configPath = arg.Substring("--config=".Length).Trim();
                }
                else if (arg.StartsWith("--url=", StringComparison.OrdinalIgnoreCase))
                {
                    overrideUrl = arg.Substring("--url=".Length).Trim();
                }
                else if (arg == "--help" || arg == "-?" || arg == "/?")
                {
                    ShowUsage();
                }
            }
        }

        // 弹出命令行用法说明（程序为 WinExe，无控制台，故用消息框呈现）
        private static void ShowUsage()
        {
            MessageBox.Show(
                "Tianshu Qitan 启动器\n\n" +
                "用法：\n" +
                "  TianshuQitanLauncher.exe [--config=<路径>] [--url=<游戏地址>] [--help]\n\n" +
                "--config=<路径>  使用指定位置的 config.ini（默认取程序同目录）\n" +
                "--url=<地址>     临时覆盖游戏页面地址（不写入配置文件）\n" +
                "--help           显示本帮助",
                "Tianshu Qitan - 用法",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }
}
