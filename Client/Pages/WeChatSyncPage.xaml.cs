using System.Collections.ObjectModel;
using System.Diagnostics;
using Client.Models;
using Client.Services;
using Client.Services.WeChat4Db;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Client.Pages
{
    public sealed partial class WeChatSyncPage : Page
    {
        private readonly ObservableCollection<string> _activityLogs = [];
        private readonly ObservableCollection<WeChatAccountDto> _linkedAccounts = [];
        private CancellationTokenSource? _accountAvatarCts;
        private bool _isInitializingUi = true;
        private bool _isBusy;

        public WeChatSyncPage()
        {
            InitializeComponent();
            NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
            ActivityLogListView.ItemsSource = _activityLogs;
            LinkedAccountsListView.ItemsSource = _linkedAccounts;
            Loaded += WeChatSyncPage_Loaded;
            Unloaded += WeChatSyncPage_Unloaded;
        }

        private async void WeChatSyncPage_Loaded(object sender, RoutedEventArgs e)
        {
            AppServices.Settings.ThemeChanged -= OnThemeChanged;
            AppServices.WeChat4Sync.AutoSyncStatusChanged -= OnAutoSyncStatusChanged;
            AppServices.WeChat4Sync.ActivityLogged -= OnActivityLogged;
            AppServices.WeChat4Sync.SyncCompleted -= OnSyncCompleted;
            AppServices.WeChat4Sync.AccountsUpdated -= OnAccountsUpdated;

            AppServices.Settings.ThemeChanged += OnThemeChanged;
            AppServices.WeChat4Sync.AutoSyncStatusChanged += OnAutoSyncStatusChanged;
            AppServices.WeChat4Sync.ActivityLogged += OnActivityLogged;
            AppServices.WeChat4Sync.SyncCompleted += OnSyncCompleted;
            AppServices.WeChat4Sync.AccountsUpdated += OnAccountsUpdated;

            _isInitializingUi = true;
            var cfg = AppServices.Settings.Current;
            AutoWatchToggle.IsOn = cfg.WeChatAutoSyncEnabled;
            OptImagesCheck.IsChecked = cfg.WeChatSyncIncludeImages;
            OptAvatarsCheck.IsChecked = cfg.WeChatSyncIncludeAvatars;
            _isInitializingUi = false;

            LoadExistingLogs();
            SyncLinkedAccountsCollection();
            await RefreshStatusAsync();
        }

        private void WeChatSyncPage_Unloaded(object sender, RoutedEventArgs e)
        {
            AppServices.Settings.ThemeChanged -= OnThemeChanged;
            AppServices.WeChat4Sync.AutoSyncStatusChanged -= OnAutoSyncStatusChanged;
            AppServices.WeChat4Sync.ActivityLogged -= OnActivityLogged;
            AppServices.WeChat4Sync.SyncCompleted -= OnSyncCompleted;
            AppServices.WeChat4Sync.AccountsUpdated -= OnAccountsUpdated;
            _accountAvatarCts?.Cancel();
            _accountAvatarCts?.Dispose();
            _accountAvatarCts = null;
        }

        private void OnThemeChanged(bool _)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var acc in _linkedAccounts) acc.NotifyVisualsChanged();
                RefreshAccountAndKpiUi();
            });
        }

        private void OnAccountsUpdated()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                SyncLinkedAccountsCollection();
                RefreshAccountAndKpiUi();
            });
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
            DispatcherQueue.TryEnqueue(() =>
            {
                SyncLinkedAccountsCollection();
                RefreshAccountAndKpiUi();
            });
        }

        private async Task RefreshStatusAsync()
        {
            await AppServices.WeChat4Sync.RefreshMonitoredCountsAsync();
            SyncLinkedAccountsCollection();
            RefreshAccountAndKpiUi();
        }

        private void RefreshAccountAndKpiUi()
        {
            bool wechatRunning = WeChat4Paths.FindWeixinProcesses().Count > 0;
            string? fallbackLocalWxid = AppServices.WeChat4Sync.ActiveAccount?.Wxid;
            if (string.IsNullOrWhiteSpace(fallbackLocalWxid))
            {
                fallbackLocalWxid = WeChat4Paths.ListAccountDirs(AppServices.Settings.Current.WeChatSyncAccountDir).FirstOrDefault()?.Wxid;
            }
            UpdateCurrentAccountCard(wechatRunning, fallbackLocalWxid ?? string.Empty);
            UpdateKpiCards(wechatRunning, fallbackLocalWxid ?? string.Empty);
        }

        private void SyncLinkedAccountsCollection()
        {
            var prevAvatars = _linkedAccounts
                .Where(a => a.AvatarImage is not null && !string.IsNullOrWhiteSpace(a.AvatarUrl))
                .GroupBy(a => a.AvatarUrl, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().AvatarImage!, StringComparer.Ordinal);

            var latest = AppServices.WeChat4Sync.KnownAccounts;
            _linkedAccounts.Clear();
            foreach (var item in latest)
            {
                if (item.AvatarImage is null &&
                    !string.IsNullOrWhiteSpace(item.AvatarUrl) &&
                    prevAvatars.TryGetValue(item.AvatarUrl, out var cachedImg))
                {
                    item.AvatarImage = cachedImg;
                }
                _linkedAccounts.Add(item);
            }

            LinkedAccountsCountText.Text = $"{_linkedAccounts.Count} 个账号";
            bool hasAccounts = _linkedAccounts.Count > 0;
            LinkedAccountsListView.Visibility = hasAccounts ? Visibility.Visible : Visibility.Collapsed;
            EmptyAccountsHint.Visibility = hasAccounts ? Visibility.Collapsed : Visibility.Visible;

            StartAccountAvatarHydration();
        }

        private void StartAccountAvatarHydration()
        {
            _accountAvatarCts?.Cancel();
            _accountAvatarCts?.Dispose();
            var cts = new CancellationTokenSource();
            _accountAvatarCts = cts;

            var snapshot = _linkedAccounts.ToList();
            if (snapshot.Count == 0) return;

            _ = ChatPageSharedHelper.HydrateAvatarsConcurrentlyAsync(
                snapshot,
                a => a.AvatarUrl,
                a => a.AvatarImage is not null,
                (a, img) =>
                {
                    a.AvatarImage = img;
                    var current = ResolveDisplayedCurrentAccount();
                    if (current is not null &&
                        string.Equals(current.WechatId, a.WechatId, StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyCurrentAccountAvatar(img, a.AvatarInitial);
                    }
                },
                DispatcherQueue,
                cts.Token,
                maxConcurrency: 4,
                logLabel: "微信账号头像");
        }

        private WeChatAccountDto? ResolveDisplayedCurrentAccount()
        {
            var sync = AppServices.WeChat4Sync;
            if (sync.ActiveAccount is not null)
            {
                var exact = _linkedAccounts.FirstOrDefault(a =>
                    string.Equals(a.WechatId, sync.ActiveAccount.Wxid, StringComparison.OrdinalIgnoreCase));
                if (exact is not null) return exact;
            }
            return _linkedAccounts.FirstOrDefault(a => a.IsOnline)
                ?? _linkedAccounts.FirstOrDefault()
                ?? sync.CurrentOnlineAccount;
        }

        private void ApplyCurrentAccountAvatar(ImageSource? img, string initial)
        {
            CurrentAccountAvatarText.Text = string.IsNullOrWhiteSpace(initial) ? "微" : initial;
            if (img is not null)
            {
                CurrentAccountAvatarImage.Source = img;
                CurrentAccountAvatarImage.Visibility = Visibility.Visible;
                CurrentAccountAvatarText.Visibility = Visibility.Collapsed;
            }
            else
            {
                CurrentAccountAvatarImage.Source = null;
                CurrentAccountAvatarImage.Visibility = Visibility.Collapsed;
                CurrentAccountAvatarText.Visibility = Visibility.Visible;
            }
        }

        private void UpdateCurrentAccountCard(bool? wechatRunningOverride = null, string? fallbackLocalWxidOverride = null)
        {
            var sync = AppServices.WeChat4Sync;
            bool wechatRunning = wechatRunningOverride ?? WeChat4Paths.FindWeixinProcesses().Count > 0;
            var current = ResolveDisplayedCurrentAccount();
            var selfLocal = sync.ActiveReader?.SelfContact;

            if (current is not null)
            {
                CurrentAccountNameText.Text = current.DisplayName;
                ApplyCurrentAccountAvatar(current.AvatarImage, current.AvatarInitial);

                var idParts = new List<string>(3);
                if (!string.IsNullOrWhiteSpace(current.Alias)) idParts.Add($"微信号：{current.Alias}");
                if (!string.IsNullOrWhiteSpace(current.WechatId) &&
                    !current.WechatId.Equals(current.Alias, StringComparison.OrdinalIgnoreCase))
                {
                    idParts.Add($"ID：{current.WechatId}");
                }
                if (!string.IsNullOrWhiteSpace(current.Region)) idParts.Add($"地区：{current.Region}");
                CurrentAccountIdText.Text = idParts.Count > 0
                    ? string.Join(" · ", idParts)
                    : current.CountsSummary;

                if (!string.IsNullOrWhiteSpace(current.Signature))
                {
                    CurrentAccountBioText.Text = $"个性签名：{current.Signature}";
                    CurrentAccountBioText.Visibility = Visibility.Visible;
                }
                else
                {
                    CurrentAccountBioText.Text = current.CountsSummary;
                    CurrentAccountBioText.Visibility = Visibility.Visible;
                }

                bool showOnline = wechatRunning && current.IsOnline;
                CurrentAccountStatusDot.Fill = (Brush)Application.Current.Resources[showOnline ? "SuccessTextBrush" : "TextMutedBrush"];
                CurrentAccountStatusBadge.Background = (Brush)Application.Current.Resources[showOnline ? "SuccessBgBrush" : "AppInputBgBrush"];
                CurrentAccountStatusText.Foreground = (Brush)Application.Current.Resources[showOnline ? "SuccessTextBrush" : "TextSecondaryBrush"];
                CurrentAccountStatusText.Text = showOnline
                    ? "当前在线"
                    : (wechatRunning ? "历史账号 · 点击右侧刷新" : "电脑微信未运行");
                return;
            }

            if (selfLocal is not null)
            {
                string display = selfLocal.DisplayName;
                CurrentAccountNameText.Text = display;
                ApplyCurrentAccountAvatar(null, display.Length > 0 ? display[..1].ToUpperInvariant() : "微");

                var parts = new List<string>(3);
                if (!string.IsNullOrWhiteSpace(selfLocal.Alias)) parts.Add($"微信号：{selfLocal.Alias}");
                if (!string.IsNullOrWhiteSpace(selfLocal.Username)) parts.Add($"ID：{selfLocal.Username}");
                if (!string.IsNullOrWhiteSpace(selfLocal.Region)) parts.Add($"地区：{selfLocal.Region}");
                CurrentAccountIdText.Text = parts.Count > 0 ? string.Join(" · ", parts) : "已读取本地微信资料";

                CurrentAccountBioText.Text = !string.IsNullOrWhiteSpace(selfLocal.Signature) ? $"个性签名：{selfLocal.Signature}" : string.Empty;
                CurrentAccountBioText.Visibility = string.IsNullOrWhiteSpace(selfLocal.Signature) ? Visibility.Collapsed : Visibility.Visible;

                CurrentAccountStatusDot.Fill = (Brush)Application.Current.Resources[wechatRunning ? "SuccessTextBrush" : "TextMutedBrush"];
                CurrentAccountStatusBadge.Background = (Brush)Application.Current.Resources[wechatRunning ? "SuccessBgBrush" : "AppInputBgBrush"];
                CurrentAccountStatusText.Foreground = (Brush)Application.Current.Resources[wechatRunning ? "SuccessTextBrush" : "TextSecondaryBrush"];
                CurrentAccountStatusText.Text = wechatRunning ? "当前在线" : "未运行";
                return;
            }

            string localWxid = !string.IsNullOrWhiteSpace(fallbackLocalWxidOverride)
                ? fallbackLocalWxidOverride
                : sync.ActiveAccount?.Wxid
                    ?? WeChat4Paths.ListAccountDirs(AppServices.Settings.Current.WeChatSyncAccountDir).FirstOrDefault()?.Wxid
                    ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(localWxid))
            {
                CurrentAccountNameText.Text = localWxid;
                ApplyCurrentAccountAvatar(null, "微");
                CurrentAccountIdText.Text = "已发现本机微信数据目录，点击右侧「识别当前微信」即可获取头像与昵称资料";
                CurrentAccountBioText.Visibility = Visibility.Collapsed;
                CurrentAccountStatusDot.Fill = (Brush)Application.Current.Resources[wechatRunning ? "SuccessTextBrush" : "TextMutedBrush"];
                CurrentAccountStatusBadge.Background = (Brush)Application.Current.Resources[wechatRunning ? "SuccessBgBrush" : "AppInputBgBrush"];
                CurrentAccountStatusText.Foreground = (Brush)Application.Current.Resources[wechatRunning ? "SuccessTextBrush" : "TextSecondaryBrush"];
                CurrentAccountStatusText.Text = wechatRunning ? "待识别资料" : "微信未运行";
                return;
            }

            CurrentAccountNameText.Text = "等待识别当前登录微信";
            ApplyCurrentAccountAvatar(null, "微");
            CurrentAccountIdText.Text = "登录电脑微信后将自动识别头像、昵称与微信号，也可点击右侧按钮立即识别";
            CurrentAccountBioText.Visibility = Visibility.Collapsed;
            CurrentAccountStatusDot.Fill = (Brush)Application.Current.Resources["TextMutedBrush"];
            CurrentAccountStatusBadge.Background = (Brush)Application.Current.Resources["AppInputBgBrush"];
            CurrentAccountStatusText.Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"];
            CurrentAccountStatusText.Text = wechatRunning ? "待识别" : "未运行";
        }

        private void UpdateKpiCards(bool? wechatRunningOverride = null, string? fallbackLocalWxidOverride = null)
        {
            var sync = AppServices.WeChat4Sync;
            bool wechatRunning = wechatRunningOverride ?? WeChat4Paths.FindWeixinProcesses().Count > 0;
            var currentAcc = ResolveDisplayedCurrentAccount();
            string accountLabel = currentAcc is not null
                ? $"{currentAcc.DisplayName} ({currentAcc.DisplayAccountId})"
                : !string.IsNullOrWhiteSpace(fallbackLocalWxidOverride)
                    ? fallbackLocalWxidOverride
                    : sync.ActiveAccount?.Wxid
                        ?? WeChat4Paths.ListAccountDirs(AppServices.Settings.Current.WeChatSyncAccountDir).FirstOrDefault()?.Wxid
                        ?? string.Empty;

            if (wechatRunning)
            {
                KpiWeChatText.Text = "微信已运行";
                KpiWeChatSubText.Text = !string.IsNullOrWhiteSpace(accountLabel)
                    ? $"当前账号：{accountLabel}"
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

        private async void DetectAccountButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            _isBusy = true;
            DetectAccountButton.IsEnabled = false;
            try
            {
                ShowBanner("正在识别电脑当前登录的微信账号与资料...", isError: false);
                AppServices.WeChat4Sync.StartAutoSyncIfConfigured();
                var (ok, msg, _) = await AppServices.WeChat4Sync.DetectAndSyncCurrentWeChatAccountAsync(forceReload: true);
                ShowBanner(ok ? $"✓ {msg}" : msg, isError: !ok);
                await RefreshStatusAsync();
            }
            catch (Exception ex)
            {
                ShowBanner($"识别当前微信失败：{ex.Message}", isError: true);
            }
            finally
            {
                _isBusy = false;
                DetectAccountButton.IsEnabled = true;
            }
        }

        private async void SyncNowButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            _isBusy = true;
            SyncNowButton.IsEnabled = false;
            try
            {
                ShowBanner("正在同步当前在线微信已导入的客户与群聊最新消息...", isError: false);
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

        private static void ShowBanner(string message, bool isError)
        {
            WorkbenchPage.Current?.ShowToast(message, isError);
        }
    }
}
