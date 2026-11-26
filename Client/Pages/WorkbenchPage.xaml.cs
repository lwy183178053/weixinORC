using Client.Dialogs;
using Client.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Client.Pages
{
    public sealed partial class WorkbenchPage : Page
    {
        public static WorkbenchPage? Current { get; private set; }

        private string _activeTag = "customers";
        private bool _isLoggingOut;
        private Storyboard? _tabFadeStoryboard;
        private Storyboard? _toastFadeStoryboard;
        private CancellationTokenSource? _toastDismissCts;
        private string _lastToastMessage = string.Empty;
        private bool _lastToastIsError;
        private DateTimeOffset _lastToastDismissedAtUtc = DateTimeOffset.MinValue;
        private bool _isToastVisible;

        public WorkbenchPage()
        {
            InitializeComponent();
            Current = this;
            WorkspaceFrame.ContentTransitions = [];
            WorkspaceFrame.CacheSize = 5;
            Loaded += WorkbenchPage_Loaded;
            Unloaded += WorkbenchPage_Unloaded;
        }

        private void WorkbenchPage_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= WorkbenchPage_Loaded;
            AppServices.Settings.ThemeChanged += OnThemeChanged;
            AppServices.WeChat4Sync.AutoSyncStatusChanged += OnAutoSyncStatusChanged;
            MainWindow.Instance?.SetDragRegion(WorkbenchDragRegion);
            RefreshHeaderAndFooter();
            UpdateThemeToggleVisual(AppServices.Settings.IsDarkMode);
            AppServices.GroupAutoReply.EnsureInitialized();
            AppServices.WeChat4Sync.StartAutoSyncIfConfigured();
            string initialSection = AppSettingsService.NormalizeWorkbenchSection(AppServices.Settings.Current.LastWorkbenchSection);
            SwitchSection(initialSection, persist: false);
        }

        private void WorkbenchPage_Unloaded(object sender, RoutedEventArgs e)
        {
            AppServices.Settings.ThemeChanged -= OnThemeChanged;
            AppServices.WeChat4Sync.AutoSyncStatusChanged -= OnAutoSyncStatusChanged;
            _toastDismissCts?.Cancel();
            _toastDismissCts?.Dispose();
            _toastDismissCts = null;
        }

        /// <summary>
        /// Displays a floating, non-blocking, auto-dismissing toast at top-center of the workspace
        /// without pushing or squeezing any page UI. Deduplicates rapid repeated calls for the same message.
        /// </summary>
        public void ShowToast(string message, bool isError = false, int? durationMs = null)
        {
            string clean = (message ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(clean)) return;

            DispatcherQueue.TryEnqueue(() =>
            {
                bool isProgress = !isError && clean.StartsWith("正在", StringComparison.Ordinal);
                int hideAfterMs = durationMs ?? (isError ? 4200 : (isProgress ? 8000 : 2800));
                bool sameMessage = string.Equals(clean, _lastToastMessage, StringComparison.Ordinal) && _lastToastIsError == isError;

                // Prevent rapid duplicate re-pop when user clicks repeatedly
                if (sameMessage && _isToastVisible)
                {
                    ScheduleToastAutoDismiss(hideAfterMs);
                    return;
                }
                if (sameMessage && !_isToastVisible && (DateTimeOffset.UtcNow - _lastToastDismissedAtUtc).TotalMilliseconds < 700)
                {
                    return;
                }

                _lastToastMessage = clean;
                _lastToastIsError = isError;

                // Clean leading check glyph if we already show a crisp success icon
                string display = !isError && clean.StartsWith("✓ ", StringComparison.Ordinal)
                    ? clean[2..].TrimStart()
                    : clean;

                GlobalToastText.Text = display;
                if (isError)
                {
                    GlobalToastBorder.Background = (Brush)Application.Current.Resources["DangerBgBrush"];
                    GlobalToastBorder.BorderBrush = (Brush)Application.Current.Resources["DangerBorderBrush"];
                    GlobalToastIcon.Glyph = "\uEA39";
                    GlobalToastIcon.Foreground = (Brush)Application.Current.Resources["DangerTextBrush"];
                    GlobalToastText.Foreground = (Brush)Application.Current.Resources["DangerTextBrush"];
                }
                else if (isProgress)
                {
                    GlobalToastBorder.Background = (Brush)Application.Current.Resources["BrandSoftBgBrush"];
                    GlobalToastBorder.BorderBrush = (Brush)Application.Current.Resources["BrandSoftBorderBrush"];
                    GlobalToastIcon.Glyph = "\uE946";
                    GlobalToastIcon.Foreground = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
                    GlobalToastText.Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"];
                }
                else
                {
                    GlobalToastBorder.Background = (Brush)Application.Current.Resources["SuccessBgBrush"];
                    GlobalToastBorder.BorderBrush = (Brush)Application.Current.Resources["SuccessTextBrush"];
                    GlobalToastIcon.Glyph = "\uE73E";
                    GlobalToastIcon.Foreground = (Brush)Application.Current.Resources["SuccessTextBrush"];
                    GlobalToastText.Foreground = (Brush)Application.Current.Resources["SuccessTextBrush"];
                }

                if (!_isToastVisible)
                {
                    _isToastVisible = true;
                    GlobalToastBorder.Visibility = Visibility.Visible;
                    AnimateToastOpacity(to: 1.0, durationMs: 140);
                }
                else
                {
                    GlobalToastBorder.Opacity = 1.0;
                }

                ScheduleToastAutoDismiss(hideAfterMs);
            });
        }

        private void ScheduleToastAutoDismiss(int delayMs)
        {
            _toastDismissCts?.Cancel();
            _toastDismissCts?.Dispose();
            var cts = new CancellationTokenSource();
            _toastDismissCts = cts;
            _ = AutoDismissToastAsync(delayMs, cts.Token);
        }

        private async Task AutoDismissToastAsync(int delayMs, CancellationToken ct)
        {
            try
            {
                await Task.Delay(delayMs, ct);
                if (ct.IsCancellationRequested) return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!ct.IsCancellationRequested)
                    {
                        HideToast();
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void HideToast()
        {
            if (!_isToastVisible) return;
            _isToastVisible = false;
            _lastToastDismissedAtUtc = DateTimeOffset.UtcNow;
            AnimateToastOpacity(to: 0.0, durationMs: 160, onCompleted: () =>
            {
                if (!_isToastVisible)
                {
                    GlobalToastBorder.Visibility = Visibility.Collapsed;
                }
            });
        }

        private void AnimateToastOpacity(double to, int durationMs, Action? onCompleted = null)
        {
            _toastFadeStoryboard?.Stop();
            var anim = new DoubleAnimation
            {
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(anim, GlobalToastBorder);
            Storyboard.SetTargetProperty(anim, "Opacity");

            var sb = new Storyboard();
            sb.Children.Add(anim);
            if (onCompleted is not null)
            {
                sb.Completed += (_, _) => onCompleted();
            }
            _toastFadeStoryboard = sb;
            sb.Begin();
        }

        private void DismissToastButton_Click(object sender, RoutedEventArgs e)
        {
            _toastDismissCts?.Cancel();
            HideToast();
        }

        private void OnAutoSyncStatusChanged(string status)
        {
            DispatcherQueue.TryEnqueue(() => FooterOcrText.Text = status);
        }

        private void OnThemeChanged(bool isDark)
        {
            UpdateThemeToggleVisual(isDark);
            UpdateRailVisuals();
        }

        private void UpdateThemeToggleVisual(bool isDark)
        {
            // When in Dark mode, show Sun icon ("明亮") to switch to Light; in Light mode, show Moon icon ("黑夜") to switch to Dark
            ThemeToggleIcon.Glyph = isDark ? "\uE706" : "\uE708";
            ThemeToggleLabel.Text = isDark ? "明亮" : "黑夜";
            ToolTipService.SetToolTip(ThemeToggleButton, isDark ? "切换到明亮模式" : "切换到黑夜模式");
        }

        private void RefreshHeaderAndFooter()
        {
            var employee = AppServices.Backend.CurrentEmployee;
            string host = AppServices.Backend.BaseUrl.Replace("http://", "").Replace("https://", "");
            TopBackendText.Text = "服务已连接";
            ToolTipService.SetToolTip(TopServerBadge, $"当前服务地址：{host}");

            if (employee is not null)
            {
                string display = string.IsNullOrWhiteSpace(employee.DisplayName) ? employee.Account : employee.DisplayName;
                TopEmployeeNameText.Text = $"{display} ({employee.Account})";
                EmployeeInitialText.Text = display.Length > 0 ? display[..1].ToUpperInvariant() : "E";
                FooterEmployeeText.Text = $"员工 ID: {employee.EmployeeId}";
            }

            FooterOcrText.Text = AppServices.WeChat4Sync.WatchStatusText;
        }

        private void RailNav_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                SwitchSection(tag);
            }
        }

        public void SwitchSection(string tag, bool persist = true)
        {
            string normalized = AppSettingsService.NormalizeWorkbenchSection(tag);
            _activeTag = normalized;
            UpdateRailVisuals();

            if (persist && !string.Equals(AppServices.Settings.Current.LastWorkbenchSection, normalized, StringComparison.OrdinalIgnoreCase))
            {
                AppServices.Settings.Current.LastWorkbenchSection = normalized;
                AppServices.Settings.Save();
            }

            (Type pageType, string title) = normalized switch
            {
                "customers" => (typeof(CustomersPage), "客户档案"),
                "groups" => (typeof(GroupsPage), "群组档案"),
                "ai" => (typeof(AiAssistantPage), "AI 助手"),
                "autoreply" => (typeof(AutoReplyPage), "自动回复"),
                _ => (typeof(WeChatSyncPage), "微信监听")
            };

            ActiveTabTitleText.Text = title;
            if (WorkspaceFrame.CurrentSourcePageType != pageType)
            {
                bool hadPage = WorkspaceFrame.CurrentSourcePageType is not null;
                WorkspaceFrame.Navigate(pageType, null, new SuppressNavigationTransitionInfo());
                if (hadPage)
                {
                    PlaySubtleTabFade();
                }
            }
        }

        private void PlaySubtleTabFade()
        {
            _tabFadeStoryboard?.Stop();
            var anim = new DoubleAnimation
            {
                From = 0.88,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(95)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(anim, WorkspaceFrame);
            Storyboard.SetTargetProperty(anim, "Opacity");

            var sb = new Storyboard();
            sb.Children.Add(anim);
            _tabFadeStoryboard = sb;
            sb.Begin();
        }

        private void UpdateRailVisuals()
        {
            var activeBg = (Brush)Application.Current.Resources["BrandSoftBgBrush"];
            var activeFg = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
            var inactiveBg = new SolidColorBrush(Colors.Transparent);
            var inactiveFg = (Brush)Application.Current.Resources["TextSecondaryBrush"];

            SetRailItemState(RailCustomersButton, RailCustomersIcon, RailCustomersLabel, _activeTag == "customers", activeBg, activeFg, inactiveBg, inactiveFg);
            SetRailItemState(RailGroupsButton, RailGroupsIcon, RailGroupsLabel, _activeTag == "groups", activeBg, activeFg, inactiveBg, inactiveFg);
            SetRailItemState(RailAiButton, RailAiIcon, RailAiLabel, _activeTag == "ai", activeBg, activeFg, inactiveBg, inactiveFg);
            SetRailItemState(RailOcrButton, RailOcrIcon, RailOcrLabel, _activeTag == "ocr", activeBg, activeFg, inactiveBg, inactiveFg);
            SetRailItemState(RailAutoReplyButton, RailAutoReplyIcon, RailAutoReplyLabel, _activeTag == "autoreply", activeBg, activeFg, inactiveBg, inactiveFg);
        }

        private static void SetRailItemState(
            Button button,
            FontIcon icon,
            TextBlock label,
            bool isActive,
            Brush activeBg,
            Brush activeFg,
            Brush inactiveBg,
            Brush inactiveFg)
        {
            button.Background = isActive ? activeBg : inactiveBg;
            icon.Foreground = isActive ? activeFg : inactiveFg;
            label.Foreground = isActive ? activeFg : inactiveFg;
            label.FontWeight = isActive ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }

        private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
        {
            AppServices.Settings.ToggleTheme();
        }

        private async void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (!AppServices.TryBeginModalDialog()) return;
            try
            {
                var dialog = new SettingsDialog
                {
                    XamlRoot = XamlRoot,
                    RequestedTheme = AppServices.Settings.CurrentElementTheme
                };
                await dialog.ShowAsync();
                RefreshHeaderAndFooter();
            }
            catch (Exception ex)
            {
                AppServices.Logs.Error("UI", $"打开设置弹窗失败：{ex.Message}");
            }
            finally
            {
                AppServices.EndModalDialog();
            }
        }

        private async void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoggingOut) return;
            _isLoggingOut = true;
            try
            {
                AppServices.GroupAutoReply.ResetRuntimeStateForLogout();
                AppServices.WeChat4Sync.ResetAccountCachesForLogout();
                await AppServices.Backend.LogoutAsync();
                CustomersPage.ClearSessionLocalState();
                // Evict all cached workspace pages so switching accounts starts with a clean slate.
                WorkspaceFrame.CacheSize = 0;
                WorkspaceFrame.Content = null;
                WorkspaceFrame.CacheSize = 5;
                MainWindow.Instance?.ShowLoginView();
            }
            catch (Exception ex)
            {
                AppServices.Logs.Error("AUTH", $"退出登录异常：{ex.Message}");
            }
            finally
            {
                _isLoggingOut = false;
            }
        }
    }
}
