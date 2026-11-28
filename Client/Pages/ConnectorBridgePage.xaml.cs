using System.Collections.ObjectModel;
using Client.Services;
using Client.Services.WeChat4Db;
using Client.Services.WeComDb;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Client.Pages
{
    public sealed partial class ConnectorBridgePage : Page
    {
        private readonly ObservableCollection<string> _trafficLogs = [];
        private List<WeComAccountSnapshot> _wecomAccounts = [];
        private bool _isInitializingUi;

        public ConnectorBridgePage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Required;
            TrafficLogListView.ItemsSource = _trafficLogs;
            Loaded += ConnectorBridgePage_Loaded;
            Unloaded += ConnectorBridgePage_Unloaded;
        }

        private async void ConnectorBridgePage_Loaded(object sender, RoutedEventArgs e)
        {
            AppServices.LocalBridge.StateChanged -= OnBridgeStateChanged;
            AppServices.LocalBridge.TrafficLogged -= OnTrafficLogged;
            AppServices.WeComSync.AccountsUpdated -= OnWeComUpdated;

            AppServices.LocalBridge.StateChanged += OnBridgeStateChanged;
            AppServices.LocalBridge.TrafficLogged += OnTrafficLogged;
            AppServices.WeComSync.AccountsUpdated += OnWeComUpdated;

            LoadExistingTrafficLogs();
            RefreshUiState();

            if (AppServices.WeComSync.LoadedAccounts.Count == 0)
            {
                await AppServices.WeComSync.EnsureAllAccountsReadyAsync(forceRefresh: false);
                RefreshUiState();
            }
        }

        private void ConnectorBridgePage_Unloaded(object sender, RoutedEventArgs e)
        {
            AppServices.LocalBridge.StateChanged -= OnBridgeStateChanged;
            AppServices.LocalBridge.TrafficLogged -= OnTrafficLogged;
            AppServices.WeComSync.AccountsUpdated -= OnWeComUpdated;
        }

        private void OnBridgeStateChanged() => DispatcherQueue.TryEnqueue(RefreshUiState);
        private void OnWeComUpdated() => DispatcherQueue.TryEnqueue(RefreshUiState);

        private void OnTrafficLogged(LocalConnectorBridgeService.BridgeTrafficEntry entry)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_trafficLogs.Count == 1 && _trafficLogs[0].StartsWith("暂无接口请求", StringComparison.Ordinal))
                {
                    _trafficLogs.Clear();
                }
                _trafficLogs.Insert(0, entry.FormattedLine);
                while (_trafficLogs.Count > 120) _trafficLogs.RemoveAt(_trafficLogs.Count - 1);
            });
        }

        private void LoadExistingTrafficLogs()
        {
            _trafficLogs.Clear();
            foreach (var item in AppServices.LocalBridge.GetRecentTraffic())
            {
                _trafficLogs.Add(item.FormattedLine);
            }
            if (_trafficLogs.Count == 0)
            {
                _trafficLogs.Add("暂无接口请求记录。点击上方任意自检按钮或由甲方连接器调用 8989/8990 后，此处将实时显示流量日志。");
            }
        }

        private void RefreshUiState()
        {
            _isInitializingUi = true;
            try
            {
                var cfg = AppServices.Settings.Current;
                var bridge = AppServices.LocalBridge;

                BridgeMasterToggle.IsOn = cfg.LocalBridgeEnabled;
                BridgeStatusDot.Fill = (Brush)Application.Current.Resources[bridge.IsRunning ? "SuccessTextBrush" : "TextMutedBrush"];
                BridgeBadgeText.Text = bridge.IsRunning ? "双端口运行中" : "已停止";

                // Personal WeChat 8990
                WxEndpointText.Text = $"http://127.0.0.1:{cfg.WeChatBridgePort}/api";
                WxStatusPillText.Text = bridge.IsWeChatListening ? "监听中" : "未启动";
                WxStatusPill.Background = (Brush)Application.Current.Resources[bridge.IsWeChatListening ? "SuccessBgBrush" : "AppInputBgBrush"];
                WxStatusPillText.Foreground = (Brush)Application.Current.Resources[bridge.IsWeChatListening ? "SuccessTextBrush" : "TextSecondaryBrush"];

                var wxProcs = WeChat4Paths.FindWeixinProcesses();
                var wxReader = AppServices.WeChat4Sync.ActiveReader;
                string wxId = AppServices.WeChat4Sync.ActiveOwnerWxid;
                string wxNick = wxReader?.SelfContact?.NickName ?? string.Empty;
                WxAccountInfoText.Text = wxProcs.Count > 0
                    ? $"Weixin.exe PID={wxProcs[0].Pid} · 账号：{(string.IsNullOrWhiteSpace(wxNick) ? (string.IsNullOrWhiteSpace(wxId) ? "待首次查询自动加载" : wxId) : $"{wxNick} ({wxId})")}"
                    : "电脑个人微信未运行（启动登录后自动接入）";
                WxCountsText.Text = wxReader is not null
                    ? $"已解析好友 {wxReader.Friends.Count} 位 · 群聊 {wxReader.Groups.Count} 个 · 推送 {cfg.WeChatPushUrl}"
                    : $"推送目标：{cfg.WeChatPushUrl}";

                // Enterprise WeCom 8989
                WcEndpointText.Text = $"http://127.0.0.1:{cfg.WeComBridgePort}/api";
                WcStatusPillText.Text = bridge.IsWeComListening ? "监听中" : "未启动";
                WcStatusPill.Background = (Brush)Application.Current.Resources[bridge.IsWeComListening ? "SuccessBgBrush" : "AppInputBgBrush"];
                WcStatusPillText.Foreground = (Brush)Application.Current.Resources[bridge.IsWeComListening ? "SuccessTextBrush" : "TextSecondaryBrush"];

                int wcPid = WeComPaths.GetPrimaryWXWorkPid();
                var wcReader = AppServices.WeComSync.ActiveReader;
                WcAccountInfoText.Text = wcReader is not null
                    ? $"WXWork PID={(wcPid > 0 ? wcPid.ToString() : "离线缓存")} · 当前企业：{wcReader.SelfCorpName} ({wcReader.SelfUser?.DisplayName ?? wcReader.AccountId})"
                    : (wcPid > 0 ? $"WXWork PID={wcPid} · 点击刷新加载企业数据" : "企业微信未运行");
                WcCountsText.Text = wcReader is not null
                    ? $"当前企业联系人 {wcReader.Users.Count} 位 · 群聊 {wcReader.Groups.Count} 个 · 推送 {cfg.WeComPushUrl}"
                    : $"推送目标：{cfg.WeComPushUrl}";

                // Populate WeCom corp switcher
                _wecomAccounts = AppServices.WeComSync.LoadedAccounts.ToList();
                WeComAccountCombo.Items.Clear();
                int selectedIndex = -1;
                for (int i = 0; i < _wecomAccounts.Count; i++)
                {
                    var a = _wecomAccounts[i];
                    WeComAccountCombo.Items.Add($"{a.CorpName} · {a.SelfUser.DisplayName} ({a.AccountId})");
                    if (a.IsPrimary) selectedIndex = i;
                }
                if (selectedIndex >= 0) WeComAccountCombo.SelectedIndex = selectedIndex;
            }
            finally
            {
                _isInitializingUi = false;
            }
        }

        private void BridgeMasterToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializingUi) return;
            AppServices.LocalBridge.ConfigureEnabled(BridgeMasterToggle.IsOn);
            RefreshUiState();
        }

        private async void RefreshAllStatusButton_Click(object sender, RoutedEventArgs e)
        {
            WorkbenchPage.Current?.ShowToast("正在刷新个人微信与企业微信双端只读快照...");
            await Task.WhenAll(
                AppServices.WeComSync.EnsureAllAccountsReadyAsync(forceRefresh: true),
                AppServices.WeChat4Sync.EnsureSnapshotReadyAsync(forceReload: false));
            RefreshUiState();
            WorkbenchPage.Current?.ShowToast("✓ 双端适配状态已刷新");
        }

        private async void WeComAccountCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializingUi) return;
            int idx = WeComAccountCombo.SelectedIndex;
            if (idx < 0 || idx >= _wecomAccounts.Count) return;
            var target = _wecomAccounts[idx];
            if (target.IsPrimary) return;

            var (ok, msg) = await AppServices.WeComSync.SwitchPrimaryAccountAsync(target.AccountId);
            RefreshUiState();
            WorkbenchPage.Current?.ShowToast(ok ? $"✓ 8989 端口已切换至企业「{target.CorpName}」" : msg, isError: !ok);
        }

        private async Task ExecuteSelfTestAsync(bool isWeCom, string label, string path, string? jsonBody = null)
        {
            TestResultHeaderText.Text = $"正在请求 {label}...";
            var (code, body) = await AppServices.LocalBridge.RunLocalSelfTestAsync(isWeCom, path, jsonBody);
            TestResultHeaderText.Text = $"{label} -> HTTP {code} ({DateTime.Now:HH:mm:ss})";
            TestResponseJsonText.Text = body;
            RefreshUiState();
        }

        private async void TestWxHealth_Click(object sender, RoutedEventArgs e) =>
            await ExecuteSelfTestAsync(false, "个微:8990 GET /health", "/health");

        private async void TestWxProfile_Click(object sender, RoutedEventArgs e) =>
            await ExecuteSelfTestAsync(false, "个微:8990 POST /api type=3", "/api", "{\"type\":3}");

        private async void TestWxContacts_Click(object sender, RoutedEventArgs e) =>
            await ExecuteSelfTestAsync(false, "个微:8990 POST /api type=5", "/api", "{\"type\":5,\"pull_type\":1}");

        private async void TestWcHealth_Click(object sender, RoutedEventArgs e) =>
            await ExecuteSelfTestAsync(true, "企微:8989 GET /health", "/health");

        private async void TestWcProfile_Click(object sender, RoutedEventArgs e) =>
            await ExecuteSelfTestAsync(true, "企微:8989 POST /api type=1002", "/api", "{\"type\":1002}");

        private async void TestWcGroups_Click(object sender, RoutedEventArgs e) =>
            await ExecuteSelfTestAsync(true, "企微:8989 POST /api type=2002", "/api", "{\"type\":2002,\"page_num\":1,\"page_size\":20}");

        private void CopyWxEndpoint_Click(object sender, RoutedEventArgs e)
        {
            if (Win32WindowHelper.SetClipboardUnicodeText(WxEndpointText.Text))
                WorkbenchPage.Current?.ShowToast($"已复制：{WxEndpointText.Text}");
        }

        private void CopyWcEndpoint_Click(object sender, RoutedEventArgs e)
        {
            if (Win32WindowHelper.SetClipboardUnicodeText(WcEndpointText.Text))
                WorkbenchPage.Current?.ShowToast($"已复制：{WcEndpointText.Text}");
        }

        private void ClearTrafficLogsButton_Click(object sender, RoutedEventArgs e)
        {
            AppServices.LocalBridge.ClearTrafficLogs();
            LoadExistingTrafficLogs();
        }
    }
}
