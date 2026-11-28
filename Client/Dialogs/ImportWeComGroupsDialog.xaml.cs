using System.Collections.ObjectModel;
using Client.Models;
using Client.Services;
using Client.Services.WeComDb;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Client.Dialogs
{
    public sealed partial class ImportWeComGroupsDialog : ContentDialog
    {
        private readonly ObservableCollection<WeComCorpFilterOption> _corpOptions = [];
        private readonly ObservableCollection<WeComGroupPickItem> _visibleGroups = [];
        private readonly HashSet<string> _selectedKeys = new(StringComparer.OrdinalIgnoreCase);

        private List<WeComGroupPickItem> _allGroups = [];
        private WeComCorpFilterOption? _activeCorp;
        private bool _isBusy;

        public int ImportedAddedCount { get; private set; }
        public int ImportedSelectedTotal { get; private set; }

        public ImportWeComGroupsDialog()
        {
            InitializeComponent();
            RequestedTheme = AppServices.Settings.CurrentElementTheme;
            CorpsListView.ItemsSource = _corpOptions;
            GroupsListView.ItemsSource = _visibleGroups;
            Loaded += ImportWeComGroupsDialog_Loaded;
        }

        private async void ImportWeComGroupsDialog_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= ImportWeComGroupsDialog_Loaded;
            await LoadAccountsAsync(forceRefresh: false);
        }

        private async void ReloadWeComButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            await LoadAccountsAsync(forceRefresh: true);
        }

        private async Task LoadAccountsAsync(bool forceRefresh)
        {
            SetLoadingState(true, forceRefresh ? "正在重新扫描本机企业微信群聊..." : "正在读取本机企业微信群聊列表...");
            StatusBanner.Visibility = Visibility.Collapsed;
            try
            {
                var (_, msg) = await AppServices.WeComSync.EnsureAllAccountsReadyAsync(forceRefresh);
                var accounts = AppServices.WeComSync.LoadedAccounts.ToList();
                if (accounts.Count == 0)
                {
                    SetLoadingState(false);
                    ShowStatusBanner(string.IsNullOrWhiteSpace(msg) ? "未检测到已登录企业微信，请先登录电脑端企业微信后重试。" : msg, isError: true);
                    return;
                }

                var alreadyImported = AppServices.WeComSync.GetImportedGroupKeys();
                _allGroups = [];
                _corpOptions.Clear();

                int totalGroups = accounts.Sum(a => a.Reader.Groups.Count);
                _corpOptions.Add(new WeComCorpFilterOption
                {
                    AccountId = string.Empty,
                    CorpName = "全部企业",
                    IsAllBucket = true,
                    TotalCount = totalGroups
                });

                foreach (var acc in accounts)
                {
                    _corpOptions.Add(new WeComCorpFilterOption
                    {
                        AccountId = acc.AccountId,
                        CorpName = acc.CorpName,
                        IsAllBucket = false,
                        TotalCount = acc.Reader.Groups.Count
                    });

                    foreach (var g in acc.Reader.Groups)
                    {
                        string key = WeComSyncService.MakeGroupKey(acc.AccountId, g.ConversationId);
                        _allGroups.Add(new WeComGroupPickItem
                        {
                            Key = key,
                            Group = g,
                            IsAlreadyImported = alreadyImported.Contains(key),
                            IsSelected = _selectedKeys.Contains(key)
                        });
                    }
                }

                HeaderSubtitleText.Text = $"已识别 {accounts.Count} 家企业 · 共 {_allGroups.Count} 个群聊（已导入 {alreadyImported.Count} 个）";
                SetLoadingState(false);
                PickerBodyGrid.Visibility = Visibility.Visible;

                if (_corpOptions.Count > 0)
                {
                    CorpsListView.SelectedIndex = 0;
                }
                UpdateSelectionCounters();
            }
            catch (Exception ex)
            {
                SetLoadingState(false);
                ShowStatusBanner($"读取企业微信群聊失败：{ex.Message}", isError: true);
            }
        }

        private List<WeComGroupPickItem> GetItemsInCorp(WeComCorpFilterOption? corp)
        {
            if (corp is null || corp.IsAllBucket) return _allGroups;
            return _allGroups
                .Where(g => string.Equals(g.Group.OwnerAccountId, corp.AccountId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private void RefreshVisibleGroups()
        {
            string q = GroupSearchBox.Text?.Trim() ?? string.Empty;
            var source = GetItemsInCorp(_activeCorp);

            _visibleGroups.Clear();
            foreach (var item in source)
            {
                if (!string.IsNullOrEmpty(q))
                {
                    var g = item.Group;
                    if (!g.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                        !g.OwnerCorpName.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                        !g.ConversationId.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                        !g.Members.Any(m => m.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                }
                item.IsSelected = _selectedKeys.Contains(item.Key);
                _visibleGroups.Add(item);
            }
        }

        private void CorpsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _activeCorp = CorpsListView.SelectedItem as WeComCorpFilterOption;
            RefreshVisibleGroups();
        }

        private void CorpSelectAllCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not WeComCorpFilterOption corp) return;
            var members = GetItemsInCorp(corp);
            bool allSelected = members.Count > 0 && members.All(m => _selectedKeys.Contains(m.Key));
            bool target = !allSelected;
            foreach (var m in members)
            {
                if (target) _selectedKeys.Add(m.Key);
                else _selectedKeys.Remove(m.Key);
                m.IsSelected = target;
            }
            UpdateSelectionCounters();
        }

        private void GroupSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshVisibleGroups();

        private void GroupCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not WeComGroupPickItem item) return;
            if (item.IsSelected) _selectedKeys.Add(item.Key);
            else _selectedKeys.Remove(item.Key);
            UpdateSelectionCounters();
        }

        private void GroupsListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not WeComGroupPickItem item) return;
            item.IsSelected = !item.IsSelected;
            if (item.IsSelected) _selectedKeys.Add(item.Key);
            else _selectedKeys.Remove(item.Key);
            UpdateSelectionCounters();
        }

        private void SelectWithMsgs_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _visibleGroups)
            {
                if (item.HasMessages)
                {
                    item.IsSelected = true;
                    _selectedKeys.Add(item.Key);
                }
            }
            UpdateSelectionCounters();
        }

        private void SelectAllVisible_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _visibleGroups)
            {
                item.IsSelected = true;
                _selectedKeys.Add(item.Key);
            }
            UpdateSelectionCounters();
        }

        private void ClearVisible_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _visibleGroups)
            {
                item.IsSelected = false;
                _selectedKeys.Remove(item.Key);
            }
            UpdateSelectionCounters();
        }

        private void UpdateSelectionCounters()
        {
            foreach (var corp in _corpOptions)
            {
                var members = GetItemsInCorp(corp);
                int sel = members.Count(m => _selectedKeys.Contains(m.Key));
                corp.SelectedCount = sel;
                corp.IsSelected = members.Count > 0 && sel == members.Count;
            }

            int total = _selectedKeys.Count;
            ConfirmImportButton.IsEnabled = !_isBusy && total > 0;
            ConfirmImportButton.Content = total > 0 ? $"导入所选群聊 ({total})" : "导入所选群聊";
            SelectionSummaryText.Text = total > 0
                ? $"已选 {total} 个群聊 · 导入后将按企业分组显示并自动同步群消息"
                : "请勾选左侧企业空间或右侧群聊进行导入";
        }

        private void ConfirmImportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy || _selectedKeys.Count == 0) return;
            ImportedSelectedTotal = _selectedKeys.Count;
            ImportedAddedCount = AppServices.WeComSync.AddImportedGroupKeys(_selectedKeys);
            Hide();
        }

        private void SetLoadingState(bool loading, string? message = null)
        {
            _isBusy = loading;
            LoadingPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            LoadingRing.IsActive = loading;
            if (message is not null) LoadingText.Text = message;
            if (loading) PickerBodyGrid.Visibility = Visibility.Collapsed;
            ReloadWeComButton.IsEnabled = !loading;
            ConfirmImportButton.IsEnabled = !loading && _selectedKeys.Count > 0;
        }

        private void ShowStatusBanner(string msg, bool isError)
        {
            StatusBannerText.Text = msg;
            StatusBanner.Background = (Brush)Application.Current.Resources[isError ? "DangerBgBrush" : "BrandSoftBgBrush"];
            StatusBanner.BorderBrush = (Brush)Application.Current.Resources[isError ? "DangerBorderBrush" : "BrandSoftBorderBrush"];
            StatusBanner.Visibility = Visibility.Visible;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

        private void CloseAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            if (!_isBusy) Hide();
        }
    }
}
