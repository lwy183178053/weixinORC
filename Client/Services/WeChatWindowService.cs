using System.Diagnostics;
using Client.Models;
using Microsoft.Win32;
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

        private string _cachedPersonalExePath = string.Empty;

        public string PauseReason { get; private set; } = string.Empty;

        public WeChatMonitorSnapshot CaptureMonitorSnapshot()
        {
            var windows = EnumerateWindows(includeAllVisibleWindows: false);
            var windowsByPid = windows.GroupBy(w => w.ProcessId).ToDictionary(g => g.Key, g => g.ToList());
            var processes = new List<WeChatProcessInfo>();

            foreach (string procName in new[] { "Weixin", "WeChat", "WXWork" })
            {
                Process[] found;
                try
                {
                    found = Process.GetProcessesByName(procName);
                }
                catch
                {
                    continue;
                }

                foreach (var proc in found)
                {
                    using (proc)
                    {
                        try
                        {
                            int pid = proc.Id;
                            if (pid <= 0 || pid == Environment.ProcessId) continue;

                            var accountType = ClassifyProcess(proc.ProcessName);
                            string exePath = TryGetProcessPath(proc);
                            string version = TryGetFileVersion(exePath);
                            DateTime? startTime = TryGetStartTime(proc);
                            double memMb = 0;
                            int threads = 0;
                            try { memMb = proc.WorkingSet64 / (1024.0 * 1024.0); } catch { }
                            try { threads = proc.Threads.Count; } catch { }

                            if (accountType == WeChatAccountType.Personal && !string.IsNullOrWhiteSpace(exePath))
                            {
                                _cachedPersonalExePath = exePath;
                            }

                            windowsByPid.TryGetValue(pid, out var pidWindows);
                            pidWindows ??= [];
                            bool isMain = pidWindows.Count > 0;
                            WeChatLoginState procLoginState = InferProcessLoginState(pidWindows, isMain);

                            processes.Add(new WeChatProcessInfo
                            {
                                ProcessId = pid,
                                ProcessName = proc.ProcessName,
                                AccountType = accountType,
                                Version = version,
                                ExecutablePath = exePath,
                                MemoryMb = memMb,
                                ThreadCount = threads,
                                StartTime = startTime,
                                WindowCount = pidWindows.Count,
                                IsMainProcess = isMain,
                                LoginState = procLoginState
                            });
                        }
                        catch
                        {
                            // Process may exit mid-enumeration; skip cleanly.
                        }
                    }
                }
            }

            // If only 1 process of a given name exists (e.g. hidden in tray with 0 visible windows), mark it as main process.
            foreach (var grp in processes.GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase))
            {
                if (!grp.Any(p => p.IsMainProcess))
                {
                    var top = grp.OrderByDescending(p => p.MemoryMb).First();
                    top.IsMainProcess = true;
                    if (top.LoginState == WeChatLoginState.Unknown)
                    {
                        top.LoginState = WeChatLoginState.HiddenOrMinimized;
                    }
                }
            }

            processes = processes
                .OrderBy(p => p.AccountType == WeChatAccountType.Personal ? 0 : 1)
                .ThenByDescending(p => p.IsMainProcess)
                .ThenByDescending(p => p.WindowCount)
                .ThenByDescending(p => p.MemoryMb)
                .ToList();

            int loggedInCount = processes.Count(p => p.IsMainProcess && p.LoginState == WeChatLoginState.LoggedIn);
            int loginPendingCount = processes.Count(p => p.IsMainProcess && p.LoginState == WeChatLoginState.LoginPage);
            int mainWindowCount = windows.Count(w => w.WindowRole == "主窗口");
            string resolvedExe = ResolveWeChatExecutablePath(processes);
            string primaryVersion = processes.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Version))?.Version
                                    ?? TryGetFileVersion(resolvedExe);

            string overallStatus = processes.Count == 0
                ? "未运行"
                : loggedInCount > 0
                    ? $"运行中 · 已登录 ({loggedInCount})"
                    : loginPendingCount > 0
                        ? "运行中 · 待扫码登录"
                        : "运行中 · 后台/托盘驻留";

            return new WeChatMonitorSnapshot
            {
                CapturedAt = DateTime.Now,
                Processes = processes,
                Windows = windows,
                LoggedInCount = loggedInCount,
                LoginPendingCount = loginPendingCount,
                MainWindowCount = mainWindowCount,
                PrimaryVersion = primaryVersion,
                ResolvedExecutablePath = resolvedExe,
                OverallStatusLabel = overallStatus
            };
        }

        public string ResolveWeChatExecutablePath(List<WeChatProcessInfo>? runningProcesses = null)
        {
            // 1. Running personal WeChat process
            string? fromRunning = runningProcesses?
                .FirstOrDefault(p => p.AccountType == WeChatAccountType.Personal && !string.IsNullOrWhiteSpace(p.ExecutablePath) && File.Exists(p.ExecutablePath))
                ?.ExecutablePath;
            if (!string.IsNullOrWhiteSpace(fromRunning))
            {
                _cachedPersonalExePath = fromRunning;
                return fromRunning;
            }

            // 2. User-configured path in settings
            string configured = AppServices.Settings.Current.WeChatExecutablePath;
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            {
                return configured;
            }

            // 3. Cached runtime path
            if (!string.IsNullOrWhiteSpace(_cachedPersonalExePath) && File.Exists(_cachedPersonalExePath))
            {
                return _cachedPersonalExePath;
            }

            // 4. Windows Registry (WeChat 4.x Weixin & WeChat 3.x WeChat)
            string? fromRegistry = TryFindFromRegistry();
            if (!string.IsNullOrWhiteSpace(fromRegistry) && File.Exists(fromRegistry))
            {
                _cachedPersonalExePath = fromRegistry;
                return fromRegistry;
            }

            // 5. Common default install paths
            string[] candidates =
            [
                @"C:\Program Files\Tencent\Weixin\Weixin.exe",
                @"C:\Program Files (x86)\Tencent\WeChat\WeChat.exe",
                @"C:\Program Files\Tencent\WeChat\WeChat.exe",
                @"D:\Program Files\Tencent\Weixin\Weixin.exe",
                @"D:\Program Files (x86)\Tencent\WeChat\WeChat.exe",
                @"D:\Tencent\Weixin\Weixin.exe",
                @"D:\Tencent\WeChat\WeChat.exe"
            ];
            foreach (string path in candidates)
            {
                if (File.Exists(path))
                {
                    _cachedPersonalExePath = path;
                    return path;
                }
            }

            return string.Empty;
        }

        public async Task<WeChatEnsureWindowResult> LaunchOrActivateWeChatAsync(CancellationToken ct = default) =>
            await EnsurePersonalWeChatWindowReadyAsync(autoLaunchIfClosed: true, allowLoginPageSuccess: true, ct: ct);

        public async Task<WeChatEnsureWindowResult> RestartWeChatAsync(CancellationToken ct = default)
        {
            var snap = CaptureMonitorSnapshot();
            string exePath = ResolveWeChatExecutablePath(snap.Processes);
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return new WeChatEnsureWindowResult
                {
                    Success = false,
                    Message = "未找到微信程序路径，请先点击「配置路径」选择 Weixin.exe 或 WeChat.exe。"
                };
            }

            AppServices.Logs.Info("WIN", $"正在安全重启微信：{exePath}");

            // 1. Gracefully request close on main windows first
            foreach (string procName in PersonalWeChatProcesses)
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName(procName))
                    {
                        using (p)
                        {
                            try { _ = p.CloseMainWindow(); } catch { }
                        }
                    }
                }
                catch { }
            }

            // Wait up to 2.4s for graceful shutdown
            for (int i = 0; i < 12; i++)
            {
                await Task.Delay(200, ct);
                if (!HasRunningPersonalWeChat()) break;
            }

            // 2. Force-terminate any leftover personal WeChat processes if still alive
            foreach (string procName in PersonalWeChatProcesses)
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName(procName))
                    {
                        using (p)
                        {
                            try
                            {
                                if (!p.HasExited) p.Kill(entireProcessTree: true);
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            await Task.Delay(650, ct);

            // 3. Relaunch and wait for window
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                return new WeChatEnsureWindowResult
                {
                    Success = false,
                    Message = $"重启启动微信失败：{ex.Message}"
                };
            }

            var win = await WaitForPersonalWindowAsync(timeoutMs: 9000, ct);
            if (win is not null)
            {
                _ = Win32WindowHelper.ForceBringToForeground(win.Hwnd);
                return new WeChatEnsureWindowResult
                {
                    Success = true,
                    Window = win,
                    WasAutoLaunched = true,
                    IsLoginPage = win.DetectedState == WeChatLoginState.LoginPage,
                    Message = win.DetectedState == WeChatLoginState.LoginPage
                        ? "微信已重启，当前位于登录界面（请扫码登录）。"
                        : $"微信已重启并唤起主窗口（PID {win.ProcessId}）。"
                };
            }

            return new WeChatEnsureWindowResult
            {
                Success = true,
                WasAutoLaunched = true,
                Message = "已发送重启微信指令，程序正在初始化中..."
            };
        }

        /// <summary>
        /// 确保个人微信主窗口处于桌面可见状态（用于批量导入、窗口 OCR 诊断等场景）：
        /// 1. 若窗口已最小化或隐藏在托盘，自动恢复并置顶；
        /// 2. 若微信未启动且允许自动启动，自动启动并等待主窗口弹出。
        /// </summary>
        public async Task<WeChatEnsureWindowResult> EnsurePersonalWeChatWindowReadyAsync(
            bool autoLaunchIfClosed = true,
            bool allowLoginPageSuccess = false,
            CancellationToken ct = default)
        {
            var windows = EnumerateWindows(includeAllVisibleWindows: false)
                .Where(w => w.AccountType == WeChatAccountType.Personal)
                .ToList();

            var mainWin = PickBestMainWindow(windows);
            if (mainWin is not null)
            {
                bool neededRestore = !mainWin.IsVisible || mainWin.IsMinimized || !mainWin.IsForeground;
                if (neededRestore)
                {
                    _ = Win32WindowHelper.ForceBringToForeground(mainWin.Hwnd);
                    await Task.Delay(260, ct);

                    // Some WeChat 4.x tray-hidden windows require single-instance launch to unhide cleanly.
                    if (!Win32WindowHelper.IsWindowVisible(mainWin.Hwnd) || Win32WindowHelper.IsIconic(mainWin.Hwnd))
                    {
                        string exe = ResolveWeChatExecutablePath();
                        if (!string.IsNullOrWhiteSpace(exe))
                        {
                            TryStartExecutable(exe);
                            await Task.Delay(500, ct);
                            _ = Win32WindowHelper.ForceBringToForeground(mainWin.Hwnd);
                        }
                    }
                }

                // Re-read fresh window state after restore
                var refreshed = EnumerateWindows(includeAllVisibleWindows: false)
                    .FirstOrDefault(w => w.Hwnd == mainWin.Hwnd) ?? mainWin;
                if (refreshed.DetectedState == WeChatLoginState.LoginPage)
                {
                    return new WeChatEnsureWindowResult
                    {
                        Success = allowLoginPageSuccess,
                        Window = refreshed,
                        WasAutoRestored = neededRestore,
                        IsLoginPage = true,
                        Message = "已自动弹出微信窗口，当前处于待扫码登录界面，请先扫码登录后重试。"
                    };
                }

                AppServices.Logs.Info("WIN", neededRestore
                    ? $"已自动唤起微信窗口：{refreshed.DisplayTitle} (PID {refreshed.ProcessId})"
                    : $"已定位微信主窗口：{refreshed.DisplayTitle} (PID {refreshed.ProcessId})");

                return new WeChatEnsureWindowResult
                {
                    Success = true,
                    Window = refreshed,
                    WasAutoRestored = neededRestore,
                    Message = neededRestore ? "已自动从后台/最小化唤起微信主窗口。" : "微信主窗口已就绪。"
                };
            }

            // Check if only login window is open
            var loginWin = windows.FirstOrDefault(w => w.DetectedState == WeChatLoginState.LoginPage);
            if (loginWin is not null)
            {
                _ = Win32WindowHelper.ForceBringToForeground(loginWin.Hwnd);
                return new WeChatEnsureWindowResult
                {
                    Success = allowLoginPageSuccess,
                    Window = loginWin,
                    WasAutoRestored = true,
                    IsLoginPage = true,
                    Message = "已自动弹出微信登录窗口，请先扫码登录后重试。"
                };
            }

            // No window detected: either hidden in system tray without a visible top-level window, or not running at all.
            bool wasRunning = HasRunningPersonalWeChat();
            if (!wasRunning && !autoLaunchIfClosed)
            {
                return new WeChatEnsureWindowResult
                {
                    Success = false,
                    Message = "未检测到运行中的个人微信。"
                };
            }

            string exePath = ResolveWeChatExecutablePath();
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return new WeChatEnsureWindowResult
                {
                    Success = false,
                    Message = "未检测到微信窗口，且未自动找到微信安装路径；请先在「微信监控」页面配置微信程序路径。"
                };
            }

            AppServices.Logs.Info("WIN", wasRunning
                ? $"微信在系统托盘驻留，正在自动唤起：{exePath}"
                : $"微信未运行，正在自动启动：{exePath}");

            if (!TryStartExecutable(exePath))
            {
                return new WeChatEnsureWindowResult
                {
                    Success = false,
                    Message = $"自动启动/唤起微信失败（路径：{exePath}）。"
                };
            }

            var popped = await WaitForPersonalWindowAsync(timeoutMs: wasRunning ? 4500 : 9000, ct);
            if (popped is null)
            {
                return new WeChatEnsureWindowResult
                {
                    Success = false,
                    WasAutoLaunched = !wasRunning,
                    Message = wasRunning
                        ? "已尝试唤起托盘中的微信，但尚未检测到主窗口，请确认微信未被系统拦截。"
                        : "已启动微信程序，但等待窗口弹出超时，请稍候再试。"
                };
            }

            _ = Win32WindowHelper.ForceBringToForeground(popped.Hwnd);
            await Task.Delay(250, ct);

            // Refresh once more so dimensions & state are accurate
            popped = EnumerateWindows(includeAllVisibleWindows: false)
                .FirstOrDefault(w => w.Hwnd == popped.Hwnd) ?? popped;

            if (popped.DetectedState == WeChatLoginState.LoginPage)
            {
                return new WeChatEnsureWindowResult
                {
                    Success = allowLoginPageSuccess,
                    Window = popped,
                    WasAutoLaunched = !wasRunning,
                    WasAutoRestored = wasRunning,
                    IsLoginPage = true,
                    Message = "已自动启动并弹出微信窗口，请先完成扫码登录后重试。"
                };
            }

            return new WeChatEnsureWindowResult
            {
                Success = true,
                Window = popped,
                WasAutoLaunched = !wasRunning,
                WasAutoRestored = wasRunning,
                Message = wasRunning ? "已自动从系统托盘唤起微信主窗口。" : "已自动启动并弹出微信主窗口。"
            };
        }

        public List<WeChatWindowInfo> EnumerateWindows(bool includeAllVisibleWindows = false)
        {
            var results = new List<WeChatWindowInfo>();
            var processCache = new Dictionary<int, string>();
            int currentPid = Environment.ProcessId;
            nint foregroundHwnd = Win32WindowHelper.GetForegroundWindow();

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
                    if (!isVisible || string.IsNullOrWhiteSpace(title) || rect.Width < 200 || rect.Height < 150)
                    {
                        return true;
                    }
                }
                else
                {
                    bool isKnownClass = KnownLoginClasses.Contains(className) || KnownMainClasses.Contains(className);
                    bool isContactMgr = title.Contains("通讯录管理", StringComparison.Ordinal) ||
                                        className.Contains("ContactManage", StringComparison.OrdinalIgnoreCase);
                    bool looksLikeMainWindow = !string.IsNullOrWhiteSpace(title) || isKnownClass || isContactMgr;
                    bool hasReasonableSize = isMinimized || isKnownClass || isContactMgr || (rect.Width >= 240 && rect.Height >= 200);
                    if (!looksLikeMainWindow || (!isVisible && !isKnownClass && !isContactMgr) || !hasReasonableSize)
                    {
                        return true;
                    }
                }

                int safeW = Math.Max(0, rect.Width);
                int safeH = Math.Max(0, rect.Height);
                string role = InferWindowRole(className, title, safeW, safeH, isVisible, isMinimized);
                var initialState = InferInitialState(className, title, role, isVisible, isMinimized);

                results.Add(new WeChatWindowInfo
                {
                    Hwnd = hWnd,
                    ProcessId = pid,
                    ProcessName = processName,
                    ClassName = className,
                    Title = title,
                    WindowRole = role,
                    AccountType = accountType,
                    IsVisible = isVisible,
                    IsMinimized = isMinimized,
                    IsForeground = hWnd == foregroundHwnd,
                    Width = safeW,
                    Height = safeH,
                    DetectedState = initialState
                });

                return true;
            }, nint.Zero);

            return results
                .OrderBy(w => w.AccountType == WeChatAccountType.OtherWindow ? 2 : w.AccountType == WeChatAccountType.Personal ? 0 : 1)
                .ThenBy(w => w.WindowRole == "主窗口" ? 0 : w.WindowRole == "登录窗口" ? 1 : w.WindowRole == "通讯录管理" ? 2 : 3)
                .ThenByDescending(w => w.Width * w.Height)
                .ToList();
        }

        public bool ShowWindow(WeChatWindowInfo window)
        {
            if (!Win32WindowHelper.IsWindow(window.Hwnd))
            {
                return false;
            }

            _ = Win32WindowHelper.ForceBringToForeground(window.Hwnd);

            // Tray-hidden WeChat 4.x fallback: single-instance activation unhides cleanly
            if (!Win32WindowHelper.IsWindowVisible(window.Hwnd) && window.AccountType == WeChatAccountType.Personal)
            {
                string exe = ResolveWeChatExecutablePath();
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    TryStartExecutable(exe);
                }
            }

            window.IsVisible = true;
            window.IsMinimized = false;
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

            _ = Win32WindowHelper.ShowWindow(window.Hwnd, Win32WindowHelper.SW_MINIMIZE);
            window.IsMinimized = true;
            window.DetectedState = WeChatLoginState.HiddenOrMinimized;
            PauseReason = "已最小化该微信窗口，可在任务栏或点击「唤起前台」随时恢复。";
            AppServices.Logs.Info("WIN", $"最小化窗口：{window.DisplayTitle}");
            return true;
        }

        public SoftwareBitmap CaptureWindow(WeChatWindowInfo window)
        {
            if (!window.IsVisible || window.IsMinimized || Win32WindowHelper.IsIconic(window.Hwnd) || !Win32WindowHelper.IsWindowVisible(window.Hwnd))
            {
                _ = ShowWindow(window);
                Thread.Sleep(280);
            }
            AppServices.Logs.Info("WIN", $"正在截取窗口画面：{window.DisplayTitle} ({window.DimensionsLabel})");
            return Win32WindowHelper.CaptureWindowToSoftwareBitmap(window.Hwnd);
        }

        private async Task<WeChatWindowInfo?> WaitForPersonalWindowAsync(int timeoutMs, CancellationToken ct)
        {
            int elapsed = 0;
            const int step = 300;
            while (elapsed < timeoutMs)
            {
                ct.ThrowIfCancellationRequested();
                var list = EnumerateWindows(includeAllVisibleWindows: false)
                    .Where(w => w.AccountType == WeChatAccountType.Personal)
                    .ToList();
                var best = PickBestMainWindow(list) ?? list.FirstOrDefault(w => w.DetectedState == WeChatLoginState.LoginPage);
                if (best is not null && (best.IsVisible || best.Width >= 220))
                {
                    return best;
                }
                await Task.Delay(step, ct);
                elapsed += step;
            }
            return null;
        }

        private static WeChatWindowInfo? PickBestMainWindow(List<WeChatWindowInfo> personalWindows)
        {
            return personalWindows
                .Where(w => w.WindowRole == "主窗口")
                .OrderByDescending(w => w.IsVisible && !w.IsMinimized)
                .ThenByDescending(w => w.Width * w.Height)
                .FirstOrDefault()
                ?? personalWindows
                    .Where(w => w.WindowRole != "通讯录管理" && w.WindowRole != "登录窗口" && w.Width >= 420)
                    .OrderByDescending(w => w.Width * w.Height)
                    .FirstOrDefault();
        }

        private static bool HasRunningPersonalWeChat()
        {
            foreach (string name in PersonalWeChatProcesses)
            {
                try
                {
                    var procs = Process.GetProcessesByName(name);
                    bool any = procs.Length > 0;
                    foreach (var p in procs) p.Dispose();
                    if (any) return true;
                }
                catch { }
            }
            return false;
        }

        private static bool TryStartExecutable(string exePath)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                AppServices.Logs.Error("WIN", $"启动程序失败 ({exePath})：{ex.Message}");
                return false;
            }
        }

        private static string? TryFindFromRegistry()
        {
            string?[] regChecks =
            [
                ReadRegInstallExe(Registry.CurrentUser, @"Software\Tencent\Weixin", "InstallPath", "Weixin.exe"),
                ReadRegInstallExe(Registry.CurrentUser, @"Software\Tencent\WeChat", "InstallPath", "WeChat.exe"),
                ReadRegUninstallExe(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Weixin", "Weixin.exe"),
                ReadRegUninstallExe(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Weixin", "Weixin.exe"),
                ReadRegUninstallExe(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\WeChat", "WeChat.exe"),
                ReadRegUninstallExe(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WeChat", "WeChat.exe")
            ];

            return regChecks.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
        }

        private static string? ReadRegInstallExe(RegistryKey root, string subKey, string valueName, string exeName)
        {
            try
            {
                using var key = root.OpenSubKey(subKey);
                string? dir = key?.GetValue(valueName) as string;
                if (string.IsNullOrWhiteSpace(dir)) return null;
                dir = dir.Trim().Trim('"');
                if (dir.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return dir;
                return Path.Combine(dir, exeName);
            }
            catch
            {
                return null;
            }
        }

        private static string? ReadRegUninstallExe(RegistryKey root, string subKey, string exeName)
        {
            try
            {
                using var key = root.OpenSubKey(subKey);
                if (key is null) return null;

                if (key.GetValue("DisplayIcon") is string icon && !string.IsNullOrWhiteSpace(icon))
                {
                    string clean = icon.Split(',')[0].Trim().Trim('"');
                    if (clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(clean))
                        return clean;
                }

                if (key.GetValue("InstallLocation") is string loc && !string.IsNullOrWhiteSpace(loc))
                {
                    string candidate = Path.Combine(loc.Trim().Trim('"'), exeName);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { }
            return null;
        }

        private static WeChatLoginState InferProcessLoginState(List<WeChatWindowInfo> pidWindows, bool isMainProcess)
        {
            if (pidWindows.Count == 0)
            {
                return isMainProcess ? WeChatLoginState.HiddenOrMinimized : WeChatLoginState.Unknown;
            }
            if (pidWindows.Any(w => w.DetectedState == WeChatLoginState.LoggedIn)) return WeChatLoginState.LoggedIn;
            if (pidWindows.Any(w => w.DetectedState == WeChatLoginState.LoginPage)) return WeChatLoginState.LoginPage;
            if (pidWindows.Any(w => w.DetectedState == WeChatLoginState.HiddenOrMinimized)) return WeChatLoginState.HiddenOrMinimized;
            return WeChatLoginState.Unknown;
        }

        private static string InferWindowRole(string className, string title, int width, int height, bool isVisible, bool isMinimized)
        {
            if (KnownLoginClasses.Contains(className) || title.Contains("登录", StringComparison.Ordinal))
            {
                return "登录窗口";
            }

            if (title.Contains("通讯录管理", StringComparison.Ordinal) ||
                className.Contains("ContactManage", StringComparison.OrdinalIgnoreCase))
            {
                return "通讯录管理";
            }

            // WeChat 4.x login card uses mmui::MainWindow with compact portrait dimensions (~280x400)
            if (className.Equals("mmui::MainWindow", StringComparison.OrdinalIgnoreCase) &&
                isVisible && !isMinimized &&
                width >= 200 && width <= 390 && height >= 300 && height <= 540)
            {
                return "登录窗口";
            }

            if (KnownMainClasses.Contains(className) ||
                title.Equals("微信", StringComparison.Ordinal) ||
                title.Equals("WeChat", StringComparison.OrdinalIgnoreCase) ||
                title.Equals("Weixin", StringComparison.OrdinalIgnoreCase) ||
                title.Equals("企业微信", StringComparison.Ordinal))
            {
                return "主窗口";
            }

            return "子窗口";
        }

        private static WeChatLoginState InferInitialState(string className, string title, string role, bool isVisible, bool isMinimized)
        {
            if (role == "登录窗口")
            {
                return WeChatLoginState.LoginPage;
            }

            if (!isVisible || isMinimized)
            {
                return WeChatLoginState.HiddenOrMinimized;
            }

            if (role is "主窗口" or "通讯录管理" || KnownMainClasses.Contains(className))
            {
                return WeChatLoginState.LoggedIn;
            }

            return WeChatLoginState.Unknown;
        }

        private static WeChatAccountType ClassifyProcess(string processName)
        {
            if (PersonalWeChatProcesses.Contains(processName)) return WeChatAccountType.Personal;
            if (EnterpriseWeChatProcesses.Contains(processName)) return WeChatAccountType.Enterprise;
            return WeChatAccountType.OtherWindow;
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

        private static string TryGetProcessPath(Process proc)
        {
            try { return proc.MainModule?.FileName ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static DateTime? TryGetStartTime(Process proc)
        {
            try { return proc.StartTime; }
            catch { return null; }
        }

        private static string TryGetFileVersion(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return string.Empty;
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exePath);
                string ver = !string.IsNullOrWhiteSpace(info.ProductVersion) ? info.ProductVersion : info.FileVersion ?? string.Empty;
                return ver.Split(' ')[0].Trim();
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
