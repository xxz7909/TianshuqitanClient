using System;
using System.IO;

namespace TianshuQitanLauncher
{
    internal static class Logger
    {
        private static readonly object SyncRoot = new object();
        private static string logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

        public static void Configure(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Log directory is required.", "directory");
            lock (SyncRoot)
            {
                logDirectory = Path.GetFullPath(directory);
                Directory.CreateDirectory(logDirectory);
            }
        }

        public static void Info(string message)
        {
            Write("launcher.log", "INFO", message);
        }

        public static void Error(string message, Exception ex)
        {
            Write("launcher.log", "ERROR", message + " " + ex);
        }

        public static void Mouse(string message)
        {
            Write("mouse-diagnostics.log", "MOUSE", message);
        }

        private static void Write(string fileName, string level, string message)
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(logDirectory);
                File.AppendAllText(
                    Path.Combine(logDirectory, fileName),
                    string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}{3}", DateTime.Now, level, message, Environment.NewLine));
            }
        }
    }
}
