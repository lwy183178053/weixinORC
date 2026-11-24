using Client.Controls;
using Client.Dialogs;
using Client.Models;
using Client.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Client.Pages
{
    public sealed partial class GroupsPage : Page
    {
        private List<GroupDto> _allGroups = [];
        private GroupDto? _selectedGroup;
        private bool _isEditing;

        public GroupsPage()
        {
            InitializeComponent();
            _ = ThreePaneResizeController.Attach(
                RootThreePaneGrid,
                LeftPaneColumn, LeftPaneSplitter,
                CenterPaneColumn, RightPaneSplitter,
                RightPaneColumn,
                leftDefault: 268, leftMin: 220, leftMax: 420,
                centerMin: 450,
                rightDefault: 356, rightMin: 290, rightMax: 520);
            _ = ComposerResizeController.Attach(ChatStageGrid, ComposerRow, ComposerSplitter, minHeight: 128);
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            try { await LoadGroupsAsync(); }
            catch (Exception ex) { AppServices.Logs.Error("UI", $"加载群组列表异常：{ex.Message}"); }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            try { await LoadGroupsAsync(_selectedGroup?.Id); }
            catch (Exception ex) { ShowDetailStatus($"刷新失败：{ex.Message}", isError: true); }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (GroupsListView is null) return;
            ApplyFilter(_selectedGroup?.Id);
        }

        private async Task LoadGroupsAsync(long? selectId = null)
        {
            var res = await AppServices.Backend.ListGroupsAsync();
            if (res.IsSuccess && res.Data is not null)
            {
                _allGroups = res.Data.Items;
                ApplyFilter(selectId);
            }
            else
            {
                ShowDetailStatus($"拉取群组列表失败：{res.ErrorMessage}", isError: true);
            }
        }

        private void ApplyFilter(long? preferredSelectId = null)
        {
            string q = SearchBox.Text.Trim();
            var filtered = string.IsNullOrEmpty(q)
                ? _allGroups
                : _allGroups.Where(g =>
                    g.GroupName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    g.MembersText.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    g.Note.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

            GroupsListView.ItemsSource = filtered;
            EmptyListHint.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (filtered.Count > 0)
            {
                int idx = preferredSelectId.HasValue
                    ? filtered.FindIndex(g => g.Id == preferredSelectId.Value)
                    : 0;
                GroupsListView.SelectedIndex = idx >= 0 ? idx : 0;
            }
            else
            {
                _selectedGroup = null;
                SetEditMode(false);
                UpdateSelectedGroupUi();
            }
        }

        private async void GroupsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedGroup = GroupsListView.SelectedItem as GroupDto;
            SetEditMode(false);
            DetailStatusBanner.Visibility = Visibility.Collapsed;
            UpdateSelectedGroupUi();

            if (_selectedGroup is { } selected)
            {
                try { await LoadGroupMessagesAsync(selected); }
                catch (Exception ex) { AppServices.Logs.Error("UI", $"加载群聊记录异常：{ex.Message}"); }
            }
            else
            {
                MessagesListView.ItemsSource = null;
                EmptyChatTitle.Text = "暂无群聊记录";
                EmptyChatHint.Visibility = Visibility.Visible;
            }
        }

        private void UpdateSelectedGroupUi()
        {
            var g = _selectedGroup;
            bool hasGroup = g is not null;

            SendMessageButton.IsEnabled = hasGroup;
            ImportChatScreenshotButton.IsEnabled = hasGroup;
            EditGroupButton.IsEnabled = hasGroup;
            DeleteGroupButton.IsEnabled = hasGroup;
            EmptyDetailHint.Visibility = hasGroup ? Visibility.Collapsed : Visibility.Visible;
            DetailContentScroll.Visibility = hasGroup ? Visibility.Visible : Visibility.Collapsed;

            if (g is null)
            {
                ActiveGroupTitle.Text = "请选择左侧群组";
                ActiveGroupMeta.Text = "点左上角「+」可弹出截图识别建群窗口";
                return;
            }

            ActiveGroupTitle.Text = g.DisplayTitleWithCount;
            ActiveGroupMeta.Text = string.IsNullOrWhiteSpace(g.Note)
                ? $"群组 ID #{g.Id} · 成员 {g.MemberCount} 人"
                : g.Note;

            DetailAvatarText.Text = g.AvatarInitial;
            DetailHeaderNameText.Text = g.DisplayName;
            DetailHeaderCountText.Text = $"成员人数：{(g.MemberCount > 0 ? $"{g.MemberCount} 人" : "未填写")}";
            DetailHeaderKeyText.Text = $"群标识：{(string.IsNullOrWhiteSpace(g.GroupKey) ? "-" : g.GroupKey)}";
            ViewMembersText.Text = string.IsNullOrWhiteSpace(g.MembersText) ? "暂未采集群成员" : g.MembersText;

            if (string.IsNullOrWhiteSpace(g.Note))
            {
                ViewGroupNoteText.Text = "暂无员工群备注，点击「编辑」即可补充群属性与跟进计划。";
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
            var target = _selectedGroup;
            if (target is null || !AppServices.TryBeginModalDialog()) return;

            try
            {
                var confirm = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    RequestedTheme = AppServices.Settings.CurrentElementTheme,
                    Title = "确认删除群组",
                    Content = $"确定要删除群组「{target.DisplayName}」及其全部群聊记录吗？删除后无法恢复。",
                    PrimaryButtonText = "确认删除",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    PrimaryButtonStyle = (Style)Application.Current.Resources["DangerOutlineButtonStyle"]
                };

                var choice = await confirm.ShowAsync();
                if (choice != ContentDialogResult.Primary) return;

                var res = await AppServices.Backend.DeleteGroupAsync(target.Id);
                if (res.IsSuccess)
                {
                    SetEditMode(false);
                    await LoadGroupsAsync();
                    if (_selectedGroup is not null)
                    {
                        ShowDetailStatus($"已删除群组「{target.DisplayName}」。");
                    }
                }
                else
                {
                    ShowDetailStatus($"删除失败：{res.ErrorMessage}", isError: true);
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
            try { await ShowImportDialogAsync(autoPasteClipboard: false); }
            catch (Exception ex) { ShowDetailStatus($"打开建群窗口失败：{ex.Message}", isError: true); }
        }

        private async void PasteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            if (AppServices.IsModalDialogOpen || _isEditing) return;
            if (OcrService.HasClipboardBitmapFast())
            {
                args.Handled = true;
                try { await ShowImportDialogAsync(autoPasteClipboard: true); }
                catch (Exception ex) { ShowDetailStatus($"快捷建群失败：{ex.Message}", isError: true); }
                return;
            }
            if (!await OcrService.HasClipboardImageAsync()) return;

            args.Handled = true;
            try { await ShowImportDialogAsync(autoPasteClipboard: true); }
            catch (Exception ex) { ShowDetailStatus($"快捷建群失败：{ex.Message}", isError: true); }
        }

        private async Task ShowImportDialogAsync(bool autoPasteClipboard)
        {
            if (!AppServices.TryBeginModalDialog()) return;
            try
            {
                var dialog = new ImportGroupDialog { XamlRoot = XamlRoot };
                if (autoPasteClipboard)
                {
                    dialog.Opened += async (_, _) => await dialog.TriggerClipboardImportAsync();
                }

                await dialog.ShowAsync();
                if (dialog.CreatedGroup is { } created)
                {
                    await LoadGroupsAsync(created.Id);
                    ShowDetailStatus($"已成功导入新群组「{created.DisplayName}」。");
                }
            }
            finally
            {
                AppServices.EndModalDialog();
            }
        }

        private async Task LoadGroupMessagesAsync(GroupDto group)
        {
            var res = await AppServices.Backend.ListGroupMessagesAsync(group.Id);
            if (_selectedGroup?.Id != group.Id) return;

            if (!res.IsSuccess || res.Data is null)
            {
                EmptyChatTitle.Text = $"加载群消息失败：{res.ErrorMessage}";
                EmptyChatHint.Visibility = Visibility.Visible;
                MessagesListView.ItemsSource = null;
                return;
            }

            var bubbles = res.Data.Items
                .AsEnumerable()
                .Reverse()
                .Select(m =>
                {
                    string sender = string.IsNullOrWhiteSpace(m.SenderName) ? "群成员" : m.SenderName;
                    return new ChatBubbleItem
                    {
                        Id = m.Id,
                        SenderName = sender,
                        SenderInitial = sender[..1].ToUpperInvariant(),
                        Content = m.Content,
                        TimeLabel = m.TimeLabel,
                        IsOutgoing = m.IsOutgoing
                    };
                })
                .ToList();

            MessagesListView.ItemsSource = bubbles;
            EmptyChatTitle.Text = $"「{group.DisplayName}」暂无群聊记录";
            EmptyChatHint.Visibility = bubbles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (bubbles.Count > 0)
            {
                MessagesListView.ScrollIntoView(bubbles[^1]);
            }
        }

        private async void SendMessageButton_Click(object sender, RoutedEventArgs e)
        {
            var target = _selectedGroup;
            if (target is null) return;
            string content = MessageInputBox.Text.Trim();
            if (string.IsNullOrEmpty(content)) return;

            bool isIncoming = DirIncomingRadio.IsChecked == true;
            string direction = isIncoming ? "incoming" : "outgoing";
            string senderName = isIncoming
                ? (string.IsNullOrWhiteSpace(SenderNameBox.Text) ? "群成员" : SenderNameBox.Text.Trim())
                : (AppServices.Backend.CurrentEmployee?.DisplayName ?? "我");

            SendMessageButton.IsEnabled = false;
            try
            {
                var res = await AppServices.Backend.CreateGroupMessageAsync(target.Id, senderName, direction, content);
                if (res.IsSuccess)
                {
                    MessageInputBox.Text = string.Empty;
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

        private async void ImportChatScreenshotButton_Click(object sender, RoutedEventArgs e)
        {
            var target = _selectedGroup;
            if (target is null || !AppServices.TryBeginModalDialog()) return;
            try
            {
                var dialog = new ImportChatScreenshotDialog { XamlRoot = XamlRoot };
                var choice = await dialog.ShowAsync();
                if (choice != ContentDialogResult.Primary || dialog.ConfirmedMessages.Count == 0) return;

                string fallbackSender = string.IsNullOrWhiteSpace(SenderNameBox.Text) ? "群成员" : SenderNameBox.Text.Trim();
                string myName = AppServices.Backend.CurrentEmployee?.DisplayName ?? "我";

                int saved = 0;
                foreach (var msg in dialog.ConfirmedMessages)
                {
                    string senderName = msg.IsOutgoing ? myName : fallbackSender;
                    var res = await AppServices.Backend.CreateGroupMessageAsync(target.Id, senderName, msg.Direction, msg.Content);
                    if (res.IsSuccess) saved++;
                }

                if (_selectedGroup?.Id == target.Id)
                {
                    await LoadGroupMessagesAsync(target);
                }
                ComposerHintText.Text = $"已从群聊截图批量导入 {saved} 条消息。";
            }
            catch (Exception ex)
            {
                ComposerHintText.Text = $"截图导入异常：{ex.Message}";
            }
            finally
            {
                AppServices.EndModalDialog();
            }
        }

        private async void OcrChatClipboardButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ComposerHintText.Text = "正在提取截图文字...";
                var (ocr, err) = await AppServices.Ocr.TryRecognizeClipboardTextAsync();
                if (ocr is null)
                {
                    ComposerHintText.Text = err;
                    return;
                }

                if (!string.IsNullOrWhiteSpace(ocr.FullText))
                {
                    MessageInputBox.Text = string.IsNullOrWhiteSpace(MessageInputBox.Text)
                        ? ocr.FullText
                        : $"{MessageInputBox.Text}\n{ocr.FullText}";
                    ComposerHintText.Text = $"已识别 {ocr.Lines.Count} 行群聊文字（{ocr.ElapsedMilliseconds} ms）。";
                }
                else
                {
                    ComposerHintText.Text = "未识别到文字。";
                }
            }
            catch (Exception ex)
            {
                ComposerHintText.Text = $"识别失败：{ex.Message}";
            }
        }

        private void ShowDetailStatus(string message, bool isError = false)
        {
            DetailStatusBanner.Background = (Brush)Application.Current.Resources[isError ? "DangerBgBrush" : "SuccessBgBrush"];
            DetailStatusText.Foreground = (Brush)Application.Current.Resources[isError ? "DangerTextBrush" : "SuccessTextBrush"];
            DetailStatusText.Text = message;
            DetailStatusBanner.Visibility = Visibility.Visible;
        }
    }
}
