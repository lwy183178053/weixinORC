using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Client.Controls;
using Client.Dialogs;
using Client.Models;
using Client.Services;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace Client.Pages
{
    public sealed partial class GroupsPage : Page
    {
        private List<GroupDto> _allGroups = [];
        private List<GroupDto> _filteredGroups = [];
        private GroupDto? _selectedGroup;
        private readonly HashSet<long> _selectedGroupIds = [];
        private readonly ObservableCollection<ChatBubbleItem> _currentMessages = [];
        private readonly ObservableCollection<GroupMemberDto> _currentMembers = [];
        private readonly ObservableCollection<AiChatMessageItem> _aiChatMessages = [];
        private readonly ObservableCollection<AiImageAttachment> _aiChatPendingImages = [];
        private readonly HashSet<long> _locallyDeletedMessageIds = [];
        private PendingChatImage? _pendingChatImage;
        private GroupAiWorkspaceData? _currentAiWorkspace;
        private CancellationTokenSource? _chatImageHydrationCts;
        private CancellationTokenSource? _groupAvatarHydrationCts;
        private CancellationTokenSource? _memberAvatarHydrationCts;
        private string _activeRightTab = "profile";
        private bool _isGroupMultiSelectMode;
        private bool _isMultiSelectMode;
        private long _nextOlderMessageCursor;
        private bool _isLoadingOlderMessages;
        private bool _isEditing;
        private bool _isIncomingDirection;
        private bool _isAiSummaryBusy;
        private bool _isAiChatBusy;

        public GroupsPage()
        {
            InitializeComponent();
            MessagesListView.ItemsSource = _currentMessages;
            GroupMembersListView.ItemsSource = _currentMembers;
            AiChatListView.ItemsSource = _aiChatMessages;
            AiChatPendingImagesItems.ItemsSource = _aiChatPendingImages;
            _ = ThreePaneResizeController.Attach(
                RootThreePaneGrid,
                LeftPaneColumn, LeftPaneSplitter,
                CenterPaneColumn, RightPaneSplitter,
                RightPaneColumn,
                leftDefault: 268, leftMin: 220, leftMax: 420,
                centerMin: 450,
                rightDefault: 356, rightMin: 290, rightMax: 520);
            _ = ComposerResizeController.Attach(ChatStageGrid, ComposerRow, ComposerSplitter, minHeight: 128);
            Loaded += GroupsPage_Loaded;
            Unloaded += GroupsPage_Unloaded;
        }

        private void GroupsPage_Loaded(object sender, RoutedEventArgs e)
        {
            AppServices.Settings.ThemeChanged += OnSettingsThemeChanged;
            AppServices.WeChat4Sync.SyncCompleted += OnWeChatSyncCompleted;
            UpdateRightTabVisuals();
            UpdateDirectionToggleVisuals();
        }

        private void GroupsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            AppServices.Settings.ThemeChanged -= OnSettingsThemeChanged;
            AppServices.WeChat4Sync.SyncCompleted -= OnWeChatSyncCompleted;
            _chatImageHydrationCts?.Cancel();
            _chatImageHydrationCts?.Dispose();
            _chatImageHydrationCts = null;
            _groupAvatarHydrationCts?.Cancel();
            _groupAvatarHydrationCts?.Dispose();
            _groupAvatarHydrationCts = null;
            _memberAvatarHydrationCts?.Cancel();
            _memberAvatarHydrationCts?.Dispose();
            _memberAvatarHydrationCts = null;
        }

        private void OnWeChatSyncCompleted(Client.Services.WeChat4Db.WeChat4SyncReport report)
        {
            if (report.GroupsCreated == 0 && report.GroupsUpdated == 0 && report.GroupMessagesInserted == 0 && report.AvatarsUploaded == 0)
                return;
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await LoadGroupsAsync(_selectedGroup?.Id);
                    if (_selectedGroup is not null)
                    {
                        await LoadGroupMessagesAsync(_selectedGroup);
                    }
                }
                catch { }
            });
        }

        private async void SyncActiveGroupsNowButton_Click(object sender, RoutedEventArgs e)
        {
            SyncActiveGroupsNowButton.IsEnabled = false;
            try
            {
                ShowDetailStatus("正在同步已导入群聊的最新群消息、群成员与群头像...");
                var (ok, msg, _) = await AppServices.WeChat4Sync.SyncImportedTargetsAsync(
                    syncCustomers: false,
                    syncGroups: true);
                await LoadGroupsAsync(_selectedGroup?.Id);
                if (_selectedGroup is not null)
                {
                    await LoadGroupMessagesAsync(_selectedGroup);
                }
                if (!ok)
                {
                    ShowDetailStatus(msg, isError: true);
                    return;
                }
                ShowDetailStatus($"✓ {msg}");
            }
            catch (Exception ex)
            {
                ShowDetailStatus($"同步群聊失败：{ex.Message}", isError: true);
            }
            finally
            {
                SyncActiveGroupsNowButton.IsEnabled = true;
            }
        }

        private void OnSettingsThemeChanged(bool _)
        {
            UpdateRightTabVisuals();
            UpdateDirectionToggleVisuals();
            foreach (var g in _filteredGroups) g.NotifyRowVisualsChanged();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            try { await LoadGroupsAsync(); }
            catch (Exception ex) { AppServices.Logs.Error("UI", $"加载群组列表异常：{ex.Message}"); }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (GroupsListView is null) return;
            ApplyFilter(_selectedGroup?.Id, forceReloadDetail: false);
        }

        private async Task LoadGroupsAsync(long? selectId = null)
        {
            try
            {
                _allGroups = await AppServices.Backend.ListAllGroupsAsync(1000);
                var validIds = _allGroups.Select(g => g.Id).ToHashSet();
                _selectedGroupIds.RemoveWhere(id => !validIds.Contains(id));
                ApplyFilter(selectId, forceReloadDetail: true);
                StartGroupAvatarHydration(_allGroups);
            }
            catch (Exception ex)
            {
                ShowDetailStatus($"拉取群组列表失败：{ex.Message}", isError: true);
            }
        }

        private void StartGroupAvatarHydration(List<GroupDto> groups)
        {
            _groupAvatarHydrationCts?.Cancel();
            _groupAvatarHydrationCts?.Dispose();
            var cts = new CancellationTokenSource();
            _groupAvatarHydrationCts = cts;
            _ = HydrateGroupAvatarsAsync(groups, cts.Token);
        }

        private async Task HydrateGroupAvatarsAsync(List<GroupDto> groups, CancellationToken ct)
        {
            try
            {
                foreach (var g in groups)
                {
                    if (ct.IsCancellationRequested) return;
                    if (string.IsNullOrWhiteSpace(g.AvatarUrl) || g.AvatarImage is not null) continue;
                    byte[]? bytes = await AppServices.Backend.DownloadAvatarBytesAsync(g.AvatarUrl, ct);
                    if (bytes is null || ct.IsCancellationRequested) continue;
                    var img = await AvatarImageService.BytesToBitmapImageAsync(bytes);
                    if (img is not null && !ct.IsCancellationRequested)
                    {
                        g.AvatarImage = img;
                        if (_selectedGroup?.Id == g.Id)
                        {
                            ApplyDetailGroupAvatarVisual(g);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppServices.Logs.Warn("UI", $"加载群头像异常：{ex.Message}");
            }
        }

        private void ApplyDetailGroupAvatarVisual(GroupDto g)
        {
            if (g.AvatarImage is not null)
            {
                DetailAvatarImage.Source = g.AvatarImage;
                DetailAvatarImage.Visibility = Visibility.Visible;
                DetailAvatarText.Visibility = Visibility.Collapsed;
            }
            else
            {
                DetailAvatarImage.Source = null;
                DetailAvatarImage.Visibility = Visibility.Collapsed;
                DetailAvatarText.Visibility = Visibility.Visible;
            }
        }

        private void ApplyFilter(long? preferredSelectId = null, bool forceReloadDetail = false)
        {
            string q = SearchBox.Text.Trim();
            _filteredGroups = string.IsNullOrEmpty(q)
                ? [.. _allGroups]
                : _allGroups.Where(g =>
                    g.GroupName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    g.MembersText.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    g.Note.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

            GroupDto? nextSelected = null;
            if (_filteredGroups.Count > 0)
            {
                if (preferredSelectId.HasValue)
                {
                    nextSelected = _filteredGroups.FirstOrDefault(g => g.Id == preferredSelectId.Value);
                }
                nextSelected ??= _selectedGroup is not null
                    ? _filteredGroups.FirstOrDefault(g => g.Id == _selectedGroup.Id)
                    : null;
                nextSelected ??= _filteredGroups[0];
            }

            bool selectionChanged = _selectedGroup?.Id != nextSelected?.Id;
            _selectedGroup = nextSelected;

            SyncGroupRowStates();
            GroupsListView.ItemsSource = _filteredGroups;
            GroupsListView.Visibility = _filteredGroups.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            EmptyListHint.Visibility = _filteredGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateGroupMultiSelectSummary();

            if (selectionChanged || forceReloadDetail)
            {
                _ = ActivateSelectedGroupAsync(nextSelected);
            }
        }

        private void SyncGroupRowStates()
        {
            foreach (var g in _allGroups)
            {
                g.IsSelectionMode = _isGroupMultiSelectMode;
                g.IsChecked = _isGroupMultiSelectMode && _selectedGroupIds.Contains(g.Id);
                g.IsSelectedRow = _selectedGroup?.Id == g.Id;
            }
        }

        private async void GroupsListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not GroupDto group) return;

            if (_isGroupMultiSelectMode)
            {
                if (!_selectedGroupIds.Add(group.Id))
                {
                    _selectedGroupIds.Remove(group.Id);
                }
                group.IsChecked = _selectedGroupIds.Contains(group.Id);
                UpdateGroupMultiSelectSummary();
                return;
            }

            if (_selectedGroup?.Id == group.Id) return;
            _selectedGroup = group;
            SyncGroupRowStates();
            await ActivateSelectedGroupAsync(group);
        }

        private async Task ActivateSelectedGroupAsync(GroupDto? selected)
        {
            SetMultiSelectMode(false);
            SetEditMode(false);
            ClearPendingChatImage();
            DetailStatusBanner.Visibility = Visibility.Collapsed;
            UpdateSelectedGroupUi();

            if (selected is not null)
            {
                LoadGroupAiWorkspace(selected);
                try
                {
                    await LoadGroupMembersAsync(selected);
                    await LoadGroupMessagesAsync(selected);
                }
                catch (Exception ex) { AppServices.Logs.Error("UI", $"加载群成员/群聊记录异常：{ex.Message}"); }
                UpdateAiContextLabels();
            }
            else
            {
                _currentMembers.Clear();
                _currentMessages.Clear();
                _nextOlderMessageCursor = 0;
                LoadOlderMessagesBar.Visibility = Visibility.Collapsed;
                _aiChatMessages.Clear();
                _currentAiWorkspace = null;
                MultiSelectMessagesButton.IsEnabled = false;
                EmptyChatTitle.Text = "暂无群聊记录";
                EmptyChatHint.Visibility = Visibility.Visible;
            }
        }

        private void SetGroupMultiSelectMode(bool enabled)
        {
            _isGroupMultiSelectMode = enabled;
            GroupMultiSelectBar.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            MultiSelectGroupsIcon.Foreground = (Brush)Application.Current.Resources[
                enabled ? "BrandPrimaryBrush" : "TextSecondaryBrush"];
            if (!enabled)
            {
                _selectedGroupIds.Clear();
            }
            SyncGroupRowStates();
            UpdateGroupMultiSelectSummary();
        }

        private void UpdateGroupMultiSelectSummary()
        {
            int selectedCount = _filteredGroups.Count(g => _selectedGroupIds.Contains(g.Id));
            SelectedGroupsCountText.Text = $"已选 {selectedCount}";
            DeleteSelectedGroupsButton.IsEnabled = selectedCount > 0;
            DeleteSelectedGroupsButton.Content = selectedCount > 0 ? $"删除({selectedCount})" : "删除";
            SelectAllGroupsButton.Content = _filteredGroups.Count > 0 && selectedCount == _filteredGroups.Count
                ? "全不选"
                : "全选";
        }

        private void MultiSelectGroupsButton_Click(object sender, RoutedEventArgs e) =>
            SetGroupMultiSelectMode(!_isGroupMultiSelectMode);

        private void ExitGroupMultiSelectButton_Click(object sender, RoutedEventArgs e) =>
            SetGroupMultiSelectMode(false);

        private void SelectAllGroupsButton_Click(object sender, RoutedEventArgs e)
        {
            bool selectAll = _filteredGroups.Any(g => !_selectedGroupIds.Contains(g.Id));
            foreach (var g in _filteredGroups)
            {
                if (selectAll) _selectedGroupIds.Add(g.Id);
                else _selectedGroupIds.Remove(g.Id);
            }
            SyncGroupRowStates();
            UpdateGroupMultiSelectSummary();
        }

        private void GroupRowCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox cb || cb.DataContext is not GroupDto group) return;
            if (cb.IsChecked == true) _selectedGroupIds.Add(group.Id);
            else _selectedGroupIds.Remove(group.Id);
            group.IsChecked = cb.IsChecked == true;
            UpdateGroupMultiSelectSummary();
        }

        private void MultiSelectGroupRowMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not GroupDto group) return;
            if (!_isGroupMultiSelectMode) SetGroupMultiSelectMode(true);
            _selectedGroupIds.Add(group.Id);
            SyncGroupRowStates();
            UpdateGroupMultiSelectSummary();
        }

        private async void DeleteGroupRowMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not GroupDto group) return;
            await ConfirmAndDeleteGroupsAsync([group]);
        }

        private async void DeleteSelectedGroupsButton_Click(object sender, RoutedEventArgs e)
        {
            var targets = _filteredGroups.Where(g => _selectedGroupIds.Contains(g.Id)).ToList();
            if (targets.Count == 0) return;
            await ConfirmAndDeleteGroupsAsync(targets);
        }

        private void UpdateSelectedGroupUi()
        {
            var g = _selectedGroup;
            bool hasGroup = g is not null;

            SendMessageButton.IsEnabled = hasGroup;
            UploadChatImageButton.IsEnabled = hasGroup;
            EditGroupButton.IsEnabled = hasGroup;
            DeleteGroupButton.IsEnabled = hasGroup;
            EmptyDetailHint.Visibility = hasGroup ? Visibility.Collapsed : Visibility.Visible;
            UpdateRightTabVisibility();

            if (g is null)
            {
                ActiveGroupTitle.Text = "请选择左侧群组";
                ActiveGroupMeta.Text = "点击左上角「+」选择要管理的微信群，导入后自动巡检监听新消息";
                return;
            }

            ActiveGroupTitle.Text = g.DisplayTitleWithCount;
            ActiveGroupMeta.Text = string.IsNullOrWhiteSpace(g.Note)
                ? $"群组 ID #{g.Id} · 成员 {g.MemberCount} 人" + (!string.IsNullOrWhiteSpace(g.MyNickname) ? $" · 我在本群：{g.MyNickname}" : "")
                : g.Note;

            DetailAvatarText.Text = g.AvatarInitial;
            ApplyDetailGroupAvatarVisual(g);
            DetailHeaderNameText.Text = g.DisplayName;
            DetailHeaderCountText.Text = $"成员人数：{(g.MemberCount > 0 ? $"{g.MemberCount} 人" : "未填写")}";
            DetailHeaderKeyText.Text = !string.IsNullOrWhiteSpace(g.MyNickname)
                ? $"我在本群：{g.MyNickname}"
                : $"群标识：{(string.IsNullOrWhiteSpace(g.GroupKey) ? "-" : g.GroupKey)}";
            ViewMembersText.Text = string.IsNullOrWhiteSpace(g.MembersText) ? "暂无已建档成员，点击顶部「刷新同步」即可拉取群成员。" : g.MembersText;

            if (string.IsNullOrWhiteSpace(g.Note))
            {
                ViewGroupNoteText.Text = "暂无员工群备注，点击上方编辑图标即可补充群属性与跟进计划。";
                ViewGroupNoteText.Foreground = (Brush)Application.Current.Resources["TextMutedBrush"];
            }
            else
            {
                ViewGroupNoteText.Text = g.Note;
                ViewGroupNoteText.Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"];
            }

            PopulateEditBoxes(g);
        }

        private void PopulateEditBoxes(GroupDto g)
        {
            EditGroupNameBox.Text = g.GroupName;
            EditMyNicknameBox.Text = g.MyNickname;
            EditGroupKeyBox.Text = g.GroupKey;
            EditMemberCountBox.Text = g.MemberCount > 0 ? g.MemberCount.ToString() : string.Empty;
            EditMembersTextBox.Text = g.MembersText;
            EditGroupNoteBox.Text = g.Note;
        }

        private void SetEditMode(bool editing)
        {
            _isEditing = editing;
            ViewActionsBar.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
            EditActionsBar.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
            DetailViewModePanel.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
            DetailEditModePanel.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        }

        private void EditGroupButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGroup is null) return;
            PopulateEditBoxes(_selectedGroup);
            DetailStatusBanner.Visibility = Visibility.Collapsed;
            SetEditMode(true);
        }

        private void CancelEditButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGroup is not null)
            {
                PopulateEditBoxes(_selectedGroup);
            }
            SetEditMode(false);
        }

        private async void SaveGroupButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGroup is null) return;
            string name = EditGroupNameBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                ShowDetailStatus("群组名称不能为空。", isError: true);
                return;
            }

            _ = int.TryParse(EditMemberCountBox.Text.Trim(), out int count);
            SaveGroupButton.IsEnabled = false;
            try
            {
                var res = await AppServices.Backend.UpdateGroupAsync(_selectedGroup.Id, new CreateGroupRequest
                {
                    GroupName = name,
                    GroupKey = EditGroupKeyBox.Text.Trim(),
                    MyNickname = EditMyNicknameBox.Text.Trim(),
                    MemberCount = Math.Max(0, count),
                    MembersText = EditMembersTextBox.Text.Trim(),
                    Note = EditGroupNoteBox.Text.Trim()
                });

                if (res.IsSuccess && res.Data is not null)
                {
                    long id = res.Data.Id;
                    SetEditMode(false);
                    await LoadGroupsAsync(id);
                    ShowDetailStatus("✓ 群组信息与员工备注已保存。");
                }
                else
                {
                    ShowDetailStatus($"保存失败：{res.ErrorMessage}", isError: true);
                }
            }
            finally
            {
                SaveGroupButton.IsEnabled = true;
            }
        }

        private async void DeleteGroupButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGroup is null) return;
            await ConfirmAndDeleteGroupsAsync([_selectedGroup]);
        }

        private async Task ConfirmAndDeleteGroupsAsync(List<GroupDto> targets)
        {
            if (targets.Count == 0 || !AppServices.TryBeginModalDialog()) return;

            try
            {
                bool isBatch = targets.Count > 1;
                var confirm = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    RequestedTheme = AppServices.Settings.CurrentElementTheme,
                    Title = isBatch ? "确认批量删除群组" : "确认删除群组",
                    Content = isBatch
                        ? $"确定要删除已选中的 {targets.Count} 个群组及其全部群聊记录吗？删除后无法恢复。"
                        : $"确定要删除群组「{targets[0].DisplayName}」及其全部群聊记录吗？删除后无法恢复。",
                    PrimaryButtonText = isBatch ? $"确认删除 ({targets.Count})" : "确认删除",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    PrimaryButtonStyle = (Style)Application.Current.Resources["DangerOutlineButtonStyle"]
                };

                var choice = await confirm.ShowAsync();
                if (choice != ContentDialogResult.Primary) return;

                int deleted = 0;
                int failed = 0;
                string lastError = string.Empty;
                var deletedIds = new HashSet<long>();

                foreach (var target in targets)
                {
                    var res = await AppServices.Backend.DeleteGroupAsync(target.Id);
                    if (res.IsSuccess)
                    {
                        deleted++;
                        deletedIds.Add(target.Id);
                        _selectedGroupIds.Remove(target.Id);
                    }
                    else
                    {
                        failed++;
                        lastError = res.ErrorMessage;
                    }
                }

                if (_isGroupMultiSelectMode && deleted > 0)
                {
                    SetGroupMultiSelectMode(false);
                }

                long? keepId = _selectedGroup is not null && !deletedIds.Contains(_selectedGroup.Id)
                    ? _selectedGroup.Id
                    : null;

                SetEditMode(false);
                await LoadGroupsAsync(keepId);

                if (_selectedGroup is not null)
                {
                    if (failed == 0)
                    {
                        ShowDetailStatus(isBatch
                            ? $"✓ 已批量删除 {deleted} 个群组。"
                            : $"已删除群组「{targets[0].DisplayName}」。");
                    }
                    else
                    {
                        ShowDetailStatus($"已删除 {deleted} 个群组，{failed} 个失败：{lastError}", isError: true);
                    }
                }
            }
            catch (Exception ex)
            {
                ShowDetailStatus($"删除异常：{ex.Message}", isError: true);
            }
            finally
            {
                AppServices.EndModalDialog();
            }
        }

        private async void AddGroupButton_Click(object sender, RoutedEventArgs e)
        {
            if (!AppServices.TryBeginModalDialog()) return;
            try
            {
                var dialog = new ImportWeChatGroupsDialog(_allGroups)
                {
                    XamlRoot = XamlRoot
                };
                await dialog.ShowAsync();

                if (dialog.CompletedReport is { } report)
                {
                    await LoadGroupsAsync(_selectedGroup?.Id);
                    if (_selectedGroup is not null)
                    {
                        await LoadGroupMessagesAsync(_selectedGroup);
                    }
                    ShowDetailStatus($"✓ 已导入微信群聊：新增 {report.GroupsCreated} 个 / 更新 {report.GroupsUpdated} 个，同步群消息 +{report.GroupMessagesInserted} 条。");
                }
            }
            catch (Exception ex)
            {
                ShowDetailStatus($"打开添加群聊弹窗失败：{ex.Message}", isError: true);
            }
            finally
            {
                AppServices.EndModalDialog();
            }
        }

        // ---- Group Members Loading, Avatar Hydration & Chat Sender Matching ----

        private async Task LoadGroupMembersAsync(GroupDto group)
        {
            var members = await AppServices.Backend.ListAllGroupMembersAsync(group.Id);
            if (_selectedGroup?.Id != group.Id) return;

            _currentMembers.Clear();
            foreach (var m in members.AsEnumerable().Reverse())
            {
                _currentMembers.Add(m);
            }

            bool hasStructured = _currentMembers.Count > 0;
            StructuredMembersHeaderText.Text = $"已建档群成员 ({_currentMembers.Count})";
            GroupMembersListView.Visibility = hasStructured ? Visibility.Visible : Visibility.Collapsed;
            MembersTextFallbackCard.Visibility = hasStructured ? Visibility.Collapsed : Visibility.Visible;

            if (hasStructured)
            {
                StartMemberAvatarHydration(_currentMembers.ToList());
            }
            UpdateAiContextLabels();
        }

        private void StartMemberAvatarHydration(List<GroupMemberDto> members)
        {
            _memberAvatarHydrationCts?.Cancel();
            _memberAvatarHydrationCts?.Dispose();
            var cts = new CancellationTokenSource();
            _memberAvatarHydrationCts = cts;
            _ = HydrateGroupMemberAvatarsAsync(members, cts.Token);
        }

        private async Task HydrateGroupMemberAvatarsAsync(List<GroupMemberDto> members, CancellationToken ct)
        {
            try
            {
                foreach (var m in members)
                {
                    if (ct.IsCancellationRequested) return;
                    if (string.IsNullOrWhiteSpace(m.AvatarUrl) || m.AvatarImage is not null) continue;
                    byte[]? bytes = await AppServices.Backend.DownloadAvatarBytesAsync(m.AvatarUrl, ct);
                    if (bytes is null || ct.IsCancellationRequested) continue;
                    var img = await AvatarImageService.BytesToBitmapImageAsync(bytes);
                    if (img is not null && !ct.IsCancellationRequested)
                    {
                        m.AvatarImage = img;
                        ApplyMemberAvatarToMatchingBubbles(m, img);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppServices.Logs.Warn("UI", $"加载群成员头像异常：{ex.Message}");
            }
        }

        private void ApplyMemberAvatarToMatchingBubbles(GroupMemberDto member, ImageSource avatar)
        {
            foreach (var bubble in _currentMessages)
            {
                if (bubble.IsOutgoing || bubble.SenderAvatar is not null) continue;
                if (IsMemberMatchingSender(member, bubble.SenderName))
                {
                    bubble.SenderAvatar = avatar;
                }
            }
        }

        private GroupMemberDto? MatchGroupMember(string senderName)
        {
            if (string.IsNullOrWhiteSpace(senderName) || _currentMembers.Count == 0) return null;
            string s = senderName.Trim();

            // 1. Exact match
            foreach (var m in _currentMembers)
            {
                if ((!string.IsNullOrWhiteSpace(m.GroupNickname) && m.GroupNickname.Equals(s, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(m.WechatRemark) && m.WechatRemark.Equals(s, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(m.WechatName) && m.WechatName.Equals(s, StringComparison.OrdinalIgnoreCase)))
                {
                    return m;
                }
            }

            // 2. Normalized / fuzzy prefix match (handles OCR punctuation & truncated ellipsis names)
            foreach (var m in _currentMembers)
            {
                if (IsMemberMatchingSender(m, s)) return m;
            }
            return null;
        }

        private static bool IsMemberMatchingSender(GroupMemberDto m, string senderName)
        {
            string normSender = NormalizeNameForMatch(senderName);
            if (normSender.Length == 0) return false;

            string[] candidates = [m.GroupNickname, m.WechatRemark, m.WechatName];
            foreach (string raw in candidates)
            {
                string norm = NormalizeNameForMatch(raw);
                if (norm.Length == 0) continue;
                if (norm.Equals(normSender, StringComparison.OrdinalIgnoreCase)) return true;
                if (norm.Length >= 2 && normSender.StartsWith(norm, StringComparison.OrdinalIgnoreCase)) return true;
                if (normSender.Length >= 2 && norm.StartsWith(normSender, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string NormalizeNameForMatch(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var sb = new System.Text.StringBuilder(raw.Length);
            foreach (char ch in raw.Trim())
            {
                if (char.IsLetterOrDigit(ch) || (ch >= 0x4E00 && ch <= 0x9FFF))
                {
                    sb.Append(char.ToLowerInvariant(ch));
                }
            }
            return sb.ToString();
        }

        private async void GroupMembersListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not GroupMemberDto m || !AppServices.TryBeginModalDialog()) return;
            try
            {
                var panel = new StackPanel { Width = 360, Spacing = 10 };

                var headerGrid = new Grid { ColumnSpacing = 12 };
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var avatarBox = new Border
                {
                    Width = 54,
                    Height = 54,
                    CornerRadius = new CornerRadius(10),
                    Background = (Brush)Application.Current.Resources["BrandSoftBgBrush"],
                    BorderBrush = (Brush)Application.Current.Resources["BrandSoftBorderBrush"],
                    BorderThickness = new Thickness(1),
                    Child = m.AvatarImage is not null
                        ? (UIElement)new Image { Source = m.AvatarImage, Stretch = Stretch.UniformToFill }
                        : new TextBlock
                        {
                            Text = m.AvatarInitial,
                            FontSize = 20,
                            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                            Foreground = (Brush)Application.Current.Resources["BrandPrimaryBrush"],
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                };
                var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
                nameStack.Children.Add(new TextBlock
                {
                    Text = m.DisplayName + (!string.IsNullOrWhiteSpace(m.Gender) ? $"  ({m.Gender})" : ""),
                    FontSize = 15.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                    TextWrapping = TextWrapping.Wrap
                });
                nameStack.Children.Add(new TextBlock
                {
                    Text = $"微信号：{(string.IsNullOrWhiteSpace(m.WechatAccount) ? "-" : m.WechatAccount)}",
                    FontSize = 12,
                    Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
                    IsTextSelectionEnabled = true
                });
                Grid.SetColumn(avatarBox, 0);
                Grid.SetColumn(nameStack, 1);
                headerGrid.Children.Add(avatarBox);
                headerGrid.Children.Add(nameStack);
                panel.Children.Add(headerGrid);

                panel.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["AppBorderSubtleBrush"] });

                void AddRow(string label, string val)
                {
                    if (string.IsNullOrWhiteSpace(val)) return;
                    var row = new Grid { ColumnSpacing = 10 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var l = new TextBlock { Text = label, FontSize = 12.5, Foreground = (Brush)Application.Current.Resources["TextMutedBrush"] };
                    var v = new TextBlock { Text = val, FontSize = 13, Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"], TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                    Grid.SetColumn(l, 0);
                    Grid.SetColumn(v, 1);
                    row.Children.Add(l);
                    row.Children.Add(v);
                    panel.Children.Add(row);
                }

                AddRow("群昵称", m.GroupNickname);
                AddRow("微信昵称", m.WechatName);
                AddRow("朋友备注", m.WechatRemark);
                AddRow("地区", m.Region);
                AddRow("电话", m.Phone);
                AddRow("个性签名", m.Signature);
                AddRow("备注", m.Note);

                var dlg = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    RequestedTheme = AppServices.Settings.CurrentElementTheme,
                    Title = "群成员档案",
                    Content = panel,
                    CloseButtonText = "关闭",
                    DefaultButton = ContentDialogButton.Close
                };
                await dlg.ShowAsync();
            }
            finally
            {
                AppServices.EndModalDialog();
            }
        }

        private async Task LoadGroupMessagesAsync(GroupDto group)
        {
            _nextOlderMessageCursor = 0;
            _isLoadingOlderMessages = false;
            LoadOlderMessagesBar.Visibility = Visibility.Collapsed;

            var res = await AppServices.Backend.ListGroupMessagesAsync(group.Id, limit: 100);
            if (_selectedGroup?.Id != group.Id) return;

            _currentMessages.Clear();
            SetMultiSelectMode(false);

            if (!res.IsSuccess || res.Data is null)
            {
                EmptyChatTitle.Text = $"加载群消息失败：{res.ErrorMessage}";
                EmptyChatHint.Visibility = Visibility.Visible;
                MultiSelectMessagesButton.IsEnabled = false;
                return;
            }

            _nextOlderMessageCursor = res.Data.NextCursor;
            UpdateLoadOlderMessagesBar();

            foreach (var m in res.Data.Items.AsEnumerable().Reverse())
            {
                if (_locallyDeletedMessageIds.Contains(m.Id)) continue;
                _currentMessages.Add(BuildGroupBubbleItem(m));
            }

            StartChatImageHydration(_currentMessages.ToList());
            RefreshMessageListEmptyState(group.DisplayName);
            if (_currentMessages.Count > 0)
            {
                MessagesListView.ScrollIntoView(_currentMessages[^1]);
            }
        }

        private ChatBubbleItem BuildGroupBubbleItem(GroupMessageDto m)
        {
            string sender = string.IsNullOrWhiteSpace(m.SenderName) ? "群成员" : m.SenderName;
            var matchedMember = !m.IsOutgoing ? MatchGroupMember(sender) : null;
            string displaySender = matchedMember is not null ? matchedMember.DisplayName : sender;
            return new ChatBubbleItem
            {
                Id = m.Id,
                SenderName = displaySender,
                SenderInitial = displaySender[..1].ToUpperInvariant(),
                SenderAvatar = matchedMember?.AvatarImage,
                Content = m.Content,
                ImageUrl = m.ImageUrl,
                AudioUrl = m.AudioUrl,
                VideoUrl = m.VideoUrl,
                TimeLabel = m.TimeLabel,
                IsOutgoing = m.IsOutgoing,
                IsSelectionMode = _isMultiSelectMode
            };
        }

        private void UpdateLoadOlderMessagesBar()
        {
            LoadOlderMessagesBar.Visibility = _nextOlderMessageCursor > 0 ? Visibility.Visible : Visibility.Collapsed;
            LoadOlderMessagesButton.IsEnabled = !_isLoadingOlderMessages && _nextOlderMessageCursor > 0;
            LoadOlderMessagesText.Text = _isLoadingOlderMessages ? "正在加载更早记录..." : "查看更早群聊记录";
        }

        private async void LoadOlderMessagesButton_Click(object sender, RoutedEventArgs e)
        {
            var group = _selectedGroup;
            if (group is null || _isLoadingOlderMessages || _nextOlderMessageCursor <= 0) return;

            _isLoadingOlderMessages = true;
            UpdateLoadOlderMessagesBar();
            try
            {
                var anchor = _currentMessages.Count > 0 ? _currentMessages[0] : null;
                var res = await AppServices.Backend.ListGroupMessagesAsync(group.Id, limit: 100, cursor: _nextOlderMessageCursor);
                if (_selectedGroup?.Id != group.Id) return;

                if (res.IsSuccess && res.Data is not null)
                {
                    var existingIds = _currentMessages.Where(x => x.Id > 0).Select(x => x.Id).ToHashSet();
                    var olderBubbles = new List<ChatBubbleItem>();
                    foreach (var m in res.Data.Items.AsEnumerable().Reverse())
                    {
                        if (_locallyDeletedMessageIds.Contains(m.Id) || existingIds.Contains(m.Id)) continue;
                        olderBubbles.Add(BuildGroupBubbleItem(m));
                    }

                    for (int i = 0; i < olderBubbles.Count; i++)
                    {
                        _currentMessages.Insert(i, olderBubbles[i]);
                    }

                    _nextOlderMessageCursor = res.Data.NextCursor;
                    if (olderBubbles.Count > 0)
                    {
                        _ = HydrateChatBubbleImagesAsync(olderBubbles, _chatImageHydrationCts?.Token ?? CancellationToken.None);
                        if (anchor is not null)
                        {
                            MessagesListView.ScrollIntoView(anchor, ScrollIntoViewAlignment.Leading);
                        }
                    }
                    UpdateAiContextLabels();
                }
            }
            catch (Exception ex)
            {
                AppServices.Logs.Warn("UI", $"加载更早群聊记录失败：{ex.Message}");
            }
            finally
            {
                _isLoadingOlderMessages = false;
                UpdateLoadOlderMessagesBar();
            }
        }

        private void StartChatImageHydration(List<ChatBubbleItem> bubbles)
        {
            _chatImageHydrationCts?.Cancel();
            _chatImageHydrationCts?.Dispose();
            var cts = new CancellationTokenSource();
            _chatImageHydrationCts = cts;
            _ = HydrateChatBubbleImagesAsync(bubbles, cts.Token);
        }

        private async Task HydrateChatBubbleImagesAsync(List<ChatBubbleItem> bubbles, CancellationToken ct)
        {
            try
            {
                foreach (var b in bubbles)
                {
                    if (ct.IsCancellationRequested) return;
                    if (string.IsNullOrWhiteSpace(b.ImageUrl) || b.BubbleImage is not null) continue;
                    byte[]? bytes = await AppServices.Backend.DownloadChatImageBytesAsync(b.ImageUrl, ct);
                    if (bytes is null || ct.IsCancellationRequested) continue;
                    var img = await ChatImageHelper.BytesToBitmapImageAsync(bytes);
                    if (img is not null && !ct.IsCancellationRequested)
                    {
                        b.BubbleImageBytes = bytes;
                        b.BubbleImage = img;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppServices.Logs.Warn("UI", $"加载群聊图片异常：{ex.Message}");
            }
        }

        private void RefreshMessageListEmptyState(string groupName)
        {
            bool hasMessages = _currentMessages.Count > 0;
            EmptyChatTitle.Text = $"「{groupName}」暂无群聊记录";
            EmptyChatHint.Visibility = hasMessages ? Visibility.Collapsed : Visibility.Visible;
            MultiSelectMessagesButton.IsEnabled = hasMessages;
            if (!hasMessages && _isMultiSelectMode)
            {
                SetMultiSelectMode(false);
            }
            UpdateAiContextLabels();
        }

        private void SetMultiSelectMode(bool enabled)
        {
            _isMultiSelectMode = enabled;
            MessagesListView.IsItemClickEnabled = enabled;
            MessageMultiSelectBar.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            MultiSelectMessagesButton.Content = enabled ? "取消多选" : "多选";
            foreach (var item in _currentMessages)
            {
                item.IsSelectionMode = enabled;
                if (!enabled) item.IsSelected = false;
            }
            UpdateMultiSelectSummary();
        }

        private void UpdateMultiSelectSummary()
        {
            int selectedCount = _currentMessages.Count(m => m.IsSelected);
            SelectedMessagesCountText.Text = $"已选 {selectedCount} 条消息";
            DeleteSelectedMessagesButton.IsEnabled = selectedCount > 0;
            DeleteSelectedMessagesButton.Content = selectedCount > 0 ? $"删除所选 ({selectedCount})" : "删除所选";
            SelectAllMessagesButton.Content = _currentMessages.Count > 0 && selectedCount == _currentMessages.Count ? "取消全选" : "全选";
        }

        private void MultiSelectMessagesButton_Click(object sender, RoutedEventArgs e) =>
            SetMultiSelectMode(!_isMultiSelectMode);

        private void ExitMultiSelectButton_Click(object sender, RoutedEventArgs e) =>
            SetMultiSelectMode(false);

        private void SelectAllMessagesButton_Click(object sender, RoutedEventArgs e)
        {
            bool selectAll = _currentMessages.Any(m => !m.IsSelected);
            foreach (var item in _currentMessages) item.IsSelected = selectAll;
            UpdateMultiSelectSummary();
        }

        private void BubbleCheckBox_Click(object sender, RoutedEventArgs e) =>
            UpdateMultiSelectSummary();

        private void MessagesListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (!_isMultiSelectMode || e.ClickedItem is not ChatBubbleItem item) return;
            item.IsSelected = !item.IsSelected;
            UpdateMultiSelectSummary();
        }

        private async void ChatBubbleImage_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ChatBubbleItem item) return;
            if (_isMultiSelectMode)
            {
                item.IsSelected = !item.IsSelected;
                UpdateMultiSelectSummary();
                return;
            }

            byte[]? bytes = item.BubbleImageBytes;
            if ((bytes is null || bytes.Length == 0) && !string.IsNullOrWhiteSpace(item.ImageUrl))
            {
                bytes = await AppServices.Backend.DownloadChatImageBytesAsync(item.ImageUrl);
                if (bytes is { Length: > 0 })
                {
                    item.BubbleImageBytes = bytes;
                }
            }

            if (bytes is { Length: > 0 })
            {
                string kind = item.IsVideoMessage ? "视频封面" : "群聊图片";
                string title = _selectedGroup is not null
                    ? $"{_selectedGroup.DisplayName} · {kind}"
                    : $"{kind}查看器";
                ImageViewerWindow.ShowImage(bytes, title);
            }
        }

        private void BubbleContextFlyout_Opening(object sender, object e)
        {
            if (sender is not MenuFlyout flyout || flyout.Target is not FrameworkElement target || target.DataContext is not ChatBubbleItem item)
            {
                return;
            }
            foreach (var mi in flyout.Items.OfType<MenuFlyoutItem>())
            {
                if (mi.Tag as string == "VoiceTranscribe")
                {
                    mi.Visibility = item.VoiceTranscribeMenuVisibility;
                    mi.Text = VoiceTranscriptionService.HasSavedTranscript(item.Content) ? "重新转文字" : "语音转文字";
                }
            }
        }

        private async void TranscribeVoiceBubbleMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ChatBubbleItem item) return;
            if (string.IsNullOrWhiteSpace(item.AudioUrl))
            {
                ComposerHintText.Text = "该条语音暂无音频文件，请先点击顶部「刷新同步」。";
                return;
            }

            var group = _selectedGroup;
            ComposerHintText.Text = "正在将语音转为文字...";
            var res = await VoiceTranscriptionService.TranscribeAsync(item.AudioUrl);
            if (!res.Success || string.IsNullOrWhiteSpace(res.Text))
            {
                ComposerHintText.Text = res.ErrorMessage;
                return;
            }

            string mergedContent = VoiceTranscriptionService.MergeTranscriptIntoContent(item.Content, res.Text);
            item.Content = mergedContent;

            if (item.Id > 0 && group is not null)
            {
                string dir = item.IsOutgoing ? "outgoing" : "incoming";
                string senderName = item.IsOutgoing
                    ? (AppServices.Backend.CurrentEmployee?.DisplayName ?? "我")
                    : item.SenderName;
                var patchRes = await AppServices.Backend.UpdateGroupMessageAsync(
                    group.Id, item.Id, senderName, dir, mergedContent, item.ImageUrl);
                if (patchRes.IsSuccess)
                {
                    ComposerHintText.Text = $"✓ 语音转文字已完成并永久保存（{res.EngineLabel}）。";
                }
                else
                {
                    ComposerHintText.Text = $"已识别文字，但保存到服务端失败：{patchRes.ErrorMessage}";
                }
            }
            else
            {
                ComposerHintText.Text = $"✓ 语音转文字已完成（{res.EngineLabel}）。";
            }
        }

        private async void EditBubbleMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ChatBubbleItem item) return;
            var group = _selectedGroup;
            var res = await ChatBubbleEditorHelper.ShowAsync(
                XamlRoot,
                title: "编辑群聊消息",
                primaryButtonText: "保存修改",
                initialIsOutgoing: item.IsOutgoing,
                initialContent: item.Content,
                isGroupChat: true,
                initialSenderName: item.SenderName,
                initialImageUrl: item.ImageUrl,
                initialImagePreview: item.BubbleImage,
                initialImageBytes: item.BubbleImageBytes);
            if (!res.Confirmed) return;

            string finalImageUrl = res.ImageRemoved ? string.Empty : item.ImageUrl;
            ImageSource? finalPreview = res.ImageRemoved ? null : item.BubbleImage;
            byte[]? finalBytes = res.ImageRemoved ? null : item.BubbleImageBytes;

            if (res.NewImageBytes is { Length: > 0 })
            {
                var upRes = await AppServices.Backend.UploadChatImageAsync(res.NewImageBytes);
                if (!upRes.IsSuccess || upRes.Data is null || string.IsNullOrWhiteSpace(upRes.Data.ImageUrl))
                {
                    ComposerHintText.Text = $"上传图片失败：{upRes.ErrorMessage}";
                    return;
                }
                finalImageUrl = upRes.Data.ImageUrl;
                finalPreview = res.ImagePreview;
                finalBytes = res.NewImageBytes;
            }

            item.IsOutgoing = res.IsOutgoing;
            item.SenderName = res.SenderName;
            item.Content = res.Content;
            item.ImageUrl = finalImageUrl;
            item.BubbleImageBytes = finalBytes;
            item.BubbleImage = finalPreview;

            if (item.Id > 0 && group is not null)
            {
                string dir = res.IsOutgoing ? "outgoing" : "incoming";
                string senderName = res.IsOutgoing
                    ? (AppServices.Backend.CurrentEmployee?.DisplayName ?? "我")
                    : res.SenderName;
                _ = await AppServices.Backend.UpdateGroupMessageAsync(group.Id, item.Id, senderName, dir, res.Content, finalImageUrl);
            }
            ComposerHintText.Text = "已修改该条群聊消息。";
        }

        private async void InsertAboveBubbleMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ChatBubbleItem anchor) return;
            await InsertGroupBubbleRelativeToAsync(anchor, insertAfter: false);
        }

        private async void InsertBelowBubbleMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ChatBubbleItem anchor) return;
            await InsertGroupBubbleRelativeToAsync(anchor, insertAfter: true);
        }

        private async Task InsertGroupBubbleRelativeToAsync(ChatBubbleItem anchor, bool insertAfter)
        {
            var group = _selectedGroup;
            if (group is null) return;

            var res = await ChatBubbleEditorHelper.ShowAsync(
                XamlRoot,
                title: insertAfter ? "在下方插入群聊消息" : "在上方插入群聊消息",
                primaryButtonText: "确认插入",
                initialIsOutgoing: anchor.IsOutgoing,
                initialContent: string.Empty,
                isGroupChat: true,
                initialSenderName: anchor.SenderName);
            if (!res.Confirmed) return;

            int idx = _currentMessages.IndexOf(anchor);
            if (idx < 0) return;

            string uploadedUrl = string.Empty;
            if (res.NewImageBytes is { Length: > 0 })
            {
                var upRes = await AppServices.Backend.UploadChatImageAsync(res.NewImageBytes);
                if (upRes.IsSuccess && upRes.Data is not null)
                {
                    uploadedUrl = upRes.Data.ImageUrl;
                }
            }

            string initial = !string.IsNullOrWhiteSpace(res.SenderName) ? res.SenderName[..1].ToUpperInvariant() : "群";
            var newBubble = new ChatBubbleItem
            {
                Id = 0,
                SenderName = res.SenderName,
                SenderInitial = initial,
                Content = res.Content,
                ImageUrl = uploadedUrl,
                BubbleImageBytes = res.NewImageBytes,
                BubbleImage = res.ImagePreview,
                TimeLabel = string.IsNullOrWhiteSpace(anchor.TimeLabel) ? DateTime.Now.ToString("HH:mm") : anchor.TimeLabel,
                IsOutgoing = res.IsOutgoing,
                IsSelectionMode = _isMultiSelectMode
            };

            _currentMessages.Insert(insertAfter ? idx + 1 : idx, newBubble);
            RefreshMessageListEmptyState(group.DisplayName);
            ComposerHintText.Text = insertAfter ? "已在下方插入 1 条群聊消息。" : "已在上方插入 1 条群聊消息。";
        }

        private async void CopyBubbleMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ChatBubbleItem item) return;
            try
            {
                var pkg = new DataPackage();
                if (!string.IsNullOrWhiteSpace(item.Content))
                {
                    pkg.SetText(item.Content);
                    Clipboard.SetContent(pkg);
                    ComposerHintText.Text = "已复制该条消息内容。";
                }
                else if (item.BubbleImageBytes is { Length: > 0 } bytes)
                {
                    var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    await stream.WriteAsync(bytes.AsBuffer());
                    stream.Seek(0);
                    pkg.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(stream));
                    Clipboard.SetContent(pkg);
                    ComposerHintText.Text = "已复制该条消息图片。";
                }
            }
            catch (Exception ex)
            {
                ComposerHintText.Text = $"复制失败：{ex.Message}";
            }
        }

        private void MultiSelectFromBubbleMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ChatBubbleItem item) return;
            if (!_isMultiSelectMode) SetMultiSelectMode(true);
            item.IsSelected = true;
            UpdateMultiSelectSummary();
        }

        private void DeleteBubbleMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ChatBubbleItem item) return;
            RemoveBubblesFromUi([item]);
        }

        private void DeleteSelectedMessagesButton_Click(object sender, RoutedEventArgs e)
        {
            var targets = _currentMessages.Where(m => m.IsSelected).ToList();
            if (targets.Count == 0) return;
            RemoveBubblesFromUi(targets);
            SetMultiSelectMode(false);
        }

        private void RemoveBubblesFromUi(List<ChatBubbleItem> targets)
        {
            foreach (var item in targets)
            {
                if (item.Id > 0) _locallyDeletedMessageIds.Add(item.Id);
                _currentMessages.Remove(item);
            }
            RefreshMessageListEmptyState(_selectedGroup?.DisplayName ?? string.Empty);
            ComposerHintText.Text = $"已移除 {targets.Count} 条群聊记录（待接入服务端删除接口后同步持久化）。";
        }

        private void DirToggleBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                _isIncomingDirection = string.Equals(tag, "incoming", StringComparison.OrdinalIgnoreCase);
                UpdateDirectionToggleVisuals();
            }
        }

        private void UpdateDirectionToggleVisuals()
        {
            var activeBg = (Brush)Application.Current.Resources["BrandSoftBgBrush"];
            var activeFg = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
            var inactiveBg = new SolidColorBrush(Colors.Transparent);
            var inactiveFg = (Brush)Application.Current.Resources["TextSecondaryBrush"];

            DirOutgoingBtn.Background = !_isIncomingDirection ? activeBg : inactiveBg;
            DirOutgoingBtn.Foreground = !_isIncomingDirection ? activeFg : inactiveFg;
            DirOutgoingBtn.FontWeight = !_isIncomingDirection ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

            DirIncomingBtn.Background = _isIncomingDirection ? activeBg : inactiveBg;
            DirIncomingBtn.Foreground = _isIncomingDirection ? activeFg : inactiveFg;
            DirIncomingBtn.FontWeight = _isIncomingDirection ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

            SenderNameBox.Visibility = _isIncomingDirection ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void MessageInputBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter) return;
            var shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
            if ((shiftState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down) return;

            e.Handled = true;
            await SendCurrentGroupMessageAsync();
        }

        private async void SendMessageButton_Click(object sender, RoutedEventArgs e)
        {
            await SendCurrentGroupMessageAsync();
        }

        private void SetPendingChatImage(PendingChatImage? image)
        {
            _pendingChatImage = image;
            if (image is not null)
            {
                PendingChatImageThumb.Source = image.Preview;
                PendingChatImageNameText.Text = $"{image.DisplayName}（点击缩略图可放大）";
                PendingChatImageBar.Visibility = Visibility.Visible;
            }
            else
            {
                PendingChatImageThumb.Source = null;
                PendingChatImageBar.Visibility = Visibility.Collapsed;
            }
        }

        private void ClearPendingChatImage() => SetPendingChatImage(null);

        private void ClearPendingChatImage_Click(object sender, RoutedEventArgs e) => ClearPendingChatImage();

        private void PreviewPendingChatImage_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingChatImage is { Bytes.Length: > 0 } img)
            {
                ImageViewerWindow.ShowImage(img.Bytes, img.DisplayName);
            }
        }

        private async void UploadChatImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGroup is null) return;
            string? path = Win32WindowHelper.PickImageFilePath(AppServices.MainWindowHandle);
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var loaded = await ChatImageHelper.TryLoadFromFileAsync(path);
                if (loaded is not null)
                {
                    SetPendingChatImage(loaded);
                    ComposerHintText.Text = $"已附加图片「{loaded.DisplayName}」，按 Enter 或点发送即可插入。";
                }
                else
                {
                    ComposerHintText.Text = "读取图片失败，请选择 PNG / JPG / WebP 格式。";
                }
            }
            catch (Exception ex)
            {
                ComposerHintText.Text = $"加载图片异常：{ex.Message}";
            }
        }

        private async void MessageInputBox_Paste(object sender, TextControlPasteEventArgs e)
        {
            if (_selectedGroup is null) return;
            try
            {
                var content = Clipboard.GetContent();
                if (content.Contains(StandardDataFormats.Bitmap))
                {
                    e.Handled = true;
                    var loaded = await ChatImageHelper.TryLoadFromClipboardAsync();
                    if (loaded is not null)
                    {
                        SetPendingChatImage(loaded);
                        ComposerHintText.Text = "已从剪贴板附加图片，发送即可插入群聊记录。";
                    }
                }
            }
            catch
            {
            }
        }

        private async Task SendCurrentGroupMessageAsync()
        {
            var target = _selectedGroup;
            if (target is null) return;
            string content = MessageInputBox.Text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            var pendingImage = _pendingChatImage;
            if (string.IsNullOrEmpty(content) && pendingImage is null) return;

            bool isIncoming = _isIncomingDirection;
            string direction = isIncoming ? "incoming" : "outgoing";
            string senderName = isIncoming
                ? (string.IsNullOrWhiteSpace(SenderNameBox.Text) ? "群成员" : SenderNameBox.Text.Trim())
                : (AppServices.Backend.CurrentEmployee?.DisplayName ?? "我");

            SendMessageButton.IsEnabled = false;
            try
            {
                string imageUrl = string.Empty;
                if (pendingImage is not null)
                {
                    ComposerHintText.Text = "正在上传图片...";
                    var upRes = await AppServices.Backend.UploadChatImageAsync(pendingImage.Bytes);
                    if (!upRes.IsSuccess || upRes.Data is null || string.IsNullOrWhiteSpace(upRes.Data.ImageUrl))
                    {
                        ComposerHintText.Text = $"上传图片失败：{upRes.ErrorMessage}";
                        return;
                    }
                    imageUrl = upRes.Data.ImageUrl;
                }

                var res = await AppServices.Backend.CreateGroupMessageAsync(target.Id, senderName, direction, content, imageUrl);
                if (res.IsSuccess)
                {
                    MessageInputBox.Text = string.Empty;
                    ClearPendingChatImage();
                    ComposerHintText.Text = string.Empty;
                    await LoadGroupsAsync(target.Id);
                }
                else
                {
                    ComposerHintText.Text = $"发送失败：{res.ErrorMessage}";
                }
            }
            catch (Exception ex)
            {
                ComposerHintText.Text = $"发送异常：{ex.Message}";
            }
            finally
            {
                SendMessageButton.IsEnabled = _selectedGroup is not null;
            }
        }

        private void ShowDetailStatus(string message, bool isError = false)
        {
            DetailStatusBanner.Background = (Brush)Application.Current.Resources[isError ? "DangerBgBrush" : "SuccessBgBrush"];
            DetailStatusText.Foreground = (Brush)Application.Current.Resources[isError ? "DangerTextBrush" : "SuccessTextBrush"];
            DetailStatusText.Text = message;
            DetailStatusBanner.Visibility = Visibility.Visible;
        }

        // ---- Right Panel 3-Tab Switcher & Group AI Summary / AI Chat ----

        private void RightTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tab)
            {
                _activeRightTab = tab;
                UpdateRightTabVisuals();
                UpdateRightTabVisibility();
            }
        }

        private void UpdateRightTabVisibility()
        {
            bool hasGroup = _selectedGroup is not null;
            ProfileTabContainer.Visibility = hasGroup && _activeRightTab == "profile" ? Visibility.Visible : Visibility.Collapsed;
            AiSummaryTabContainer.Visibility = hasGroup && _activeRightTab == "summary" ? Visibility.Visible : Visibility.Collapsed;
            AiChatTabContainer.Visibility = hasGroup && _activeRightTab == "aichat" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateRightTabVisuals()
        {
            var activeFg = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
            var inactiveFg = (Brush)Application.Current.Resources["TextSecondaryBrush"];
            var transparent = new SolidColorBrush(Colors.Transparent);

            ApplyTabVisual(RightTabProfileIcon, RightTabProfileText, RightTabProfileLine, _activeRightTab == "profile", activeFg, inactiveFg, transparent);
            ApplyTabVisual(RightTabSummaryIcon, RightTabSummaryText, RightTabSummaryLine, _activeRightTab == "summary", activeFg, inactiveFg, transparent);
            ApplyTabVisual(RightTabChatIcon, RightTabChatText, RightTabChatLine, _activeRightTab == "aichat", activeFg, inactiveFg, transparent);
        }

        private static void ApplyTabVisual(FontIcon icon, TextBlock text, Border line, bool active, Brush activeFg, Brush inactiveFg, Brush transparent)
        {
            icon.Foreground = active ? activeFg : inactiveFg;
            text.Foreground = active ? activeFg : inactiveFg;
            text.FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            line.Background = active ? activeFg : transparent;
        }

        private void LoadGroupAiWorkspace(GroupDto group)
        {
            _currentAiWorkspace = AppServices.AiWorkspace.LoadGroup(group.Id);
            _aiChatPendingImages.Clear();
            UpdateGroupAiPendingImagesUi();
            AiSummaryErrorBanner.Visibility = Visibility.Collapsed;
            AiSummaryNoticeText.Visibility = Visibility.Collapsed;
            RenderAiSummaryState();

            _aiChatMessages.Clear();
            foreach (var msg in _currentAiWorkspace.Messages)
            {
                _aiChatMessages.Add(msg);
            }
            UpdateAiChatEmptyState();
            UpdateAiContextLabels();
        }

        private void UpdateAiContextLabels()
        {
            if (_selectedGroup is null) return;
            int memberCount = Math.Max(_currentMembers.Count, _selectedGroup.MemberCount);
            int msgCount = _currentMessages.Count;
            AiSummaryContextText.Text = $"{memberCount} 位成员 · {msgCount} 条对话";
            AiChatContextTitle.Text = $"{_selectedGroup.DisplayName} · {msgCount} 条对话";
        }

        private void RenderAiSummaryState()
        {
            bool hasSummary = _currentAiWorkspace is not null && !string.IsNullOrWhiteSpace(_currentAiWorkspace.SummaryText);
            AiSummaryLoadingPanel.Visibility = Visibility.Collapsed;
            AiSummarySpinner.IsActive = false;
            AiSummaryEmptyPanel.Visibility = hasSummary ? Visibility.Collapsed : Visibility.Visible;
            AiSummaryResultScroll.Visibility = hasSummary ? Visibility.Visible : Visibility.Collapsed;
            AiSummaryFooterBar.Visibility = hasSummary ? Visibility.Visible : Visibility.Collapsed;

            if (hasSummary && _currentAiWorkspace is not null)
            {
                AiSummaryMarkdownView.Text = _currentAiWorkspace.SummaryText;
                AiSummaryTimeText.Text = string.IsNullOrWhiteSpace(_currentAiWorkspace.SummaryUpdatedAt)
                    ? string.Empty
                    : $"更新于 {_currentAiWorkspace.SummaryUpdatedAt}";
            }
        }

        private async void GenerateAiSummaryButton_Click(object sender, RoutedEventArgs e)
        {
            var group = _selectedGroup;
            if (group is null || _isAiSummaryBusy) return;

            _isAiSummaryBusy = true;
            AiSummaryErrorBanner.Visibility = Visibility.Collapsed;
            AiSummaryNoticeText.Visibility = Visibility.Collapsed;
            AiSummaryEmptyPanel.Visibility = Visibility.Collapsed;
            AiSummaryResultScroll.Visibility = Visibility.Collapsed;
            AiSummaryFooterBar.Visibility = Visibility.Collapsed;
            AiSummaryLoadingPanel.Visibility = Visibility.Visible;
            AiSummarySpinner.IsActive = true;

            try
            {
                var res = await AppServices.Backend.SummarizeGroupWithAiAsync(group.Id);
                if (_selectedGroup?.Id != group.Id) return;

                if (res.IsSuccess && res.Data is not null && !string.IsNullOrWhiteSpace(res.Data.Summary))
                {
                    string timestamp = DateTime.Now.ToString("MM-dd HH:mm");
                    _currentAiWorkspace ??= new GroupAiWorkspaceData { GroupId = group.Id };
                    _currentAiWorkspace.SummaryText = res.Data.Summary;
                    _currentAiWorkspace.SummaryUpdatedAt = timestamp;
                    _currentAiWorkspace.SummaryContextMeta = $"{res.Data.MessageCount} 条消息";
                    AppServices.AiWorkspace.SaveGroup(_currentAiWorkspace);
                }
                else
                {
                    AiSummaryErrorText.Text = $"生成失败：{res.ErrorMessage}";
                    AiSummaryErrorBanner.Visibility = Visibility.Visible;
                    AiSummaryNoticeText.Foreground = (Brush)Application.Current.Resources["DangerTextBrush"];
                    AiSummaryNoticeText.Text = $"生成失败：{res.ErrorMessage}";
                    AiSummaryNoticeText.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                if (_selectedGroup?.Id == group.Id)
                {
                    AiSummaryErrorText.Text = $"生成异常：{ex.Message}";
                    AiSummaryErrorBanner.Visibility = Visibility.Visible;
                }
            }
            finally
            {
                _isAiSummaryBusy = false;
                if (_selectedGroup?.Id == group.Id)
                {
                    RenderAiSummaryState();
                }
            }
        }

        private void CopyAiSummaryButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentAiWorkspace is null || string.IsNullOrWhiteSpace(_currentAiWorkspace.SummaryText)) return;
            try
            {
                var pkg = new DataPackage();
                pkg.SetText(_currentAiWorkspace.SummaryText);
                Clipboard.SetContent(pkg);
                AiSummaryNoticeText.Foreground = (Brush)Application.Current.Resources["SuccessTextBrush"];
                AiSummaryNoticeText.Text = "已复制到剪贴板";
                AiSummaryNoticeText.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                AiSummaryNoticeText.Foreground = (Brush)Application.Current.Resources["DangerTextBrush"];
                AiSummaryNoticeText.Text = $"复制失败：{ex.Message}";
                AiSummaryNoticeText.Visibility = Visibility.Visible;
            }
        }

        private void CopyAiChatMessageMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not AiChatMessageItem item) return;
            try
            {
                var pkg = new DataPackage();
                pkg.SetText(item.Content);
                Clipboard.SetContent(pkg);
            }
            catch (Exception ex)
            {
                AppServices.Logs.Warn("AI", $"复制群 AI 回复失败：{ex.Message}");
            }
        }

        private void UpdateAiChatEmptyState()
        {
            bool hasMsgs = _aiChatMessages.Count > 0;
            AiChatEmptyPanel.Visibility = hasMsgs ? Visibility.Collapsed : Visibility.Visible;
            AiChatListView.Visibility = hasMsgs ? Visibility.Visible : Visibility.Collapsed;
            ClearAiChatButton.IsEnabled = hasMsgs && !_isAiChatBusy;
        }

        private async void AiQuickPrompt_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Content is string prompt)
            {
                await SendAiChatMessageAsync(prompt);
            }
        }

        private void UpdateGroupAiPendingImagesUi()
        {
            AiChatPendingImagesScroll.Visibility = _aiChatPendingImages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void UploadGroupAiImage_Click(object sender, RoutedEventArgs e)
        {
            if (_isAiChatBusy || _aiChatPendingImages.Count >= AiImageAttachmentHelper.MaxAttachmentsPerMessage) return;
            string? path = Win32WindowHelper.PickImageFilePath(AppServices.MainWindowHandle);
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var item = await AiImageAttachmentHelper.TryCreateFromFileAsync(path);
                if (item is not null)
                {
                    _aiChatPendingImages.Add(item);
                    UpdateGroupAiPendingImagesUi();
                }
            }
            catch (Exception ex)
            {
                AppServices.Logs.Warn("AI", $"加载图片失败：{ex.Message}");
            }
        }

        private async void PasteGroupAiImage_Click(object sender, RoutedEventArgs e) =>
            await TryAttachGroupAiClipboardImageAsync();

        private async void AiChatInputBox_Paste(object sender, TextControlPasteEventArgs e)
        {
            try
            {
                var content = Clipboard.GetContent();
                if (content.Contains(StandardDataFormats.Bitmap))
                {
                    e.Handled = true;
                    await TryAttachGroupAiClipboardImageAsync();
                }
            }
            catch
            {
            }
        }

        private async Task TryAttachGroupAiClipboardImageAsync()
        {
            if (_isAiChatBusy || _aiChatPendingImages.Count >= AiImageAttachmentHelper.MaxAttachmentsPerMessage) return;
            try
            {
                var item = await AiImageAttachmentHelper.TryCreateFromClipboardAsync();
                if (item is not null)
                {
                    _aiChatPendingImages.Add(item);
                    UpdateGroupAiPendingImagesUi();
                }
            }
            catch (Exception ex)
            {
                AppServices.Logs.Warn("AI", $"粘贴图片失败：{ex.Message}");
            }
        }

        private void RemoveGroupAiImage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                var target = _aiChatPendingImages.FirstOrDefault(x => x.Id == id);
                if (target is not null)
                {
                    _aiChatPendingImages.Remove(target);
                    UpdateGroupAiPendingImagesUi();
                }
            }
        }

        private async void AiChatSendButton_Click(object sender, RoutedEventArgs e) =>
            await SendAiChatMessageAsync(AiChatInputBox.Text);

        private async void AiChatInputBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter) return;
            var shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
            if ((shiftState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down) return;

            e.Handled = true;
            await SendAiChatMessageAsync(AiChatInputBox.Text);
        }

        private async Task SendAiChatMessageAsync(string rawText)
        {
            var group = _selectedGroup;
            string prompt = rawText.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            var attachedUris = _aiChatPendingImages.Select(x => x.DataUri).ToList();
            if (group is null || (string.IsNullOrEmpty(prompt) && attachedUris.Count == 0) || _isAiChatBusy) return;

            _isAiChatBusy = true;
            AiChatSendButton.IsEnabled = false;
            AiChatAttachButton.IsEnabled = false;
            AiChatSendIcon.Visibility = Visibility.Collapsed;
            AiChatSendBusyRing.Visibility = Visibility.Visible;
            AiChatSendBusyRing.IsActive = true;
            ClearAiChatButton.IsEnabled = false;

            AiChatInputBox.Text = string.Empty;
            _aiChatPendingImages.Clear();
            UpdateGroupAiPendingImagesUi();

            AiChatMessageItem? pendingBubble = null;
            try
            {
                _currentAiWorkspace ??= new GroupAiWorkspaceData { GroupId = group.Id };

                var historyTurns = _currentAiWorkspace.Messages
                    .TakeLast(12)
                    .Select(m => new AiChatTurnDto { Role = m.Role, Content = m.Content })
                    .ToList();

                var userMsg = new AiChatMessageItem
                {
                    Role = "user",
                    Content = prompt,
                    Images = attachedUris,
                    Timestamp = DateTime.Now.ToString("HH:mm")
                };
                _currentAiWorkspace.Messages.Add(userMsg);
                _aiChatMessages.Add(userMsg);

                pendingBubble = new AiChatMessageItem
                {
                    Role = "assistant",
                    IsPending = true,
                    Content = "正在查阅群聊记录并分析..."
                };
                _aiChatMessages.Add(pendingBubble);

                UpdateAiChatEmptyState();
                AiChatListView.ScrollIntoView(pendingBubble);
                AppServices.AiWorkspace.SaveGroup(_currentAiWorkspace);

                var res = await AppServices.Backend.GroupAiChatAsync(group.Id, historyTurns, prompt, attachedUris);

                if (pendingBubble is not null)
                {
                    _aiChatMessages.Remove(pendingBubble);
                    pendingBubble = null;
                }

                if (_selectedGroup?.Id != group.Id) return;

                string replyText = res.IsSuccess && res.Data is not null && !string.IsNullOrWhiteSpace(res.Data.Reply)
                    ? res.Data.Reply
                    : $"请求失败：{res.ErrorMessage}";

                List<AiToolTraceDto> traceList = res.IsSuccess && res.Data?.ToolTrace is { Count: > 0 }
                    ? res.Data.ToolTrace
                    : [];
                string traceText = traceList.Count > 0
                    ? "已调用工具：" + string.Join(" · ", traceList.Select(t => string.IsNullOrWhiteSpace(t.Summary) ? t.SkillName : $"{t.SkillName}（{t.Summary}）"))
                    : string.Empty;

                var reply = new AiChatMessageItem
                {
                    Role = "assistant",
                    Content = replyText,
                    ToolTrace = traceList,
                    ToolTraceSummary = traceText,
                    Timestamp = DateTime.Now.ToString("HH:mm")
                };
                _currentAiWorkspace.Messages.Add(reply);
                _aiChatMessages.Add(reply);
                AiChatListView.ScrollIntoView(reply);
                AppServices.AiWorkspace.SaveGroup(_currentAiWorkspace);
            }
            catch (Exception ex)
            {
                if (pendingBubble is not null)
                {
                    _aiChatMessages.Remove(pendingBubble);
                }
                AppServices.Logs.Error("AI", $"群 AI 对话异常：{ex.Message}");
            }
            finally
            {
                _isAiChatBusy = false;
                AiChatSendButton.IsEnabled = true;
                AiChatAttachButton.IsEnabled = true;
                AiChatSendBusyRing.IsActive = false;
                AiChatSendBusyRing.Visibility = Visibility.Collapsed;
                AiChatSendIcon.Visibility = Visibility.Visible;
                UpdateAiChatEmptyState();
            }
        }

        private void ClearAiChatButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGroup is null || _currentAiWorkspace is null) return;
            _currentAiWorkspace.Messages.Clear();
            _aiChatMessages.Clear();
            AppServices.AiWorkspace.SaveGroup(_currentAiWorkspace);
            UpdateAiChatEmptyState();
        }
    }
}
