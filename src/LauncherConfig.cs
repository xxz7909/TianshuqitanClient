using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace TianshuQitanLauncher
{
    internal sealed class LauncherConfig
    {
        public string GameUrl { get; private set; }
        public bool StartFullScreen { get; private set; }
        public bool StartFitToWorkArea { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public string BrowserViewportMode { get; private set; }
        public int BrowserWidth { get; private set; }
        public int BrowserHeight { get; private set; }
        public string BrowserAlign { get; private set; }
        public bool FixFlashPosition { get; private set; }
        public string FlashWindowMode { get; private set; }
        public bool FlashRepaintWorkaround { get; private set; }
        public int FlashRepaintDelayMs { get; private set; }
        public bool TopMost { get; private set; }
        public bool ScriptErrorsSuppressed { get; private set; }
        public bool ReleaseMouseClip { get; private set; }
        public bool MouseDiagnosticsEnabled { get; private set; }
        public int MouseDiagnosticsIntervalMs { get; private set; }
        public bool MouseTopJumpGuardEnabled { get; private set; }
        public int MouseTopJumpThresholdPixels { get; private set; }
        public int MouseTopJumpReturnOffsetPixels { get; private set; }
        public bool AudioFilterEnabled { get; private set; }
        public int AudioFilterProxyPort { get; private set; }
        public string FfmpegPath { get; private set; }
        public string AudioFilterGraph { get; private set; }
        public int AudioBitrateKbps { get; private set; }
        public int AudioMp3Quality { get; private set; }
        public string AudioCacheDirectory { get; private set; }
        public string AudioSoundHost { get; private set; }
        public string AudioSoundPathPrefix { get; private set; }

        private LauncherConfig()
        {
            GameUrl = "http://v5.t.imop.com/";
            StartFullScreen = false;
            StartFitToWorkArea = true;
            Width = 1024;
            Height = 768;
            BrowserViewportMode = "fixed";
            BrowserWidth = 1005;
            BrowserHeight = 600;
            BrowserAlign = "topLeft";
            FixFlashPosition = true;
            FlashWindowMode = "window";
            FlashRepaintWorkaround = true;
            FlashRepaintDelayMs = 150;
            TopMost = false;
            ScriptErrorsSuppressed = true;
            ReleaseMouseClip = true;
            MouseDiagnosticsEnabled = true;
            MouseDiagnosticsIntervalMs = 1000;
            MouseTopJumpGuardEnabled = true;
            MouseTopJumpThresholdPixels = 120;
            MouseTopJumpReturnOffsetPixels = 8;
            AudioFilterEnabled = true;
            AudioFilterProxyPort = 0;
            FfmpegPath = "ffmpeg";
            AudioFilterGraph = "highpass=f=25,lowpass=f=19000,afftdn=nr=6:nf=-50:tn=1:gs=6,adeclick=t=3,alimiter=limit=0.97";
            AudioBitrateKbps = 0;
            AudioMp3Quality = 2;
            AudioCacheDirectory = "data/audio-cache";
            AudioSoundHost = "resource.t.imop.com";
            AudioSoundPathPrefix = "/sound";
        }

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
            config.FlashWindowMode = ReadFlashWindowMode(values, "flashWindowMode", config.FlashWindowMode);
            config.FlashRepaintWorkaround = ReadBool(values, "flashRepaintWorkaround", config.FlashRepaintWorkaround);
            config.FlashRepaintDelayMs = ReadInt(values, "flashRepaintDelayMs", config.FlashRepaintDelayMs, 50, 2000);
            config.TopMost = ReadBool(values, "topMost", config.TopMost);
            config.ScriptErrorsSuppressed = ReadBool(values, "scriptErrorsSuppressed", config.ScriptErrorsSuppressed);
            config.ReleaseMouseClip = ReadBool(values, "releaseMouseClip", config.ReleaseMouseClip);
            config.MouseDiagnosticsEnabled = ReadBool(values, "mouseDiagnosticsEnabled", config.MouseDiagnosticsEnabled);
            config.MouseDiagnosticsIntervalMs = ReadInt(values, "mouseDiagnosticsIntervalMs", config.MouseDiagnosticsIntervalMs, 100, 10000);
            config.MouseTopJumpGuardEnabled = ReadBool(values, "mouseTopJumpGuardEnabled", config.MouseTopJumpGuardEnabled);
            config.MouseTopJumpThresholdPixels = ReadInt(values, "mouseTopJumpThresholdPixels", config.MouseTopJumpThresholdPixels, 20, 1000);
            config.MouseTopJumpReturnOffsetPixels = ReadInt(values, "mouseTopJumpReturnOffsetPixels", config.MouseTopJumpReturnOffsetPixels, 1, 200);
            config.AudioFilterEnabled = ReadBool(values, "audioFilterEnabled", config.AudioFilterEnabled);
            config.AudioFilterProxyPort = ReadInt(values, "audioFilterProxyPort", config.AudioFilterProxyPort, 0, 65535);
            config.FfmpegPath = ReadString(values, "ffmpegPath", config.FfmpegPath);
            config.AudioFilterGraph = ReadString(values, "audioFilterGraph", config.AudioFilterGraph);
            config.AudioBitrateKbps = ReadInt(values, "audioBitrateKbps", config.AudioBitrateKbps, 0, 320);
            config.AudioMp3Quality = ReadInt(values, "audioMp3Quality", config.AudioMp3Quality, 0, 9);
            config.AudioCacheDirectory = ReadString(values, "audioCacheDirectory", config.AudioCacheDirectory);
            config.AudioSoundHost = ReadString(values, "audioSoundHost", config.AudioSoundHost);
            config.AudioSoundPathPrefix = ReadString(values, "audioSoundPathPrefix", config.AudioSoundPathPrefix);

            return config;
        }

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

        private static string NormalizeUrl(string value)
        {
            Uri uri;
            if (Uri.TryCreate(value, UriKind.Absolute, out uri))
            {
                return value;
            }

            return "http://" + value.TrimStart('/');
        }

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

        private static string ReadString(Dictionary<string, string> values, string key, string defaultValue)
        {
            string value;
            if (!values.TryGetValue(key, out value) || string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            return value.Trim();
        }

        private static string ReadFlashWindowMode(Dictionary<string, string> values, string key, string defaultValue)
        {
            string value = ReadString(values, key, defaultValue);
            if (string.Equals(value, "window", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "opaque", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "transparent", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "page", StringComparison.OrdinalIgnoreCase))
            {
                return value.ToLowerInvariant();
            }

            return defaultValue;
        }

        private static int ReadInt(Dictionary<string, string> values, string key, int defaultValue, int min, int max)
        {
            string value;
            int result;

            if (!values.TryGetValue(key, out value) || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
            {
                return defaultValue;
            }

            if (result < min)
            {
                return min;
            }

            if (result > max)
            {
                return max;
            }

            return result;
        }
    }
}
