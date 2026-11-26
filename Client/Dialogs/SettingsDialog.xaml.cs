using System.Collections.ObjectModel;
using System.Diagnostics;
using Client.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Client.Dialogs
{
    public sealed class CompactLogRow
    {
        public CompactLogRow(AppLogEntry entry)
        {
            Entry = entry;
            TimeLabel = entry.TimeText;
            LevelLabel = entry.LevelTag;
            ModuleLabel = $"[{entry.Module}]";
            Message = entry.Message;

            string brushKey = entry.Level switch
            {
                AppLogLevel.Success => "SuccessTextBrush",
                AppLogLevel.Warning => "WarningTextBrush",
                AppLogLevel.Error => "DangerTextBrush",
                _ => "InfoTextBrush"
            };
            LevelBrush = Application.Current?.Resources[brushKey] as Brush
                ?? new SolidColorBrush(Colors.Gray);
        }

        public AppLogEntry Entry { get; }
        public string TimeLabel { get; }
        public string LevelLabel { get; }
        public string ModuleLabel { get; }
        public string Message { get; }
        public Brush LevelBrush { get; }
    }

    public sealed partial class SettingsDialog : ContentDialog
    {
        private readonly ObservableCollection<CompactLogRow> _visibleLogs = [];
        private string _activeSection = "general";
        private bool _logsSubscribed;

        public SettingsDialog()
        {
            InitializeComponent();
            RequestedTheme = AppServices.Settings.CurrentElementTheme;
            LogsListView.ItemsSource = _visibleLogs;
            Loaded += SettingsDialog_Loaded;
            Closed += (_, _) =>
            {
                AppServices.Settings.ThemeChanged -= Settings_ThemeChanged;
                UnsubscribeLogs();
            };
        }

        private void SettingsDialog_Loaded(object sender, RoutedEventArgs e)
        {
            AppServices.Settings.ThemeChanged += Settings_ThemeChanged;
            BackendUrlBox.Text = AppServices.Backend.BaseUrl;

            var emp = AppServices.Backend.CurrentEmployee;
            if (emp is not null)
            {
                string display = string.IsNullOrWhiteSpace(emp.DisplayName) ? emp.Account : emp.DisplayName;
                AccountAvatarText.Text = display.Length > 0 ? display[..1].ToUpperInvariant() : "E";
                AccountHeadlineName.Text = display;
                AccountHeadlineSub.Text = $"账号：{emp.Account}";
                AccountNameText.Text = display;
                AccountLoginText.Text = emp.Account;
                AccountIdText.Text = emp.EmployeeId;
            }

            UpdateAppearanceControls();
            SelectCategory("general");
        }

        private void Settings_ThemeChanged(bool isDark)
        {
            RequestedTheme = AppServices.Settings.CurrentElementTheme;
            UpdateAppearanceControls();
            UpdateNavVisuals();
        }

        private void ThemeModeBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string mode)
            {
                AppServices.Settings.ApplyTheme(isDark: mode == "dark", save: true);
            }
        }

        private void PaletteSwatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string palette)
            {
                AppServices.Settings.SetPalette(palette, save: true);
            }
        }

        private void UpdateAppearanceControls()
        {
            bool isDark = AppServices.Settings.IsDarkMode;
            var activeBg = (Brush)Application.Current.Resources["AppSurfaceBrush"];
            var activeFg = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
            var inactiveBg = new SolidColorBrush(Colors.Transparent);
            var inactiveFg = (Brush)Application.Current.Resources["TextSecondaryBrush"];

            SetNavState(LightModeBtn, LightModeIcon, LightModeText, !isDark, activeBg, activeFg, inactiveBg, inactiveFg);
            SetNavState(DarkModeBtn, DarkModeIcon, DarkModeText, isDark, activeBg, activeFg, inactiveBg, inactiveFg);

            string currentPalette = AppServices.Settings.CurrentPalette;
            var brandBorder = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
            var defaultBorder = (Brush)Application.Current.Resources["AppBorderBrush"];
            var activeCardBg = (Brush)Application.Current.Resources["BrandSoftBgBrush"];
            var defaultCardBg = (Brush)Application.Current.Resources["AppCanvasBgBrush"];

            SetSwatchState(PaletteVioletBtn, PaletteVioletCheck, currentPalette == "violet", brandBorder, defaultBorder, activeCardBg, defaultCardBg);
            SetSwatchState(PalettePeachBtn, PalettePeachCheck, currentPalette == "peach", brandBorder, defaultBorder, activeCardBg, defaultCardBg);
            SetSwatchState(PaletteBlueBtn, PaletteBlueCheck, currentPalette == "blue", brandBorder, defaultBorder, activeCardBg, defaultCardBg);
            SetSwatchState(PaletteAmberBtn, PaletteAmberCheck, currentPalette == "amber", brandBorder, defaultBorder, activeCardBg, defaultCardBg);
            SetSwatchState(PaletteSageBtn, PaletteSageCheck, currentPalette == "sage", brandBorder, defaultBorder, activeCardBg, defaultCardBg);

            PaletteSwatchGrid.Opacity = isDark ? 0.58 : 1.0;
            PaletteHintText.Text = isDark
                ? "当前为黑夜模式，所选颜色偏好将在切换回明亮模式时生效"
                : "选择工作台主色调（仅在明亮模式下生效）";
        }

        private static void SetSwatchState(
            Button btn,
            FontIcon checkIcon,
            bool selected,
            Brush activeBorder,
            Brush defaultBorder,
            Brush activeBg,
            Brush defaultBg)
        {
            btn.BorderBrush = selected ? activeBorder : defaultBorder;
            btn.Background = selected ? activeBg : defaultBg;
            checkIcon.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }

        private void NavCategory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                SelectCategory(tag);
            }
        }

        private void SelectCategory(string tag)
        {
            _activeSection = tag;
            bool isLogs = tag == "logs";

            GeneralPanel.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
            AccountPanel.Visibility = tag == "account" ? Visibility.Visible : Visibility.Collapsed;
            ServerPanel.Visibility = tag == "server" ? Visibility.Visible : Visibility.Collapsed;
            LogsPanel.Visibility = isLogs ? Visibility.Visible : Visibility.Collapsed;
            BackToServerButton.Visibility = isLogs ? Visibility.Visible : Visibility.Collapsed;

            (SectionTitleText.Text, SectionSubtitleText.Text) = tag switch
            {
                "account" => ("账号", "当前已登录员工的账户信息"),
                "server" => ("服务", "配置后端服务端 IP 地址并检测连接状态"),
                "logs" => ("运行日志", "轻量实时日志流，支持筛选、自检与一键复制"),
                _ => ("通用", "工作台基础偏好配置")
            };

            if (isLogs)
            {
                SubscribeLogs();
                ReloadLogs();
            }
            else
            {
                UnsubscribeLogs();
            }

            UpdateNavVisuals();
        }

        private void UpdateNavVisuals()
        {
            var activeBg = (Brush)Application.Current.Resources["BrandSoftBgBrush"];
            var activeFg = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
            var inactiveBg = new SolidColorBrush(Colors.Transparent);
            var inactiveFg = (Brush)Application.Current.Resources["TextSecondaryBrush"];

            SetNavState(NavGeneralButton, NavGeneralIcon, NavGeneralText, _activeSection == "general", activeBg, activeFg, inactiveBg, inactiveFg);
            SetNavState(NavAccountButton, NavAccountIcon, NavAccountText, _activeSection == "account", activeBg, activeFg, inactiveBg, inactiveFg);
            SetNavState(NavServerButton, NavServerIcon, NavServerText, _activeSection is "server" or "logs", activeBg, activeFg, inactiveBg, inactiveFg);
        }

        private static void SetNavState(Button btn, FontIcon icon, TextBlock text, bool active, Brush activeBg, Brush activeFg, Brush inactiveBg, Brush inactiveFg)
        {
            btn.Background = active ? activeBg : inactiveBg;
            icon.Foreground = active ? activeFg : inactiveFg;
            text.Foreground = active ? activeFg : inactiveFg;
            text.FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }

        private async void TestBackendBtn_Click(object sender, RoutedEventArgs e)
        {
            string url = BackendUrlBox.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                SetServerPingStatus("请输入有效的服务端地址（例如 http://127.0.0.1:28765）。", "DangerBgBrush", "DangerTextBrush");
                return;
            }

            try
            {
                AppServices.Backend.SetBaseUrl(url);
            }
            catch (Exception ex)
            {
                SetServerPingStatus($"✕ {ex.Message}", "DangerBgBrush", "DangerTextBrush");
                return;
            }

            AppServices.Settings.Current.BackendUrl = AppServices.Backend.BaseUrl;
            AppServices.Settings.Save();

            TestBackendBtn.IsEnabled = false;
            SetServerPingStatus("正在检测后端服务连通性...", "AppInputBgBrush", "TextSecondaryBrush");
            var sw = Stopwatch.StartNew();
            try
            {
                var res = await AppServices.Backend.CheckHealthAsync();
                sw.Stop();
                if (res.IsSuccess)
                {
                    SetServerPingStatus(
                        $"✓ 服务连接正常（{AppServices.Backend.BaseUrl}，耗时 {sw.ElapsedMilliseconds} ms）",
                        "SuccessBgBrush",
                        "SuccessTextBrush");
                }
                else
                {
                    SetServerPingStatus($"✕ 连接失败：{res.ErrorMessage}", "DangerBgBrush", "DangerTextBrush");
                }
            }
            catch (Exception ex)
            {
                SetServerPingStatus($"✕ 连接异常：{ex.Message}", "DangerBgBrush", "DangerTextBrush");
            }
            finally
            {
                TestBackendBtn.IsEnabled = true;
            }
        }

        private void SetServerPingStatus(string message, string bgKey, string fgKey)
        {
            ServerPingText.Text = message;
            ServerPingBanner.Background = (Brush)Application.Current.Resources[bgKey];
            ServerPingText.Foreground = (Brush)Application.Current.Resources[fgKey];
        }

        private void OpenLogsSubView_Click(object sender, RoutedEventArgs e)
        {
            SelectCategory("logs");
        }

        private void BackToServerButton_Click(object sender, RoutedEventArgs e)
        {
            SelectCategory("server");
        }

        private void SubscribeLogs()
        {
            if (_logsSubscribed) return;
            AppServices.Logs.LogAdded += Logs_LogAdded;
            AppServices.Logs.LogsCleared += Logs_LogsCleared;
            _logsSubscribed = true;
        }

        private void UnsubscribeLogs()
        {
            if (!_logsSubscribed) return;
            AppServices.Logs.LogAdded -= Logs_LogAdded;
            AppServices.Logs.LogsCleared -= Logs_LogsCleared;
            _logsSubscribed = false;
        }

        private void Logs_LogAdded(object? sender, AppLogEntry entry)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_logsSubscribed) return;
                if (MatchesLogFilter(entry))
                {
                    var row = new CompactLogRow(entry);
                    _visibleLogs.Add(row);
                    while (_visibleLogs.Count > 1000) _visibleLogs.RemoveAt(0);
                    UpdateLogSummary();
                    LogsListView.ScrollIntoView(row);
                }
            });
        }

        private void Logs_LogsCleared(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_logsSubscribed) return;
                _visibleLogs.Clear();
                UpdateLogSummary();
            });
        }

        private void ReloadLogs()
        {
            _visibleLogs.Clear();
            foreach (var entry in AppServices.Logs.GetSnapshot())
            {
                if (MatchesLogFilter(entry))
                {
                    _visibleLogs.Add(new CompactLogRow(entry));
                }
            }

            UpdateLogSummary();
            if (_visibleLogs.Count > 0)
            {
                LogsListView.ScrollIntoView(_visibleLogs[^1]);
            }
        }

        private bool MatchesLogFilter(AppLogEntry entry)
        {
            string levelTag = (LogLevelFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
            bool levelOk = levelTag switch
            {
                "info" => entry.Level == AppLogLevel.Info,
                "ok" => entry.Level == AppLogLevel.Success,
                "warn" => entry.Level == AppLogLevel.Warning,
                "err" => entry.Level == AppLogLevel.Error,
                _ => true
            };
            if (!levelOk) return false;

            string modTag = (LogModuleFilterCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
            if (modTag != "all")
            {
                bool modOk = modTag == "SYS_GROUP"
                    ? entry.Module is "SYS" or "UI" or "AUTH" or "WX" or "WIN32"
                    : string.Equals(entry.Module, modTag, StringComparison.OrdinalIgnoreCase);
                if (!modOk) return false;
            }

            string query = LogSearchBox.Text.Trim();
            if (string.IsNullOrEmpty(query)) return true;

            return entry.Message.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Module.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.LevelTag.Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateLogSummary()
        {
            LogCountText.Text = $"共 {_visibleLogs.Count} 条记录";
            EmptyLogsHint.Visibility = _visibleLogs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void LogFilter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (LogsListView is not null && _activeSection == "logs")
            {
                ReloadLogs();
            }
        }

        private void LogSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (LogsListView is not null && _activeSection == "logs")
            {
                ReloadLogs();
            }
        }

        private async void RunSelfCheck_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppServices.Logs.Info("SYS", $"开始执行运行环境自检...（日志文件：{AppServices.Logs.CurrentLogFilePath}）");
                var procs = Client.Services.WeChat4Db.WeChat4Paths.FindWeixinProcesses();
                var dirs = Client.Services.WeChat4Db.WeChat4Paths.ListAccountDirs();
                if (procs.Count > 0 && dirs.Count > 0)
                {
                    AppServices.Logs.Success("WX4", $"电脑微信连接环境正常（已检测到运行进程 {procs.Count} 个、本地账号目录 {dirs.Count} 个）。");
                }
                else
                {
                    AppServices.Logs.Warn("WX4", $"电脑微信检测：未发现运行中的微信或本地账号目录（进程 {procs.Count}、目录 {dirs.Count}）。");
                }

                var snap = AppServices.WeChatWindows.CaptureMonitorSnapshot();
                var mainWin = snap.Windows.FirstOrDefault(w => w.AccountType == Models.WeChatAccountType.Personal && w.WindowRole == "主窗口");
                if (mainWin is not null)
                {
                    string? mismatch = Win32WindowHelper.GetPrivilegeMismatchDetail(mainWin.Hwnd);
                    if (!string.IsNullOrWhiteSpace(mismatch))
                    {
                        AppServices.Logs.Warn("WIN", mismatch);
                    }
                    else
                    {
                        AppServices.Logs.Success("WIN", $"微信主窗口就绪：{mainWin.DisplayTitle} ({mainWin.DimensionsLabel}，PID {mainWin.ProcessId})。");
                    }
                }
                else
                {
                    AppServices.Logs.Warn("WIN", $"当前未发现可见微信主窗口（微信状态：{snap.OverallStatusLabel}）。");
                }

                if (AppServices.Ocr.RapidEngine.IsAvailable)
                {
                    AppServices.Logs.Success("OCR", $"本地识别引擎就绪（{AppServices.Ocr.RapidEngine.AvailabilityNote}）。");
                }
                else
                {
                    AppServices.Logs.Error("OCR", AppServices.Ocr.RapidEngine.AvailabilityNote);
                }

                var health = await AppServices.Backend.CheckHealthAsync();
                if (health.IsSuccess)
                {
                    AppServices.Logs.Success("API", $"后端服务连通正常（{AppServices.Backend.BaseUrl}）。");
                }
                else
                {
                    AppServices.Logs.Error("API", $"后端服务连接失败：{health.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                AppServices.Logs.Error("SYS", $"自检异常：{ex.Message}");
            }
        }

        private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            AppServices.Logs.OpenLogDirectory();
        }

        private void CopyLogs_Click(object sender, RoutedEventArgs e)
        {
            if (_visibleLogs.Count == 0) return;

            try
            {
                var text = string.Join(Environment.NewLine, _visibleLogs.Select(x => x.Entry.FormattedLine));
                var pkg = new DataPackage();
                pkg.SetText(text);
                Clipboard.SetContent(pkg);

                LogCopyFeedbackText.Text = $"已复制 {_visibleLogs.Count} 条日志";
                LogCopyFeedbackText.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                LogCopyFeedbackText.Text = $"复制失败：{ex.Message}";
                LogCopyFeedbackText.Visibility = Visibility.Visible;
            }
        }

        private void ClearLogs_Click(object sender, RoutedEventArgs e)
        {
            LogCopyFeedbackText.Visibility = Visibility.Collapsed;
            AppServices.Logs.Clear();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void CloseAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            Hide();
        }
    }
}
