using System;
using Microsoft.Win32;

namespace TianshuQitanLauncher
{
    // 环境探测：在启动前检查本机 IE 版本与 Flash ActiveX 是否可用，
    // 并据此给出最合适的 WebBrowser 仿真值。
    // 这样即便在 Win7（IE8/9/10）等老系统上，也能正确加载老 Flash 页面，
    // 而不是写死 IE11 导致页面被降级到 IE7 兼容模式。
    internal static class Compatibility
    {
        // 返回本机安装的 IE 主版本号（如 8/9/10/11）；无法确定时返回 0
        public static int GetInstalledIeVersion()
        {
            // x86 进程在 64 位系统上会被注册表重定向到 WOW6432Node，
            // 而 IE 信息位于 native 视图，因此优先尝试 Registry64（native）视图，
            // 再回退到 Default（32 位视图，对应纯 32 位系统），避免一次注定失败的视图打开与误记错误日志。
            int version = ReadIeVersion(RegistryView.Registry64);
            if (version > 0)
            {
                return version;
            }

            return ReadIeVersion(RegistryView.Default);
        }

        private static int ReadIeVersion(RegistryView view)
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (RegistryKey key = root.OpenSubKey(@"Software\Microsoft\Internet Explorer"))
                {
                    if (key != null)
                    {
                        // Win7 及以后版本号位于 svcVersion，更老的版本位于 Version
                        string raw = (key.GetValue("svcVersion") as string) ?? (key.GetValue("Version") as string);
                        if (!string.IsNullOrEmpty(raw))
                        {
                            int major;
                            if (int.TryParse(raw.Split('.')[0], out major))
                            {
                                return major;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to detect IE version (view=" + view + ")", ex);
            }

            return 0;
        }

        // 根据已安装的 IE 版本，返回最合适的 FEATURE_BROWSER_EMULATION 值。
        // 注意：这里统一使用“尊重页面 !DOCTYPE”的取值（奇数），避免强制标准模式
        // 把老游戏的怪异模式（quirks）页面打坏。无法识别时返回 0。
        public static int GetBrowserEmulationValue(int ieVersion)
        {
            if (ieVersion >= 11) return 11001;
            if (ieVersion == 10) return 10001;
            if (ieVersion == 9) return 9999;
            if (ieVersion == 8) return 8888;
            if (ieVersion >= 7) return 7000;
            return 0; // 低于 IE7，WebBrowser 无法仿真
        }

        // 检测 Flash ActiveX 是否已注册（游戏依赖它来渲染）。
        // Flash 在不同系统/位数下注册位置不同，这里跨 32/64 位视图与多个已知路径一并检查。
        // 优先用 Flash 的 ActiveX CLSID（{D27CDB6E-AE6D-11CF-96B8-444553540000}）判定，最可靠；
        // 再辅以 ShockwaveFlash ProgID 与 Macromedia 注册项做交叉验证。
        public static bool IsFlashInstalled()
        {
            string[] paths = new[]
            {
                // Flash ActiveX 的 CLSID（32/64 位视图下分别对应不同位数安装）
                @"Software\Classes\CLSID\{D27CDB6E-AE6D-11CF-96B8-444553540000}\InprocServer32",
                @"Software\WOW6432Node\Classes\CLSID\{D27CDB6E-AE6D-11CF-96B8-444553540000}\InprocServer32",
                // 传统 ProgID 与厂商注册项
                @"Software\Classes\ShockwaveFlash.ShockwaveFlash\CurVer",
                @"Software\WOW6432Node\Classes\ShockwaveFlash.ShockwaveFlash\CurVer",
                @"Software\Macromedia\FlashPlayer",
                @"Software\WOW6432Node\Macromedia\FlashPlayer",
            };

            RegistryView[] views = new[] { RegistryView.Registry32, RegistryView.Registry64 };

            foreach (RegistryView view in views)
            {
                foreach (string path in paths)
                {
                    try
                    {
                        using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                        using (RegistryKey key = root.OpenSubKey(path))
                        {
                            if (key != null && key.ValueCount + key.SubKeyCount > 0)
                            {
                                return true;
                            }
                        }
                    }
                    catch
                    {
                        // 某个视图/路径无权限或不存在，忽略后继续尝试其他路径
                    }
                }
            }

            return false;
        }
    }
}
