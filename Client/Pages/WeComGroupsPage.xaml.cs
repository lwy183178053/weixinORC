using System.Collections.ObjectModel;
using Client.Controls;
using Client.Models;
using Client.Services;
using Client.Services.WeComDb;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

        private List<WeComAccountSnapshot> _accounts = [];
        private WeComGroup? _selectedGroup;
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
            AppServices.Settings.ThemeChanged += OnThemeChanged;
            AppServices.WeComSync.AccountsUpdated += OnWeComAccountsUpdated;

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
        }

        private void OnThemeChanged(bool _)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var row in _rows) row.NotifyVisualsChanged();
            });
        }

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
            SidebarSummaryText.Text = "正在扫描企业微信各企业群聊...";
            try
            {
                var (_, msg) = await AppServices.WeComSync.EnsureAllAccountsReadyAsync(forceRefresh);
                _accounts = AppServices.WeComSync.LoadedAccounts.ToList();
                RebuildRows();
                if (_accounts.Count == 0)
                {
                    SidebarSummaryText.Text = "未检测到已登录企业微信";
                    EmptySidebarText.Text = string.IsNullOrWhiteSpace(msg)
                        ? "请先在电脑上登录企业微信，然后点击刷新同步"
                        : msg;
                }
                else
                {
                    int totalGroups = _accounts.Sum(a => a.Reader.Groups.Count);
                    SidebarSummaryText.Text = $"已接入 {_accounts.Count} 家企业 · 共 {totalGroups} 个企微群";
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
            _rows.Clear();
            WeComSidebarRow? activeRow = null;

            foreach (var acc in _accounts)
            {
                IEnumerable<WeComGroup> list = acc.Reader.Groups;
                if (!string.IsNullOrEmpty(q))
                {
                    list = list.Where(g =>
                        g.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        g.OwnerCorpName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        g.ConversationId.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        g.Members.Any(m => m.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)));
                }

                var filtered = list.ToList();
                if (filtered.Count == 0 && !string.IsNullOrEmpty(q)) continue;

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
                    bool isSelected = _selectedGroup is not null &&
                                      string.Equals(_selectedGroup.OwnerAccountId, g.OwnerAccountId, StringComparison.OrdinalIgnoreCase) &&
                                      string.Equals(_selectedGroup.ConversationId, g.ConversationId, StringComparison.OrdinalIgnoreCase);
                    var row = new WeComSidebarRow
                    {
                        IsCorpHeader = false,
                        CorpKey = corpKey,
                        CorpName = acc.CorpName,
                        Group = g,
                        IsActive = isSelected
                    };
                    if (isSelected) activeRow = row;
                    _rows.Add(row);
                }
            }

            EmptySidebarHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (activeRow is null && _selectedGroup is null)
            {
                var first = _rows.FirstOrDefault(r => !r.IsCorpHeader && r.Group is not null);
                if (first?.Group is not null) SelectGroup(first);
            }
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
