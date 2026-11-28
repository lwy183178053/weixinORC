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
        private readonly ObservableCollection<string> _autoAcceptLogs = [];
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
            AutoAcceptLogListView.ItemsSource = _autoAcceptLogs;
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
            AppServices.WeComSync.ImportedSelectionChanged -= OnWeComAccountsUpdated;
            AppServices.FriendAutoAccept.StateChanged -= OnFriendAutoAcceptStateChanged;
            AppServices.FriendAutoAccept.RecordAdded -= OnFriendAutoAcceptRecordAdded;

            AppServices.Settings.ThemeChanged += OnThemeChanged;
            AppServices.WeChat4Sync.AutoSyncStatusChanged += OnAutoSyncStatusChanged;
            AppServices.WeChat4Sync.ActivityLogged += OnActivityLogged;
            AppServices.WeChat4Sync.SyncCompleted += OnSyncCompleted;
            AppServices.WeChat4Sync.AccountsUpdated += OnAccountsUpdated;
            AppServices.WeComSync.StatusChanged += OnWeComUpdated;
            AppServices.WeComSync.AccountsUpdated += OnWeComAccountsUpdated;
            AppServices.WeComSync.ImportedSelectionChanged += OnWeComAccountsUpdated;
            AppServices.FriendAutoAccept.StateChanged += OnFriendAutoAcceptStateChanged;
            AppServices.FriendAutoAccept.RecordAdded += OnFriendAutoAcceptRecordAdded;

            _isInitializingUi = true;
            var cfg = AppServices.Settings.Current;
            AutoWatchToggle.IsOn = cfg.WeChatAutoSyncEnabled;
            WeComWatchToggle.IsOn = AppServices.WeComSync.IsWatcherRunning;
            OptImagesCheck.IsChecked = cfg.WeChatSyncIncludeImages;
            OptAvatarsCheck.IsChecked = cfg.WeChatSyncIncludeAvatars;

            AutoAcceptFriendToggle.IsOn = cfg.AutoAcceptFriendEnabled;
            AutoAcceptModeCombo.SelectedIndex = cfg.AutoAcceptTriggerMode switch
            {
                "interval_30" => 1,
                "interval_60" => 2,
                "interval_120" => 3,
                _ => 0
            };
            AutoAcceptPermissionCombo.SelectedIndex = cfg.AutoAcceptChatOnlyPermission ? 1 : 0;
            AutoAcceptRemarkBox.Text = cfg.AutoAcceptRemarkPrefix;
            AutoAcceptTagBox.Text = cfg.AutoAcceptAutoTag;
            AutoAcceptKeywordBox.Text = cfg.AutoAcceptKeywordFilter;
            AutoAcceptIdleCheck.IsChecked = cfg.AutoAcceptOnlyWhenIdle;
            AutoAcceptReturnChatCheck.IsChecked = cfg.AutoAcceptReturnToChatTab;
            AutoAcceptOverlayCheck.IsChecked = cfg.AutoAcceptShowClickOverlay;
            AutoAcceptIncludeExpiredCheck.IsChecked = cfg.AutoAcceptIncludeExpired;
            _isInitializingUi = false;

            LoadExistingLogs();
            LoadAutoAcceptLogs();
            RefreshAutoAcceptCard();
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
            AppServices.WeComSync.ImportedSelectionChanged -= OnWeComAccountsUpdated;
            AppServices.FriendAutoAccept.StateChanged -= OnFriendAutoAcceptStateChanged;
            AppServices.FriendAutoAccept.RecordAdded -= OnFriendAutoAcceptRecordAdded;
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
                RefreshAutoAcceptCard();
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

            int importedContacts = wecom.GetImportedContactKeys().Count;
            int importedGroups = wecom.GetImportedGroupKeys().Count;
            WeComKpiCorpsText.Text = $"{accounts.Count} 家企业";
            WeComKpiTotalsText.Text = $"{importedContacts} 联系人 · {importedGroups} 群";
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

        // ---- Auto-Accept WeChat Friend Requests Handlers ----

        private void OnFriendAutoAcceptStateChanged() => DispatcherQueue.TryEnqueue(RefreshAutoAcceptCard);

        private void OnFriendAutoAcceptRecordAdded(FriendAutoAcceptRecord rec)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_autoAcceptLogs.Count == 1 && _autoAcceptLogs[0].StartsWith("暂无自动同意记录", StringComparison.Ordinal))
                {
                    _autoAcceptLogs.Clear();
                }
                _autoAcceptLogs.Insert(0, rec.FormattedLine);
                while (_autoAcceptLogs.Count > 60) _autoAcceptLogs.RemoveAt(_autoAcceptLogs.Count - 1);
                RefreshAutoAcceptCard();
            });
        }

        private void LoadAutoAcceptLogs()
        {
            _autoAcceptLogs.Clear();
            foreach (var r in AppServices.FriendAutoAccept.GetRecentRecords())
            {
                _autoAcceptLogs.Add(r.FormattedLine);
            }
            if (_autoAcceptLogs.Count == 0)
            {
                _autoAcceptLogs.Add("暂无自动同意记录。开启上方开关或点击「立即检测并同意」后将显示每次 OCR 识别与同意结果。");
            }
        }

        private void RefreshAutoAcceptCard()
        {
            var svc = AppServices.FriendAutoAccept;
            var cfg = AppServices.Settings.Current;
            bool enabled = cfg.AutoAcceptFriendEnabled;
            bool runningNow = svc.IsExecuting;

            AutoAcceptStatusDot.Fill = (Brush)Application.Current.Resources[
                runningNow ? "BrandPrimaryBrush" : (enabled ? "SuccessTextBrush" : "TextMutedBrush")];
            AutoAcceptStatusBadge.Background = (Brush)Application.Current.Resources[
                runningNow ? "BrandSoftBgBrush" : (enabled ? "SuccessBgBrush" : "AppInputBgBrush")];
            AutoAcceptStatusText.Foreground = (Brush)Application.Current.Resources[
                runningNow ? "BrandPrimaryBrush" : (enabled ? "SuccessTextBrush" : "TextSecondaryBrush")];
            AutoAcceptStatusText.Text = runningNow ? "OCR 执行中..." : (enabled ? "自动监听中" : "未开启");

            string lastCheck = svc.LastCheckedAt.HasValue ? svc.LastCheckedAt.Value.ToString("HH:mm:ss") : "尚未执行";
            AutoAcceptStatsText.Text = $"累计已自动同意 {cfg.AutoAcceptTotalAcceptedCount} 位好友 · 上次检测：{lastCheck} · {svc.StatusText}";
            RunAutoAcceptNowButton.IsEnabled = !runningNow;
        }

        private void AutoAcceptFriendToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializingUi) return;
            AppServices.FriendAutoAccept.ConfigureEnabled(AutoAcceptFriendToggle.IsOn);
            RefreshAutoAcceptCard();
            ShowBanner(AutoAcceptFriendToggle.IsOn ? "✓ 已开启自动同意微信好友申请" : "已关闭自动同意微信好友申请", isError: false);
        }

        private void AutoAcceptConfig_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializingUi) return;
            SaveAutoAcceptSettingsFromUi();
        }

        private void AutoAcceptText_Changed(object sender, TextChangedEventArgs e)
        {
            if (_isInitializingUi) return;
            SaveAutoAcceptSettingsFromUi();
        }

        private void SaveAutoAcceptSettingsFromUi()
        {
            var cfg = AppServices.Settings.Current;
            if (AutoAcceptModeCombo.SelectedItem is ComboBoxItem modeItem && modeItem.Tag is string modeTag)
            {
                cfg.AutoAcceptTriggerMode = modeTag;
            }
            if (AutoAcceptPermissionCombo.SelectedItem is ComboBoxItem permItem && permItem.Tag is string permTag)
            {
                cfg.AutoAcceptChatOnlyPermission = string.Equals(permTag, "chat_only", StringComparison.OrdinalIgnoreCase);
            }
            cfg.AutoAcceptRemarkPrefix = AutoAcceptRemarkBox.Text?.Trim() ?? string.Empty;
            cfg.AutoAcceptAutoTag = AutoAcceptTagBox.Text?.Trim() ?? string.Empty;
            cfg.AutoAcceptKeywordFilter = AutoAcceptKeywordBox.Text?.Trim() ?? string.Empty;
            cfg.AutoAcceptOnlyWhenIdle = AutoAcceptIdleCheck.IsChecked == true;
            cfg.AutoAcceptReturnToChatTab = AutoAcceptReturnChatCheck.IsChecked == true;
            cfg.AutoAcceptShowClickOverlay = AutoAcceptOverlayCheck.IsChecked == true;
            cfg.AutoAcceptIncludeExpired = AutoAcceptIncludeExpiredCheck.IsChecked == true;
            AppServices.Settings.Save();
            AppServices.FriendAutoAccept.NotifyConfigUpdated();
            RefreshAutoAcceptCard();
        }

        private async void RunAutoAcceptNowButton_Click(object sender, RoutedEventArgs e)
        {
            RunAutoAcceptNowButton.IsEnabled = false;
            try
            {
                ShowBanner("正在唤起电脑微信并通过 OCR 检查「新的朋友」好友申请（按 ESC 可中止）...", isError: false);
                var result = await AppServices.FriendAutoAccept.RunOnceNowAsync();
                RefreshAutoAcceptCard();
                ShowBanner(result.Success ? $"✓ {result.Message}" : result.Message, isError: !result.Success && !result.CancelledByEsc);
            }
            catch (Exception ex)
            {
                ShowBanner($"检测好友申请异常：{ex.Message}", isError: true);
            }
            finally
            {
                RefreshAutoAcceptCard();
            }
        }

        private void ClearAutoAcceptLogs_Click(object sender, RoutedEventArgs e)
        {
            AppServices.FriendAutoAccept.ClearRecords();
            LoadAutoAcceptLogs();
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

        private static void ShowBanner(string message, bool isError) =>
            WorkbenchPage.Current?.ShowToast(message, isError);
    }
}
