using System.Collections.ObjectModel;
using System.Diagnostics;
using Client.Services;
using Client.Services.WeChat4Db;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Client.Pages
{
    public sealed partial class WeChatSyncPage : Page
    {
        private readonly ObservableCollection<string> _activityLogs = [];
        private bool _isInitializingUi = true;
        private bool _isBusy;

        public WeChatSyncPage()
        {
            InitializeComponent();
            ActivityLogListView.ItemsSource = _activityLogs;
            Loaded += WeChatSyncPage_Loaded;
            Unloaded += WeChatSyncPage_Unloaded;
        }

        private async void WeChatSyncPage_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= WeChatSyncPage_Loaded;
            AppServices.WeChat4Sync.AutoSyncStatusChanged += OnAutoSyncStatusChanged;
            AppServices.WeChat4Sync.ActivityLogged += OnActivityLogged;
            AppServices.WeChat4Sync.SyncCompleted += OnSyncCompleted;

            var cfg = AppServices.Settings.Current;
            AutoWatchToggle.IsOn = cfg.WeChatAutoSyncEnabled;
            OptImagesCheck.IsChecked = cfg.WeChatSyncIncludeImages;
            OptAvatarsCheck.IsChecked = cfg.WeChatSyncIncludeAvatars;
            _isInitializingUi = false;

            LoadExistingLogs();
            await RefreshStatusAsync();
        }

        private void WeChatSyncPage_Unloaded(object sender, RoutedEventArgs e)
        {
            AppServices.WeChat4Sync.AutoSyncStatusChanged -= OnAutoSyncStatusChanged;
            AppServices.WeChat4Sync.ActivityLogged -= OnActivityLogged;
            AppServices.WeChat4Sync.SyncCompleted -= OnSyncCompleted;
        }

        private void LoadExistingLogs()
        {
            _activityLogs.Clear();
            foreach (var line in AppServices.WeChat4Sync.GetRecentActivityLogs())
            {
                _activityLogs.Add(line);
            }
            if (_activityLogs.Count == 0)
            {
                _activityLogs.Add("等待微信消息变化中... 导入客户或群聊后，有新消息时会自动同步。");
            }
        }

        private void OnAutoSyncStatusChanged(string status)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                WatchStatusSummaryText.Text = status;
                UpdateKpiCards();
            });
        }

        private void OnActivityLogged(string stampedLine)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_activityLogs.Count == 1 && _activityLogs[0].StartsWith("等待微信消息变化", StringComparison.Ordinal))
                {
                    _activityLogs.Clear();
                }
                _activityLogs.Insert(0, stampedLine);
                while (_activityLogs.Count > 80) _activityLogs.RemoveAt(_activityLogs.Count - 1);
            });
        }

        private void OnSyncCompleted(WeChat4SyncReport _)
        {
            DispatcherQueue.TryEnqueue(UpdateKpiCards);
        }

        private async Task RefreshStatusAsync()
        {
            await AppServices.WeChat4Sync.RefreshMonitoredCountsAsync();
            UpdateKpiCards();
        }

        private void UpdateKpiCards()
        {
            var sync = AppServices.WeChat4Sync;
            var procs = WeChat4Paths.FindWeixinProcesses();
            var accounts = WeChat4Paths.ListAccountDirs(AppServices.Settings.Current.WeChatSyncAccountDir);
            string accountLabel = sync.ActiveAccount?.Wxid
                ?? accounts.FirstOrDefault()?.Wxid
                ?? string.Empty;

            if (procs.Count > 0)
            {
                KpiWeChatText.Text = "微信已运行";
                KpiWeChatSubText.Text = !string.IsNullOrWhiteSpace(accountLabel)
                    ? $"已识别账号：{accountLabel}"
                    : "已检测到运行中的电脑微信";
                StatusDot.Fill = (Brush)Application.Current.Resources["SuccessTextBrush"];
                StatusBadgeText.Text = sync.IsFileWatcherRunning ? "自动监听中" : "已连接微信";
            }
            else
            {
                KpiWeChatText.Text = "未运行";
                KpiWeChatSubText.Text = "请先在电脑上登录微信";
                StatusDot.Fill = (Brush)Application.Current.Resources["TextMutedBrush"];
                StatusBadgeText.Text = "等待微信登录";
            }

            KpiMonitoredText.Text = $"{sync.MonitoredCustomersCount} 客户 · {sync.MonitoredGroupsCount} 群";
            WatchStatusSummaryText.Text = sync.WatchStatusText;

            if (sync.LastSyncAt.HasValue)
            {
                KpiLastSyncText.Text = sync.LastSyncAt.Value.ToString("HH:mm:ss");
                if (sync.LastReport is { } r)
                {
                    int newMsgs = r.CustomerMessagesInserted + r.GroupMessagesInserted;
                    KpiLastSyncSubText.Text = newMsgs > 0
                        ? $"最近同步新增 {newMsgs} 条消息"
                        : "已核对最新记录，暂无新消息";
                }
            }
            else
            {
                KpiLastSyncText.Text = "等待新消息";
                KpiLastSyncSubText.Text = "微信收到新消息后将自动同步";
            }

            RelaunchAdminButton.Visibility = sync.LastSyncNeedsAdmin ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AutoWatchToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializingUi) return;
            AppServices.WeChat4Sync.ConfigureAutoWatch(AutoWatchToggle.IsOn);
            UpdateKpiCards();
        }

        private void OptCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializingUi) return;
            AppServices.Settings.Current.WeChatSyncIncludeImages = OptImagesCheck.IsChecked == true;
            AppServices.Settings.Current.WeChatSyncIncludeAvatars = OptAvatarsCheck.IsChecked == true;
            AppServices.Settings.Save();
        }

        private async void SyncNowButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            _isBusy = true;
            SyncNowButton.IsEnabled = false;
            try
            {
                ShowBanner("正在同步已导入的客户与群聊最新消息...", isError: false);
                var (ok, msg, _) = await AppServices.WeChat4Sync.SyncImportedTargetsAsync(
                    syncCustomers: true,
                    syncGroups: true,
                    isBackgroundAuto: false);
                ShowBanner(ok ? $"✓ {msg}" : msg, isError: !ok);
                await RefreshStatusAsync();
            }
            catch (Exception ex)
            {
                ShowBanner($"同步失败：{ex.Message}", isError: true);
            }
            finally
            {
                _isBusy = false;
                SyncNowButton.IsEnabled = true;
            }
        }

        private async void RefreshStatusButton_Click(object sender, RoutedEventArgs e)
        {
            AppServices.WeChat4Sync.StartAutoSyncIfConfigured();
            await RefreshStatusAsync();
            ShowBanner("✓ 已重新检测微信运行与监听状态。", isError: false);
        }

        private async void SelectDirButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FolderPicker();
                picker.FileTypeFilter.Add("*");
                if (AppServices.MainWindowHandle != 0)
                {
                    InitializeWithWindow.Initialize(picker, AppServices.MainWindowHandle);
                }
                var folder = await picker.PickSingleFolderAsync();
                if (folder is null) return;

                AppServices.WeChat4Sync.SetCustomAccountDir(folder.Path);
                await RefreshStatusAsync();
                ShowBanner($"✓ 已切换微信数据目录：{folder.Path}", isError: false);
            }
            catch (Exception ex)
            {
                ShowBanner($"选择目录失败：{ex.Message}", isError: true);
            }
        }

        private void RelaunchAdminButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath)) return;
                Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true, Verb = "runas" });
                Application.Current.Exit();
            }
            catch (Exception ex)
            {
                ShowBanner($"管理员重启失败：{ex.Message}", isError: true);
            }
        }

        private void GoToCustomersButton_Click(object sender, RoutedEventArgs e) =>
            WorkbenchPage.Current?.SwitchSection("customers");

        private void GoToGroupsButton_Click(object sender, RoutedEventArgs e) =>
            WorkbenchPage.Current?.SwitchSection("groups");

        private void CloseBannerButton_Click(object sender, RoutedEventArgs e) =>
            FeedbackBanner.Visibility = Visibility.Collapsed;

        private void ShowBanner(string message, bool isError)
        {
            FeedbackText.Text = message;
            FeedbackBanner.Background = (Brush)Application.Current.Resources[isError ? "DangerBgBrush" : "BrandSoftBgBrush"];
            FeedbackBanner.BorderBrush = (Brush)Application.Current.Resources[isError ? "DangerBorderBrush" : "BrandSoftBorderBrush"];
            FeedbackBanner.Visibility = Visibility.Visible;
        }
    }
}
