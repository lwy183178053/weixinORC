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
        NotRunning,
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

    public sealed class WeChatWindowInfo
    {
        public nint Hwnd { get; init; }
        public int ProcessId { get; init; }
        public string ProcessName { get; init; } = string.Empty;
        public string ClassName { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public WeChatAccountType AccountType { get; init; }
        public bool IsVisible { get; set; }
        public bool IsMinimized { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public WeChatLoginState DetectedState { get; set; } = WeChatLoginState.Unknown;

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

        public string DimensionsLabel => $"{Width}×{Height}";

        public string StateLabel => DetectedState switch
        {
            WeChatLoginState.LoginPage => "登录页（待扫码）",
            WeChatLoginState.LoggedIn => "主界面（已登录）",
            WeChatLoginState.Disconnected => "掉线/已退出",
            WeChatLoginState.HiddenOrMinimized => "已隐藏/最小化",
            WeChatLoginState.NotRunning => "未运行",
            _ => "待 OCR 识别"
        };

        public string SummaryLine =>
            $"{AccountTypeLabel} · {ProcessName} (PID {ProcessId}) · {Width}x{Height} · {StateLabel}";

        public string DisplayTitle =>
            string.IsNullOrWhiteSpace(Title) ? $"[{ClassName}] (0x{Hwnd:X})" : Title;
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
        public string CandidateRegion { get; init; } = string.Empty;
        public string CandidatePhone { get; init; } = string.Empty;
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
