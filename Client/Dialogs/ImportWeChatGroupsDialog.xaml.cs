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
    public sealed partial class ImportWeChatGroupsDialog : ContentDialog
    {
        private readonly HashSet<string> _existingGroupKeys;
        private readonly ObservableCollection<WeChat4Group> _visibleGroups = [];
        private readonly HashSet<string> _selectedGroupKeys = new(StringComparer.OrdinalIgnoreCase);

        private List<WeChat4Group> _allGroups = [];
        private CancellationTokenSource? _workCts;
        private CancellationTokenSource? _avatarCts;
        private bool _isBusy;

        public WeChat4SyncReport? CompletedReport { get; private set; }

        public ImportWeChatGroupsDialog(IEnumerable<GroupDto> existingGroups)
        {
            InitializeComponent();
            RequestedTheme = AppServices.Settings.CurrentElementTheme;
            _existingGroupKeys = existingGroups
                .Select(g => g.GroupKey?.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

            GroupsListView.ItemsSource = _visibleGroups;
            Loaded += ImportWeChatGroupsDialog_Loaded;
            Closed += ImportWeChatGroupsDialog_Closed;
        }

        private async void ImportWeChatGroupsDialog_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= ImportWeChatGroupsDialog_Loaded;
            await LoadWeChatGroupsAsync(forceReload: false);
        }

        private void ImportWeChatGroupsDialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
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
            await LoadWeChatGroupsAsync(forceReload: true);
        }

        private async Task LoadWeChatGroupsAsync(bool forceReload)
        {
            SetLoadingState(true, forceReload ? "正在刷新微信最新群聊列表..." : "正在读取本机微信群聊列表...");
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
                _allGroups = reader.Groups.ToList();
                foreach (var g in _allGroups)
                {
                    g.IsAlreadyImported = _existingGroupKeys.Contains(g.Username);
                    g.IsSelected = _selectedGroupKeys.Contains(g.Username);
                }

                int withMsgs = _allGroups.Count(g => g.HasMessages);
                HeaderSubtitleText.Text = $"账号 {reader.SelfWxid} · 共 {_allGroups.Count} 个群聊（其中 {withMsgs} 个有本地聊天记录）";

                SetLoadingState(false);
                PickerBodyGrid.Visibility = Visibility.Visible;
                RefreshVisibleGroups();
                UpdateSelectionSummary();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SetLoadingState(false);
                ShowStatusBanner($"读取微信群聊失败：{ex.Message}", isError: true, showAdminBtn: false);
            }
        }

        private void RefreshVisibleGroups()
        {
            string q = GroupSearchBox.Text?.Trim() ?? string.Empty;
            _visibleGroups.Clear();
            foreach (var g in _allGroups)
            {
                if (!string.IsNullOrEmpty(q) &&
                    !g.GroupName.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !g.Username.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                g.IsSelected = _selectedGroupKeys.Contains(g.Username);
                _visibleGroups.Add(g);
            }

            StartGroupAvatarHydration(_visibleGroups.Take(80).ToList());
        }

        private void StartGroupAvatarHydration(List<WeChat4Group> batch)
        {
            _avatarCts?.Cancel();
            _avatarCts?.Dispose();
            var cts = new CancellationTokenSource();
            _avatarCts = cts;
            var reader = AppServices.WeChat4Sync.ActiveReader;
            if (reader is null) return;

            _ = Task.Run(async () =>
            {
                foreach (var g in batch)
                {
                    if (cts.IsCancellationRequested) return;
                    if (g.AvatarImage is not null) continue;
                    byte[]? bytes = await reader.TryGetGroupAvatarOrCollageBytesAsync(g.Username, cts.Token);
                    if (bytes is null || cts.IsCancellationRequested) continue;
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        if (cts.IsCancellationRequested) return;
                        var img = await AvatarImageService.BytesToBitmapImageAsync(bytes);
                        if (img is not null && !cts.IsCancellationRequested)
                        {
                            g.AvatarImage = img;
                        }
                    });
                }
            }, cts.Token);
        }

        private void GroupSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshVisibleGroups();

        private void GroupCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not WeChat4Group group) return;
            if (group.IsSelected) _selectedGroupKeys.Add(group.Username);
            else _selectedGroupKeys.Remove(group.Username);
            UpdateSelectionSummary();
        }

        private void GroupsListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not WeChat4Group group) return;
            group.IsSelected = !group.IsSelected;
            if (group.IsSelected) _selectedGroupKeys.Add(group.Username);
            else _selectedGroupKeys.Remove(group.Username);
            UpdateSelectionSummary();
        }

        private void SelectWithMsgs_Click(object sender, RoutedEventArgs e)
        {
            foreach (var g in _visibleGroups)
            {
                if (g.HasMessages)
                {
                    g.IsSelected = true;
                    _selectedGroupKeys.Add(g.Username);
                }
            }
            UpdateSelectionSummary();
        }

        private void SelectAllVisible_Click(object sender, RoutedEventArgs e)
        {
            foreach (var g in _visibleGroups)
            {
                g.IsSelected = true;
                _selectedGroupKeys.Add(g.Username);
            }
            UpdateSelectionSummary();
        }

        private void ClearAllVisible_Click(object sender, RoutedEventArgs e)
        {
            foreach (var g in _visibleGroups)
            {
                g.IsSelected = false;
                _selectedGroupKeys.Remove(g.Username);
            }
            UpdateSelectionSummary();
        }

        private void UpdateSelectionSummary()
        {
            int total = _selectedGroupKeys.Count;
            ConfirmImportButton.IsEnabled = !_isBusy && total > 0;
            ConfirmImportButton.Content = total > 0 ? $"导入所选群聊 ({total})" : "导入所选群聊";
            SelectionSummaryText.Text = total > 0
                ? $"已选 {total} 个群聊 · 导入后将自动巡检并同步新群消息"
                : "请勾选要导入管理的微信群";
        }

        private async void ConfirmImportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy || _selectedGroupKeys.Count == 0) return;

            _isBusy = true;
            ConfirmImportButton.IsEnabled = false;
            ReloadWeChatButton.IsEnabled = false;
            CancelButton.Content = "取消导入";
            ImportProgressBar.Visibility = Visibility.Visible;
            ImportProgressBar.Value = 5;
            ShowStatusBanner($"正在导入 {_selectedGroupKeys.Count} 个微信群的头像、群成员与聊天记录...", isError: false, showAdminBtn: false);

            _workCts?.Cancel();
            _workCts?.Dispose();
            _workCts = new CancellationTokenSource();

            try
            {
                var report = await AppServices.WeChat4Sync.ImportSelectedGroupsAsync(
                    _selectedGroupKeys,
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
                UpdateSelectionSummary();
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
            ConfirmImportButton.IsEnabled = !loading && _selectedGroupKeys.Count > 0;
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
