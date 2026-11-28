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
    public sealed partial class WeComGroupsPage : Page
    {
        private readonly ObservableCollection<WeComSidebarRow> _rows = [];
        private readonly ObservableCollection<ChatBubbleItem> _messages = [];
        private readonly ObservableCollection<WeComGroupMemberRow> _members = [];
        private readonly HashSet<string> _collapsedCorps = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _checkedItemKeys = new(StringComparer.OrdinalIgnoreCase);

        private List<WeComAccountSnapshot> _accounts = [];
        private WeComGroup? _selectedGroup;
        private bool _isMultiSelectMode;
        private bool _isLoading;

        public WeComGroupsPage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Required;
            GroupsListView.ItemsSource = _rows;
            MessagesListView.ItemsSource = _messages;
            MembersListView.ItemsSource = _members;
            _ = ThreePaneResizeController.Attach(
                RootThreePaneGrid,
                LeftPaneColumn, LeftPaneSplitter,
                CenterPaneColumn, RightPaneSplitter,
                RightPaneColumn,
                leftDefault: 278, leftMin: 220, leftMax: 420,
                centerMin: 440,
                rightDefault: 346, rightMin: 280, rightMax: 500);
            Loaded += WeComGroupsPage_Loaded;
            Unloaded += WeComGroupsPage_Unloaded;
        }

        private async void WeComGroupsPage_Loaded(object sender, RoutedEventArgs e)
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

        private void WeComGroupsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            AppServices.Settings.ThemeChanged -= OnThemeChanged;
            AppServices.WeComSync.AccountsUpdated -= OnWeComAccountsUpdated;
            AppServices.WeComSync.ImportedSelectionChanged -= OnImportedSelectionChanged;
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
                if (_selectedGroup is not null)
                {
                    LoadGroupMessages(_selectedGroup);
                }
            });
        }

        private async Task LoadAllAccountsAsync(bool forceRefresh)
        {
            if (_isLoading) return;
            _isLoading = true;
            ReloadChatButton.IsEnabled = false;
            SidebarSummaryText.Text = "正在同步企业微信群聊...";
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
            var importedKeys = AppServices.WeComSync.GetImportedGroupKeys();
            _rows.Clear();

            int totalImportedMatched = 0;
            int corpsWithItems = 0;
            WeComSidebarRow? activeRow = null;

            foreach (var acc in _accounts)
            {
                IEnumerable<WeComGroup> list = acc.Reader.Groups
                    .Where(g => importedKeys.Contains(WeComSyncService.MakeGroupKey(acc.AccountId, g.ConversationId)));

                if (!string.IsNullOrEmpty(q))
                {
                    list = list.Where(g =>
                        g.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        g.OwnerCorpName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        g.ConversationId.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        g.Members.Any(m => m.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)));
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

                foreach (var g in filtered)
                {
                    string itemKey = WeComSyncService.MakeGroupKey(acc.AccountId, g.ConversationId);
                    bool isSelected = _selectedGroup is not null &&
                                      string.Equals(_selectedGroup.OwnerAccountId, g.OwnerAccountId, StringComparison.OrdinalIgnoreCase) &&
                                      string.Equals(_selectedGroup.ConversationId, g.ConversationId, StringComparison.OrdinalIgnoreCase);
                    var row = new WeComSidebarRow
                    {
                        IsCorpHeader = false,
                        ItemKey = itemKey,
                        CorpKey = corpKey,
                        CorpName = acc.CorpName,
                        Group = g,
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
                    : "点击右上角「+」从企业微信导入群聊";
                EmptySidebarText.Text = "点击下方或左上角「+」，按企业勾选要管理的企微群导入群成员与聊天记录。";
            }
            else
            {
                SidebarSummaryText.Text = $"已导入 {totalImportedMatched} 个群聊 · {corpsWithItems} 家企业";
                EmptySidebarText.Text = string.IsNullOrEmpty(q)
                    ? "当前已导入群聊所属企业暂未就绪，请点击「刷新同步」或点「+」重新导入。"
                    : $"未找到匹配「{q}」的已导入企微群。";
            }

            EmptySidebarHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (_selectedGroup is not null)
            {
                string selKey = WeComSyncService.MakeGroupKey(_selectedGroup.OwnerAccountId, _selectedGroup.ConversationId);
                if (!importedKeys.Contains(selKey))
                {
                    ClearSelectedGroup();
                }
            }

            if (activeRow is null && _selectedGroup is null)
            {
                var first = _rows.FirstOrDefault(r => !r.IsCorpHeader && r.Group is not null);
                if (first?.Group is not null) SelectGroup(first);
            }

            UpdateMultiSelectBarState();
        }

        private void ClearSelectedGroup()
        {
            _selectedGroup = null;
            _messages.Clear();
            _members.Clear();
            ActiveGroupTitle.Text = "请选择左侧企微群聊";
            ActiveCorpBadge.Visibility = Visibility.Collapsed;
            ActiveGroupMeta.Text = "点击左上角「+」按企业挑选并导入群聊，导入后按企业分组实时监听";
            SendMessageButton.IsEnabled = false;
            EmptyChatHint.Visibility = Visibility.Visible;

            ProfileGroupNameText.Text = "企微群聊档案";
            ProfileOwnerCorpText.Text = "归属企业：-";
            ProfileRoomIdText.Text = "chat_room_id: -";
            ProfileStatsText.Text = "群成员 0 人 · 本地消息 0 条";
            AnnouncementCard.Visibility = Visibility.Collapsed;
            MembersHeaderText.Text = "群成员 (0)";
        }

        private async void AddWeComGroupButton_Click(object sender, RoutedEventArgs e)
        {
            if (!AppServices.TryBeginModalDialog()) return;
            try
            {
                var dialog = new ImportWeComGroupsDialog { XamlRoot = XamlRoot };
                await dialog.ShowAsync();
                if (dialog.ImportedSelectedTotal > 0)
                {
                    _accounts = AppServices.WeComSync.LoadedAccounts.ToList();
                    RebuildRows();
                    string toast = dialog.ImportedAddedCount > 0
                        ? $"✓ 已导入 {dialog.ImportedAddedCount} 个企业微信群聊"
                        : "所选群聊已在监控列表中";
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

        private void MultiSelectGroupsButton_Click(object sender, RoutedEventArgs e) =>
            SetMultiSelectMode(!_isMultiSelectMode);

        private void ExitMultiSelectButton_Click(object sender, RoutedEventArgs e) =>
            SetMultiSelectMode(false);

        private void SetMultiSelectMode(bool enabled)
        {
            _isMultiSelectMode = enabled;
            if (!enabled) _checkedItemKeys.Clear();
            GroupMultiSelectBar.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            MultiSelectGroupsIcon.Foreground = (Brush)Application.Current.Resources[
                enabled ? "BrandPrimaryBrush" : "TextSecondaryBrush"];
            foreach (var row in _rows)
            {
                row.IsMultiSelectMode = enabled;
                if (!enabled) row.IsChecked = false;
            }
            UpdateMultiSelectBarState();
        }

        private void GroupRowCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not WeComSidebarRow row || row.IsCorpHeader) return;
            if (row.IsChecked) _checkedItemKeys.Add(row.ItemKey);
            else _checkedItemKeys.Remove(row.ItemKey);
            UpdateMultiSelectBarState();
        }

        private void SelectAllGroupsButton_Click(object sender, RoutedEventArgs e)
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

        private void RemoveSelectedGroupsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_checkedItemKeys.Count == 0) return;
            int removed = AppServices.WeComSync.RemoveImportedGroupKeys(_checkedItemKeys.ToList());
            SetMultiSelectMode(false);
            RebuildRows();
            WorkbenchPage.Current?.ShowToast($"✓ 已从监控列表移除 {removed} 个企微群聊");
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

        private void RemoveGroupRowMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not WeComSidebarRow row || row.IsCorpHeader) return;
            AppServices.WeComSync.RemoveImportedGroupKeys(new[] { row.ItemKey });
            RebuildRows();
            WorkbenchPage.Current?.ShowToast($"已移除群聊「{row.DisplayName}」");
        }

        private void UpdateMultiSelectBarState()
        {
            int count = _checkedItemKeys.Count;
            SelectedGroupsCountText.Text = $"已选 {count}";
            RemoveSelectedGroupsButton.IsEnabled = count > 0;
        }

        private void GroupsListView_ItemClick(object sender, ItemClickEventArgs e)
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
            SelectGroup(row);
        }

        private void SelectGroup(WeComSidebarRow row)
        {
            if (row.Group is null) return;
            foreach (var r in _rows) r.IsActive = ReferenceEquals(r, row);
            _selectedGroup = row.Group;

            var g = row.Group;
            ActiveGroupTitle.Text = g.DisplayName;
            ActiveCorpBadgeText.Text = g.OwnerCorpName;
            ActiveCorpBadge.Visibility = Visibility.Visible;
            ActiveGroupMeta.Text = $"{g.OwnerCorpName} · 群成员 {g.Members.Count} 人 · {g.ConversationId}";

            SendMessageButton.IsEnabled = true;

            ProfileGroupNameText.Text = g.DisplayName;
            ProfileOwnerCorpText.Text = $"归属企业：{g.OwnerCorpName} (账号 {g.OwnerAccountId})";
            ProfileRoomIdText.Text = $"chat_room_id: {g.ConversationId}";
            ProfileStatsText.Text = $"群成员 {g.Members.Count} 人 · 本地消息 {g.MessageCount} 条";

            if (!string.IsNullOrWhiteSpace(g.Announcement))
            {
                AnnouncementText.Text = g.Announcement;
                AnnouncementCard.Visibility = Visibility.Visible;
            }
            else
            {
                AnnouncementCard.Visibility = Visibility.Collapsed;
            }

            RebuildMembersList();
            LoadGroupMessages(g);
        }

        private void RebuildMembersList()
        {
            _members.Clear();
            if (_selectedGroup is null)
            {
                MembersHeaderText.Text = "群成员 (0)";
                return;
            }

            string q = MemberSearchBox.Text.Trim();
            var list = _selectedGroup.Members.AsEnumerable();
            if (!string.IsNullOrEmpty(q))
            {
                list = list.Where(m =>
                    m.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    m.UserId.Contains(q, StringComparison.OrdinalIgnoreCase));
            }

            var items = list.ToList();
            MembersHeaderText.Text = $"群成员 ({items.Count}/{_selectedGroup.Members.Count})";

            foreach (var m in items)
            {
                bool isOwner = string.Equals(m.UserId, _selectedGroup.OwnerUserId, StringComparison.OrdinalIgnoreCase);
                _members.Add(new WeComGroupMemberRow
                {
                    UserId = m.UserId,
                    DisplayName = m.DisplayName,
                    SubText = m.UserId,
                    RoleText = isOwner ? "群主" : (m.IsAdmin ? "管理员" : string.Empty),
                    AvatarUrl = m.AvatarUrl
                });
            }
        }

        private void LoadGroupMessages(WeComGroup g)
        {
            _messages.Clear();
            var reader = AppServices.WeComSync.GetReaderForAccount(g.OwnerAccountId);
            if (reader is null)
            {
                EmptyChatHint.Visibility = Visibility.Visible;
                return;
            }

            var msgs = reader.ReadConversationMessages(g.ConversationId, 180);
            foreach (var m in msgs)
            {
                string sender = string.IsNullOrWhiteSpace(m.SenderName) ? m.SenderId : m.SenderName;
                var item = new ChatBubbleItem
                {
                    Id = m.MessageId,
                    IsOutgoing = m.IsOutgoing,
                    SenderName = sender,
                    SenderInitial = sender.Length > 0 ? sender[..1].ToUpperInvariant() : "员",
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
        private void MemberSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RebuildMembersList();

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadAllAccountsAsync(forceRefresh: true);
            if (_selectedGroup is not null) LoadGroupMessages(_selectedGroup);
        }

        private async void SendMessageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGroup is null) return;
            string text = SendInputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(text)) return;

            SendMessageButton.IsEnabled = false;
            SendStatusText.Text = $"正在通过无 Hook RPA 向企微群「{_selectedGroup.DisplayName}」发送...";
            try
            {
                var executor = new WeComAutoReplyExecutor();
                var (ok, _, msg) = await executor.SendMessageAsync(
                    searchKey: _selectedGroup.DisplayName,
                    expectedDisplayName: _selectedGroup.DisplayName,
                    isGroup: true,
                    text: text,
                    verifyConversationId: _selectedGroup.ConversationId);

                SendStatusText.Text = msg;
                WorkbenchPage.Current?.ShowToast(ok ? $"✓ {msg}" : msg, isError: !ok);
                if (ok)
                {
                    SendInputBox.Text = string.Empty;
                    await LoadAllAccountsAsync(forceRefresh: true);
                    if (_selectedGroup is not null) LoadGroupMessages(_selectedGroup);
                }
            }
            catch (Exception ex)
            {
                SendStatusText.Text = $"发送失败：{ex.Message}";
                WorkbenchPage.Current?.ShowToast($"发送失败：{ex.Message}", isError: true);
            }
            finally
            {
                SendMessageButton.IsEnabled = _selectedGroup is not null;
            }
        }
    }
}
