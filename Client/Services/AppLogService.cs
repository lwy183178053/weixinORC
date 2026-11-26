using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Client.Services
{
    public enum AppLogLevel
    {
        Info,
        Success,
        Warning,
        Error
    }

    public sealed class AppLogEntry
    {
        public DateTime Timestamp { get; init; } = DateTime.Now;
        public AppLogLevel Level { get; init; } = AppLogLevel.Info;
        public string Module { get; init; } = "SYS";
        public string Message { get; init; } = string.Empty;

        public string TimeText => Timestamp.ToString("HH:mm:ss.fff");

        public string LevelTag => Level switch
        {
            AppLogLevel.Success => "OK  ",
            AppLogLevel.Warning => "WARN",
            AppLogLevel.Error => "ERR ",
            _ => "INFO"
        };

        public string FormattedLine => $"[{TimeText}] [{LevelTag}] [{Module,-5}] {Message}";
        public string FileLine => $"[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{LevelTag}] [{Module,-5}] {Message}";
    }

    /// <summary>
    /// Centralized diagnostic log service:
    /// - Keeps a 2,000-entry in-memory ring buffer for instant UI inspection &amp; filtering
    /// - Persists daily rolling UTF-8 log files under %LocalAppData%\weixinORC\logs\app-YYYY-MM-DD.log
    ///   via a non-blocking background writer (auto-retains 14 days)
    /// - Standardized module tags: SYS, AUTH, API, WX4, REPLY, AUTO, OCR, CLICK, WIN, AI, UI
    /// </summary>
    public sealed class AppLogService
    {
        private const int MaxEntries = 2000;
        private const int RetentionDays = 14;

        private readonly object _gate = new();
        private readonly LinkedList<AppLogEntry> _entries = new();
        private readonly ConcurrentQueue<string> _fileQueue = new();
        private readonly AutoResetEvent _fileWake = new(false);

        public string LogDirectory { get; }
        public string CurrentLogFilePath => Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyy-MM-dd}.log");

        public event EventHandler<AppLogEntry>? LogAdded;
        public event EventHandler? LogsCleared;

        public AppLogService()
        {
            LogDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "weixinORC",
                "logs");

            try
            {
                Directory.CreateDirectory(LogDirectory);
            }
            catch { }

            var writerThread = new Thread(FileWriterLoop)
            {
                IsBackground = true,
                Name = "AppLogFileWriterThread"
            };
            writerThread.Start();

            _ = Task.Run(PruneOldLogFiles);
            Info("SYS", $"=== 智美员工工作台日志启动 (PID={Environment.ProcessId}, 日志目录={LogDirectory}) ===");
        }

        public void Info(string module, string message) => Write(AppLogLevel.Info, module, message);
        public void Success(string module, string message) => Write(AppLogLevel.Success, module, message);
        public void Warn(string module, string message) => Write(AppLogLevel.Warning, module, message);
        public void Error(string module, string message) => Write(AppLogLevel.Error, module, message);

        public void Write(AppLogLevel level, string module, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            string normalizedMessage = message.Replace("\r\n", " | ").Replace('\n', ' ').Trim();
            var entry = new AppLogEntry
            {
                Timestamp = DateTime.Now,
                Level = level,
                Module = string.IsNullOrWhiteSpace(module) ? "SYS" : module.Trim().ToUpperInvariant(),
                Message = normalizedMessage
            };

            lock (_gate)
            {
                _entries.AddLast(entry);
                while (_entries.Count > MaxEntries)
                {
                    _entries.RemoveFirst();
                }
            }

            _fileQueue.Enqueue(entry.FileLine);
            _fileWake.Set();
            LogAdded?.Invoke(this, entry);
        }

        public List<AppLogEntry> GetSnapshot()
        {
            lock (_gate)
            {
                return new List<AppLogEntry>(_entries);
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
            }

            LogsCleared?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Synchronously drains pending queued log lines to disk (called on app shutdown / crash).
        /// </summary>
        public void Flush()
        {
            var batch = new StringBuilder(2048);
            while (_fileQueue.TryDequeue(out string? line))
            {
                batch.AppendLine(line);
            }
            if (batch.Length == 0) return;

            try
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(CurrentLogFilePath, batch.ToString(), Encoding.UTF8);
            }
            catch
            {
            }
        }

        public bool OpenLogDirectory()
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = LogDirectory,
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                Warn("SYS", $"打开日志目录失败：{ex.Message}");
                return false;
            }
        }

        private void FileWriterLoop()
        {
            var batch = new StringBuilder(4096);
            while (true)
            {
                _fileWake.WaitOne(1500);
                batch.Clear();
                while (_fileQueue.TryDequeue(out string? line))
                {
                    batch.AppendLine(line);
                    if (batch.Length >= 32768) break;
                }
                if (batch.Length == 0) continue;

                try
                {
                    Directory.CreateDirectory(LogDirectory);
                    File.AppendAllText(CurrentLogFilePath, batch.ToString(), Encoding.UTF8);
                }
                catch
                {
                    // Never crash application due to transient file lock
                }
            }
        }

        private void PruneOldLogFiles()
        {
            try
            {
                if (!Directory.Exists(LogDirectory)) return;
                DateTime cutoff = DateTime.Now.AddDays(-RetentionDays);
                foreach (string file in Directory.EnumerateFiles(LogDirectory, "app-*.log"))
                {
                    try
                    {
                        if (File.GetLastWriteTime(file) < cutoff)
                        {
                            File.Delete(file);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
