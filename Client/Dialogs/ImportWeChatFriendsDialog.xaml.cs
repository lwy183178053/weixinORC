using System.Collections.ObjectModel;
using System.Diagnostics;
using Client.Models;
using Client.Services;
using Client.Services.WeChat4Db;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Client.Dialogs
{
    public sealed partial class ImportWeChatFriendsDialog : ContentDialog
    {
        private readonly List<CustomerDto> _existingCustomers;
        private readonly ObservableCollection<WeChat4TagOption> _tagOptions = [];
        private readonly ObservableCollection<WeChat4Contact> _visibleFriends = [];
        private readonly HashSet<string> _selectedUsernames = new(StringComparer.OrdinalIgnoreCase);

        private List<WeChat4Contact> _allFriends = [];
        private WeChat4TagOption? _activeTag;
        private CancellationTokenSource? _workCts;
        private CancellationTokenSource? _avatarCts;
        private bool _isBusy;

        public WeChat4SyncReport? CompletedReport { get; private set; }

        public ImportWeChatFriendsDialog(IEnumerable<CustomerDto> existingCustomers)
        {
            InitializeComponent();
            RequestedTheme = AppServices.Settings.CurrentElementTheme;
            _existingCustomers = existingCustomers.ToList();

            TagsListView.ItemsSource = _tagOptions;
            FriendsListView.ItemsSource = _visibleFriends;

            Loaded += ImportWeChatFriendsDialog_Loaded;
            Closed += ImportWeChatFriendsDialog_Closed;
        }

        private async void ImportWeChatFriendsDialog_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= ImportWeChatFriendsDialog_Loaded;
            await LoadWeChatFriendsAsync(forceReload: false);
        }

        private void ImportWeChatFriendsDialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
        {
            _avatarCts?.Cancel();
            _avatarCts?.Dispose();
            _avatarCts = null;
            _workCts?.Cancel();
            _workCts?.Dispose();
            _workCts = null;
        }

        private async void ReloadWeChatButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            await LoadWeChatFriendsAsync(forceReload: true);
        }

        private async Task LoadWeChatFriendsAsync(bool forceReload)
        {
            SetLoadingState(true, forceReload ? "正在刷新微信最新好友与标签..." : "正在读取本机微信好友与标签...");
            StatusBanner.Visibility = Visibility.Collapsed;
            RelaunchAdminButton.Visibility = Visibility.Collapsed;

            _workCts?.Cancel();
            _workCts?.Dispose();
            _workCts = new CancellationTokenSource();

            try
            {
                var res = await AppServices.WeChat4Sync.EnsureSnapshotReadyAsync(
                    forceReload,
                    p => DispatcherQueue.TryEnqueue(() => LoadingText.Text = p.Message),
                    _workCts.Token);

                if (!res.Success || AppServices.WeChat4Sync.ActiveReader is null)
                {
                    SetLoadingState(false);
                    ShowStatusBanner(res.ErrorMessage, isError: true, showAdminBtn: res.NeedsAdminPrivilege);
                    return;
                }

                var reader = AppServices.WeChat4Sync.ActiveReader;
                string selfWxid = reader.SelfWxid ?? string.Empty;
                // Only mark friends already imported under THIS WeChat account (or legacy unassigned records)
                var scopedExisting = _existingCustomers
                    .Where(c => string.IsNullOrWhiteSpace(c.OwnerWxid) ||
                                string.IsNullOrWhiteSpace(selfWxid) ||
                                c.OwnerWxid.Equals(selfWxid, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var existingNativeIds = scopedExisting
                    .Select(c => c.NativeId?.Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
                var existingAccounts = scopedExisting
                    .Select(c => c.WechatAccount?.Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

                _allFriends = reader.Friends.ToList();
                foreach (var f in _allFriends)
                {
                    f.IsAlreadyImported = existingNativeIds.Contains(f.Username) ||
                                          (!string.IsNullOrWhiteSpace(f.Alias) && existingAccounts.Contains(f.Alias));
                    f.IsSelected = _selectedUsernames.Contains(f.Username);
                }

                BuildTagCategories(reader);
                string selfLabel = !string.IsNullOrWhiteSpace(reader.SelfContact?.NickName)
                    ? $"{reader.SelfContact.NickName} ({selfWxid})"
                    : selfWxid;
                HeaderSubtitleText.Text = $"当前微信：{selfLabel} · 共 {_allFriends.Count} 位好友 · {reader.LabelNames.Count} 个微信标签";
                SetLoadingState(false);
                PickerBodyGrid.Visibility = Visibility.Visible;

                if (_tagOptions.Count > 0)
                {
                    TagsListView.SelectedIndex = 0;
                }
                UpdateSelectionCounters();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SetLoadingState(false);
                ShowStatusBanner($"读取微信好友失败：{ex.Message}", isError: true, showAdminBtn: false);
            }
        }

        private void BuildTagCategories(WeChat4Reader reader)
        {
            _tagOptions.Clear();

            int totalWithMsgs = _allFriends.Count(f => f.HasMessages);
            _tagOptions.Add(new WeChat4TagOption
            {
                TagName = "全部好友",
                IsAllBucket = true,
                ContactCount = _allFriends.Count,
                ContactsWithMessagesCount = totalWithMsgs
            });

            var rawTags = reader.BuildTagOptions();
            foreach (var t in rawTags)
            {
                _tagOptions.Add(t);
            }
        }

        private List<WeChat4Contact> GetFriendsInTag(WeChat4TagOption? tag)
        {
            if (tag is null || tag.IsAllBucket) return _allFriends;
            if (tag.IsUntaggedBucket) return _allFriends.Where(f => f.TagList.Count == 0).ToList();
            return _allFriends.Where(f => f.TagList.Any(t => t.Equals(tag.TagName, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        private void RefreshVisibleFriends()
        {
            string q = FriendSearchBox.Text?.Trim() ?? string.Empty;
            var source = GetFriendsInTag(_activeTag);

            _visibleFriends.Clear();
            foreach (var f in source)
            {
                if (!string.IsNullOrEmpty(q) &&
                    !f.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !f.NickName.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !f.Alias.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !f.Tags.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                f.IsSelected = _selectedUsernames.Contains(f.Username);
                _visibleFriends.Add(f);
            }

            StartFriendAvatarHydration(_visibleFriends.Take(80).ToList());
        }

        private void StartFriendAvatarHydration(List<WeChat4Contact> batch)
        {
            _avatarCts?.Cancel();
            _avatarCts?.Dispose();
            var cts = new CancellationTokenSource();
            _avatarCts = cts;
            var reader = AppServices.WeChat4Sync.ActiveReader;
            if (reader is null) return;

            _ = Task.Run(async () =>
            {
                foreach (var f in batch)
                {
                    if (cts.IsCancellationRequested) return;
                    if (f.AvatarImage is not null) continue;
                    byte[]? bytes = await reader.TryGetAvatarBytesAsync(f.Username, cts.Token);
                    if (bytes is null || cts.IsCancellationRequested) continue;
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        if (cts.IsCancellationRequested) return;
                        var img = await AvatarImageService.BytesToBitmapImageAsync(bytes);
                        if (img is not null && !cts.IsCancellationRequested)
                        {
                            f.AvatarImage = img;
                        }
                    });
                }
            }, cts.Token);
        }

        private void TagsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _activeTag = TagsListView.SelectedItem as WeChat4TagOption;
            RefreshVisibleFriends();
        }

        private void TagSelectAllCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not WeChat4TagOption tag) return;
            var members = GetFriendsInTag(tag);
            bool allSelected = members.Count > 0 && members.All(m => _selectedUsernames.Contains(m.Username));
            bool targetState = !allSelected;

            foreach (var m in members)
            {
                if (targetState) _selectedUsernames.Add(m.Username);
                else _selectedUsernames.Remove(m.Username);
                m.IsSelected = targetState;
            }
            UpdateSelectionCounters();
        }

        private void FriendSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshVisibleFriends();

        private void FriendCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not WeChat4Contact contact) return;
            if (contact.IsSelected) _selectedUsernames.Add(contact.Username);
            else _selectedUsernames.Remove(contact.Username);
            UpdateSelectionCounters();
        }

        private void FriendsListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not WeChat4Contact contact) return;
            contact.IsSelected = !contact.IsSelected;
            if (contact.IsSelected) _selectedUsernames.Add(contact.Username);
            else _selectedUsernames.Remove(contact.Username);
            UpdateSelectionCounters();
        }

        private void SelectCurrentCategory_Click(object sender, RoutedEventArgs e)
        {
            foreach (var f in _visibleFriends)
            {
                f.IsSelected = true;
                _selectedUsernames.Add(f.Username);
            }
            UpdateSelectionCounters();
        }

        private void SelectCurrentWithMsgs_Click(object sender, RoutedEventArgs e)
        {
            foreach (var f in _visibleFriends)
            {
                if (f.HasMessages)
                {
                    f.IsSelected = true;
                    _selectedUsernames.Add(f.Username);
                }
            }
            UpdateSelectionCounters();
        }

        private void ClearCurrentCategory_Click(object sender, RoutedEventArgs e)
        {
            foreach (var f in _visibleFriends)
            {
                f.IsSelected = false;
                _selectedUsernames.Remove(f.Username);
            }
            UpdateSelectionCounters();
        }

        private void UpdateSelectionCounters()
        {
            foreach (var tag in _tagOptions)
            {
                var members = GetFriendsInTag(tag);
                int sel = members.Count(m => _selectedUsernames.Contains(m.Username));
                tag.SelectedCount = sel;
                tag.IsSelected = members.Count > 0 && sel == members.Count;
            }

            int total = _selectedUsernames.Count;
            ConfirmImportButton.IsEnabled = !_isBusy && total > 0;
            ConfirmImportButton.Content = total > 0 ? $"导入所选好友 ({total})" : "导入所选好友";
            SelectionSummaryText.Text = total > 0
                ? $"已选 {total} 位好友 · 导入后将自动监听这些好友的新消息"
                : "请勾选左侧微信标签或右侧好友进行导入";
        }

        private async void ConfirmImportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy || _selectedUsernames.Count == 0) return;

            _isBusy = true;
            ConfirmImportButton.IsEnabled = false;
            ReloadWeChatButton.IsEnabled = false;
            CancelButton.Content = "取消导入";
            ImportProgressBar.Visibility = Visibility.Visible;
            ImportProgressBar.Value = 5;
            ShowStatusBanner($"正在导入 {_selectedUsernames.Count} 位微信好友及聊天记录...", isError: false, showAdminBtn: false);

            _workCts?.Cancel();
            _workCts?.Dispose();
            _workCts = new CancellationTokenSource();

            try
            {
                var report = await AppServices.WeChat4Sync.ImportSelectedFriendsAsync(
                    _selectedUsernames,
                    p => DispatcherQueue.TryEnqueue(() =>
                    {
                        ImportProgressBar.Value = p.Percent;
                        StatusBannerText.Text = p.Message;
                    }),
                    _workCts.Token);

                CompletedReport = report;
                Hide();
            }
            catch (OperationCanceledException)
            {
                ShowStatusBanner("已取消导入。", isError: false, showAdminBtn: false);
            }
            catch (Exception ex)
            {
                ShowStatusBanner($"导入失败：{ex.Message}", isError: true, showAdminBtn: false);
            }
            finally
            {
                _isBusy = false;
                ImportProgressBar.Visibility = Visibility.Collapsed;
                ReloadWeChatButton.IsEnabled = true;
                CancelButton.Content = "取消";
                UpdateSelectionCounters();
            }
        }

        private void SetLoadingState(bool loading, string? message = null)
        {
            _isBusy = loading;
            LoadingPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            LoadingRing.IsActive = loading;
            if (message is not null) LoadingText.Text = message;
            if (loading) PickerBodyGrid.Visibility = Visibility.Collapsed;
            ReloadWeChatButton.IsEnabled = !loading;
            ConfirmImportButton.IsEnabled = !loading && _selectedUsernames.Count > 0;
        }

        private void ShowStatusBanner(string msg, bool isError, bool showAdminBtn)
        {
            StatusBannerText.Text = msg;
            StatusBanner.Background = (Brush)Application.Current.Resources[isError ? "DangerBgBrush" : "BrandSoftBgBrush"];
            StatusBanner.BorderBrush = (Brush)Application.Current.Resources[isError ? "DangerBorderBrush" : "BrandSoftBorderBrush"];
            RelaunchAdminButton.Visibility = showAdminBtn ? Visibility.Visible : Visibility.Collapsed;
            StatusBanner.Visibility = Visibility.Visible;
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
                ShowStatusBanner($"管理员重启失败：{ex.Message}", isError: true, showAdminBtn: false);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
            {
                _workCts?.Cancel();
                return;
            }
            Hide();
        }

        private void CloseAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            if (!_isBusy) Hide();
        }
    }
}
