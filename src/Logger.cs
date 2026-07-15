using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace TianshuQitanLauncher
{
    // 简单的文件日志记录器：所有日志带时间戳与级别，写入可执行文件同级的 logs 目录。
    // 写盘在独立后台线程进行，UI 线程只负责把日志行入队，从而把磁盘 I/O 从 Flash 游戏所在的
    // UI/消息循环线程上剥离——避免鼠标裁剪释放、顶部跳跃纠正等高频事件触发日志时卡顿游戏。
    internal static class Logger
    {
        private static readonly object SyncRoot = new object();
        private static readonly string LogDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        private static bool logDirectoryReady;
        // 单个日志文件超过此大小（字节）时自动轮转，避免长期运行撑满磁盘；0 表示不限制
        private static long maxLogSizeBytes = 10L * 1024 * 1024;

        // 待写日志队列与后台写盘线程（UI 线程只入队，后台线程负责真正的磁盘写入）
        private static readonly ConcurrentQueue<LogEntry> queue = new ConcurrentQueue<LogEntry>();
        private static readonly AutoResetEvent hasItem = new AutoResetEvent(false);
        private static readonly Thread writerThread;
        private static volatile bool stopRequested;

        // 单条日志：文件名 + 已格式化好的整行文本
        private sealed class LogEntry
        {
            public string FileName;
            public string Line;
        }

        static Logger()
        {
            writerThread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "LoggerWriter",
            };
            writerThread.Start();
        }

        // 由启动器根据 config.ini 设置单个日志文件的最大体积
        public static void SetMaxLogSizeBytes(long value)
        {
            maxLogSizeBytes = value;
        }

        // 普通信息，写入 launcher.log
        public static void Info(string message)
        {
            Enqueue("launcher.log", "INFO", message);
        }

        // 异常信息，写入 launcher.log；异常为 null（如热键注册失败等轻量告警）时不残留多余空格
        public static void Error(string message, Exception ex)
        {
            Enqueue("launcher.log", "ERROR", ex == null ? message : message + " " + ex);
        }

        // 鼠标诊断信息，写入 mouse-diagnostics.log
        public static void Mouse(string message)
        {
            Enqueue("mouse-diagnostics.log", "MOUSE", message);
        }

        // 把一条日志格式化后入队，并唤醒后台写盘线程（本方法本身几乎零开销，不阻塞调用线程）
        private static void Enqueue(string fileName, string level, string message)
        {
            string line = string.Format(
                "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}{3}",
                DateTime.Now,
                level,
                message,
                Environment.NewLine);

            queue.Enqueue(new LogEntry { FileName = fileName, Line = line });
            hasItem.Set();
        }

        // 后台线程：被唤醒后把队列里的日志依次落盘，直到收到停止信号且队列清空后退出
        private static void WriterLoop()
        {
            while (true)
            {
                hasItem.WaitOne();

                LogEntry entry;
                while (queue.TryDequeue(out entry))
                {
                    WriteToFile(entry.FileName, entry.Line);
                }

                if (stopRequested)
                {
                    // 停止信号前可能还有残余入队，再清空一次后退出
                    while (queue.TryDequeue(out entry))
                    {
                        WriteToFile(entry.FileName, entry.Line);
                    }

                    return;
                }
            }
        }

        // 统一写盘：确保目录存在，按大小轮转后追加一行；任何失败都不影响主流程
        private static void WriteToFile(string fileName, string line)
        {
            try
            {
                lock (SyncRoot)
                {
                    if (!logDirectoryReady)
                    {
                        Directory.CreateDirectory(LogDirectory);
                        logDirectoryReady = true;
                    }

                    string fullPath = Path.Combine(LogDirectory, fileName);

                    // 超过上限时滚动：当前文件更名 .1 作为备份（仅保留一份），随后从头写入，
                    // 将磁盘占用稳定在约 2 倍上限内，避免诊断日志 7x24 运行撑满磁盘。
                    if (maxLogSizeBytes > 0 && File.Exists(fullPath))
                    {
                        try
                        {
                            if (new FileInfo(fullPath).Length >= maxLogSizeBytes)
                            {
                                string backup = fullPath + ".1";
                                if (File.Exists(backup))
                                {
                                    File.Delete(backup);
                                }

                                File.Move(fullPath, backup);
                            }
                        }
                        catch
                        {
                            // 轮转失败不影响正常写入
                        }
                    }

                    File.AppendAllText(fullPath, line);
                }
            }
            catch
            {
                // 日志写入失败（如磁盘满、权限不足、UAC 限制）绝不影响主流程
            }
        }

        // 应用退出前调用：请求后台线程停止并等待其把残留日志落盘（最多短暂等待，避免退出卡住）
        public static void Shutdown()
        {
            stopRequested = true;
            hasItem.Set();

            // 等待后台写盘线程把队列清空（最多 2 秒，避免退出卡住）。
            writerThread.Join(TimeSpan.FromSeconds(2));

            // 兜底：后台线程退出后不再消费队列，若退出瞬间仍有残留日志则在本线程落盘，
            // 覆盖“stop 信号之后新入队”的项，避免丢失退出瞬间的日志。
            LogEntry entry;
            while (queue.TryDequeue(out entry))
            {
                try
                {
                    WriteToFile(entry.FileName, entry.Line);
                }
                catch
                {
                    // 落盘失败不影响主流程
                }
            }
        }
    }
}
