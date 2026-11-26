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

        public WorkbenchPage()
        {
            InitializeComponent();
            Current = this;
            WorkspaceFrame.ContentTransitions = [];
            WorkspaceFrame.CacheSize = 4;
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
            AppServices.WeChat4Sync.StartAutoSyncIfConfigured();
            string initialSection = AppSettingsService.NormalizeWorkbenchSection(AppServices.Settings.Current.LastWorkbenchSection);
            SwitchSection(initialSection, persist: false);
        }

        private void WorkbenchPage_Unloaded(object sender, RoutedEventArgs e)
        {
            AppServices.Settings.ThemeChanged -= OnThemeChanged;
            AppServices.WeChat4Sync.AutoSyncStatusChanged -= OnAutoSyncStatusChanged;
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
                AppServices.WeChat4Sync.StopAutoSyncTimer();
                await AppServices.Backend.LogoutAsync();
                CustomersPage.ClearSessionLocalState();
                // Evict all cached workspace pages so switching accounts starts with a clean slate.
                WorkspaceFrame.CacheSize = 0;
                WorkspaceFrame.Content = null;
                WorkspaceFrame.CacheSize = 4;
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
