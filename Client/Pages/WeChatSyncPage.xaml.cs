using System.Collections.ObjectModel;
using System.Diagnostics;
using Client.Models;
using Client.Services;
using Client.Services.WeChat4Db;
using Client.Services.WeComDb;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Client.Pages
{
    public sealed partial class WeChatSyncPage : Page
    {
        private readonly ObservableCollection<string> _activityLogs = [];
        private readonly ObservableCollection<WeChatAccountDto> _linkedAccounts = [];
        private readonly ObservableCollection<string> _wecomCorpLines = [];
        private CancellationTokenSource? _accountAvatarCts;
        private bool _isInitializingUi = true;
        private bool _isBusy;

        public WeChatSyncPage()
        {
            InitializeComponent();
            NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
            ActivityLogListView.ItemsSource = _activityLogs;
            LinkedAccountsListView.ItemsSource = _linkedAccounts;
            WeComCorpsListView.ItemsSource = _wecomCorpLines;
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
            AppServices.WeComSync.StatusChanged -= OnWeComUpdated;
            AppServices.WeComSync.AccountsUpdated -= OnWeComAccountsUpdated;

            AppServices.Settings.ThemeChanged += OnThemeChanged;
            AppServices.WeChat4Sync.AutoSyncStatusChanged += OnAutoSyncStatusChanged;
            AppServices.WeChat4Sync.ActivityLogged += OnActivityLogged;
            AppServices.WeChat4Sync.SyncCompleted += OnSyncCompleted;
            AppServices.WeChat4Sync.AccountsUpdated += OnAccountsUpdated;
            AppServices.WeComSync.StatusChanged += OnWeComUpdated;
            AppServices.WeComSync.AccountsUpdated += OnWeComAccountsUpdated;

            _isInitializingUi = true;
            var cfg = AppServices.Settings.Current;
            AutoWatchToggle.IsOn = cfg.WeChatAutoSyncEnabled;
            WeComWatchToggle.IsOn = AppServices.WeComSync.IsWatcherRunning;
            OptImagesCheck.IsChecked = cfg.WeChatSyncIncludeImages;
            OptAvatarsCheck.IsChecked = cfg.WeChatSyncIncludeAvatars;
            _isInitializingUi = false;

            LoadExistingLogs();
            SyncLinkedAccountsCollection();
            await RefreshAllUiAsync();

            if (AppServices.WeComSync.LoadedAccounts.Count == 0)
            {
                await AppServices.WeComSync.EnsureAllAccountsReadyAsync(forceRefresh: false);
                RefreshWeComCard();
                UpdateOverallHeader();
            }
        }

        private void WeChatSyncPage_Unloaded(object sender, RoutedEventArgs e)
        {
            AppServices.Settings.ThemeChanged -= OnThemeChanged;
            AppServices.WeChat4Sync.AutoSyncStatusChanged -= OnAutoSyncStatusChanged;
            AppServices.WeChat4Sync.ActivityLogged -= OnActivityLogged;
            AppServices.WeChat4Sync.SyncCompleted -= OnSyncCompleted;
            AppServices.WeChat4Sync.AccountsUpdated -= OnAccountsUpdated;
            AppServices.WeComSync.StatusChanged -= OnWeComUpdated;
            AppServices.WeComSync.AccountsUpdated -= OnWeComAccountsUpdated;
            _accountAvatarCts?.Cancel();
            _accountAvatarCts?.Dispose();
            _accountAvatarCts = null;
        }

        private void OnThemeChanged(bool _)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var acc in _linkedAccounts) acc.NotifyVisualsChanged();
                RefreshWeChatCard();
                RefreshWeComCard();
                UpdateOverallHeader();
            });
        }

        private void OnAccountsUpdated()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                SyncLinkedAccountsCollection();
                RefreshWeChatCard();
                UpdateOverallHeader();
            });
        }

        private void OnWeComUpdated(string _) => DispatcherQueue.TryEnqueue(() =>
        {
            RefreshWeComCard();
            UpdateOverallHeader();
        });

        private void OnWeComAccountsUpdated() => DispatcherQueue.TryEnqueue(() =>
        {
            RefreshWeComCard();
            UpdateOverallHeader();
        });

        private void LoadExistingLogs()
        {
            _activityLogs.Clear();
            foreach (var line in AppServices.WeChat4Sync.GetRecentActivityLogs())
            {
                _activityLogs.Add(line);
            }
            if (_activityLogs.Count == 0)
            {
                _activityLogs.Add("等待个人微信与企业微信消息变化中...");
            }
        }

        private void OnAutoSyncStatusChanged(string status)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                WatchStatusSummaryText.Text = status;
                RefreshWeChatCard();
                UpdateOverallHeader();
            });
        }

        private void OnActivityLogged(string stampedLine)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_activityLogs.Count == 1 && _activityLogs[0].StartsWith("等待个人微信", StringComparison.Ordinal))
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
                RefreshWeChatCard();
                UpdateOverallHeader();
            });
        }

        private async Task RefreshAllUiAsync()
        {
            await AppServices.WeChat4Sync.RefreshMonitoredCountsAsync();
            SyncLinkedAccountsCollection();
            RefreshWeChatCard();
            RefreshWeComCard();
            UpdateOverallHeader();
        }

        private void UpdateOverallHeader()
        {
            bool wxRunning = WeChat4Paths.FindWeixinProcesses().Count > 0;
            bool wcRunning = WeComPaths.FindWXWorkProcesses().Count > 0 || AppServices.WeComSync.LoadedAccounts.Count > 0;

            if (wxRunning && wcRunning)
            {
                StatusDot.Fill = (Brush)Application.Current.Resources["SuccessTextBrush"];
                StatusBadgeText.Text = "双端在线监听中";
            }
            else if (wxRunning || wcRunning)
            {
                StatusDot.Fill = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
                StatusBadgeText.Text = wxRunning ? "个微在线 · 企微待机" : "企微在线 · 个微待机";
            }
            else
            {
                StatusDot.Fill = (Brush)Application.Current.Resources["TextMutedBrush"];
                StatusBadgeText.Text = "等待微信 / 企微登录";
            }

            RelaunchAdminButton.Visibility = AppServices.WeChat4Sync.LastSyncNeedsAdmin ? Visibility.Visible : Visibility.Collapsed;
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

            LinkedAccountsCountText.Text = _linkedAccounts.Count.ToString();
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

        private void RefreshWeChatCard()
        {
            var sync = AppServices.WeChat4Sync;
            bool wechatRunning = WeChat4Paths.FindWeixinProcesses().Count > 0;
            var current = ResolveDisplayedCurrentAccount();
            var selfLocal = sync.ActiveReader?.SelfContact;

            KpiMonitoredText.Text = $"{sync.MonitoredCustomersCount} 客户 · {sync.MonitoredGroupsCount} 群";
            WatchStatusSummaryText.Text = sync.WatchStatusText;
            KpiLastSyncText.Text = sync.LastSyncAt.HasValue ? sync.LastSyncAt.Value.ToString("HH:mm:ss") : "等待新消息";

            if (current is not null)
            {
                CurrentAccountNameText.Text = current.DisplayName;
                ApplyCurrentAccountAvatar(current.AvatarImage, current.AvatarInitial);

                var idParts = new List<string>(2);
                if (!string.IsNullOrWhiteSpace(current.Alias)) idParts.Add($"微信号：{current.Alias}");
                if (!string.IsNullOrWhiteSpace(current.WechatId) && !current.WechatId.Equals(current.Alias, StringComparison.OrdinalIgnoreCase))
                {
                    idParts.Add($"ID：{current.WechatId}");
                }
                CurrentAccountIdText.Text = idParts.Count > 0 ? string.Join(" · ", idParts) : current.CountsSummary;
                CurrentAccountBioText.Text = !string.IsNullOrWhiteSpace(current.Signature) ? current.Signature : string.Empty;
                CurrentAccountBioText.Visibility = string.IsNullOrWhiteSpace(current.Signature) ? Visibility.Collapsed : Visibility.Visible;

                bool showOnline = wechatRunning && current.IsOnline;
                CurrentAccountStatusDot.Fill = (Brush)Application.Current.Resources[showOnline ? "SuccessTextBrush" : "TextMutedBrush"];
                CurrentAccountStatusBadge.Background = (Brush)Application.Current.Resources[showOnline ? "SuccessBgBrush" : "AppInputBgBrush"];
                CurrentAccountStatusText.Foreground = (Brush)Application.Current.Resources[showOnline ? "SuccessTextBrush" : "TextSecondaryBrush"];
                CurrentAccountStatusText.Text = showOnline ? "在线" : (wechatRunning ? "运行中" : "未运行");
                return;
            }

            if (selfLocal is not null)
            {
                CurrentAccountNameText.Text = selfLocal.DisplayName;
                ApplyCurrentAccountAvatar(null, selfLocal.DisplayName.Length > 0 ? selfLocal.DisplayName[..1].ToUpperInvariant() : "微");
                CurrentAccountIdText.Text = !string.IsNullOrWhiteSpace(selfLocal.Alias) ? $"微信号：{selfLocal.Alias}" : selfLocal.Username;
                CurrentAccountBioText.Visibility = Visibility.Collapsed;
            }
            else
            {
                string localWxid = sync.ActiveAccount?.Wxid ?? WeChat4Paths.ListAccountDirs(AppServices.Settings.Current.WeChatSyncAccountDir).FirstOrDefault()?.Wxid ?? string.Empty;
                CurrentAccountNameText.Text = string.IsNullOrWhiteSpace(localWxid) ? "等待识别个人微信" : localWxid;
                ApplyCurrentAccountAvatar(null, "微");
                CurrentAccountIdText.Text = wechatRunning ? "已检测到电脑微信，点击右上角「刷新同步双端」识别资料" : "请先在电脑上登录个人微信";
                CurrentAccountBioText.Visibility = Visibility.Collapsed;
            }

            CurrentAccountStatusDot.Fill = (Brush)Application.Current.Resources[wechatRunning ? "SuccessTextBrush" : "TextMutedBrush"];
            CurrentAccountStatusBadge.Background = (Brush)Application.Current.Resources[wechatRunning ? "SuccessBgBrush" : "AppInputBgBrush"];
            CurrentAccountStatusText.Foreground = (Brush)Application.Current.Resources[wechatRunning ? "SuccessTextBrush" : "TextSecondaryBrush"];
            CurrentAccountStatusText.Text = wechatRunning ? "运行中" : "未运行";
        }

        private void RefreshWeComCard()
        {
            var wecom = AppServices.WeComSync;
            bool running = WeComPaths.FindWXWorkProcesses().Count > 0;
            var accounts = wecom.LoadedAccounts;

            _isInitializingUi = true;
            WeComWatchToggle.IsOn = wecom.IsWatcherRunning;
            _isInitializingUi = false;

            WeComStatusDot.Fill = (Brush)Application.Current.Resources[running ? "SuccessTextBrush" : (accounts.Count > 0 ? "BrandPrimaryBrush" : "TextMutedBrush")];
            WeComStatusBadge.Background = (Brush)Application.Current.Resources[running ? "SuccessBgBrush" : (accounts.Count > 0 ? "BrandSoftBgBrush" : "AppInputBgBrush")];
            WeComStatusBadgeText.Foreground = (Brush)Application.Current.Resources[running ? "SuccessTextBrush" : (accounts.Count > 0 ? "BrandPrimaryBrush" : "TextSecondaryBrush")];
            WeComStatusBadgeText.Text = running ? "在线" : (accounts.Count > 0 ? "离线缓存" : "未运行");

            int totalUsers = accounts.Sum(a => a.Reader.Users.Count);
            int totalGroups = accounts.Sum(a => a.Reader.Groups.Count);
            WeComKpiCorpsText.Text = $"{accounts.Count} 家企业";
            WeComKpiTotalsText.Text = $"{totalUsers} 联系人 · {totalGroups} 群";
            WeComWatchSummaryText.Text = wecom.StatusText;

            if (accounts.Count > 0)
            {
                var primary = accounts[0];
                WeComPrimaryNameText.Text = primary.CorpName;
                WeComAvatarInitialText.Text = primary.CorpName.Length > 0 ? primary.CorpName[..1].ToUpperInvariant() : "企";
                WeComPrimarySubText.Text = $"{primary.SelfUser.DisplayName} · 账号 {primary.AccountId}";
                WeComCorpsSummaryText.Text = accounts.Count > 1
                    ? $"全部企业：{string.Join(" / ", accounts.Select(a => a.CorpName))}"
                    : "已加载当前企业全部联系人与群聊";
            }
            else
            {
                WeComPrimaryNameText.Text = "等待识别企业微信";
                WeComAvatarInitialText.Text = "企";
                WeComPrimarySubText.Text = running ? "已检测到企业微信，点击右上角「刷新同步双端」加载" : "请先在电脑上登录企业微信";
                WeComCorpsSummaryText.Text = string.Empty;
            }

            _wecomCorpLines.Clear();
            foreach (var a in accounts)
            {
                string tag = a.IsPrimary ? "[主活跃] " : "";
                _wecomCorpLines.Add($"{tag}{a.CorpName}  ·  {a.SelfUser.DisplayName}（{a.Reader.Users.Count} 联系人 / {a.Reader.Groups.Count} 群）");
            }
            WeComCorpsCountText.Text = _wecomCorpLines.Count.ToString();
            WeComCorpsListView.Visibility = _wecomCorpLines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyWeComCorpsHint.Visibility = _wecomCorpLines.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void AutoWatchToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializingUi) return;
            AppServices.WeChat4Sync.ConfigureAutoWatch(AutoWatchToggle.IsOn);
            RefreshWeChatCard();
            UpdateOverallHeader();
        }

        private void WeComWatchToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializingUi) return;
            if (WeComWatchToggle.IsOn) AppServices.WeComSync.StartWatching();
            else AppServices.WeComSync.StopWatching();
            RefreshWeComCard();
            UpdateOverallHeader();
        }

        private void OptCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializingUi) return;
            AppServices.Settings.Current.WeChatSyncIncludeImages = OptImagesCheck.IsChecked == true;
            AppServices.Settings.Current.WeChatSyncIncludeAvatars = OptAvatarsCheck.IsChecked == true;
            AppServices.Settings.Save();
        }

        private async void SyncBothButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            _isBusy = true;
            SyncBothButton.IsEnabled = false;
            try
            {
                ShowBanner("正在同时刷新个人微信与企业微信双端只读数据...", isError: false);

                var wecomTask = AppServices.WeComSync.EnsureAllAccountsReadyAsync(forceRefresh: true);
                var wxDetectTask = AppServices.WeChat4Sync.DetectAndSyncCurrentWeChatAccountAsync(forceReload: true);
                await Task.WhenAll(wecomTask, wxDetectTask);

                var (wxSyncOk, wxSyncMsg, _) = await AppServices.WeChat4Sync.SyncImportedTargetsAsync(
                    syncCustomers: true, syncGroups: true, isBackgroundAuto: false);

                await RefreshAllUiAsync();
                int corpCnt = AppServices.WeComSync.LoadedAccounts.Count;
                string summary = $"双端刷新完成：企微已接入 {corpCnt} 家企业；个微{(wxSyncOk ? "同步完成" : wxSyncMsg)}";
                ShowBanner($"✓ {summary}", isError: false);
            }
            catch (Exception ex)
            {
                ShowBanner($"双端刷新异常：{ex.Message}", isError: true);
            }
            finally
            {
                _isBusy = false;
                SyncBothButton.IsEnabled = true;
            }
        }

        private void OpenWeChatPersonal_Click(object sender, RoutedEventArgs e) => WorkbenchPage.Current?.SwitchSection("customers");
        private void OpenWeChatGroups_Click(object sender, RoutedEventArgs e) => WorkbenchPage.Current?.SwitchSection("groups");
        private void OpenWeComPersonal_Click(object sender, RoutedEventArgs e) => WorkbenchPage.Current?.SwitchSection("wecom_contacts");
        private void OpenWeComGroups_Click(object sender, RoutedEventArgs e) => WorkbenchPage.Current?.SwitchSection("wecom_groups");

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

        private static void ShowBanner(string message, bool isError) =>
            WorkbenchPage.Current?.ShowToast(message, isError);
    }
}
