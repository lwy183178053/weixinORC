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

    public sealed class WeChatProcessInfo
    {
        public int ProcessId { get; init; }
        public string ProcessName { get; init; } = string.Empty;
        public WeChatAccountType AccountType { get; init; }
        public string Version { get; set; } = string.Empty;
        public string ExecutablePath { get; set; } = string.Empty;
        public double MemoryMb { get; set; }
        public int ThreadCount { get; set; }
        public DateTime? StartTime { get; set; }
        public int WindowCount { get; set; }
        public bool IsMainProcess { get; set; }
        public WeChatLoginState LoginState { get; set; } = WeChatLoginState.Unknown;
    }

    public sealed class WeChatWindowInfo
    {
        public nint Hwnd { get; init; }
        public int ProcessId { get; init; }
        public string ProcessName { get; init; } = string.Empty;
        public string ClassName { get; init; } = string.Empty;
        public WeChatAccountType AccountType { get; init; }
        public string Title { get; set; } = string.Empty;
        public string WindowRole { get; set; } = "主窗口";
        public bool IsVisible { get; set; }
        public bool IsMinimized { get; set; }
        public bool IsForeground { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public WeChatLoginState DetectedState { get; set; } = WeChatLoginState.Unknown;

        public string HwndHex => $"0x{Hwnd:X}";
        public string DimensionsLabel => Width > 0 && Height > 0 ? $"{Width}×{Height}" : "未展开";
        public string DisplayTitle =>
            string.IsNullOrWhiteSpace(Title) ? $"[{ClassName}] ({HwndHex})" : Title;
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
}
