using System.Diagnostics;
using Client.Models;
using Windows.Graphics.Imaging;

namespace Client.Services
{
    public sealed class WeChatWindowService
    {
        private static readonly HashSet<string> PersonalWeChatProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "WeChat",
            "Weixin"
        };

        private static readonly HashSet<string> EnterpriseWeChatProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "WXWork"
        };

        private static readonly HashSet<string> KnownLoginClasses = new(StringComparer.OrdinalIgnoreCase)
        {
            "WeChatLoginWndForPC",
            "WeWorkLoginWindow"
        };

        private static readonly HashSet<string> KnownMainClasses = new(StringComparer.OrdinalIgnoreCase)
        {
            "WeChatMainWndForPC",
            "WeWorkWindow",
            "mmui::MainWindow"
        };

        public bool IsAutomationPaused { get; private set; }
        public string PauseReason { get; private set; } = string.Empty;

        public List<WeChatWindowInfo> EnumerateWindows(bool includeAllVisibleWindows = false)
        {
            var results = new List<WeChatWindowInfo>();
            var processCache = new Dictionary<int, string>();
            int currentPid = Environment.ProcessId;

            Win32WindowHelper.EnumWindows((hWnd, lParam) =>
            {
                _ = Win32WindowHelper.GetWindowThreadProcessId(hWnd, out uint rawPid);
                int pid = unchecked((int)rawPid);
                if (pid <= 0 || pid == currentPid)
                {
                    return true;
                }

                if (!processCache.TryGetValue(pid, out string? processName))
                {
                    processName = TryGetProcessName(pid);
                    processCache[pid] = processName;
                }

                if (string.IsNullOrEmpty(processName))
                {
                    return true;
                }

                var accountType = ClassifyProcess(processName);
                bool isTargetWeChat = accountType is WeChatAccountType.Personal or WeChatAccountType.Enterprise;

                if (!isTargetWeChat && !includeAllVisibleWindows)
                {
                    return true;
                }

                bool isVisible = Win32WindowHelper.IsWindowVisible(hWnd);
                bool isMinimized = Win32WindowHelper.IsIconic(hWnd);
                string title = Win32WindowHelper.GetWindowTitleText(hWnd);
                string className = Win32WindowHelper.GetWindowClassName(hWnd);

                if (!Win32WindowHelper.GetWindowRect(hWnd, out var rect))
                {
                    return true;
                }

                if (!isTargetWeChat)
                {
                    // In "all desktop windows" debug mode, keep only meaningful visible titled windows.
                    if (!isVisible || string.IsNullOrWhiteSpace(title) || rect.Width < 200 || rect.Height < 150)
                    {
                        return true;
                    }
                }
                else
                {
                    // Filter out tiny invisible helper/tray windows of WeChat while keeping minimized/hidden main windows.
                    bool isKnownClass = KnownLoginClasses.Contains(className) || KnownMainClasses.Contains(className);
                    bool looksLikeMainWindow = !string.IsNullOrWhiteSpace(title) || isKnownClass;
                    bool hasReasonableSize = isMinimized || (rect.Width >= 240 && rect.Height >= 200);
                    if (!looksLikeMainWindow || (!isVisible && !isKnownClass) || !hasReasonableSize)
                    {
                        return true;
                    }
                }

                var initialState = InferInitialState(className, title, isVisible, isMinimized);

                results.Add(new WeChatWindowInfo
                {
                    Hwnd = hWnd,
                    ProcessId = pid,
                    ProcessName = processName,
                    ClassName = className,
                    Title = title,
                    AccountType = accountType,
                    IsVisible = isVisible,
                    IsMinimized = isMinimized,
                    Width = Math.Max(0, rect.Width),
                    Height = Math.Max(0, rect.Height),
                    DetectedState = initialState
                });

                return true;
            }, nint.Zero);

            return results
                .OrderBy(w => w.AccountType == WeChatAccountType.OtherWindow ? 1 : 0)
                .ThenBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(w => w.Width * w.Height)
                .ToList();
        }

        public bool ShowWindow(WeChatWindowInfo window)
        {
            if (!Win32WindowHelper.IsWindow(window.Hwnd))
            {
                return false;
            }

            _ = Win32WindowHelper.ShowWindow(window.Hwnd, Win32WindowHelper.SW_RESTORE);
            _ = Win32WindowHelper.ShowWindow(window.Hwnd, Win32WindowHelper.SW_SHOW);
            _ = Win32WindowHelper.SetForegroundWindow(window.Hwnd);

            window.IsVisible = true;
            window.IsMinimized = false;
            IsAutomationPaused = false;
            PauseReason = string.Empty;
            AppServices.Logs.Info("WIN", $"恢复显示窗口：{window.DisplayTitle}");
            return true;
        }

        public bool HideWindow(WeChatWindowInfo window)
        {
            if (!Win32WindowHelper.IsWindow(window.Hwnd))
            {
                return false;
            }

            // Use minimize so the user can always recover the WeChat window safely from the taskbar.
            _ = Win32WindowHelper.ShowWindow(window.Hwnd, Win32WindowHelper.SW_MINIMIZE);
            window.IsMinimized = true;
            window.DetectedState = WeChatLoginState.HiddenOrMinimized;
            IsAutomationPaused = true;
            PauseReason = "窗口已隐藏，已暂停相关识别任务；重新显示后可继续。";
            AppServices.Logs.Warn("WIN", $"隐藏暂停窗口：{window.DisplayTitle}");
            return true;
        }

        public SoftwareBitmap CaptureWindow(WeChatWindowInfo window)
        {
            AppServices.Logs.Info("WIN", $"正在截取窗口画面：{window.DisplayTitle} ({window.DimensionsLabel})");
            return Win32WindowHelper.CaptureWindowToSoftwareBitmap(window.Hwnd);
        }

        private static WeChatAccountType ClassifyProcess(string processName)
        {
            if (PersonalWeChatProcesses.Contains(processName))
            {
                return WeChatAccountType.Personal;
            }

            if (EnterpriseWeChatProcesses.Contains(processName))
            {
                return WeChatAccountType.Enterprise;
            }

            return WeChatAccountType.OtherWindow;
        }

        private static WeChatLoginState InferInitialState(string className, string title, bool isVisible, bool isMinimized)
        {
            if (!isVisible || isMinimized)
            {
                return WeChatLoginState.HiddenOrMinimized;
            }

            if (KnownLoginClasses.Contains(className) || title.Contains("登录", StringComparison.Ordinal))
            {
                return WeChatLoginState.LoginPage;
            }

            if (KnownMainClasses.Contains(className))
            {
                return WeChatLoginState.LoggedIn;
            }

            return WeChatLoginState.Unknown;
        }

        private static string TryGetProcessName(int pid)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                return proc.ProcessName;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
