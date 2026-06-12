using System;
using Microsoft.Win32;

namespace TianshuQitanLauncher
{
    internal static class BrowserFeatureControl
    {
        public static void Apply()
        {
            string exeName = AppDomain.CurrentDomain.FriendlyName;

            SetFeature("FEATURE_BROWSER_EMULATION", exeName, 11001);
            SetFeature("FEATURE_GPU_RENDERING", exeName, 0);
            SetFeature("FEATURE_NINPUT_LEGACYMODE", exeName, 0);
            SetFeature("FEATURE_SCRIPTURL_MITIGATION", exeName, 1);
            SetFeature("FEATURE_WEBOC_DOCUMENT_ZOOM", exeName, 1);
            SetFeature("FEATURE_96DPI_PIXEL", exeName, 1);
        }

        private static void SetFeature(string featureName, string exeName, int value)
        {
            string path = @"Software\Microsoft\Internet Explorer\Main\FeatureControl\" + featureName;
            SetFeatureInCurrentView(path, exeName, value);
            SetFeatureInRegistryView(RegistryView.Registry32, path, exeName, value);
            SetFeatureInRegistryView(RegistryView.Registry64, path, exeName, value);
        }

        private static void SetFeatureInCurrentView(string path, string exeName, int value)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(path))
            {
                if (key != null)
                {
                    key.SetValue(exeName, value, RegistryValueKind.DWord);
                }
            }
        }

        private static void SetFeatureInRegistryView(RegistryView view, string path, string exeName, int value)
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
                using (RegistryKey key = root.CreateSubKey(path))
                {
                    if (key != null)
                    {
                        key.SetValue(exeName, value, RegistryValueKind.DWord);
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
