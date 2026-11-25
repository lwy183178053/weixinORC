using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Client.Models
{
    public enum WeChatAccountType
    {
        Unknown,
        Personal,
        Enterprise,
        OtherWindow
    }

    public enum WeChatLoginState
    {
        Unknown,
        LoginPage,
        LoggedIn,
        Disconnected,
        HiddenOrMinimized
    }

    public readonly record struct PixelRect(double X, double Y, double Width, double Height)
    {
        public string Format() => $"({X:F0}, {Y:F0}, {Width:F0}x{Height:F0})";
    }

    public sealed class WeChatProcessInfo : INotifyPropertyChanged
    {
        private string _version = string.Empty;
        private string _executablePath = string.Empty;
        private double _memoryMb;
        private int _threadCount;
        private DateTime? _startTime;
        private int _windowCount;
        private bool _isMainProcess;
        private WeChatLoginState _loginState = WeChatLoginState.Unknown;

        public event PropertyChangedEventHandler? PropertyChanged;

        public int ProcessId { get; init; }
        public string ProcessName { get; init; } = string.Empty;
        public WeChatAccountType AccountType { get; init; }

        public string Version
        {
            get => _version;
            set => _version = value;
        }

        public string ExecutablePath
        {
            get => _executablePath;
            set => _executablePath = value;
        }

        public double MemoryMb
        {
            get => _memoryMb;
            set => _memoryMb = value;
        }

        public int ThreadCount
        {
            get => _threadCount;
            set => _threadCount = value;
        }

        public DateTime? StartTime
        {
            get => _startTime;
            set => _startTime = value;
        }

        public int WindowCount
        {
            get => _windowCount;
            set => _windowCount = value;
        }

        public bool IsMainProcess
        {
            get => _isMainProcess;
            set => _isMainProcess = value;
        }

        public WeChatLoginState LoginState
        {
            get => _loginState;
            set => _loginState = value;
        }

        public string DisplayType => AccountType switch
        {
            WeChatAccountType.Enterprise => "企业微信",
            WeChatAccountType.Personal when ProcessName.Equals("Weixin", StringComparison.OrdinalIgnoreCase) => "微信 4.x",
            WeChatAccountType.Personal => "微信 3.x",
            _ => ProcessName
        };

        public string RoleTag => _isMainProcess ? "主进程" : "子进程";
        public string PidLabel => $"PID {ProcessId}";
        public string VersionLabel => string.IsNullOrWhiteSpace(_version) ? "未知版本" : $"v{_version}";
        public string MemoryLabel => $"{_memoryMb:F0} MB";
        public string WindowCountLabel => $"{_windowCount} 窗口";
        public string ThreadCountLabel => $"{_threadCount} 线程";

        public string UptimeLabel
        {
            get
            {
                if (_startTime is null) return "运行时长未知";
                var span = DateTime.Now - _startTime.Value;
                if (span.TotalSeconds < 60) return $"运行 {Math.Max(0, (int)span.TotalSeconds)} 秒";
                if (span.TotalHours < 1) return $"运行 {(int)span.TotalMinutes} 分钟";
                if (span.TotalDays < 1) return $"运行 {(int)span.TotalHours} 小时 {span.Minutes} 分";
                return $"运行 {(int)span.TotalDays} 天 {span.Hours} 小时";
            }
        }

        public string LoginStateLabel => _loginState switch
        {
            WeChatLoginState.LoggedIn => "已登录",
            WeChatLoginState.LoginPage => "待扫码",
            WeChatLoginState.Disconnected => "掉线/退出",
            WeChatLoginState.HiddenOrMinimized => "后台/最小化",
            _ => _isMainProcess ? "运行中" : "辅助运行"
        };

        public string MetricsSummary =>
            $"{PidLabel} · {VersionLabel} · {MemoryLabel} · {ThreadCountLabel} · {WindowCountLabel} · {UptimeLabel}";

        public string PathDisplay => string.IsNullOrWhiteSpace(_executablePath) ? "路径受系统权限限制" : _executablePath;

        public void UpdateFrom(WeChatProcessInfo incoming)
        {
            string oldRole = RoleTag;
            string oldLogin = LoginStateLabel;
            string oldMetrics = MetricsSummary;
            string oldPath = PathDisplay;

            _version = incoming._version;
            _executablePath = incoming._executablePath;
            _memoryMb = incoming._memoryMb;
            _threadCount = incoming._threadCount;
            _startTime = incoming._startTime;
            _windowCount = incoming._windowCount;
            _isMainProcess = incoming._isMainProcess;
            _loginState = incoming._loginState;

            if (oldRole != RoleTag) OnPropertyChanged(nameof(RoleTag));
            if (oldLogin != LoginStateLabel) OnPropertyChanged(nameof(LoginStateLabel));
            if (oldMetrics != MetricsSummary) OnPropertyChanged(nameof(MetricsSummary));
            if (oldPath != PathDisplay) OnPropertyChanged(nameof(PathDisplay));
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class WeChatWindowInfo : INotifyPropertyChanged
    {
        private string _title = string.Empty;
        private string _windowRole = "主窗口";
        private bool _isVisible;
        private bool _isMinimized;
        private bool _isForeground;
        private int _width;
        private int _height;
        private WeChatLoginState _detectedState = WeChatLoginState.Unknown;

        public event PropertyChangedEventHandler? PropertyChanged;

        public nint Hwnd { get; init; }
        public int ProcessId { get; init; }
        public string ProcessName { get; init; } = string.Empty;
        public string ClassName { get; init; } = string.Empty;
        public WeChatAccountType AccountType { get; init; }

        public string Title
        {
            get => _title;
            set => _title = value;
        }

        public string WindowRole
        {
            get => _windowRole;
            set => _windowRole = value;
        }

        public bool IsVisible
        {
            get => _isVisible;
            set => _isVisible = value;
        }

        public bool IsMinimized
        {
            get => _isMinimized;
            set => _isMinimized = value;
        }

        public bool IsForeground
        {
            get => _isForeground;
            set => _isForeground = value;
        }

        public int Width
        {
            get => _width;
            set => _width = value;
        }

        public int Height
        {
            get => _height;
            set => _height = value;
        }

        public WeChatLoginState DetectedState
        {
            get => _detectedState;
            set => _detectedState = value;
        }

        public string HwndHex => $"0x{Hwnd:X}";

        public string AccountTypeLabel => AccountType switch
        {
            WeChatAccountType.Personal => "个人微信",
            WeChatAccountType.Enterprise => "企业微信",
            WeChatAccountType.OtherWindow => "桌面窗口",
            _ => "未知"
        };

        public string ShortTag => AccountType switch
        {
            WeChatAccountType.Personal => "微信",
            WeChatAccountType.Enterprise => "企微",
            WeChatAccountType.OtherWindow => "窗口",
            _ => "未知"
        };

        public string DimensionsLabel => _width > 0 && _height > 0 ? $"{_width}×{_height}" : "未展开";

        public string VisibilityLabel => _isMinimized
            ? "已最小化"
            : !_isVisible
                ? "托盘隐藏"
                : _isForeground
                    ? "当前前台"
                    : "桌面可见";

        public string StateLabel => _detectedState switch
        {
            WeChatLoginState.LoginPage => "待扫码登录",
            WeChatLoginState.LoggedIn => "已登录",
            WeChatLoginState.Disconnected => "掉线/已退出",
            WeChatLoginState.HiddenOrMinimized => _isMinimized ? "已最小化" : "托盘隐藏",
            _ => "运行中"
        };

        public string SummaryLine =>
            $"{_windowRole} · PID {ProcessId} · {HwndHex} · {DimensionsLabel} · {VisibilityLabel}";

        public string DisplayTitle =>
            string.IsNullOrWhiteSpace(_title) ? $"[{ClassName}] ({HwndHex})" : _title;

        public void UpdateFrom(WeChatWindowInfo incoming)
        {
            string oldRole = _windowRole;
            string oldTitle = DisplayTitle;
            string oldSummary = SummaryLine;
            string oldState = StateLabel;

            _title = incoming._title;
            _windowRole = incoming._windowRole;
            _isVisible = incoming._isVisible;
            _isMinimized = incoming._isMinimized;
            _isForeground = incoming._isForeground;
            _width = incoming._width;
            _height = incoming._height;
            _detectedState = incoming._detectedState;

            if (oldRole != _windowRole) OnPropertyChanged(nameof(WindowRole));
            if (oldTitle != DisplayTitle) OnPropertyChanged(nameof(DisplayTitle));
            if (oldSummary != SummaryLine) OnPropertyChanged(nameof(SummaryLine));
            if (oldState != StateLabel) OnPropertyChanged(nameof(StateLabel));
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class WeChatMonitorSnapshot
    {
        public DateTime CapturedAt { get; init; } = DateTime.Now;
        public bool IsAlive => Processes.Count > 0;
        public bool IsPersonalWeChatAlive => Processes.Any(p => p.AccountType == WeChatAccountType.Personal);
        public List<WeChatProcessInfo> Processes { get; init; } = [];
        public List<WeChatWindowInfo> Windows { get; init; } = [];
        public int LoggedInCount { get; init; }
        public int LoginPendingCount { get; init; }
        public int MainWindowCount { get; init; }
        public string PrimaryVersion { get; init; } = string.Empty;
        public string ResolvedExecutablePath { get; init; } = string.Empty;
        public string OverallStatusLabel { get; init; } = "未运行";
    }

    public sealed class WeChatMonitorEvent
    {
        public string Timestamp { get; init; } = DateTime.Now.ToString("HH:mm:ss");
        public string Level { get; init; } = "info";
        public string Message { get; init; } = string.Empty;
        public string DisplayLine => $"[{Timestamp}] {Message}";
    }

    public sealed class WeChatEnsureWindowResult
    {
        public bool Success { get; init; }
        public WeChatWindowInfo? Window { get; init; }
        public bool WasAutoLaunched { get; init; }
        public bool WasAutoRestored { get; init; }
        public bool IsLoginPage { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    public sealed class OcrLineResult
    {
        public int Index { get; init; }
        public string Text { get; init; } = string.Empty;
        public float Confidence { get; init; }
        public PixelRect Bounds { get; init; }
        public string BoundsLabel => Confidence > 0
            ? $"{Bounds.Format()} · {Confidence * 100:F0}%"
            : Bounds.Format();
    }

    public sealed class OcrRecognitionResult
    {
        public string EngineId { get; init; } = string.Empty;
        public string EngineName { get; init; } = string.Empty;
        public string LanguageTag { get; init; } = string.Empty;
        public int ImageWidth { get; init; }
        public int ImageHeight { get; init; }
        public long ElapsedMilliseconds { get; init; }
        public List<OcrLineResult> Lines { get; init; } = [];
        public string FullText { get; init; } = string.Empty;
    }

    public sealed class WeChatOcrAnalysis
    {
        public WeChatLoginState InferredState { get; init; } = WeChatLoginState.Unknown;
        public string StateReason { get; init; } = string.Empty;
        public string CandidateWechatRemark { get; init; } = string.Empty;
        public string CandidateWechatName { get; init; } = string.Empty;
        public string CandidateWechatAccount { get; init; } = string.Empty;
        public string CandidateTags { get; init; } = string.Empty;
        public string CandidateRegion { get; init; } = string.Empty;
        public string CandidatePhone { get; init; } = string.Empty;
        public string CandidateDescription { get; init; } = string.Empty;
        public string CandidateSignature { get; init; } = string.Empty;
        public string CandidateSource { get; init; } = string.Empty;
        public string CandidateAddedDate { get; init; } = string.Empty;
    }

    public sealed class GroupOcrAnalysis
    {
        public string CandidateGroupName { get; init; } = string.Empty;
        public int CandidateMemberCount { get; init; }
        public string CandidateMembersText { get; init; } = string.Empty;
        public string CandidateNote { get; init; } = string.Empty;
    }
}
