using System.Collections.ObjectModel;
using Client.Controls;
using Client.Dialogs;
using Client.Models;
using Client.Services;
using Client.Services.WeComDb;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace Client.Pages
{
    public sealed partial class WeComContactsPage : Page
    {
        private readonly ObservableCollection<WeComSidebarRow> _rows = [];
        private readonly ObservableCollection<ChatBubbleItem> _messages = [];
        private readonly HashSet<string> _collapsedCorps = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _checkedItemKeys = new(StringComparer.OrdinalIgnoreCase);

        private List<WeComAccountSnapshot> _accounts = [];
        private WeComUser? _selectedUser;
        private bool _isMultiSelectMode;
        private bool _isLoading;
        private CancellationTokenSource? _avatarCts;

        public WeComContactsPage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Required;
            ContactsListView.ItemsSource = _rows;
            MessagesListView.ItemsSource = _messages;
            _ = ThreePaneResizeController.Attach(
                RootThreePaneGrid,
                LeftPaneColumn, LeftPaneSplitter,
                CenterPaneColumn, RightPaneSplitter,
                RightPaneColumn,
                leftDefault: 278, leftMin: 220, leftMax: 420,
                centerMin: 440,
                rightDefault: 346, rightMin: 280, rightMax: 500);
            Loaded += WeComContactsPage_Loaded;
            Unloaded += WeComContactsPage_Unloaded;
        }

        private async void WeComContactsPage_Loaded(object sender, RoutedEventArgs e)
        {
            AppServices.Settings.ThemeChanged -= OnThemeChanged;
            AppServices.WeComSync.AccountsUpdated -= OnWeComAccountsUpdated;
            AppServices.WeComSync.ImportedSelectionChanged -= OnImportedSelectionChanged;
            AppServices.Settings.ThemeChanged += OnThemeChanged;
            AppServices.WeComSync.AccountsUpdated += OnWeComAccountsUpdated;
            AppServices.WeComSync.ImportedSelectionChanged += OnImportedSelectionChanged;

            if (_accounts.Count == 0)
            {
                await LoadAllAccountsAsync(forceRefresh: false);
            }
            else
            {
                RebuildRows();
            }
        }

        private void WeComContactsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            AppServices.Settings.ThemeChanged -= OnThemeChanged;
            AppServices.WeComSync.AccountsUpdated -= OnWeComAccountsUpdated;
            AppServices.WeComSync.ImportedSelectionChanged -= OnImportedSelectionChanged;
            _avatarCts?.Cancel();
            _avatarCts?.Dispose();
            _avatarCts = null;
        }

        private void OnThemeChanged(bool _)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var row in _rows) row.NotifyVisualsChanged();
            });
        }

        private void OnImportedSelectionChanged() => DispatcherQueue.TryEnqueue(RebuildRows);

        private void OnWeComAccountsUpdated()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _accounts = AppServices.WeComSync.LoadedAccounts.ToList();
                RebuildRows();
                if (_selectedUser is not null)
                {
                    LoadConversationMessages(_selectedUser);
                }
            });
        }

        private async Task LoadAllAccountsAsync(bool forceRefresh)
        {
            if (_isLoading) return;
            _isLoading = true;
            ReloadChatButton.IsEnabled = false;
            SidebarSummaryText.Text = "正在同步企业微信数据...";
            try
            {
                var (_, msg) = await AppServices.WeComSync.EnsureAllAccountsReadyAsync(forceRefresh);
                _accounts = AppServices.WeComSync.LoadedAccounts.ToList();
                RebuildRows();
                if (_accounts.Count == 0 && !string.IsNullOrWhiteSpace(msg))
                {
                    SidebarSummaryText.Text = "未检测到已登录企业微信";
                }
            }
            catch (Exception ex)
            {
                SidebarSummaryText.Text = $"加载失败：{ex.Message}";
            }
            finally
            {
                _isLoading = false;
                ReloadChatButton.IsEnabled = true;
            }
        }

        private void RebuildRows()
        {
            string q = SearchBox.Text.Trim();
            var importedKeys = AppServices.WeComSync.GetImportedContactKeys();
            _rows.Clear();

            int totalImportedMatched = 0;
            int corpsWithItems = 0;
            WeComSidebarRow? activeRow = null;

            foreach (var acc in _accounts)
            {
                IEnumerable<WeComUser> list = acc.Reader.Users
                    .Where(u => importedKeys.Contains(WeComSyncService.MakeContactKey(acc.AccountId, u.UserId)));

                if (!string.IsNullOrEmpty(q))
                {
                    list = list.Where(u =>
                        u.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        u.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        u.Remark.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        u.ExternalCorpName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        u.OwnerCorpName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        u.UserId.Contains(q, StringComparison.OrdinalIgnoreCase));
                }

                var filtered = list.ToList();
                if (filtered.Count == 0) continue;

                corpsWithItems++;
                totalImportedMatched += filtered.Count;
                string corpKey = acc.AccountId;
                bool collapsed = _collapsedCorps.Contains(corpKey) && string.IsNullOrEmpty(q);
                string headerLabel = acc.IsPrimary ? $"{acc.CorpName} (当前活跃)" : acc.CorpName;

                _rows.Add(new WeComSidebarRow
                {
                    IsCorpHeader = true,
                    CorpKey = corpKey,
                    CorpName = headerLabel,
                    CorpItemCount = filtered.Count,
                    IsCollapsed = collapsed
                });

                if (collapsed) continue;

                foreach (var u in filtered)
                {
                    string itemKey = WeComSyncService.MakeContactKey(acc.AccountId, u.UserId);
                    bool isSelected = _selectedUser is not null &&
                                      string.Equals(_selectedUser.OwnerAccountId, u.OwnerAccountId, StringComparison.OrdinalIgnoreCase) &&
                                      string.Equals(_selectedUser.UserId, u.UserId, StringComparison.OrdinalIgnoreCase);
                    var row = new WeComSidebarRow
                    {
                        IsCorpHeader = false,
                        ItemKey = itemKey,
                        CorpKey = corpKey,
                        CorpName = acc.CorpName,
                        User = u,
                        IsActive = isSelected,
                        IsMultiSelectMode = _isMultiSelectMode,
                        IsChecked = _checkedItemKeys.Contains(itemKey)
                    };
                    if (isSelected) activeRow = row;
                    _rows.Add(row);
                }
            }

            if (importedKeys.Count == 0)
            {
                SidebarSummaryText.Text = _accounts.Count > 0
                    ? $"已识别 {_accounts.Count} 家企业 · 点右上角「+」按需导入"
                    : "点击右上角「+」从企业微信导入联系人";
                EmptySidebarText.Text = "点击下方或左上角「+」，按企业勾选要监控的企微联系人导入，导入后自动监听消息更新。";
            }
            else
            {
                SidebarSummaryText.Text = $"已导入 {totalImportedMatched} 位联系人 · {corpsWithItems} 家企业";
                EmptySidebarText.Text = string.IsNullOrEmpty(q)
                    ? "当前已导入联系人所属企业暂未就绪，请点击「刷新同步」或点「+」重新导入。"
                    : $"未找到匹配「{q}」的已导入联系人。";
            }

            EmptySidebarHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (_selectedUser is not null)
            {
                string selKey = WeComSyncService.MakeContactKey(_selectedUser.OwnerAccountId, _selectedUser.UserId);
                if (!importedKeys.Contains(selKey))
                {
                    ClearSelectedContact();
                }
            }

            if (activeRow is null && _selectedUser is null)
            {
                var firstItem = _rows.FirstOrDefault(r => !r.IsCorpHeader && r.User is not null);
                if (firstItem?.User is not null)
                {
                    SelectContact(firstItem);
                }
            }

            UpdateMultiSelectBarState();
            HydrateRowAvatars();
        }

        private void ClearSelectedContact()
        {
            _selectedUser = null;
            _messages.Clear();
            ActiveContactTitle.Text = "请选择左侧企微联系人";
            ActiveCorpBadge.Visibility = Visibility.Collapsed;
            ActiveContactMeta.Text = "点击左上角「+」按企业挑选并导入联系人，导入后按企业分组实时监听";
            SendMessageButton.IsEnabled = false;
            EmptyChatHint.Visibility = Visibility.Visible;

            ProfileNameText.Text = "未选择联系人";
            ProfileTypeText.Text = "请在左侧选择已导入联系人";
            ProfileAvatarInitial.Text = "企";
            ProfileAvatarImage.Source = null;
            ProfileAvatarImage.Visibility = Visibility.Collapsed;
            ProfileAvatarInitial.Visibility = Visibility.Visible;
            ProfileOwnerCorpText.Text = "-";
            ProfileRemarkText.Text = "-";
            ProfileCompanyText.Text = "-";
            ProfilePhoneText.Text = "-";
            ProfileDescText.Text = "-";
            ProfileUserIdText.Text = "user_id: -";
            ProfileCorpIdText.Text = "corp_id: -";
            ProfileConvIdText.Text = "conv_id: -";
            ProfileStatsText.Text = "本地消息数: 0";
        }

        private void HydrateRowAvatars()
        {
            _avatarCts?.Cancel();
            _avatarCts?.Dispose();
            var cts = new CancellationTokenSource();
            _avatarCts = cts;

            var targets = _rows.Where(r => !r.IsCorpHeader && !string.IsNullOrWhiteSpace(r.AvatarUrl) && r.AvatarImage is null).Take(45).ToList();
            if (targets.Count == 0) return;

            _ = Task.Run(async () =>
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                foreach (var row in targets)
                {
                    if (cts.IsCancellationRequested) break;
                    try
                    {
                        byte[] bytes = await http.GetByteArrayAsync(row.AvatarUrl, cts.Token);
                        if (bytes.Length == 0) continue;
                        DispatcherQueue.TryEnqueue(async () =>
                        {
                            try
                            {
                                var bmp = await AvatarImageService.BytesToBitmapImageAsync(bytes);
                                if (bmp is null) return;
                                row.AvatarImage = bmp;
                                if (_selectedUser is not null && row.User is not null &&
                                    string.Equals(_selectedUser.UserId, row.User.UserId, StringComparison.OrdinalIgnoreCase))
                                {
                                    ProfileAvatarImage.Source = bmp;
                                    ProfileAvatarImage.Visibility = Visibility.Visible;
                                    ProfileAvatarInitial.Visibility = Visibility.Collapsed;
                                }
                            }
                            catch { }
                        });
                    }
                    catch { }
                }
            }, cts.Token);
        }

        private async void AddWeComContactButton_Click(object sender, RoutedEventArgs e)
        {
            if (!AppServices.TryBeginModalDialog()) return;
            try
            {
                var dialog = new ImportWeComContactsDialog { XamlRoot = XamlRoot };
                await dialog.ShowAsync();
                if (dialog.ImportedSelectedTotal > 0)
                {
                    _accounts = AppServices.WeComSync.LoadedAccounts.ToList();
                    RebuildRows();
                    string toast = dialog.ImportedAddedCount > 0
                        ? $"✓ 已导入 {dialog.ImportedAddedCount} 位企业微信联系人"
                        : "所选联系人已在监控列表中";
                    WorkbenchPage.Current?.ShowToast(toast);
                }
            }
            catch (Exception ex)
            {
                WorkbenchPage.Current?.ShowToast($"打开导入弹窗失败：{ex.Message}", isError: true);
            }
            finally
            {
                AppServices.EndModalDialog();
            }
        }

        private void MultiSelectContactsButton_Click(object sender, RoutedEventArgs e) =>
            SetMultiSelectMode(!_isMultiSelectMode);

        private void ExitMultiSelectButton_Click(object sender, RoutedEventArgs e) =>
            SetMultiSelectMode(false);

        private void SetMultiSelectMode(bool enabled)
        {
            _isMultiSelectMode = enabled;
            if (!enabled) _checkedItemKeys.Clear();
            ContactMultiSelectBar.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            MultiSelectContactsIcon.Foreground = (Brush)Application.Current.Resources[
                enabled ? "BrandPrimaryBrush" : "TextSecondaryBrush"];
            foreach (var row in _rows)
            {
                row.IsMultiSelectMode = enabled;
                if (!enabled) row.IsChecked = false;
            }
            UpdateMultiSelectBarState();
        }

        private void ContactRowCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not WeComSidebarRow row || row.IsCorpHeader) return;
            if (row.IsChecked) _checkedItemKeys.Add(row.ItemKey);
            else _checkedItemKeys.Remove(row.ItemKey);
            UpdateMultiSelectBarState();
        }

        private void SelectAllContactsButton_Click(object sender, RoutedEventArgs e)
        {
            var items = _rows.Where(r => !r.IsCorpHeader).ToList();
            bool allChecked = items.Count > 0 && items.All(r => _checkedItemKeys.Contains(r.ItemKey));
            bool target = !allChecked;
            foreach (var r in items)
            {
                r.IsChecked = target;
                if (target) _checkedItemKeys.Add(r.ItemKey);
                else _checkedItemKeys.Remove(r.ItemKey);
            }
            UpdateMultiSelectBarState();
        }

        private void RemoveSelectedContactsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_checkedItemKeys.Count == 0) return;
            int removed = AppServices.WeComSync.RemoveImportedContactKeys(_checkedItemKeys.ToList());
            SetMultiSelectMode(false);
            RebuildRows();
            WorkbenchPage.Current?.ShowToast($"✓ 已从监控列表移除 {removed} 位企微联系人");
        }

        private void MultiSelectRowMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is WeComSidebarRow row && !row.IsCorpHeader)
            {
                _checkedItemKeys.Add(row.ItemKey);
            }
            SetMultiSelectMode(true);
            foreach (var r in _rows.Where(x => !x.IsCorpHeader))
            {
                r.IsChecked = _checkedItemKeys.Contains(r.ItemKey);
            }
            UpdateMultiSelectBarState();
        }

        private void RemoveContactRowMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not WeComSidebarRow row || row.IsCorpHeader) return;
            AppServices.WeComSync.RemoveImportedContactKeys(new[] { row.ItemKey });
            RebuildRows();
            WorkbenchPage.Current?.ShowToast($"已移除「{row.DisplayName}」");
        }

        private void UpdateMultiSelectBarState()
        {
            int count = _checkedItemKeys.Count;
            SelectedContactsCountText.Text = $"已选 {count}";
            RemoveSelectedContactsButton.IsEnabled = count > 0;
        }

        private void ContactsListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not WeComSidebarRow row) return;
            if (row.IsCorpHeader)
            {
                if (_collapsedCorps.Contains(row.CorpKey)) _collapsedCorps.Remove(row.CorpKey);
                else _collapsedCorps.Add(row.CorpKey);
                RebuildRows();
                return;
            }
            if (_isMultiSelectMode)
            {
                row.IsChecked = !row.IsChecked;
                if (row.IsChecked) _checkedItemKeys.Add(row.ItemKey);
                else _checkedItemKeys.Remove(row.ItemKey);
                UpdateMultiSelectBarState();
                return;
            }
            SelectContact(row);
        }

        private void SelectContact(WeComSidebarRow row)
        {
            if (row.User is null) return;
            foreach (var r in _rows) r.IsActive = ReferenceEquals(r, row);
            _selectedUser = row.User;

            var u = row.User;
            ActiveContactTitle.Text = u.DisplayName;
            ActiveCorpBadgeText.Text = u.OwnerCorpName;
            ActiveCorpBadge.Visibility = Visibility.Visible;
            ActiveContactMeta.Text = u.IsExternal
                ? $"外部联系人 · {(string.IsNullOrWhiteSpace(u.ExternalCorpName) ? "@微信" : u.ExternalCorpName)} · ID: {u.UserId}"
                : $"企业内部同事 · {u.OwnerCorpName} · ID: {u.UserId}";

            SendMessageButton.IsEnabled = true;

            ProfileNameText.Text = u.DisplayName;
            ProfileTypeText.Text = u.IsExternal ? "外部微信/企微客户" : "本企业内部同事";
            ProfileAvatarInitial.Text = row.AvatarInitial;
            if (row.AvatarImage is not null)
            {
                ProfileAvatarImage.Source = row.AvatarImage;
                ProfileAvatarImage.Visibility = Visibility.Visible;
                ProfileAvatarInitial.Visibility = Visibility.Collapsed;
            }
            else
            {
                ProfileAvatarImage.Source = null;
                ProfileAvatarImage.Visibility = Visibility.Collapsed;
                ProfileAvatarInitial.Visibility = Visibility.Visible;
            }

            ProfileOwnerCorpText.Text = $"{u.OwnerCorpName} (账号 {u.OwnerAccountId})";
            ProfileRemarkText.Text = string.Join(" / ", new[] { u.Remark, u.Name, u.RealName }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
            if (string.IsNullOrWhiteSpace(ProfileRemarkText.Text)) ProfileRemarkText.Text = "-";
            ProfileCompanyText.Text = string.Join(" · ", new[] { u.ExternalCorpName, u.Position }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrWhiteSpace(ProfileCompanyText.Text)) ProfileCompanyText.Text = u.IsExternal ? "@微信" : u.OwnerCorpName;
            ProfilePhoneText.Text = string.Join(" / ", new[] { u.Phone, u.Email }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrWhiteSpace(ProfilePhoneText.Text)) ProfilePhoneText.Text = "-";
            ProfileDescText.Text = string.IsNullOrWhiteSpace(u.Description) ? "-" : u.Description;

            ProfileUserIdText.Text = $"user_id: {u.UserId}";
            ProfileCorpIdText.Text = $"corp_id: {(string.IsNullOrWhiteSpace(u.CorpId) ? u.OwnerCorpId : u.CorpId)}";
            ProfileConvIdText.Text = $"conv_id: {(string.IsNullOrWhiteSpace(u.ConversationId) ? "(尚无单聊会话)" : u.ConversationId)}";
            ProfileStatsText.Text = $"本地消息数: {u.MessageCount} 条";

            LoadConversationMessages(u);
        }

        private void LoadConversationMessages(WeComUser u)
        {
            _messages.Clear();
            var reader = AppServices.WeComSync.GetReaderForAccount(u.OwnerAccountId);
            if (reader is null)
            {
                EmptyChatHint.Visibility = Visibility.Visible;
                return;
            }

            string convId = !string.IsNullOrWhiteSpace(u.ConversationId)
                ? u.ConversationId
                : reader.BuildSingleConversationId(u.UserId);

            var msgs = reader.ReadConversationMessages(convId, 150);
            foreach (var m in msgs)
            {
                var item = new ChatBubbleItem
                {
                    Id = m.MessageId,
                    IsOutgoing = m.IsOutgoing,
                    SenderName = m.IsOutgoing ? "我" : u.DisplayName,
                    SenderInitial = u.DisplayName.Length > 0 ? u.DisplayName[..1].ToUpperInvariant() : "客",
                    Content = m.Content,
                    TimeLabel = m.SendTime > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(m.SendTime).ToLocalTime().ToString("MM-dd HH:mm")
                        : string.Empty
                };

                if (m.Kind == "image" && !string.IsNullOrWhiteSpace(m.LocalFilePath) && File.Exists(m.LocalFilePath))
                {
                    try
                    {
                        item.BubbleImage = new BitmapImage(new Uri(m.LocalFilePath));
                        item.Content = string.Empty;
                    }
                    catch { }
                }

                _messages.Add(item);
            }

            EmptyChatHint.Visibility = _messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_messages.Count > 0)
            {
                MessagesListView.ScrollIntoView(_messages[^1]);
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RebuildRows();

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadAllAccountsAsync(forceRefresh: true);
            if (_selectedUser is not null)
            {
                LoadConversationMessages(_selectedUser);
            }
        }

        private async void SendMessageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedUser is null) return;
            string text = SendInputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(text)) return;

            SendMessageButton.IsEnabled = false;
            SendStatusText.Text = $"正在通过无 Hook RPA 向「{_selectedUser.DisplayName}」发送...";
            try
            {
                var reader = AppServices.WeComSync.GetReaderForAccount(_selectedUser.OwnerAccountId);
                string convId = !string.IsNullOrWhiteSpace(_selectedUser.ConversationId)
                    ? _selectedUser.ConversationId
                    : reader?.BuildSingleConversationId(_selectedUser.UserId) ?? string.Empty;

                var executor = new WeComAutoReplyExecutor();
                var (ok, _, msg) = await executor.SendMessageAsync(
                    searchKey: !string.IsNullOrWhiteSpace(_selectedUser.Remark) ? _selectedUser.Remark : _selectedUser.DisplayName,
                    expectedDisplayName: _selectedUser.DisplayName,
                    isGroup: false,
                    text: text,
                    verifyConversationId: convId);

                SendStatusText.Text = msg;
                WorkbenchPage.Current?.ShowToast(ok ? $"✓ {msg}" : msg, isError: !ok);
                if (ok)
                {
                    SendInputBox.Text = string.Empty;
                    await LoadAllAccountsAsync(forceRefresh: true);
                    if (_selectedUser is not null) LoadConversationMessages(_selectedUser);
                }
            }
            catch (Exception ex)
            {
                SendStatusText.Text = $"发送失败：{ex.Message}";
                WorkbenchPage.Current?.ShowToast($"发送失败：{ex.Message}", isError: true);
            }
            finally
            {
                SendMessageButton.IsEnabled = _selectedUser is not null;
            }
        }
    }
}
