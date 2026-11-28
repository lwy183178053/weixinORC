using System.Collections.ObjectModel;
using Client.Models;
using Client.Services;
using Client.Services.WeComDb;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Client.Dialogs
{
    public sealed partial class ImportWeComContactsDialog : ContentDialog
    {
        private readonly ObservableCollection<WeComCorpFilterOption> _corpOptions = [];
        private readonly ObservableCollection<WeComContactPickItem> _visibleContacts = [];
        private readonly HashSet<string> _selectedKeys = new(StringComparer.OrdinalIgnoreCase);

        private List<WeComContactPickItem> _allContacts = [];
        private WeComCorpFilterOption? _activeCorp;
        private CancellationTokenSource? _avatarCts;
        private bool _isBusy;

        public int ImportedAddedCount { get; private set; }
        public int ImportedSelectedTotal { get; private set; }

        public ImportWeComContactsDialog()
        {
            InitializeComponent();
            RequestedTheme = AppServices.Settings.CurrentElementTheme;
            CorpsListView.ItemsSource = _corpOptions;
            ContactsListView.ItemsSource = _visibleContacts;
            Loaded += ImportWeComContactsDialog_Loaded;
            Closed += ImportWeComContactsDialog_Closed;
        }

        private async void ImportWeComContactsDialog_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= ImportWeComContactsDialog_Loaded;
            await LoadAccountsAsync(forceRefresh: false);
        }

        private void ImportWeComContactsDialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
        {
            _avatarCts?.Cancel();
            _avatarCts?.Dispose();
            _avatarCts = null;
        }

        private async void ReloadWeComButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            await LoadAccountsAsync(forceRefresh: true);
        }

        private async Task LoadAccountsAsync(bool forceRefresh)
        {
            SetLoadingState(true, forceRefresh ? "正在重新扫描本机企业微信联系人..." : "正在读取本机企业微信企业与联系人...");
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

                var alreadyImported = AppServices.WeComSync.GetImportedContactKeys();
                _allContacts = [];
                _corpOptions.Clear();

                int totalUsers = accounts.Sum(a => a.Reader.Users.Count);
                _corpOptions.Add(new WeComCorpFilterOption
                {
                    AccountId = string.Empty,
                    CorpName = "全部企业",
                    IsAllBucket = true,
                    TotalCount = totalUsers
                });

                foreach (var acc in accounts)
                {
                    _corpOptions.Add(new WeComCorpFilterOption
                    {
                        AccountId = acc.AccountId,
                        CorpName = acc.CorpName,
                        IsAllBucket = false,
                        TotalCount = acc.Reader.Users.Count
                    });

                    foreach (var u in acc.Reader.Users)
                    {
                        string key = WeComSyncService.MakeContactKey(acc.AccountId, u.UserId);
                        _allContacts.Add(new WeComContactPickItem
                        {
                            Key = key,
                            User = u,
                            IsAlreadyImported = alreadyImported.Contains(key),
                            IsSelected = _selectedKeys.Contains(key)
                        });
                    }
                }

                HeaderSubtitleText.Text = $"已识别 {accounts.Count} 家企业 · 共 {_allContacts.Count} 位联系人（已导入 {alreadyImported.Count} 位）";
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
                ShowStatusBanner($"读取企业微信联系人失败：{ex.Message}", isError: true);
            }
        }

        private List<WeComContactPickItem> GetItemsInCorp(WeComCorpFilterOption? corp)
        {
            if (corp is null || corp.IsAllBucket) return _allContacts;
            return _allContacts
                .Where(c => string.Equals(c.User.OwnerAccountId, corp.AccountId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private void RefreshVisibleContacts()
        {
            string q = ContactSearchBox.Text?.Trim() ?? string.Empty;
            var source = GetItemsInCorp(_activeCorp);

            _visibleContacts.Clear();
            int cap = string.IsNullOrEmpty(q) ? 300 : 400;
            int added = 0;
            foreach (var item in source)
            {
                if (!string.IsNullOrEmpty(q))
                {
                    var u = item.User;
                    if (!u.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                        !u.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                        !u.Remark.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                        !u.ExternalCorpName.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                        !u.OwnerCorpName.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                        !u.UserId.Contains(q, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }
                item.IsSelected = _selectedKeys.Contains(item.Key);
                _visibleContacts.Add(item);
                added++;
                if (added >= cap) break;
            }

            StartAvatarHydration(_visibleContacts.Take(50).ToList());
        }

        private void StartAvatarHydration(List<WeComContactPickItem> batch)
        {
            _avatarCts?.Cancel();
            _avatarCts?.Dispose();
            var cts = new CancellationTokenSource();
            _avatarCts = cts;

            var targets = batch.Where(i => !string.IsNullOrWhiteSpace(i.AvatarUrl) && i.AvatarImage is null).ToList();
            if (targets.Count == 0) return;

            _ = Task.Run(async () =>
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                foreach (var item in targets)
                {
                    if (cts.IsCancellationRequested) return;
                    try
                    {
                        byte[] bytes = await http.GetByteArrayAsync(item.AvatarUrl, cts.Token);
                        if (bytes.Length == 0 || cts.IsCancellationRequested) continue;
                        DispatcherQueue.TryEnqueue(async () =>
                        {
                            if (cts.IsCancellationRequested) return;
                            try
                            {
                                var img = await AvatarImageService.BytesToBitmapImageAsync(bytes);
                                if (img is not null && !cts.IsCancellationRequested) item.AvatarImage = img;
                            }
                            catch { }
                        });
                    }
                    catch { }
                }
            }, cts.Token);
        }

        private void CorpsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _activeCorp = CorpsListView.SelectedItem as WeComCorpFilterOption;
            RefreshVisibleContacts();
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

        private void ContactSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshVisibleContacts();

        private void ContactCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not WeComContactPickItem item) return;
            if (item.IsSelected) _selectedKeys.Add(item.Key);
            else _selectedKeys.Remove(item.Key);
            UpdateSelectionCounters();
        }

        private void ContactsListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not WeComContactPickItem item) return;
            item.IsSelected = !item.IsSelected;
            if (item.IsSelected) _selectedKeys.Add(item.Key);
            else _selectedKeys.Remove(item.Key);
            UpdateSelectionCounters();
        }

        private void SelectWithMsgs_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _visibleContacts)
            {
                if (item.HasMessages)
                {
                    item.IsSelected = true;
                    _selectedKeys.Add(item.Key);
                }
            }
            UpdateSelectionCounters();
        }

        private void SelectExternal_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _visibleContacts)
            {
                if (item.IsExternal)
                {
                    item.IsSelected = true;
                    _selectedKeys.Add(item.Key);
                }
            }
            UpdateSelectionCounters();
        }

        private void SelectAllVisible_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _visibleContacts)
            {
                item.IsSelected = true;
                _selectedKeys.Add(item.Key);
            }
            UpdateSelectionCounters();
        }

        private void ClearVisible_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _visibleContacts)
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
            ConfirmImportButton.Content = total > 0 ? $"导入所选联系人 ({total})" : "导入所选联系人";
            SelectionSummaryText.Text = total > 0
                ? $"已选 {total} 位联系人 · 导入后将按所属企业分组显示并实时监听"
                : "请勾选左侧企业空间或右侧联系人进行导入";
        }

        private void ConfirmImportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy || _selectedKeys.Count == 0) return;
            ImportedSelectedTotal = _selectedKeys.Count;
            ImportedAddedCount = AppServices.WeComSync.AddImportedContactKeys(_selectedKeys);
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
