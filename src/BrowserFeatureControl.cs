using System;
using Microsoft.Win32;

namespace TianshuQitanLauncher
{
    // 通过写入注册表 FeatureControl 项来控制 IE WebBrowser 控件的运行特性，
    // 让老 Flash 页面以兼容方式正确渲染。
    internal static class BrowserFeatureControl
    {
        public static void Apply()
        {
            string exeName = AppDomain.CurrentDomain.FriendlyName;

            // 自适应仿真值：按本机实际安装的 IE 版本选择，而不是写死 IE11。
            // 这样 Win7（IE8/9/10）等老系统也能正确渲染老 Flash 页面。
            int ieVersion = Compatibility.GetInstalledIeVersion();
            int emulation = Compatibility.GetBrowserEmulationValue(ieVersion);
            if (emulation == 0)
            {
                // 无法识别 IE 版本时不强制设定仿真值，避免把老 IE 机器错误仿真成 IE11 而打坏页面，
                // 交由 WebBrowser 使用系统默认行为。
                Logger.Error("无法识别本机 IE 版本（检测到 " + ieVersion + "），未强制设定浏览器仿真值", null);
            }
            else
            {
                Logger.Info("本机 IE 版本=" + ieVersion + "，浏览器仿真值=" + emulation);
                // 以对应 IE 模式渲染（避免被强制降级为 IE7 兼容视图）
                SetFeature("FEATURE_BROWSER_EMULATION", exeName, emulation);
            }
            // 关闭 GPU 渲染（老机器/虚拟机下兼容性更好）
            SetFeature("FEATURE_GPU_RENDERING", exeName, 0);
            // 使用传统输入模式（部分老 Flash 游戏依赖此行为）
            SetFeature("FEATURE_NINPUT_LEGACYMODE", exeName, 0);
            // 缓解 script: 协议带来的潜在安全问题
            SetFeature("FEATURE_SCRIPTURL_MITIGATION", exeName, 1);
            // 允许网页自身控制文档缩放
            SetFeature("FEATURE_WEBOC_DOCUMENT_ZOOM", exeName, 1);
            // 按 96 DPI 像素模式处理，避免缩放错位
            SetFeature("FEATURE_96DPI_PIXEL", exeName, 1);
        }

        // 在当前视图以及 32/64 位注册表视图下分别写入特性值，确保不同系统架构都能生效
        private static void SetFeature(string featureName, string exeName, int value)
        {
            string path = @"Software\Microsoft\Internet Explorer\Main\FeatureControl\" + featureName;
            SetFeatureInCurrentView(path, exeName, value);
            SetFeatureInRegistryView(RegistryView.Registry32, path, exeName, value);
            SetFeatureInRegistryView(RegistryView.Registry64, path, exeName, value);
        }

        // 写入当前用户视图下的注册表值
        private static void SetFeatureInCurrentView(string path, string exeName, int value)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(path))
                {
                    if (key != null)
                    {
                        // 值已等于目标则跳过写入，避免每次启动都重复写注册表（启动期 I/O 优化）
                        object existing = key.GetValue(exeName);
                        if (existing == null || Convert.ToInt32(existing) != value)
                        {
                            key.SetValue(exeName, value, RegistryValueKind.DWord);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to write browser feature control: " + path + " view=CurrentUser", ex);
            }
        }

        // 写入指定注册表视图（32/64 位）下的值；失败仅记录日志，不中断启动
        private static void SetFeatureInRegistryView(RegistryView view, string path, string exeName, int value)
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
                using (RegistryKey key = root.CreateSubKey(path))
                {
                    if (key != null)
                    {
                        // 值已等于目标则跳过写入，避免每次启动都重复写注册表
                        object existing = key.GetValue(exeName);
                        if (existing == null || Convert.ToInt32(existing) != value)
                        {
                            key.SetValue(exeName, value, RegistryValueKind.DWord);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to write browser feature control: " + path + " view=" + view, ex);
            }
        }
    }
}
