using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace TianshuQitanLauncher
{
    // 启动器配置：从 config.ini 读取，未配置或非法项回退到内置默认值
    internal sealed class LauncherConfig
    {
        // 游戏页面地址
        public string GameUrl { get; private set; }
        // 启动即全屏
        public bool StartFullScreen { get; private set; }
        // 启动即适配工作区（填满屏幕但不遮挡任务栏）
        public bool StartFitToWorkArea { get; private set; }
        // 窗口初始宽度
        public int Width { get; private set; }
        // 窗口初始高度
        public int Height { get; private set; }
        // 浏览器视口模式：fill=填满窗口，fixed=固定尺寸
        public string BrowserViewportMode { get; private set; }
        // fixed 模式下的浏览器宽度
        public int BrowserWidth { get; private set; }
        // fixed 模式下的浏览器高度
        public int BrowserHeight { get; private set; }
        // 固定视口的对齐方式：topLeft / center
        public string BrowserAlign { get; private set; }
        // 是否强制修正 Flash 元素的定位（钉在左上角）
        public bool FixFlashPosition { get; private set; }
        // 窗口是否置顶
        public bool TopMost { get; private set; }
        // 是否抑制脚本错误弹窗
        public bool ScriptErrorsSuppressed { get; private set; }
        // 是否定时解除鼠标裁剪（防止游戏把鼠标锁死在窗口内）
        public bool ReleaseMouseClip { get; private set; }
        // 鼠标裁剪释放 / 顶部跳跃防护的轮询间隔（毫秒）
        public int MouseClipPollIntervalMs { get; private set; }
        // 是否启用鼠标诊断采样
        public bool MouseDiagnosticsEnabled { get; private set; }
        // 鼠标诊断采样间隔（毫秒）
        public int MouseDiagnosticsIntervalMs { get; private set; }
        // 是否启用“鼠标顶部跳跃”防护
        public bool MouseTopJumpGuardEnabled { get; private set; }
        // 触发顶部跳跃防护的向上位移阈值（像素）
        public int MouseTopJumpThresholdPixels { get; private set; }
        // 纠正后鼠标回落到 Flash 区域顶部的偏移（像素）
        public int MouseTopJumpReturnOffsetPixels { get; private set; }
        // 单个日志文件达到此大小（字节）后自动轮转，避免长期运行撑满磁盘；0 表示不限制
        public long MaxLogSizeBytes { get; private set; }

        // 构造内置默认值（config.ini 缺失时作为兜底配置）
        private LauncherConfig()
        {
            // 内置默认值需与随程序发布的 config.ini 保持一致，确保 config.ini 缺失时行为不变
            GameUrl = "http://v5.t.imop.com/";
            StartFullScreen = false;
            StartFitToWorkArea = true;
            Width = 1280;
            Height = 720;
            BrowserViewportMode = "fixed";
            BrowserWidth = 1005;
            BrowserHeight = 600;
            BrowserAlign = "topLeft";
            FixFlashPosition = true;
            TopMost = false;
            ScriptErrorsSuppressed = true;
            ReleaseMouseClip = true;
            MouseClipPollIntervalMs = 30;
            MouseDiagnosticsEnabled = true;
            MouseDiagnosticsIntervalMs = 1000;
            MouseTopJumpGuardEnabled = false;
            MouseTopJumpThresholdPixels = 120;
            MouseTopJumpReturnOffsetPixels = 8;
            MaxLogSizeBytes = 10L * 1024 * 1024;
        }

        // 加载配置：优先使用 ini 中的值，缺失或非法则回退默认值
        public static LauncherConfig Load(string path)
        {
            LauncherConfig config = new LauncherConfig();

            if (!File.Exists(path))
            {
                return config;
            }

            Dictionary<string, string> values = ReadIni(path);

            string gameUrl;
            if (values.TryGetValue("gameUrl", out gameUrl) && !string.IsNullOrWhiteSpace(gameUrl))
            {
                config.GameUrl = NormalizeUrl(gameUrl.Trim());
            }

            config.StartFullScreen = ReadBool(values, "startFullScreen", config.StartFullScreen);
            config.StartFitToWorkArea = ReadBool(values, "startFitToWorkArea", config.StartFitToWorkArea);
            config.Width = ReadInt(values, "width", config.Width, 640, 7680);
            config.Height = ReadInt(values, "height", config.Height, 480, 4320);
            config.BrowserViewportMode = ReadString(values, "browserViewportMode", config.BrowserViewportMode);
            config.BrowserWidth = ReadInt(values, "browserWidth", config.BrowserWidth, 320, 7680);
            config.BrowserHeight = ReadInt(values, "browserHeight", config.BrowserHeight, 240, 4320);
            config.BrowserAlign = ReadString(values, "browserAlign", config.BrowserAlign);
            config.FixFlashPosition = ReadBool(values, "fixFlashPosition", config.FixFlashPosition);
            config.TopMost = ReadBool(values, "topMost", config.TopMost);
            config.ScriptErrorsSuppressed = ReadBool(values, "scriptErrorsSuppressed", config.ScriptErrorsSuppressed);
            config.ReleaseMouseClip = ReadBool(values, "releaseMouseClip", config.ReleaseMouseClip);
            config.MouseClipPollIntervalMs = ReadInt(values, "mouseClipPollIntervalMs", config.MouseClipPollIntervalMs, 10, 1000);
            config.MouseDiagnosticsEnabled = ReadBool(values, "mouseDiagnosticsEnabled", config.MouseDiagnosticsEnabled);
            config.MouseDiagnosticsIntervalMs = ReadInt(values, "mouseDiagnosticsIntervalMs", config.MouseDiagnosticsIntervalMs, 100, 10000);
            config.MouseTopJumpGuardEnabled = ReadBool(values, "mouseTopJumpGuardEnabled", config.MouseTopJumpGuardEnabled);
            config.MouseTopJumpThresholdPixels = ReadInt(values, "mouseTopJumpThresholdPixels", config.MouseTopJumpThresholdPixels, 20, 1000);
            config.MouseTopJumpReturnOffsetPixels = ReadInt(values, "mouseTopJumpReturnOffsetPixels", config.MouseTopJumpReturnOffsetPixels, 1, 200);
            config.MaxLogSizeBytes = (long)ReadInt(values, "maxLogSizeMB", 10, 1, 200) * 1024 * 1024;

            return config;
        }

        // 解析 ini 为键值对：忽略空行、注释行（# 或 ;）与节标题（[...]），键名不区分大小写
        private static Dictionary<string, string> ReadIni(string path)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();

                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    continue;
                }

                int separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim();
                values[key] = value;
            }

            return values;
        }

        // 补全 URL 协议头：缺少 scheme 时默认补 http://
        private static string NormalizeUrl(string value)
        {
            Uri uri;
            if (Uri.TryCreate(value, UriKind.Absolute, out uri))
            {
                return value;
            }

            return "http://" + value.TrimStart('/');
        }

        // 命令行 --url 覆盖：临时替换游戏地址（不写文件，重启后失效）
        internal void OverrideGameUrl(string rawUrl)
        {
            if (!string.IsNullOrWhiteSpace(rawUrl))
            {
                GameUrl = NormalizeUrl(rawUrl.Trim());
            }
        }

        // 读取布尔值，支持 true/false 以及 1/0 表示
        private static bool ReadBool(Dictionary<string, string> values, string key, bool defaultValue)
        {
            string value;
            if (!values.TryGetValue(key, out value))
            {
                return defaultValue;
            }

            bool result;
            if (bool.TryParse(value, out result))
            {
                return result;
            }

            if (value == "1")
            {
                return true;
            }

            if (value == "0")
            {
                return false;
            }

            return defaultValue;
        }

        // 读取字符串，空值则回退默认
        private static string ReadString(Dictionary<string, string> values, string key, string defaultValue)
        {
            string value;
            if (!values.TryGetValue(key, out value) || string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            return value.Trim();
        }

        // 读取整数并限制在 [min, max] 区间内
        private static int ReadInt(Dictionary<string, string> values, string key, int defaultValue, int min, int max)
        {
            string value;
            int result;

            if (!values.TryGetValue(key, out value) || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
            {
                return defaultValue;
            }

            return Win32Util.Clamp(result, min, max);
        }
    }
}
