using System.Collections.ObjectModel;
using System.Text;
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
    public sealed partial class CustomersPage : Page
    {
        private const string UntagGroupName = "未分组";
        private static readonly char[] TagSeparators = [',', '，', '、', ';', '；', '|', '/'];

        private List<CustomerDto> _allCustomers = [];
        private List<CustomerDto> _filteredCustomers = [];
        private CustomerDto? _selectedCustomer;
        private readonly ObservableCollection<CustomerListRow> _customerRows = [];
        private readonly HashSet<string> _collapsedTags = new(StringComparer.CurrentCultureIgnoreCase);
        private readonly HashSet<long> _selectedCustomerIds = [];
        private readonly ObservableCollection<ChatBubbleItem> _currentMessages = [];
        private readonly ObservableCollection<AiChatMessageItem> _aiChatMessages = [];
        private readonly HashSet<long> _locallyDeletedMessageIds = [];
        private CustomerAiWorkspaceData? _currentAiWorkspace;
        private CancellationTokenSource? _avatarHydrationCts;
        private string _activeRightTab = "profile";
        private bool _isCustomerMultiSelectMode;
        private bool _isMultiSelectMode;
        private bool _isAiSummaryBusy;
        private bool _isAiChatBusy;

        public CustomersPage()
        {
            InitializeComponent();
            foreach (string tag in AppServices.Settings.Current.CollapsedCustomerTags)
            {
                if (!string.IsNullOrWhiteSpace(tag)) _collapsedTags.Add(tag);
            }
            CustomersListView.ItemsSource = _customerRows;
            MessagesListView.ItemsSource = _currentMessages;
            AiChatListView.ItemsSource = _aiChatMessages;
            _ = ThreePaneResizeController.Attach(
                RootThreePaneGrid,
                LeftPaneColumn, LeftPaneSplitter,
                CenterPaneColumn, RightPaneSplitter,
                RightPaneColumn,
                leftDefault: 268, leftMin: 220, leftMax: 420,
                centerMin: 450,
                rightDefault: 356, rightMin: 290, rightMax: 520);
            _ = ComposerResizeController.Attach(ChatStageGrid, ComposerRow, ComposerSplitter, minHeight: 120);
            Loaded += CustomersPage_Loaded;
            Unloaded += CustomersPage_Unloaded;
        }

        private void CustomersPage_Loaded(object sender, RoutedEventArgs e)
        {
            AppServices.Settings.ThemeChanged += OnSettingsThemeChanged;
            UpdateRightTabVisuals();
        }

        private void CustomersPage_Unloaded(object sender, RoutedEventArgs e)
        {
            AppServices.Settings.ThemeChanged -= OnSettingsThemeChanged;
            _avatarHydrationCts?.Cancel();
            _avatarHydrationCts?.Dispose();
            _avatarHydrationCts = null;
        }

        private void OnSettingsThemeChanged(bool _)
        {
            UpdateRightTabVisuals();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            try { await LoadCustomersAsync(); }
            catch (Exception ex) { AppServices.Logs.Error("UI", $"加载客户列表异常：{ex.Message}"); }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            try { await LoadCustomersAsync(_selectedCustomer?.Id); }
            catch (Exception ex) { ShowProfileStatus($"刷新失败：{ex.Message}", isError: true); }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (CustomersListView is null) return;
            ApplyFilter(_selectedCustomer?.Id, forceReloadDetail: false);
        }

        private async Task LoadCustomersAsync(long? selectId = null)
        {
            var result = await AppServices.Backend.ListCustomersAsync();
            if (result.IsSuccess && result.Data is not null)
            {
                _allCustomers = result.Data.Items;
                var validIds = _allCustomers.Select(c => c.Id).ToHashSet();
                _selectedCustomerIds.RemoveWhere(id => !validIds.Contains(id));
                ApplyFilter(selectId, forceReloadDetail: true);
                StartAvatarHydration(_allCustomers);
            }
            else
            {
                ShowProfileStatus($"拉取客户列表失败：{result.ErrorMessage}", isError: true);
            }
        }

        private void StartAvatarHydration(List<CustomerDto> customers)
        {
            _avatarHydrationCts?.Cancel();
            _avatarHydrationCts?.Dispose();
            var cts = new CancellationTokenSource();
            _avatarHydrationCts = cts;
            _ = HydrateCustomerAvatarsAsync(customers, cts.Token);
        }

        private async Task HydrateCustomerAvatarsAsync(List<CustomerDto> customers, CancellationToken ct)
        {
            try
            {
                foreach (var c in customers)
                {
                    if (ct.IsCancellationRequested) return;
                    if (string.IsNullOrWhiteSpace(c.AvatarUrl) || c.AvatarImage is not null) continue;
                    byte[]? bytes = await AppServices.Backend.DownloadAvatarBytesAsync(c.AvatarUrl, ct);
                    if (bytes is null || ct.IsCancellationRequested) continue;
                    var img = await AvatarImageService.BytesToBitmapImageAsync(bytes);
                    if (img is not null && !ct.IsCancellationRequested)
                    {
                        c.AvatarImage = img;
                        NotifyCustomerRowAvatarUpdated(c.Id);
                        if (_selectedCustomer?.Id == c.Id)
                        {
                            ApplyProfileAvatarVisual(c);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppServices.Logs.Warn("UI", $"加载客户头像异常：{ex.Message}");
            }
        }

        private void NotifyCustomerRowAvatarUpdated(long customerId)
        {
            foreach (var row in _customerRows)
            {
                if (!row.IsGroupHeader && row.Customer?.Id == customerId)
                {
                    row.NotifyAvatarChanged();
                }
            }
        }

        private static string GetPrimaryTag(CustomerDto c)
        {
            if (string.IsNullOrWhiteSpace(c.Tags)) return UntagGroupName;
            foreach (string raw in c.Tags.Split(TagSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = raw.Trim();
                if (!string.IsNullOrEmpty(trimmed)) return trimmed;
            }
            return UntagGroupName;
        }

        private void ApplyFilter(long? preferredSelectId = null, bool forceReloadDetail = false)
        {
            string q = SearchBox.Text.Trim();
            _filteredCustomers = string.IsNullOrEmpty(q)
                ? [.. _allCustomers]
                : _allCustomers.Where(c =>
                    c.WechatRemark.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    c.WechatName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    c.WechatAccount.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    c.Tags.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    c.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    c.Note.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    c.Phone.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

            CustomerDto? nextSelected = null;
            if (_filteredCustomers.Count > 0)
            {
                if (preferredSelectId.HasValue)
                {
                    nextSelected = _filteredCustomers.FirstOrDefault(c => c.Id == preferredSelectId.Value);
                }
                nextSelected ??= _selectedCustomer is not null
                    ? _filteredCustomers.FirstOrDefault(c => c.Id == _selectedCustomer.Id)
                    : null;
                nextSelected ??= _filteredCustomers[0];
            }

            bool selectionChanged = _selectedCustomer?.Id != nextSelected?.Id;
            _selectedCustomer = nextSelected;

            RebuildCustomerRows(expandAllForSearch: !string.IsNullOrEmpty(q));
            EmptyListHint.Visibility = _filteredCustomers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateCustomerMultiSelectSummary();

            if (selectionChanged || forceReloadDetail)
            {
                _ = ActivateSelectedCustomerAsync(nextSelected);
            }
        }

        private void RebuildCustomerRows(bool expandAllForSearch)
        {
            _customerRows.Clear();
            if (_filteredCustomers.Count == 0) return;

            var groups = _filteredCustomers
                .GroupBy(GetPrimaryTag, StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(g => string.Equals(g.Key, UntagGroupName, StringComparison.Ordinal) ? 1 : 0)
                .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);

            foreach (var group in groups)
            {
                string tagName = group.Key;
                var members = group.ToList();
                bool collapsed = !expandAllForSearch && _collapsedTags.Contains(tagName);
                bool allInGroupSelected = members.Count > 0 && members.All(m => _selectedCustomerIds.Contains(m.Id));

                _customerRows.Add(new CustomerListRow
                {
                    IsGroupHeader = true,
                    TagName = tagName,
                    GroupCount = members.Count,
                    IsCollapsed = collapsed,
                    IsMultiSelectMode = _isCustomerMultiSelectMode,
                    IsGroupAllSelected = allInGroupSelected
                });

                if (collapsed) continue;

                foreach (var c in members)
                {
                    _customerRows.Add(new CustomerListRow
                    {
                        IsGroupHeader = false,
                        TagName = tagName,
                        Customer = c,
                        IsMultiSelectMode = _isCustomerMultiSelectMode,
                        IsChecked = _selectedCustomerIds.Contains(c.Id),
                        IsActive = _selectedCustomer?.Id == c.Id
                    });
                }
            }
        }

        private void UpdateActiveRowHighlight()
        {
            long? activeId = _selectedCustomer?.Id;
            foreach (var row in _customerRows)
            {
                if (!row.IsGroupHeader)
                {
                    row.IsActive = activeId.HasValue && row.Customer?.Id == activeId.Value;
                }
            }
        }

        private void SaveCollapsedTags()
        {
            AppServices.Settings.Current.CollapsedCustomerTags = _collapsedTags.ToList();
            AppServices.Settings.Save();
        }

        private void ToggleTagGroupCollapsedInPlace(CustomerListRow headerRow)
        {
            int headerIdx = _customerRows.IndexOf(headerRow);
            if (headerIdx < 0) return;

            bool collapsing = _collapsedTags.Add(headerRow.TagName);
            if (!collapsing)
            {
                _collapsedTags.Remove(headerRow.TagName);
            }
            SaveCollapsedTags();

            headerRow.IsCollapsed = collapsing;

            if (collapsing)
            {
                // Remove only the child customer rows directly under this header (no full list reset!)
                int nextIdx = headerIdx + 1;
                while (nextIdx < _customerRows.Count && !_customerRows[nextIdx].IsGroupHeader)
                {
                    _customerRows.RemoveAt(nextIdx);
                }
            }
            else
            {
                // Insert only this group's customer rows right under the header
                var members = _filteredCustomers
                    .Where(c => string.Equals(GetPrimaryTag(c), headerRow.TagName, StringComparison.CurrentCultureIgnoreCase))
                    .ToList();
                int insertIdx = headerIdx + 1;
                foreach (var c in members)
                {
                    _customerRows.Insert(insertIdx++, new CustomerListRow
                    {
                        IsGroupHeader = false,
                        TagName = headerRow.TagName,
                        Customer = c,
                        IsMultiSelectMode = _isCustomerMultiSelectMode,
                        IsChecked = _selectedCustomerIds.Contains(c.Id),
                        IsActive = _selectedCustomer?.Id == c.Id
                    });
                }
            }
        }

        private async void CustomersListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not CustomerListRow row) return;

            if (row.IsGroupHeader)
            {
                if (!string.IsNullOrWhiteSpace(SearchBox.Text)) return;
                ToggleTagGroupCollapsedInPlace(row);
                return;
            }

            if (row.Customer is not { } customer) return;

            if (_isCustomerMultiSelectMode)
            {
                bool nowChecked = !_selectedCustomerIds.Contains(customer.Id);
                if (nowChecked) _selectedCustomerIds.Add(customer.Id);
                else _selectedCustomerIds.Remove(customer.Id);
                row.IsChecked = nowChecked;
                SyncGroupSelectionStates();
                UpdateCustomerMultiSelectSummary();
                return;
            }

            if (_selectedCustomer?.Id == customer.Id) return;
            _selectedCustomer = customer;
            UpdateActiveRowHighlight();
            await ActivateSelectedCustomerAsync(customer);
        }

        private async Task ActivateSelectedCustomerAsync(CustomerDto? selected)
        {
            SetMultiSelectMode(false);
            ProfileStatusBanner.Visibility = Visibility.Collapsed;
            UpdateSelectedCustomerUi();

            if (selected is not null)
            {
                LoadCustomerAiWorkspace(selected);
                try { await LoadCustomerMessagesAsync(selected); }
                catch (Exception ex) { AppServices.Logs.Error("UI", $"加载聊天记录异常：{ex.Message}"); }
                UpdateAiContextLabels();
            }
            else
            {
                _currentMessages.Clear();
                _aiChatMessages.Clear();
                _currentAiWorkspace = null;
                MultiSelectMessagesButton.IsEnabled = false;
                EmptyChatTitle.Text = "暂无聊天记录";
                EmptyChatHint.Visibility = Visibility.Visible;
            }
        }

        // ---- Left Sidebar Customer Multi-Select & Tag Group Actions ----

        private void SetCustomerMultiSelectMode(bool enabled)
        {
            _isCustomerMultiSelectMode = enabled;
            CustomerMultiSelectBar.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            MultiSelectCustomersIcon.Foreground = (Brush)Application.Current.Resources[enabled ? "BrandPrimaryBrush" : "TextSecondaryBrush"];
            ToolTipService.SetToolTip(MultiSelectCustomersButton, enabled ? "退出多选模式" : "多选批量删除客户");

            if (!enabled)
            {
                _selectedCustomerIds.Clear();
            }

            foreach (var row in _customerRows)
            {
                row.IsMultiSelectMode = enabled;
                if (!enabled)
                {
                    row.IsChecked = false;
                    row.IsGroupAllSelected = false;
                }
            }

            if (enabled) SyncGroupSelectionStates();
            UpdateCustomerMultiSelectSummary();
        }

        private void SyncGroupSelectionStates()
        {
            var byTag = _filteredCustomers
                .GroupBy(GetPrimaryTag, StringComparer.CurrentCultureIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.CurrentCultureIgnoreCase);

            foreach (var row in _customerRows)
            {
                if (row.IsGroupHeader)
                {
                    row.IsGroupAllSelected = byTag.TryGetValue(row.TagName, out var list)
                        && list.Count > 0
                        && list.All(c => _selectedCustomerIds.Contains(c.Id));
                }
                else if (row.Customer is { } c)
                {
                    row.IsChecked = _selectedCustomerIds.Contains(c.Id);
                }
            }
        }

        private void UpdateCustomerMultiSelectSummary()
        {
            int selectedCount = _filteredCustomers.Count(c => _selectedCustomerIds.Contains(c.Id));
            SelectedCustomersCountText.Text = $"已选 {selectedCount}";
            DeleteSelectedCustomersButton.IsEnabled = selectedCount > 0;
            DeleteSelectedCustomersButton.Content = selectedCount > 0 ? $"删除({selectedCount})" : "删除";
            SelectAllCustomersButton.Content = _filteredCustomers.Count > 0 && selectedCount == _filteredCustomers.Count
                ? "全不选"
                : "全选";
        }

        private void MultiSelectCustomersButton_Click(object sender, RoutedEventArgs e) =>
            SetCustomerMultiSelectMode(!_isCustomerMultiSelectMode);

        private void ExitCustomerMultiSelectButton_Click(object sender, RoutedEventArgs e) =>
            SetCustomerMultiSelectMode(false);

        private void SelectAllCustomersButton_Click(object sender, RoutedEventArgs e)
        {
            bool selectAll = _filteredCustomers.Any(c => !_selectedCustomerIds.Contains(c.Id));
            foreach (var c in _filteredCustomers)
            {
                if (selectAll) _selectedCustomerIds.Add(c.Id);
                else _selectedCustomerIds.Remove(c.Id);
            }
            SyncGroupSelectionStates();
            UpdateCustomerMultiSelectSummary();
        }

        private void CustomerRowCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox cb || cb.DataContext is not CustomerListRow row || row.Customer is null) return;
            if (cb.IsChecked == true) _selectedCustomerIds.Add(row.Customer.Id);
            else _selectedCustomerIds.Remove(row.Customer.Id);
            row.IsChecked = cb.IsChecked == true;
            SyncGroupSelectionStates();
            UpdateCustomerMultiSelectSummary();
        }

        private void GroupHeaderCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox cb || cb.DataContext is not CustomerListRow row) return;
            bool checkGroup = cb.IsChecked == true;
            foreach (var c in _filteredCustomers.Where(c => string.Equals(GetPrimaryTag(c), row.TagName, StringComparison.CurrentCultureIgnoreCase)))
            {
                if (checkGroup) _selectedCustomerIds.Add(c.Id);
                else _selectedCustomerIds.Remove(c.Id);
            }
            SyncGroupSelectionStates();
            UpdateCustomerMultiSelectSummary();
        }

        private void SelectTagGroupMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not CustomerListRow row) return;
            if (!_isCustomerMultiSelectMode) SetCustomerMultiSelectMode(true);
            foreach (var c in _filteredCustomers.Where(c => string.Equals(GetPrimaryTag(c), row.TagName, StringComparison.CurrentCultureIgnoreCase)))
            {
                _selectedCustomerIds.Add(c.Id);
            }
            SyncGroupSelectionStates();
            UpdateCustomerMultiSelectSummary();
        }

        private void ExpandAllGroupsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _collapsedTags.Clear();
            SaveCollapsedTags();
            RebuildCustomerRows(expandAllForSearch: !string.IsNullOrWhiteSpace(SearchBox.Text));
        }

        private void CollapseAllGroupsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            foreach (var c in _filteredCustomers)
            {
                _collapsedTags.Add(GetPrimaryTag(c));
            }
            SaveCollapsedTags();
            RebuildCustomerRows(expandAllForSearch: !string.IsNullOrWhiteSpace(SearchBox.Text));
        }

        private void MultiSelectCustomerRowMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not CustomerListRow row || row.Customer is null) return;
            if (!_isCustomerMultiSelectMode) SetCustomerMultiSelectMode(true);
            _selectedCustomerIds.Add(row.Customer.Id);
            SyncGroupSelectionStates();
            UpdateCustomerMultiSelectSummary();
        }

        private async void DeleteCustomerRowMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not CustomerListRow row || row.Customer is null) return;
            await ConfirmAndDeleteCustomersAsync([row.Customer]);
        }

        private async void DeleteSelectedCustomersButton_Click(object sender, RoutedEventArgs e)
        {
            var targets = _filteredCustomers.Where(c => _selectedCustomerIds.Contains(c.Id)).ToList();
            if (targets.Count == 0) return;
            await ConfirmAndDeleteCustomersAsync(targets);
        }

        private void UpdateSelectedCustomerUi()
        {
            var c = _selectedCustomer;
            bool hasCustomer = c is not null;

            SendMessageButton.IsEnabled = hasCustomer;
            ImportChatScreenshotButton.IsEnabled = hasCustomer;
            EditProfileButton.IsEnabled = hasCustomer;
            DeleteCustomerButton.IsEnabled = hasCustomer;
            EmptyProfileHint.Visibility = hasCustomer ? Visibility.Collapsed : Visibility.Visible;
            UpdateRightTabVisibility();

            if (c is null)
            {
                ActiveCustomerTitle.Text = "请选择左侧客户";
                ActiveCustomerMeta.Text = "点左上角「+」可弹出截图识别导入窗口";
                return;
            }

            ActiveCustomerTitle.Text = c.DisplayName;
            string singleLineRemark = c.WechatRemark.Replace("\r", " ").Replace("\n", " ").Trim();
            var metaParts = new[]
            {
                string.IsNullOrWhiteSpace(singleLineRemark) || string.IsNullOrWhiteSpace(c.WechatName) ? "" : $"备注: {singleLineRemark}",
                string.IsNullOrWhiteSpace(c.WechatAccount) ? "" : $"微信号: {c.WechatAccount}",
                string.IsNullOrWhiteSpace(c.Region) ? "" : $"地区: {c.Region}",
                string.IsNullOrWhiteSpace(c.Phone) ? "" : $"电话: {c.Phone}"
            }.Where(s => s.Length > 0);
            ActiveCustomerMeta.Text = metaParts.Any() ? string.Join("  ·  ", metaParts) : $"客户 ID #{c.Id}";

            ProfileAvatarText.Text = c.AvatarInitial;
            ApplyProfileAvatarVisual(c);
            if (!string.IsNullOrWhiteSpace(c.AvatarUrl) && c.AvatarImage is null)
            {
                _ = EnsureSelectedAvatarLoadedAsync(c);
            }

            ProfileHeaderNameText.Text = c.DisplayName;
            ProfileHeaderAccountText.Text = $"微信号：{(string.IsNullOrWhiteSpace(c.WechatAccount) ? "-" : c.WechatAccount)}";
            ProfileHeaderRegionText.Text = $"地区：{(string.IsNullOrWhiteSpace(c.Region) ? "-" : c.Region)}";

            // Gender badge in header (dynamic Light/Dark theme brushes)
            if (c.Gender == "男")
            {
                ProfileGenderBadge.Background = (Brush)Application.Current.Resources["GenderMaleBgBrush"];
                ProfileGenderBadgeText.Foreground = (Brush)Application.Current.Resources["GenderMaleTextBrush"];
                ProfileGenderBadgeText.Text = "♂ 男";
                ProfileGenderBadge.Visibility = Visibility.Visible;
            }
            else if (c.Gender == "女")
            {
                ProfileGenderBadge.Background = (Brush)Application.Current.Resources["GenderFemaleBgBrush"];
                ProfileGenderBadgeText.Foreground = (Brush)Application.Current.Resources["GenderFemaleTextBrush"];
                ProfileGenderBadgeText.Text = "♀ 女";
                ProfileGenderBadge.Visibility = Visibility.Visible;
            }
            else
            {
                ProfileGenderBadge.Visibility = Visibility.Collapsed;
            }

            // Follow status pill in header
            if (!string.IsNullOrWhiteSpace(c.FollowStatus))
            {
                ProfileStatusPillText.Text = c.FollowStatus;
                ProfileStatusPill.Visibility = Visibility.Visible;
            }
            else
            {
                ProfileStatusPill.Visibility = Visibility.Collapsed;
            }

            ViewRemarkText.Text = string.IsNullOrWhiteSpace(c.WechatRemark) ? "-" : c.WechatRemark;
            ViewNicknameText.Text = string.IsNullOrWhiteSpace(c.WechatName) ? "-" : c.WechatName;
            ViewGenderText.Text = string.IsNullOrWhiteSpace(c.Gender) ? "未知" : c.Gender;
            ViewPhoneText.Text = string.IsNullOrWhiteSpace(c.Phone) ? "-" : c.Phone;
            ViewSignatureText.Text = string.IsNullOrWhiteSpace(c.Signature) ? "-" : c.Signature;
            ViewSourceText.Text = string.IsNullOrWhiteSpace(c.Source) ? "-" : c.Source;
            ViewAddedDateText.Text = string.IsNullOrWhiteSpace(c.AddedDate) ? "-" : c.AddedDate;

            ViewFollowStatusText.Text = string.IsNullOrWhiteSpace(c.FollowStatus) ? "未设置" : c.FollowStatus;
            ViewTagsText.Text = string.IsNullOrWhiteSpace(c.Tags) ? "-" : c.Tags;
            ViewDescriptionText.Text = string.IsNullOrWhiteSpace(c.Description) ? "-" : c.Description;

            if (string.IsNullOrWhiteSpace(c.Note))
            {
                ViewEmployeeNoteText.Text = "暂无员工备注，点击上方编辑图标即可补充客户意向与跟进内容。";
                ViewEmployeeNoteText.Foreground = (Brush)Application.Current.Resources["TextMutedBrush"];
            }
            else
            {
                ViewEmployeeNoteText.Text = c.Note;
                ViewEmployeeNoteText.Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"];
            }
        }

        private void ApplyProfileAvatarVisual(CustomerDto c)
        {
            if (c.AvatarImage is not null)
            {
                ProfileAvatarImage.Source = c.AvatarImage;
                ProfileAvatarImage.Visibility = Visibility.Visible;
                ProfileAvatarText.Visibility = Visibility.Collapsed;
                ClearAvatarMenuItem.IsEnabled = true;
            }
            else
            {
                ProfileAvatarImage.Source = null;
                ProfileAvatarImage.Visibility = Visibility.Collapsed;
                ProfileAvatarText.Visibility = Visibility.Visible;
                ClearAvatarMenuItem.IsEnabled = !string.IsNullOrWhiteSpace(c.AvatarUrl);
            }
        }

        private async Task EnsureSelectedAvatarLoadedAsync(CustomerDto c)
        {
            try
            {
                byte[]? bytes = await AppServices.Backend.DownloadAvatarBytesAsync(c.AvatarUrl);
                if (bytes is null) return;
                var img = await AvatarImageService.BytesToBitmapImageAsync(bytes);
                if (img is not null)
                {
                    c.AvatarImage = img;
                    NotifyCustomerRowAvatarUpdated(c.Id);
                    if (_selectedCustomer?.Id == c.Id)
                    {
                        ApplyProfileAvatarVisual(c);
                    }
                }
            }
            catch (Exception ex)
            {
                AppServices.Logs.Warn("UI", $"加载当前客户头像异常：{ex.Message}");
            }
        }

        private async void EditProfileButton_Click(object sender, RoutedEventArgs e)
        {
            var target = _selectedCustomer;
            if (target is null || !AppServices.TryBeginModalDialog()) return;

            try
            {
                var dialog = new EditCustomerDialog(target)
                {
                    XamlRoot = XamlRoot
                };
                await dialog.ShowAsync();

                if (dialog.SavedCustomer is { } saved)
                {
                    await LoadCustomersAsync(saved.Id);
                    ShowProfileStatus("✓ 客户资料与员工备注已保存。");
                }
            }
            catch (Exception ex)
            {
                ShowProfileStatus($"打开编辑弹窗失败：{ex.Message}", isError: true);
            }
            finally
            {
                AppServices.EndModalDialog();
            }
        }

        private async void DeleteCustomerButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedCustomer is null) return;
            await ConfirmAndDeleteCustomersAsync([_selectedCustomer]);
        }

        private async Task ConfirmAndDeleteCustomersAsync(List<CustomerDto> targets)
        {
            if (targets.Count == 0 || !AppServices.TryBeginModalDialog()) return;

            try
            {
                bool isBatch = targets.Count > 1;
                var confirm = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    RequestedTheme = AppServices.Settings.CurrentElementTheme,
                    Title = isBatch ? "确认批量删除客户" : "确认删除客户",
                    Content = isBatch
                        ? $"确定要删除已选中的 {targets.Count} 位客户及其全部聊天记录吗？删除后无法恢复。"
                        : $"确定要删除客户「{targets[0].DisplayName}」及其全部聊天记录吗？删除后无法恢复。",
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
                    var res = await AppServices.Backend.DeleteCustomerAsync(target.Id);
                    if (res.IsSuccess)
                    {
                        deleted++;
                        deletedIds.Add(target.Id);
                        _selectedCustomerIds.Remove(target.Id);
                    }
                    else
                    {
                        failed++;
                        lastError = res.ErrorMessage;
                    }
                }

                if (_isCustomerMultiSelectMode && deleted > 0)
                {
                    SetCustomerMultiSelectMode(false);
                }

                long? keepId = _selectedCustomer is not null && !deletedIds.Contains(_selectedCustomer.Id)
                    ? _selectedCustomer.Id
                    : null;

                await LoadCustomersAsync(keepId);

                if (_selectedCustomer is not null)
                {
                    if (failed == 0)
                    {
                        ShowProfileStatus(isBatch
                            ? $"✓ 已批量删除 {deleted} 位客户。"
                            : $"已删除客户「{targets[0].DisplayName}」。");
                    }
                    else
                    {
                        ShowProfileStatus($"已删除 {deleted} 位客户，{failed} 位失败：{lastError}", isError: true);
                    }
                }
            }
            catch (Exception ex)
            {
                ShowProfileStatus($"删除异常：{ex.Message}", isError: true);
            }
            finally
            {
                AppServices.EndModalDialog();
            }
        }

        private async void AddCustomerButton_Click(object sender, RoutedEventArgs e)
        {
            try { await ShowImportDialogAsync(autoPasteClipboard: false); }
            catch (Exception ex) { ShowProfileStatus($"打开添加客户窗口失败：{ex.Message}", isError: true); }
        }

        private async void PasteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            if (AppServices.IsModalDialogOpen) return;
            if (OcrService.HasClipboardBitmapFast())
            {
                args.Handled = true;
                try { await ShowImportDialogAsync(autoPasteClipboard: true); }
                catch (Exception ex) { ShowProfileStatus($"快捷导入失败：{ex.Message}", isError: true); }
                return;
            }
            if (!await OcrService.HasClipboardImageAsync()) return;

            args.Handled = true;
            try { await ShowImportDialogAsync(autoPasteClipboard: true); }
            catch (Exception ex) { ShowProfileStatus($"快捷导入失败：{ex.Message}", isError: true); }
        }

        private async Task ShowImportDialogAsync(bool autoPasteClipboard)
        {
            if (!AppServices.TryBeginModalDialog()) return;
            try
            {
                var dialog = new ImportCustomerDialog { XamlRoot = XamlRoot };
                if (autoPasteClipboard)
                {
                    dialog.Opened += async (_, _) => await dialog.TriggerClipboardImportAsync();
                }

                await dialog.ShowAsync();
                if (dialog.CreatedCustomer is { } created)
                {
                    await LoadCustomersAsync(created.Id);
                    ShowProfileStatus(dialog.WasOverwritten
                        ? $"✓ 已覆盖更新客户「{created.DisplayName}」。"
                        : $"✓ 已成功导入新客户「{created.DisplayName}」。");
                }
                else if (dialog.TotalBatchImportedOrUpdated > 0)
                {
                    await LoadCustomersAsync(_selectedCustomer?.Id);
                    ShowProfileStatus($"✓ 微信标签批量导入完成，共新增/更新 {dialog.TotalBatchImportedOrUpdated} 位客户。");
                }
            }
            finally
            {
                AppServices.EndModalDialog();
            }
        }

        private async Task LoadCustomerMessagesAsync(CustomerDto customer)
        {
            var res = await AppServices.Backend.ListCustomerMessagesAsync(customer.Id);
            if (_selectedCustomer?.Id != customer.Id) return;

            _currentMessages.Clear();
            SetMultiSelectMode(false);

            if (!res.IsSuccess || res.Data is null)
            {
                EmptyChatTitle.Text = $"加载消息失败：{res.ErrorMessage}";
                EmptyChatHint.Visibility = Visibility.Visible;
                MultiSelectMessagesButton.IsEnabled = false;
                return;
            }

            foreach (var m in res.Data.Items.AsEnumerable().Reverse())
            {
                if (_locallyDeletedMessageIds.Contains(m.Id)) continue;
                _currentMessages.Add(new ChatBubbleItem
                {
                    Id = m.Id,
                    SenderInitial = customer.AvatarInitial,
                    SenderAvatar = customer.AvatarImage,
                    Content = m.Content,
                    TimeLabel = m.TimeLabel,
                    IsOutgoing = m.IsOutgoing
                });
            }

            RefreshMessageListEmptyState(customer.DisplayName);
            if (_currentMessages.Count > 0)
            {
                MessagesListView.ScrollIntoView(_currentMessages[^1]);
            }
        }

        // ---- Chat Bubble Right-Click & Multi-Select Delete ----

        private void RefreshMessageListEmptyState(string customerName)
        {
            bool hasMessages = _currentMessages.Count > 0;
            EmptyChatTitle.Text = $"与「{customerName}」暂无聊天记录";
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

        private void CopyBubbleMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ChatBubbleItem item) return;
            try
            {
                var pkg = new DataPackage();
                pkg.SetText(item.Content);
                Clipboard.SetContent(pkg);
                ComposerHintText.Text = "已复制该条消息内容。";
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
            RefreshMessageListEmptyState(_selectedCustomer?.DisplayName ?? string.Empty);
            ComposerHintText.Text = $"已移除 {targets.Count} 条聊天记录（待接入服务端删除接口后同步持久化）。";
        }

        // ---- Right Panel Tabs: 基本资料 / AI 总结 / AI 对话 ----

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
            bool hasCustomer = _selectedCustomer is not null;
            ProfileTabContainer.Visibility = hasCustomer && _activeRightTab == "profile" ? Visibility.Visible : Visibility.Collapsed;
            AiSummaryTabContainer.Visibility = hasCustomer && _activeRightTab == "summary" ? Visibility.Visible : Visibility.Collapsed;
            AiChatTabContainer.Visibility = hasCustomer && _activeRightTab == "aichat" ? Visibility.Visible : Visibility.Collapsed;
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

        private void LoadCustomerAiWorkspace(CustomerDto customer)
        {
            _currentAiWorkspace = AppServices.AiWorkspace.Load(customer.Id);
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
            if (_selectedCustomer is null) return;
            int msgCount = _currentMessages.Count;
            AiSummaryContextText.Text = $"基于：基本资料 · 备注 · {msgCount} 条聊天记录";
            AiChatContextTitle.Text = $"围绕「{_selectedCustomer.DisplayName}」讨论（含 {msgCount} 条对话）";
        }

        private void RenderAiSummaryState()
        {
            bool hasSummary = _currentAiWorkspace is not null && !string.IsNullOrWhiteSpace(_currentAiWorkspace.SummaryText);
            AiSummaryLoadingPanel.Visibility = Visibility.Collapsed;
            AiSummarySpinner.IsActive = false;
            AiSummaryEmptyPanel.Visibility = hasSummary ? Visibility.Collapsed : Visibility.Visible;
            AiSummaryResultScroll.Visibility = hasSummary ? Visibility.Visible : Visibility.Collapsed;
            AiSummaryFooterBar.Visibility = hasSummary ? Visibility.Visible : Visibility.Collapsed;
            RegenerateSummaryTopButton.Visibility = hasSummary ? Visibility.Visible : Visibility.Collapsed;

            if (hasSummary && _currentAiWorkspace is not null)
            {
                AiSummaryContentText.Text = _currentAiWorkspace.SummaryText;
                AiSummaryTimeText.Text = string.IsNullOrWhiteSpace(_currentAiWorkspace.SummaryUpdatedAt)
                    ? string.Empty
                    : $"更新于 {_currentAiWorkspace.SummaryUpdatedAt}";
            }
        }

        private async void GenerateAiSummaryButton_Click(object sender, RoutedEventArgs e)
        {
            var customer = _selectedCustomer;
            if (customer is null || _isAiSummaryBusy) return;

            _isAiSummaryBusy = true;
            AiSummaryEmptyPanel.Visibility = Visibility.Collapsed;
            AiSummaryResultScroll.Visibility = Visibility.Collapsed;
            AiSummaryFooterBar.Visibility = Visibility.Collapsed;
            RegenerateSummaryTopButton.Visibility = Visibility.Collapsed;
            AiSummaryLoadingPanel.Visibility = Visibility.Visible;
            AiSummarySpinner.IsActive = true;

            try
            {
                // UI-only local preview placeholder until backend OpenAI-compatible AI endpoint is wired.
                await Task.Delay(320);
                if (_selectedCustomer?.Id != customer.Id) return;

                string summary = BuildLocalPreviewSummary(customer, _currentMessages);
                string timestamp = DateTime.Now.ToString("MM-dd HH:mm");

                _currentAiWorkspace ??= new CustomerAiWorkspaceData { CustomerId = customer.Id };
                _currentAiWorkspace.SummaryText = summary;
                _currentAiWorkspace.SummaryUpdatedAt = timestamp;
                _currentAiWorkspace.SummaryContextMeta = $"基本资料 + {_currentMessages.Count} 条消息";
                AppServices.AiWorkspace.Save(_currentAiWorkspace);
            }
            finally
            {
                _isAiSummaryBusy = false;
                if (_selectedCustomer?.Id == customer.Id)
                {
                    RenderAiSummaryState();
                }
            }
        }

        private static string BuildLocalPreviewSummary(CustomerDto c, IReadOnlyCollection<ChatBubbleItem> messages)
        {
            var sb = new StringBuilder();
            sb.AppendLine("【核心画像】");
            sb.AppendLine($"• 客户：{c.DisplayName}{(string.IsNullOrWhiteSpace(c.WechatAccount) ? "" : $"（微信号：{c.WechatAccount}）")}");
            if (!string.IsNullOrWhiteSpace(c.Region) || !string.IsNullOrWhiteSpace(c.Gender))
                sb.AppendLine($"• 属性：{(string.IsNullOrWhiteSpace(c.Gender) ? "性别未知" : c.Gender)} · {(string.IsNullOrWhiteSpace(c.Region) ? "地区未填" : c.Region)}");
            if (!string.IsNullOrWhiteSpace(c.FollowStatus) || !string.IsNullOrWhiteSpace(c.Tags))
                sb.AppendLine($"• 阶段/标签：{(string.IsNullOrWhiteSpace(c.FollowStatus) ? "未设置" : c.FollowStatus)} / {(string.IsNullOrWhiteSpace(c.Tags) ? "无标签" : c.Tags)}");
            if (!string.IsNullOrWhiteSpace(c.Note))
                sb.AppendLine($"• 员工备注：{c.Note.Trim()}");

            sb.AppendLine();
            sb.AppendLine("【沟通概况】");
            if (messages.Count == 0)
            {
                sb.AppendLine("• 当前暂无已录入聊天记录，可通过中间栏“截图导入对话”补充上下文。");
            }
            else
            {
                int incoming = messages.Count(m => !m.IsOutgoing);
                int outgoing = messages.Count - incoming;
                var latest = messages.Last();
                string snippet = latest.Content.Replace("\r", " ").Replace("\n", " ");
                if (snippet.Length > 48) snippet = snippet[..48] + "...";
                sb.AppendLine($"• 已收录 {messages.Count} 条对话（客户 {incoming} 条 / 我方 {outgoing} 条）。");
                sb.AppendLine($"• 最近一条（{(latest.IsOutgoing ? "我方" : "客户")}）：{snippet}");
            }

            sb.AppendLine();
            sb.AppendLine("【后续跟进建议】");
            sb.Append("• 接入服务端 OpenAI 标准接口后，此处将自动基于上述完整上下文生成深度意向评估与跟进话术建议。");
            return sb.ToString();
        }

        private void CopyAiSummaryButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentAiWorkspace is null || string.IsNullOrWhiteSpace(_currentAiWorkspace.SummaryText)) return;
            try
            {
                var pkg = new DataPackage();
                pkg.SetText(_currentAiWorkspace.SummaryText);
                Clipboard.SetContent(pkg);
                AiSummaryNoticeText.Text = "✓ 已复制总结内容到剪贴板。";
            }
            catch (Exception ex)
            {
                AiSummaryNoticeText.Text = $"复制失败：{ex.Message}";
            }
        }

        private void UpdateAiChatEmptyState()
        {
            bool hasMsgs = _aiChatMessages.Count > 0;
            AiChatEmptyPanel.Visibility = hasMsgs ? Visibility.Collapsed : Visibility.Visible;
            AiChatListView.Visibility = hasMsgs ? Visibility.Visible : Visibility.Collapsed;
            ClearAiChatButton.IsEnabled = hasMsgs;
        }

        private async void AiQuickPrompt_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Content is string prompt)
            {
                await SendAiChatMessageAsync(prompt);
            }
        }

        private async void AiChatSendButton_Click(object sender, RoutedEventArgs e)
        {
            await SendAiChatMessageAsync(AiChatInputBox.Text);
        }

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
            var customer = _selectedCustomer;
            string prompt = rawText.Trim();
            if (customer is null || string.IsNullOrEmpty(prompt) || _isAiChatBusy) return;

            _isAiChatBusy = true;
            AiChatSendButton.IsEnabled = false;
            AiChatInputBox.Text = string.Empty;

            try
            {
                _currentAiWorkspace ??= new CustomerAiWorkspaceData { CustomerId = customer.Id };
                var userMsg = new AiChatMessageItem
                {
                    Role = "user",
                    Content = prompt,
                    Timestamp = DateTime.Now.ToString("HH:mm")
                };
                _currentAiWorkspace.Messages.Add(userMsg);
                _aiChatMessages.Add(userMsg);
                UpdateAiChatEmptyState();
                AiChatListView.ScrollIntoView(userMsg);
                AppServices.AiWorkspace.Save(_currentAiWorkspace);

                // UI-only local placeholder reply until backend AI service is enabled.
                await Task.Delay(260);
                if (_selectedCustomer?.Id != customer.Id) return;

                var reply = new AiChatMessageItem
                {
                    Role = "assistant",
                    Content = $"已载入客户「{customer.DisplayName}」完整上下文（基础资料 + 员工备注 + {_currentMessages.Count} 条聊天记录），收到你的问题：「{prompt}」。\n\n当前服务端 AI 接口尚未接入，确认后端方案后即可直连 OpenAI 标准接口进行真实回复。",
                    Timestamp = DateTime.Now.ToString("HH:mm")
                };
                _currentAiWorkspace.Messages.Add(reply);
                _aiChatMessages.Add(reply);
                AiChatListView.ScrollIntoView(reply);
                AppServices.AiWorkspace.Save(_currentAiWorkspace);
            }
            catch (Exception ex)
            {
                AppServices.Logs.Error("AI", $"本地 AI 对话 UI 异常：{ex.Message}");
            }
            finally
            {
                _isAiChatBusy = false;
                AiChatSendButton.IsEnabled = true;
            }
        }

        private void ClearAiChatButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedCustomer is null || _currentAiWorkspace is null) return;
            _currentAiWorkspace.Messages.Clear();
            _aiChatMessages.Clear();
            AppServices.AiWorkspace.Save(_currentAiWorkspace);
            UpdateAiChatEmptyState();
        }

        private async void SendMessageButton_Click(object sender, RoutedEventArgs e)
        {
            var target = _selectedCustomer;
            if (target is null) return;
            string content = MessageInputBox.Text.Trim();
            if (string.IsNullOrEmpty(content)) return;

            string direction = DirIncomingRadio.IsChecked == true ? "incoming" : "outgoing";
            SendMessageButton.IsEnabled = false;
            try
            {
                var res = await AppServices.Backend.CreateCustomerMessageAsync(target.Id, direction, content);
                if (res.IsSuccess)
                {
                    MessageInputBox.Text = string.Empty;
                    await LoadCustomersAsync(target.Id);
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
                SendMessageButton.IsEnabled = _selectedCustomer is not null;
            }
        }

        private async void ImportChatScreenshotButton_Click(object sender, RoutedEventArgs e)
        {
            var target = _selectedCustomer;
            if (target is null || !AppServices.TryBeginModalDialog()) return;
            try
            {
                var dialog = new ImportChatScreenshotDialog { XamlRoot = XamlRoot };
                var choice = await dialog.ShowAsync();
                if (choice != ContentDialogResult.Primary || dialog.ConfirmedMessages.Count == 0) return;

                int saved = 0;
                foreach (var msg in dialog.ConfirmedMessages)
                {
                    var res = await AppServices.Backend.CreateCustomerMessageAsync(target.Id, msg.Direction, msg.Content);
                    if (res.IsSuccess) saved++;
                }

                if (_selectedCustomer?.Id == target.Id)
                {
                    await LoadCustomerMessagesAsync(target);
                }
                ComposerHintText.Text = $"已从聊天截图批量导入 {saved} 条消息。";
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
                    ComposerHintText.Text = $"已识别 {ocr.Lines.Count} 行聊天文字（{ocr.ElapsedMilliseconds} ms）。";
                }
                else
                {
                    ComposerHintText.Text = "未从剪贴板图片识别出文字。";
                }
            }
            catch (Exception ex)
            {
                ComposerHintText.Text = $"识别失败：{ex.Message}";
            }
        }

        // ---- Avatar Management (Clipboard Paste / Local File / Clear) ----

        private async void PasteAvatar_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedCustomer is null) return;
            var target = _selectedCustomer;
            try
            {
                var (bmp, _) = await AppServices.Ocr.TryLoadImageFromClipboardAsync();
                if (bmp is null)
                {
                    ShowProfileStatus("剪贴板中没有图片，请先截图或复制头像后再粘贴。", isError: true);
                    return;
                }
                using (bmp)
                {
                    byte[] pngBytes = await AvatarImageService.EncodeSquarePngAsync(bmp, 256);
                    await SaveCustomerAvatarBytesAsync(target, pngBytes, "已从剪贴板保存头像。");
                }
            }
            catch (Exception ex)
            {
                ShowProfileStatus($"粘贴头像失败：{ex.Message}", isError: true);
            }
        }

        private async void UploadAvatar_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedCustomer is null) return;
            var target = _selectedCustomer;
            string? filePath = Win32WindowHelper.PickImageFilePath(AppServices.MainWindowHandle);
            if (string.IsNullOrWhiteSpace(filePath)) return;

            try
            {
                using var bmp = await Task.Run(() => AppServices.Ocr.LoadImageFileFromPathAsync(filePath));
                byte[] pngBytes = await AvatarImageService.EncodeSquarePngAsync(bmp, 256);
                await SaveCustomerAvatarBytesAsync(target, pngBytes, "已上传并保存客户头像。");
            }
            catch (Exception ex)
            {
                ShowProfileStatus($"上传头像失败：{ex.Message}", isError: true);
            }
        }

        private async void ClearAvatar_Click(object sender, RoutedEventArgs e)
        {
            var target = _selectedCustomer;
            if (target is null) return;
            try
            {
                string oldUrl = target.AvatarUrl;
                var res = await AppServices.Backend.DeleteCustomerAvatarAsync(target.Id, oldUrl);
                if (res.IsSuccess)
                {
                    target.AvatarUrl = string.Empty;
                    target.AvatarImage = null;
                    NotifyCustomerRowAvatarUpdated(target.Id);
                    if (_selectedCustomer?.Id == target.Id)
                    {
                        ApplyProfileAvatarVisual(target);
                        await LoadCustomerMessagesAsync(target);
                    }
                    ShowProfileStatus("已清除客户头像。");
                }
                else
                {
                    ShowProfileStatus($"清除头像失败：{res.ErrorMessage}", isError: true);
                }
            }
            catch (Exception ex)
            {
                ShowProfileStatus($"清除头像异常：{ex.Message}", isError: true);
            }
        }

        private async Task SaveCustomerAvatarBytesAsync(CustomerDto target, byte[] pngBytes, string successMsg)
        {
            ShowProfileStatus("正在保存头像...");
            var res = await AppServices.Backend.UploadCustomerAvatarAsync(target.Id, pngBytes);
            if (res.IsSuccess && res.Data is not null)
            {
                target.AvatarUrl = res.Data.AvatarUrl;
                var img = await AvatarImageService.BytesToBitmapImageAsync(pngBytes);
                target.AvatarImage = img;
                NotifyCustomerRowAvatarUpdated(target.Id);
                if (_selectedCustomer?.Id == target.Id)
                {
                    ApplyProfileAvatarVisual(target);
                    await LoadCustomerMessagesAsync(target);
                }
                ShowProfileStatus($"✓ {successMsg}");
            }
            else
            {
                ShowProfileStatus($"保存头像失败：{res.ErrorMessage}", isError: true);
            }
        }

        private void ShowProfileStatus(string message, bool isError = false)
        {
            ProfileStatusBanner.Background = (Brush)Application.Current.Resources[isError ? "DangerBgBrush" : "SuccessBgBrush"];
            ProfileStatusText.Foreground = (Brush)Application.Current.Resources[isError ? "DangerTextBrush" : "SuccessTextBrush"];
            ProfileStatusText.Text = message;
            ProfileStatusBanner.Visibility = Visibility.Visible;
        }
    }
}
