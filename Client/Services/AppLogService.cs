using System.Collections.Concurrent;

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

        public string FormattedLine => $"[{TimeText}] [{LevelTag}] [{Module,-4}] {Message}";
    }

    public sealed class AppLogService
    {
        private const int MaxEntries = 800;
        private readonly object _gate = new();
        private readonly LinkedList<AppLogEntry> _entries = new();

        public event EventHandler<AppLogEntry>? LogAdded;
        public event EventHandler? LogsCleared;

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

            var entry = new AppLogEntry
            {
                Timestamp = DateTime.Now,
                Level = level,
                Module = string.IsNullOrWhiteSpace(module) ? "SYS" : module.Trim().ToUpperInvariant(),
                Message = message.Trim()
            };

            lock (_gate)
            {
                _entries.AddLast(entry);
                while (_entries.Count > MaxEntries)
                {
                    _entries.RemoveFirst();
                }
            }

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
    }
}
