using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
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
        private sealed record LocalBubbleEdit(bool IsOutgoing, string Content, string ImageUrl, ImageSource? ImagePreview, byte[]? ImageBytes);
        private sealed record LocalBubbleInsert(long AnchorMessageId, Guid AnchorLocalKey, bool InsertAfter, ChatBubbleItem Item);

        private static readonly HashSet<long> SessionDeletedMessageIds = [];
        private static readonly Dictionary<long, LocalBubbleEdit> SessionEditedMessages = [];
        private static readonly Dictionary<long, List<LocalBubbleInsert>> SessionInsertedMessages = [];

        public static void ClearSessionLocalState()
        {
            SessionDeletedMessageIds.Clear();
            SessionEditedMessages.Clear();
            SessionInsertedMessages.Clear();
        }

        private readonly ObservableCollection<ChatBubbleItem> _currentMessages = [];
        private readonly EntityAiPanelController _aiPanel;
        private PendingChatImage? _pendingChatImage;
        private CancellationTokenSource? _avatarHydrationCts;
        private CancellationTokenSource? _chatImageHydrationCts;
        private string _activeRightTab = "profile";
        private bool _hasLoadedOnce;
        private long _handledSyncRevision = -1;
        private int _unreadNewMessagesCount;
        private bool _isCustomerMultiSelectMode;
        private bool _isMultiSelectMode;
        private long _nextOlderMessageCursor;
        private bool _isLoadingOlderMessages;
        private bool _isIncomingDirection;

        public CustomersPage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Required;
            _activeRightTab = AppSettingsService.NormalizeRightTab(AppServices.Settings.Current.LastCustomerRightTab);
            foreach (string tag in AppServices.Settings.Current.CollapsedCustomerTags)
            {
                if (!string.IsNullOrWhiteSpace(tag)) _collapsedTags.Add(tag);
            }
            CustomersListView.ItemsSource = _customerRows;
            MessagesListView.ItemsSource = _currentMessages;
            _aiPanel = new EntityAiPanelController(
                new EntityAiPanelControls
                {
                    SummaryErrorBanner = AiSummaryErrorBanner,
                    SummaryErrorText = AiSummaryErrorText,
                    SummaryNoticeText = AiSummaryNoticeText,
                    SummaryEmptyPanel = AiSummaryEmptyPanel,
                    SummaryResultScroll = AiSummaryResultScroll,
                    SummaryFooterBar = AiSummaryFooterBar,
                    SummaryLoadingPanel = AiSummaryLoadingPanel,
                    SummarySpinner = AiSummarySpinner,
                    SummaryMarkdownView = AiSummaryMarkdownView,
                    SummaryTimeText = AiSummaryTimeText,
                    ChatEmptyPanel = AiChatEmptyPanel,
                    ChatListView = AiChatListView,
                    ClearChatButton = ClearAiChatButton,
                    PendingImagesScroll = AiChatPendingImagesScroll,
                    PendingImagesItems = AiChatPendingImagesItems,
                    ChatInputBox = AiChatInputBox,
                    ChatSendButton = AiChatSendButton,
                    ChatAttachButton = AiChatAttachButton,
                    ChatSendIcon = AiChatSendIcon,
                    ChatSendBusyRing = AiChatSendBusyRing
                },
                new EntityAiPanelHooks
                {
                    GetActiveEntityId = () => _selectedCustomer?.Id,
                    LoadWorkspace = id => AppServices.AiWorkspace.Load(id),
                    SaveWorkspace = ws => AppServices.AiWorkspace.Save((CustomerAiWorkspaceData)ws),
                    SummarizeAsync = id => AppServices.Backend.SummarizeCustomerWithAiAsync(id),
                    ChatAsync = (id, history, prompt, imgs) => AppServices.Backend.CustomerAiChatAsync(id, history, prompt, imgs),
                    UpdateContextLabels = UpdateAiContextLabels,
                    PendingThinkingText = "正在查阅记录并思考回复...",
                    LogPrefix = "客户 AI"
                });
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
            AppServices.Settings.ThemeChanged -= OnSettingsThemeChanged;
            AppServices.WeChat4Sync.SyncCompleted -= OnWeChatSyncCompleted;
            AppServices.WeChat4Sync.AccountsUpdated -= OnWeChatAccountsUpdated;
            AppServices.Settings.ThemeChanged += OnSettingsThemeChanged;
            AppServices.WeChat4Sync.SyncCompleted += OnWeChatSyncCompleted;
            AppServices.WeChat4Sync.AccountsUpdated += OnWeChatAccountsUpdated;
            UpdateRightTabVisuals();
            UpdateDirectionToggleVisuals();
            if (AppServices.WeChat4Sync.KnownAccounts.Count == 0 && AppServices.Backend.IsAuthenticated)
            {
                _ = AppServices.WeChat4Sync.RefreshMonitoredCountsAsync();
            }
            ChatPageSharedHelper.AttachScrollNearBottomWatcher(MessagesListView, ClearNewMessagesJumpHint);
        }

        private void CustomersPage_Unloaded(object sender, RoutedEventArgs e)
        {
            AppServices.Settings.ThemeChanged -= OnSettingsThemeChanged;
            AppServices.WeChat4Sync.SyncCompleted -= OnWeChatSyncCompleted;
            AppServices.WeChat4Sync.AccountsUpdated -= OnWeChatAccountsUpdated;
            _avatarHydrationCts?.Cancel();
            _avatarHydrationCts?.Dispose();
            _avatarHydrationCts = null;
            _chatImageHydrationCts?.Cancel();
            _chatImageHydrationCts?.Dispose();
            _chatImageHydrationCts = null;
        }

        private void OnWeChatAccountsUpdated()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_hasLoadedOnce)
                {
                    ApplyFilter(_selectedCustomer?.Id, forceReloadDetail: false);
                }
                if (_selectedCustomer is not null)
                {
                    UpdateSelectedCustomerUi();
                }
            });
        }

        private void OnWeChatSyncCompleted(Client.Services.WeChat4Db.WeChat4SyncReport report)
        {
            if (report.CustomersCreated == 0 && report.CustomersUpdated == 0 && report.CustomerMessagesInserted == 0 && report.AvatarsUploaded == 0)
                return;
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    _handledSyncRevision = AppServices.WeChat4Sync.SyncRevision;
                    await RefreshAfterSyncAsync();
                }
                catch { }
            });
        }

        private async Task RefreshAfterSyncAsync()
        {
            await LoadCustomersAsync(_selectedCustomer?.Id, preserveCurrentDetail: true);
            if (_selectedCustomer is not null)
            {
                await RefreshCustomerMessagesIncrementallyAsync(_selectedCustomer);
            }
        }

        private async void SyncTagsNowButton_Click(object sender, RoutedEventArgs e)
        {
            SyncTagsNowButton.IsEnabled = false;
            try
            {
                ShowProfileStatus("正在同步已导入客户的最新微信资料与聊天记录...");
                var (ok, msg, _) = await AppServices.WeChat4Sync.SyncImportedTargetsAsync(
                    syncCustomers: true,
                    syncGroups: false);
                _handledSyncRevision = AppServices.WeChat4Sync.SyncRevision;
                await RefreshAfterSyncAsync();
                if (!ok)
                {
                    ShowProfileStatus(msg, isError: true);
                    return;
                }
                ShowProfileStatus($"✓ {msg}");
            }
            catch (Exception ex)
            {
                ShowProfileStatus($"同步失败：{ex.Message}", isError: true);
            }
            finally
            {
                SyncTagsNowButton.IsEnabled = true;
            }
        }

        private void OnSettingsThemeChanged(bool _)
        {
            UpdateRightTabVisuals();
            UpdateDirectionToggleVisuals();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            try
            {
                if (!_hasLoadedOnce)
                {
                    _hasLoadedOnce = true;
                    _handledSyncRevision = AppServices.WeChat4Sync.SyncRevision;
                    long? lastId = AppServices.Settings.Current.LastSelectedCustomerId > 0
                        ? AppServices.Settings.Current.LastSelectedCustomerId
                        : null;
                    await LoadCustomersAsync(lastId);
                }
                else if (_handledSyncRevision != AppServices.WeChat4Sync.SyncRevision)
                {
                    _handledSyncRevision = AppServices.WeChat4Sync.SyncRevision;
                    await RefreshAfterSyncAsync();
                }
                else
                {
                    StartAvatarHydration(_allCustomers);
                }
            }
            catch (Exception ex) { AppServices.Logs.Error("UI", $"加载客户列表异常：{ex.Message}"); }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (CustomersListView is null) return;
            ApplyFilter(_selectedCustomer?.Id, forceReloadDetail: false);
        }

        private async Task LoadCustomersAsync(long? selectId = null, bool preserveCurrentDetail = false)
        {
            try
            {
                var prevAvatars = _allCustomers
                    .Where(c => c.AvatarImage is not null && !string.IsNullOrWhiteSpace(c.AvatarUrl))
                    .ToDictionary(c => c.Id, c => (c.AvatarUrl, c.AvatarImage!));

                var items = await AppServices.Backend.ListAllCustomersAsync(2000);
                foreach (var item in items)
                {
                    if (prevAvatars.TryGetValue(item.Id, out var cached) &&
                        string.Equals(cached.AvatarUrl, item.AvatarUrl, StringComparison.Ordinal))
                    {
                        item.AvatarImage = cached.Item2;
                    }
                }

                _allCustomers = items;
                var validIds = _allCustomers.Select(c => c.Id).ToHashSet();
                _selectedCustomerIds.RemoveWhere(id => !validIds.Contains(id));
                ApplyFilter(selectId, forceReloadDetail: !preserveCurrentDetail);
                if (preserveCurrentDetail && _selectedCustomer is not null)
                {
                    UpdateSelectedCustomerUi();
                }
                StartAvatarHydration(_allCustomers);
            }
            catch (Exception ex)
            {
                ShowProfileStatus($"拉取客户列表失败：{ex.Message}", isError: true);
            }
        }

        private void StartAvatarHydration(List<CustomerDto> customers)
        {
            _avatarHydrationCts?.Cancel();
            _avatarHydrationCts?.Dispose();
            var cts = new CancellationTokenSource();
            _avatarHydrationCts = cts;
            _ = ChatPageSharedHelper.HydrateAvatarsConcurrentlyAsync(
                customers,
                c => c.AvatarUrl,
                c => c.AvatarImage is not null,
                (c, img) =>
                {
                    c.AvatarImage = img;
                    NotifyCustomerRowAvatarUpdated(c.Id);
                    if (_selectedCustomer?.Id == c.Id)
                    {
                        ApplyProfileAvatarVisual(c);
                        ApplyCustomerAvatarToCurrentBubbles(img);
                    }
                },
                DispatcherQueue,
                cts.Token,
                maxConcurrency: 4,
                logLabel: "客户头像");
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
            string activeWxid = AppServices.WeChat4Sync.ActiveOwnerWxid;
            IEnumerable<CustomerDto> baseList = string.IsNullOrWhiteSpace(activeWxid)
                ? _allCustomers
                : _allCustomers.Where(c =>
                    string.IsNullOrWhiteSpace(c.OwnerWxid) ||
                    string.Equals(c.OwnerWxid, activeWxid, StringComparison.OrdinalIgnoreCase));

            string q = SearchBox.Text.Trim();
            _filteredCustomers = string.IsNullOrEmpty(q)
                ? baseList.ToList()
                : baseList.Where(c =>
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

            bool isInitialRestore = _selectedCustomer is null
                && preferredSelectId.HasValue
                && nextSelected is not null
                && nextSelected.Id == preferredSelectId.Value
                && string.IsNullOrEmpty(q);
            bool selectionChanged = _selectedCustomer?.Id != nextSelected?.Id;
            _selectedCustomer = nextSelected;

            if (isInitialRestore)
            {
                // Ensure the restored customer's tag group is visible in the left list on initial open.
                _collapsedTags.Remove(GetPrimaryTag(nextSelected!));
            }

            RebuildCustomerRows(expandAllForSearch: !string.IsNullOrEmpty(q));
            CustomersListView.Visibility = _filteredCustomers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            EmptyListHint.Visibility = _filteredCustomers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateCustomerMultiSelectSummary();

            if (nextSelected is not null && (selectionChanged || forceReloadDetail))
            {
                var activeRow = _customerRows.FirstOrDefault(r => !r.IsGroupHeader && r.Customer?.Id == nextSelected.Id);
                if (activeRow is not null)
                {
                    CustomersListView.ScrollIntoView(activeRow);
                }
            }

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
            ClearPendingChatImage();
            ClearNewMessagesJumpHint();
            UpdateSelectedCustomerUi();

            if (selected is not null)
            {
                if (AppServices.Settings.Current.LastSelectedCustomerId != selected.Id)
                {
                    AppServices.Settings.Current.LastSelectedCustomerId = selected.Id;
                    AppServices.Settings.Save();
                }
                _aiPanel.LoadForEntity(selected.Id);
                try { await LoadCustomerMessagesAsync(selected); }
                catch (Exception ex) { AppServices.Logs.Error("UI", $"加载聊天记录异常：{ex.Message}"); }
                UpdateAiContextLabels();
            }
            else
            {
                _currentMessages.Clear();
                _nextOlderMessageCursor = 0;
                LoadOlderMessagesBar.Visibility = Visibility.Collapsed;
                _aiPanel.ClearForNoEntity();
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
            UploadChatImageButton.IsEnabled = hasCustomer;
            SearchMessagesButton.IsEnabled = hasCustomer;
            EditProfileButton.IsEnabled = hasCustomer;
            DeleteCustomerButton.IsEnabled = hasCustomer;
            EmptyProfileHint.Visibility = hasCustomer ? Visibility.Collapsed : Visibility.Visible;
            UpdateRightTabVisibility();

            if (c is null)
            {
                ActiveCustomerTitle.Text = "请选择左侧客户";
                ActiveCustomerMeta.Text = "点击左上角「+」按微信标签挑选好友导入，导入后自动监听消息更新";
                return;
            }

            string ownerWeChatLabel = !string.IsNullOrWhiteSpace(c.OwnerWxid)
                ? AppServices.WeChat4Sync.ResolveAccountDisplayName(c.OwnerWxid)
                : string.Empty;

            ActiveCustomerTitle.Text = c.DisplayName;
            string singleLineRemark = c.WechatRemark.Replace("\r", " ").Replace("\n", " ").Trim();
            string cleanNickname = c.WechatName.Trim();
            var metaParts = new[]
            {
                string.IsNullOrWhiteSpace(singleLineRemark) || string.IsNullOrWhiteSpace(cleanNickname) || singleLineRemark.Equals(cleanNickname, StringComparison.OrdinalIgnoreCase) ? "" : $"昵称: {cleanNickname}",
                string.IsNullOrWhiteSpace(c.WechatAccount) ? "" : $"微信号: {c.WechatAccount}",
                string.IsNullOrWhiteSpace(c.Region) ? "" : $"地区: {c.Region}",
                string.IsNullOrWhiteSpace(c.Phone) ? "" : $"电话: {c.Phone}",
                (AppServices.WeChat4Sync.KnownAccounts.Count >= 2 && !string.IsNullOrWhiteSpace(ownerWeChatLabel)) ? $"所属微信: {ownerWeChatLabel}" : ""
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
            ViewAddedDateText.Text = string.IsNullOrWhiteSpace(c.AddedDate) ? "-" : c.AddedDate;
            if (!string.IsNullOrWhiteSpace(ownerWeChatLabel))
            {
                ViewOwnerWeChatText.Text = ownerWeChatLabel;
                ViewOwnerWeChatRow.Visibility = Visibility.Visible;
            }
            else
            {
                ViewOwnerWeChatRow.Visibility = Visibility.Collapsed;
            }

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

        private void ApplyCustomerAvatarToCurrentBubbles(ImageSource? avatar)
        {
            foreach (var bubble in _currentMessages)
            {
                if (!bubble.IsOutgoing)
                {
                    bubble.SenderAvatar = avatar;
                }
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
                        ApplyCustomerAvatarToCurrentBubbles(img);
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
            if (!AppServices.TryBeginModalDialog()) return;
            try
            {
                var dialog = new ImportWeChatFriendsDialog(_allCustomers)
                {
                    XamlRoot = XamlRoot
                };
                await dialog.ShowAsync();

                if (dialog.CompletedReport is { } report)
                {
                    await LoadCustomersAsync(_selectedCustomer?.Id);
                    if (_selectedCustomer is not null)
                    {
                        await LoadCustomerMessagesAsync(_selectedCustomer);
                    }
                    ShowProfileStatus($"✓ 已导入微信好友：新增 {report.CustomersCreated} 位 / 更新 {report.CustomersUpdated} 位，同步消息 +{report.CustomerMessagesInserted} 条。");
                }
            }
            catch (Exception ex)
            {
                ShowProfileStatus($"打开添加客户弹窗失败：{ex.Message}", isError: true);
            }
            finally
            {
                AppServices.EndModalDialog();
            }
        }

        private async Task LoadCustomerMessagesAsync(CustomerDto customer)
        {
            _nextOlderMessageCursor = 0;
            _isLoadingOlderMessages = false;
            LoadOlderMessagesBar.Visibility = Visibility.Collapsed;

            var res = await AppServices.Backend.ListCustomerMessagesAsync(customer.Id, limit: 100);
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

            _nextOlderMessageCursor = res.Data.NextCursor;
            UpdateLoadOlderMessagesBar();

            foreach (var m in res.Data.Items.AsEnumerable().Reverse())
            {
                if (SessionDeletedMessageIds.Contains(m.Id)) continue;
                _currentMessages.Add(BuildCustomerBubbleItem(customer, m));
            }

            // Re-apply session-local inserted bubbles around their anchors
            if (SessionInsertedMessages.TryGetValue(customer.Id, out var inserts))
            {
                foreach (var ins in inserts)
                {
                    int anchorIdx = FindAnchorIndex(ins.AnchorMessageId, ins.AnchorLocalKey);
                    int targetIdx = anchorIdx >= 0
                        ? (ins.InsertAfter ? anchorIdx + 1 : anchorIdx)
                        : _currentMessages.Count;
                    targetIdx = Math.Clamp(targetIdx, 0, _currentMessages.Count);
                    _currentMessages.Insert(targetIdx, ins.Item);
                }
            }

            StartChatImageHydration(_currentMessages.ToList());
            RefreshMessageListEmptyState(customer.DisplayName);
            ClearNewMessagesJumpHint();
            if (_currentMessages.Count > 0)
            {
                MessagesListView.ScrollIntoView(_currentMessages[^1]);
            }
        }

        private async Task RefreshCustomerMessagesIncrementallyAsync(CustomerDto customer)
        {
            if (_currentMessages.Count == 0)
            {
                await LoadCustomerMessagesAsync(customer);
                return;
            }

            bool wasNearBottom = ChatPageSharedHelper.IsListNearBottom(MessagesListView);
            var res = await AppServices.Backend.ListCustomerMessagesAsync(customer.Id, limit: 100);
            if (_selectedCustomer?.Id != customer.Id || !res.IsSuccess || res.Data is null) return;

            var existingById = new Dictionary<long, ChatBubbleItem>();
            long maxExistingServerId = 0;
            foreach (var item in _currentMessages)
            {
                if (item.Id > 0)
                {
                    existingById[item.Id] = item;
                    if (item.Id > maxExistingServerId) maxExistingServerId = item.Id;
                }
            }

            var bubblesToHydrate = new List<ChatBubbleItem>();
            int appendedCount = 0;

            foreach (var m in res.Data.Items.AsEnumerable().Reverse())
            {
                if (SessionDeletedMessageIds.Contains(m.Id)) continue;

                if (existingById.TryGetValue(m.Id, out var existing))
                {
                    // In-place media/content backfill without disturbing scroll position
                    if (!SessionEditedMessages.ContainsKey(m.Id))
                    {
                        if (!string.IsNullOrWhiteSpace(m.Content) && !string.Equals(existing.Content, m.Content, StringComparison.Ordinal))
                        {
                            existing.Content = m.Content;
                        }
                        if (string.IsNullOrWhiteSpace(existing.AudioUrl) && !string.IsNullOrWhiteSpace(m.AudioUrl))
                        {
                            existing.AudioUrl = m.AudioUrl;
                        }
                        if (string.IsNullOrWhiteSpace(existing.ImageUrl) && !string.IsNullOrWhiteSpace(m.ImageUrl))
                        {
                            existing.ImageUrl = m.ImageUrl;
                            bubblesToHydrate.Add(existing);
                        }
                    }
                    continue;
                }

                if (m.Id > maxExistingServerId)
                {
                    var bubble = BuildCustomerBubbleItem(customer, m);
                    _currentMessages.Add(bubble);
                    if (!string.IsNullOrWhiteSpace(bubble.ImageUrl))
                    {
                        bubblesToHydrate.Add(bubble);
                    }
                    appendedCount++;
                }
            }

            if (bubblesToHydrate.Count > 0)
            {
                _ = ChatPageSharedHelper.HydrateChatBubbleImagesConcurrentlyAsync(
                    bubblesToHydrate,
                    DispatcherQueue,
                    _chatImageHydrationCts?.Token ?? CancellationToken.None,
                    maxConcurrency: 4,
                    logLabel: "聊天图片");
            }

            if (appendedCount > 0)
            {
                RefreshMessageListEmptyState(customer.DisplayName);
                if (wasNearBottom)
                {
                    ClearNewMessagesJumpHint();
                    MessagesListView.ScrollIntoView(_currentMessages[^1]);
                }
                else
                {
                    _unreadNewMessagesCount += appendedCount;
                    NewMessagesJumpText.Text = $"有 {_unreadNewMessagesCount} 条新消息";
                    NewMessagesJumpButton.Visibility = Visibility.Visible;
                }
            }
        }

        private void ClearNewMessagesJumpHint()
        {
            _unreadNewMessagesCount = 0;
            if (NewMessagesJumpButton is not null)
            {
                NewMessagesJumpButton.Visibility = Visibility.Collapsed;
            }
        }

        private void NewMessagesJumpButton_Click(object sender, RoutedEventArgs e)
        {
            ClearNewMessagesJumpHint();
            if (_currentMessages.Count > 0)
            {
                MessagesListView.ScrollIntoView(_currentMessages[^1]);
            }
        }

        private ChatBubbleItem BuildCustomerBubbleItem(CustomerDto customer, MessageDto m)
        {
            bool isOutgoing = m.IsOutgoing;
            string content = m.Content;
            string imageUrl = m.ImageUrl;
            ImageSource? cachedImg = null;
            byte[]? cachedBytes = null;
            if (SessionEditedMessages.TryGetValue(m.Id, out var patch))
            {
                isOutgoing = patch.IsOutgoing;
                content = patch.Content;
                imageUrl = patch.ImageUrl;
                cachedImg = patch.ImagePreview;
                cachedBytes = patch.ImageBytes;
            }
            return new ChatBubbleItem
            {
                Id = m.Id,
                SenderInitial = customer.AvatarInitial,
                SenderAvatar = customer.AvatarImage,
                Content = content,
                ImageUrl = imageUrl,
                AudioUrl = m.AudioUrl,
                VideoUrl = m.VideoUrl,
                BubbleImage = cachedImg,
                BubbleImageBytes = cachedBytes,
                TimeLabel = m.TimeLabel,
                IsOutgoing = isOutgoing,
                IsSelectionMode = _isMultiSelectMode
            };
        }

        private void UpdateLoadOlderMessagesBar()
        {
            LoadOlderMessagesBar.Visibility = _nextOlderMessageCursor > 0 ? Visibility.Visible : Visibility.Collapsed;
            LoadOlderMessagesButton.IsEnabled = !_isLoadingOlderMessages && _nextOlderMessageCursor > 0;
            LoadOlderMessagesText.Text = _isLoadingOlderMessages ? "正在加载更早记录..." : "查看更早聊天记录";
        }

        private async void LoadOlderMessagesButton_Click(object sender, RoutedEventArgs e)
        {
            var customer = _selectedCustomer;
            if (customer is null || _isLoadingOlderMessages || _nextOlderMessageCursor <= 0) return;

            _isLoadingOlderMessages = true;
            UpdateLoadOlderMessagesBar();
            try
            {
                var anchor = _currentMessages.Count > 0 ? _currentMessages[0] : null;
                var res = await AppServices.Backend.ListCustomerMessagesAsync(customer.Id, limit: 100, cursor: _nextOlderMessageCursor);
                if (_selectedCustomer?.Id != customer.Id) return;

                if (res.IsSuccess && res.Data is not null)
                {
                    var existingIds = _currentMessages.Where(x => x.Id > 0).Select(x => x.Id).ToHashSet();
                    var olderBubbles = new List<ChatBubbleItem>();
                    foreach (var m in res.Data.Items.AsEnumerable().Reverse())
                    {
                        if (SessionDeletedMessageIds.Contains(m.Id) || existingIds.Contains(m.Id)) continue;
                        olderBubbles.Add(BuildCustomerBubbleItem(customer, m));
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
                AppServices.Logs.Warn("UI", $"加载更早聊天记录失败：{ex.Message}");
            }
            finally
            {
                _isLoadingOlderMessages = false;
                UpdateLoadOlderMessagesBar();
            }
        }

        private async void SearchMessagesButton_Click(object sender, RoutedEventArgs e)
        {
            var customer = _selectedCustomer;
            if (customer is null || !AppServices.TryBeginModalDialog()) return;

            long jumpMsgId = 0;
            try
            {
                var dialog = new ChatHistorySearchDialog(
                    isGroup: false,
                    targetId: customer.Id,
                    conversationTitle: customer.DisplayName,
                    peerFallbackName: customer.DisplayName)
                {
                    XamlRoot = XamlRoot
                };
                await dialog.ShowAsync();
                jumpMsgId = dialog.SelectedJumpMessageId;
            }
            catch (Exception ex)
            {
                AppServices.Logs.Error("UI", $"打开客户聊天记录搜索弹窗失败：{ex.Message}");
            }
            finally
            {
                AppServices.EndModalDialog();
            }

            if (jumpMsgId > 0 && _selectedCustomer?.Id == customer.Id)
            {
                await JumpToCustomerMessageAsync(customer, jumpMsgId);
            }
        }

        private async Task JumpToCustomerMessageAsync(CustomerDto customer, long targetMessageId)
        {
            var found = _currentMessages.FirstOrDefault(m => m.Id == targetMessageId);
            int guardPages = 0;
            while (found is null && _nextOlderMessageCursor > 0 && guardPages < 35 && _selectedCustomer?.Id == customer.Id)
            {
                guardPages++;
                var res = await AppServices.Backend.ListCustomerMessagesAsync(customer.Id, limit: 100, cursor: _nextOlderMessageCursor);
                if (_selectedCustomer?.Id != customer.Id || !res.IsSuccess || res.Data is null || res.Data.Items.Count == 0)
                {
                    break;
                }

                var existingIds = _currentMessages.Where(x => x.Id > 0).Select(x => x.Id).ToHashSet();
                var olderBubbles = new List<ChatBubbleItem>();
                foreach (var m in res.Data.Items.AsEnumerable().Reverse())
                {
                    if (SessionDeletedMessageIds.Contains(m.Id) || existingIds.Contains(m.Id)) continue;
                    olderBubbles.Add(BuildCustomerBubbleItem(customer, m));
                }
                for (int i = 0; i < olderBubbles.Count; i++)
                {
                    _currentMessages.Insert(i, olderBubbles[i]);
                }
                _nextOlderMessageCursor = res.Data.NextCursor;
                if (olderBubbles.Count > 0)
                {
                    _ = HydrateChatBubbleImagesAsync(olderBubbles, _chatImageHydrationCts?.Token ?? CancellationToken.None);
                }
                found = _currentMessages.FirstOrDefault(m => m.Id == targetMessageId);
            }

            UpdateLoadOlderMessagesBar();
            if (found is not null)
            {
                MessagesListView.ScrollIntoView(found, ScrollIntoViewAlignment.Leading);
                DispatcherQueue.TryEnqueue(() => MessagesListView.ScrollIntoView(found, ScrollIntoViewAlignment.Leading));
            }
        }

        private void StartChatImageHydration(List<ChatBubbleItem> bubbles)
        {
            _chatImageHydrationCts?.Cancel();
            _chatImageHydrationCts?.Dispose();
            var cts = new CancellationTokenSource();
            _chatImageHydrationCts = cts;
            _ = ChatPageSharedHelper.HydrateChatBubbleImagesConcurrentlyAsync(
                bubbles, DispatcherQueue, cts.Token, maxConcurrency: 4, logLabel: "聊天图片");
        }

        private Task HydrateChatBubbleImagesAsync(List<ChatBubbleItem> bubbles, CancellationToken ct) =>
            ChatPageSharedHelper.HydrateChatBubbleImagesConcurrentlyAsync(
                bubbles, DispatcherQueue, ct, maxConcurrency: 4, logLabel: "聊天图片");

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

        private int FindAnchorIndex(long messageId, Guid localKey)
        {
            for (int i = 0; i < _currentMessages.Count; i++)
            {
                if (messageId > 0 && _currentMessages[i].Id == messageId) return i;
                if (messageId == 0 && _currentMessages[i].LocalKey == localKey) return i;
            }
            return -1;
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
            await ChatPageSharedHelper.OpenBubbleImageViewerAsync(item, _selectedCustomer?.DisplayName, "聊天图片");
        }

        private void BubbleContextFlyout_Opening(object sender, object e) =>
            ChatPageSharedHelper.ConfigureBubbleContextFlyout(sender);

        private async void TranscribeVoiceBubbleMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ChatBubbleItem item) return;
            var customer = _selectedCustomer;
            ComposerHintText.Text = "正在将语音转为文字...";
            ComposerHintText.Text = await ChatPageSharedHelper.TranscribeVoiceBubbleAsync(
                item,
                customer is not null
                    ? async mergedContent =>
                    {
                        string dir = item.IsOutgoing ? "outgoing" : "incoming";
                        var patchRes = await AppServices.Backend.UpdateCustomerMessageAsync(
                            customer.Id, item.Id, dir, mergedContent, item.ImageUrl);
                        if (patchRes.IsSuccess)
                        {
                            SessionEditedMessages.Remove(item.Id);
                            return (true, string.Empty);
                        }
                        return (false, patchRes.ErrorMessage);
                    }
                    : null);
        }

        private async void EditBubbleMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ChatBubbleItem item) return;
            var customer = _selectedCustomer;
            var res = await ChatBubbleEditorHelper.ShowAsync(
                XamlRoot,
                title: "编辑对话消息",
                primaryButtonText: "保存修改",
                initialIsOutgoing: item.IsOutgoing,
                initialContent: item.Content,
                isGroupChat: false,
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
            item.Content = res.Content;
            item.ImageUrl = finalImageUrl;
            item.BubbleImageBytes = finalBytes;
            item.BubbleImage = finalPreview;

            if (item.Id > 0)
            {
                SessionEditedMessages[item.Id] = new LocalBubbleEdit(res.IsOutgoing, res.Content, finalImageUrl, finalPreview, finalBytes);
                if (customer is not null)
                {
                    string dir = res.IsOutgoing ? "outgoing" : "incoming";
                    var patchRes = await AppServices.Backend.UpdateCustomerMessageAsync(customer.Id, item.Id, dir, res.Content, finalImageUrl);
                    if (patchRes.IsSuccess)
                    {
                        SessionEditedMessages.Remove(item.Id);
                    }
                }
            }
            ComposerHintText.Text = "已修改该条消息。";
        }

        private async void InsertAboveBubbleMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ChatBubbleItem anchor) return;
            await InsertBubbleRelativeToAsync(anchor, insertAfter: false);
        }

        private async void InsertBelowBubbleMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ChatBubbleItem anchor) return;
            await InsertBubbleRelativeToAsync(anchor, insertAfter: true);
        }

        private async Task InsertBubbleRelativeToAsync(ChatBubbleItem anchor, bool insertAfter)
        {
            var customer = _selectedCustomer;
            if (customer is null) return;

            var res = await ChatBubbleEditorHelper.ShowAsync(
                XamlRoot,
                title: insertAfter ? "在下方插入对话消息" : "在上方插入对话消息",
                primaryButtonText: "确认插入",
                initialIsOutgoing: anchor.IsOutgoing,
                initialContent: string.Empty,
                isGroupChat: false);
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

            var newBubble = new ChatBubbleItem
            {
                Id = 0,
                SenderInitial = customer.AvatarInitial,
                SenderAvatar = customer.AvatarImage,
                Content = res.Content,
                ImageUrl = uploadedUrl,
                BubbleImageBytes = res.NewImageBytes,
                BubbleImage = res.ImagePreview,
                TimeLabel = string.IsNullOrWhiteSpace(anchor.TimeLabel) ? DateTime.Now.ToString("HH:mm") : anchor.TimeLabel,
                IsOutgoing = res.IsOutgoing,
                IsSelectionMode = _isMultiSelectMode
            };

            int insertIdx = insertAfter ? idx + 1 : idx;
            _currentMessages.Insert(insertIdx, newBubble);

            if (!SessionInsertedMessages.TryGetValue(customer.Id, out var list))
            {
                list = [];
                SessionInsertedMessages[customer.Id] = list;
            }
            list.Add(new LocalBubbleInsert(anchor.Id, anchor.LocalKey, insertAfter, newBubble));

            RefreshMessageListEmptyState(customer.DisplayName);
            ComposerHintText.Text = insertAfter ? "已在下方插入 1 条消息。" : "已在上方插入 1 条消息。";
        }

        private async void CopyBubbleMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ChatBubbleItem item) return;
            ComposerHintText.Text = await ChatPageSharedHelper.CopyBubbleToClipboardAsync(item);
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
            long? cid = _selectedCustomer?.Id;
            foreach (var item in targets)
            {
                if (item.Id > 0)
                {
                    SessionDeletedMessageIds.Add(item.Id);
                    SessionEditedMessages.Remove(item.Id);
                }
                else if (cid.HasValue && SessionInsertedMessages.TryGetValue(cid.Value, out var inserts))
                {
                    inserts.RemoveAll(x => x.Item.LocalKey == item.LocalKey);
                }
                _currentMessages.Remove(item);
            }
            RefreshMessageListEmptyState(_selectedCustomer?.DisplayName ?? string.Empty);
            ComposerHintText.Text = $"已移除 {targets.Count} 条聊天记录。";
        }

        // ---- Right Panel Tabs: 基本资料 / AI 总结 / AI 对话 ----

        private void RightTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tab)
            {
                string normalized = AppSettingsService.NormalizeRightTab(tab);
                _activeRightTab = normalized;
                if (!string.Equals(AppServices.Settings.Current.LastCustomerRightTab, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    AppServices.Settings.Current.LastCustomerRightTab = normalized;
                    AppServices.Settings.Save();
                }
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

        private void UpdateRightTabVisuals() =>
            ChatPageSharedHelper.ApplyRightTabVisuals(
                _activeRightTab,
                RightTabProfileIcon, RightTabProfileText, RightTabProfileLine,
                RightTabSummaryIcon, RightTabSummaryText, RightTabSummaryLine,
                RightTabChatIcon, RightTabChatText, RightTabChatLine);

        private void UpdateAiContextLabels()
        {
            if (_selectedCustomer is null) return;
            int msgCount = _currentMessages.Count;
            AiSummaryContextText.Text = $"{msgCount} 条对话";
            AiChatContextTitle.Text = $"{_selectedCustomer.DisplayName} · {msgCount} 条对话";
        }

        private async void GenerateAiSummaryButton_Click(object sender, RoutedEventArgs e) =>
            await _aiPanel.GenerateSummaryAsync();

        private void CopyAiSummaryButton_Click(object sender, RoutedEventArgs e) =>
            _aiPanel.CopySummary();

        private void CopyAiChatMessageMenuItem_Click(object sender, RoutedEventArgs e) =>
            _aiPanel.CopyChatMessage(sender);

        private async void AiQuickPrompt_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Content is string prompt)
            {
                await _aiPanel.SendMessageAsync(prompt);
            }
        }

        private async void UploadCustomerAiImage_Click(object sender, RoutedEventArgs e) =>
            await _aiPanel.UploadImageAsync();

        private async void PasteCustomerAiImage_Click(object sender, RoutedEventArgs e) =>
            await _aiPanel.PasteClipboardImageAsync();

        private async void AiChatInputBox_Paste(object sender, TextControlPasteEventArgs e) =>
            await _aiPanel.HandleInputPasteAsync(e);

        private void RemoveCustomerAiImage_Click(object sender, RoutedEventArgs e) =>
            _aiPanel.RemovePendingImage(sender);

        private async void AiChatSendButton_Click(object sender, RoutedEventArgs e) =>
            await _aiPanel.SendMessageAsync(AiChatInputBox.Text);

        private async void AiChatInputBox_KeyDown(object sender, KeyRoutedEventArgs e) =>
            await _aiPanel.HandleInputKeyDownAsync(e);

        private void ClearAiChatButton_Click(object sender, RoutedEventArgs e) =>
            _aiPanel.ClearChat();

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
        }

        private async void MessageInputBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter) return;
            var shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
            if ((shiftState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down) return;

            e.Handled = true;
            await SendCurrentCustomerMessageAsync();
        }

        private async void SendMessageButton_Click(object sender, RoutedEventArgs e)
        {
            await SendCurrentCustomerMessageAsync();
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
            if (_selectedCustomer is null) return;
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
            if (_selectedCustomer is null) return;
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
                        ComposerHintText.Text = "已从剪贴板附加图片，发送即可插入聊天记录。";
                    }
                }
            }
            catch
            {
            }
        }

        private async Task SendCurrentCustomerMessageAsync()
        {
            var target = _selectedCustomer;
            if (target is null) return;
            string content = MessageInputBox.Text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            var pendingImage = _pendingChatImage;
            if (string.IsNullOrEmpty(content) && pendingImage is null) return;

            string direction = _isIncomingDirection ? "incoming" : "outgoing";
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

                var res = await AppServices.Backend.CreateCustomerMessageAsync(target.Id, direction, content, imageUrl);
                if (res.IsSuccess)
                {
                    MessageInputBox.Text = string.Empty;
                    ClearPendingChatImage();
                    ComposerHintText.Text = string.Empty;
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

        // ---- Avatar Management (Clipboard Paste / Local File / Clear) ----

        private async void PasteAvatar_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedCustomer is null) return;
            var target = _selectedCustomer;
            try
            {
                var (bmp, _) = await OcrService.TryLoadImageFromClipboardAsync();
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
                using var bmp = await Task.Run(() => OcrService.LoadImageFileFromPathAsync(filePath));
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
                        ApplyCustomerAvatarToCurrentBubbles(null);
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
                    ApplyCustomerAvatarToCurrentBubbles(img);
                }
                ShowProfileStatus($"✓ {successMsg}");
            }
            else
            {
                ShowProfileStatus($"保存头像失败：{res.ErrorMessage}", isError: true);
            }
        }

        private static void ShowProfileStatus(string message, bool isError = false)
        {
            WorkbenchPage.Current?.ShowToast(message, isError);
        }
    }
}
