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
    public sealed partial class CustomersPage : Page
    {
        private List<CustomerDto> _allCustomers = [];
        private CustomerDto? _selectedCustomer;
        private CancellationTokenSource? _avatarHydrationCts;
        private bool _isEditing;

        public CustomersPage()
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
            _ = ComposerResizeController.Attach(ChatStageGrid, ComposerRow, ComposerSplitter, minHeight: 120);
            Unloaded += (_, _) =>
            {
                _avatarHydrationCts?.Cancel();
                _avatarHydrationCts?.Dispose();
                _avatarHydrationCts = null;
            };
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
            ApplyFilter(_selectedCustomer?.Id);
        }

        private static readonly string[] FollowStatusOptions = ["未设置", "新线索", "跟进中", "高意向", "已报价", "已成交", "暂缓/流失"];

        private async Task LoadCustomersAsync(long? selectId = null)
        {
            var result = await AppServices.Backend.ListCustomersAsync();
            if (result.IsSuccess && result.Data is not null)
            {
                _allCustomers = result.Data.Items;
                ApplyFilter(selectId);
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

        private void ApplyFilter(long? preferredSelectId = null)
        {
            string q = SearchBox.Text.Trim();
            var filtered = string.IsNullOrEmpty(q)
                ? _allCustomers
                : _allCustomers.Where(c =>
                    c.WechatRemark.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    c.WechatName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    c.WechatAccount.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    c.Tags.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    c.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    c.Note.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    c.Phone.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

            CustomersListView.ItemsSource = filtered;
            EmptyListHint.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (filtered.Count > 0)
            {
                int idx = preferredSelectId.HasValue
                    ? filtered.FindIndex(c => c.Id == preferredSelectId.Value)
                    : 0;
                CustomersListView.SelectedIndex = idx >= 0 ? idx : 0;
            }
            else
            {
                _selectedCustomer = null;
                SetEditMode(false);
                UpdateSelectedCustomerUi();
            }
        }

        private async void CustomersListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedCustomer = CustomersListView.SelectedItem as CustomerDto;
            SetEditMode(false);
            ProfileStatusBanner.Visibility = Visibility.Collapsed;
            UpdateSelectedCustomerUi();

            if (_selectedCustomer is { } selected)
            {
                try { await LoadCustomerMessagesAsync(selected); }
                catch (Exception ex) { AppServices.Logs.Error("UI", $"加载聊天记录异常：{ex.Message}"); }
            }
            else
            {
                MessagesListView.ItemsSource = null;
                EmptyChatTitle.Text = "暂无聊天记录";
                EmptyChatHint.Visibility = Visibility.Visible;
            }
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
            ProfileContentScroll.Visibility = hasCustomer ? Visibility.Visible : Visibility.Collapsed;

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

            bool hasSystemIds = !string.IsNullOrWhiteSpace(c.LocalKey) || !string.IsNullOrWhiteSpace(c.NativeId);
            ViewSystemIdsPanel.Visibility = hasSystemIds ? Visibility.Visible : Visibility.Collapsed;
            ViewLocalKeyText.Text = string.IsNullOrWhiteSpace(c.LocalKey) ? "-" : c.LocalKey;
            ViewNativeIdText.Text = string.IsNullOrWhiteSpace(c.NativeId) ? "-" : c.NativeId;

            if (string.IsNullOrWhiteSpace(c.Note))
            {
                ViewEmployeeNoteText.Text = "暂无员工备注，点击「编辑」即可补充客户意向与跟进内容。";
                ViewEmployeeNoteText.Foreground = (Brush)Application.Current.Resources["TextMutedBrush"];
            }
            else
            {
                ViewEmployeeNoteText.Text = c.Note;
                ViewEmployeeNoteText.Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"];
            }

            PopulateEditBoxes(c);
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

        private void PopulateEditBoxes(CustomerDto c)
        {
            EditRemarkBox.Text = c.WechatRemark;
            EditNameBox.Text = c.WechatName;
            EditAccountBox.Text = c.WechatAccount;
            EditGenderBox.SelectedIndex = c.Gender switch
            {
                "男" => 1,
                "女" => 2,
                _ => 0
            };
            EditRegionBox.Text = c.Region;
            EditPhoneBox.Text = c.Phone;
            EditSignatureBox.Text = c.Signature;
            EditSourceBox.Text = c.Source;
            EditAddedDateBox.Text = c.AddedDate;

            int statusIdx = Array.FindIndex(FollowStatusOptions, s => s == c.FollowStatus);
            EditFollowStatusBox.SelectedIndex = statusIdx >= 0 ? statusIdx : 0;
            EditTagsBox.Text = c.Tags;
            EditDescriptionBox.Text = c.Description;
            EditEmployeeNoteBox.Text = c.Note;
            EditLocalKeyBox.Text = c.LocalKey;
            EditNativeIdBox.Text = c.NativeId;
        }

        private void SetEditMode(bool editing)
        {
            _isEditing = editing;
            ViewActionsBar.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
            EditActionsBar.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
            ProfileViewModePanel.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
            ProfileEditModePanel.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        }

        private void EditProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedCustomer is null) return;
            PopulateEditBoxes(_selectedCustomer);
            ProfileStatusBanner.Visibility = Visibility.Collapsed;
            SetEditMode(true);
        }

        private void CancelEditButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedCustomer is not null)
            {
                PopulateEditBoxes(_selectedCustomer);
            }
            SetEditMode(false);
        }

        private async void SaveProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedCustomer is null) return;

            string remark = EditRemarkBox.Text.Trim();
            string name = EditNameBox.Text.Trim();
            string account = EditAccountBox.Text.Trim();
            if (string.IsNullOrEmpty(remark) && string.IsNullOrEmpty(name) && string.IsNullOrEmpty(account))
            {
                ShowProfileStatus("微信备注、微信昵称和微信号不能同时为空。", isError: true);
                return;
            }

            SaveProfileButton.IsEnabled = false;
            try
            {
                string gender = EditGenderBox.SelectedIndex switch
                {
                    1 => "男",
                    2 => "女",
                    _ => string.Empty
                };
                string followStatus = EditFollowStatusBox.SelectedIndex > 0 && EditFollowStatusBox.SelectedIndex < FollowStatusOptions.Length
                    ? FollowStatusOptions[EditFollowStatusBox.SelectedIndex]
                    : string.Empty;

                var res = await AppServices.Backend.UpdateCustomerAsync(_selectedCustomer.Id, new CreateCustomerRequest
                {
                    WechatRemark = remark,
                    WechatName = name,
                    WechatAccount = account,
                    Gender = gender,
                    Region = EditRegionBox.Text.Trim(),
                    Phone = EditPhoneBox.Text.Trim(),
                    Signature = EditSignatureBox.Text.Trim(),
                    Source = EditSourceBox.Text.Trim(),
                    AddedDate = EditAddedDateBox.Text.Trim(),
                    FollowStatus = followStatus,
                    Tags = EditTagsBox.Text.Trim(),
                    Description = EditDescriptionBox.Text.Trim(),
                    Note = EditEmployeeNoteBox.Text.Trim(),
                    LocalKey = EditLocalKeyBox.Text.Trim(),
                    NativeId = EditNativeIdBox.Text.Trim()
                });

                if (res.IsSuccess && res.Data is not null)
                {
                    long id = res.Data.Id;
                    SetEditMode(false);
                    await LoadCustomersAsync(id);
                    ShowProfileStatus("✓ 客户资料与员工备注已保存。");
                }
                else
                {
                    ShowProfileStatus($"保存失败：{res.ErrorMessage}", isError: true);
                }
            }
            finally
            {
                SaveProfileButton.IsEnabled = true;
            }
        }

        private async void DeleteCustomerButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedCustomer is null || !AppServices.TryBeginModalDialog()) return;
            var target = _selectedCustomer;

            try
            {
                var confirm = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    RequestedTheme = AppServices.Settings.CurrentElementTheme,
                    Title = "确认删除客户",
                    Content = $"确定要删除客户「{target.DisplayName}」及其全部聊天记录吗？删除后无法恢复。",
                    PrimaryButtonText = "确认删除",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    PrimaryButtonStyle = (Style)Application.Current.Resources["DangerOutlineButtonStyle"]
                };

                var choice = await confirm.ShowAsync();
                if (choice != ContentDialogResult.Primary) return;

                var res = await AppServices.Backend.DeleteCustomerAsync(target.Id);
                if (res.IsSuccess)
                {
                    SetEditMode(false);
                    await LoadCustomersAsync();
                    if (_selectedCustomer is not null)
                    {
                        ShowProfileStatus($"已删除客户「{target.DisplayName}」。");
                    }
                }
                else
                {
                    ShowProfileStatus($"删除失败：{res.ErrorMessage}", isError: true);
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
            catch (Exception ex) { ShowProfileStatus($"打开导入窗口失败：{ex.Message}", isError: true); }
        }

        private async void BatchTagImportButton_Click(object sender, RoutedEventArgs e)
        {
            try { await ShowBatchTagImportDialogAsync(); }
            catch (Exception ex) { ShowProfileStatus($"打开批量导入失败：{ex.Message}", isError: true); }
        }

        private async void PasteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            if (AppServices.IsModalDialogOpen || _isEditing) return;
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
            bool openBatchAfter = false;
            try
            {
                var dialog = new ImportCustomerDialog { XamlRoot = XamlRoot };
                if (autoPasteClipboard)
                {
                    dialog.Opened += async (_, _) => await dialog.TriggerClipboardImportAsync();
                }

                await dialog.ShowAsync();
                if (dialog.RequestedBatchTagImport)
                {
                    openBatchAfter = true;
                }
                else if (dialog.CreatedCustomer is { } created)
                {
                    await LoadCustomersAsync(created.Id);
                    ShowProfileStatus(dialog.WasOverwritten
                        ? $"✓ 已覆盖更新客户「{created.DisplayName}」。"
                        : $"✓ 已成功导入新客户「{created.DisplayName}」。");
                }
            }
            finally
            {
                AppServices.EndModalDialog();
            }

            if (openBatchAfter)
            {
                await ShowBatchTagImportDialogAsync();
            }
        }

        private async Task ShowBatchTagImportDialogAsync()
        {
            if (!AppServices.TryBeginModalDialog()) return;
            try
            {
                var dialog = new BatchTagImportDialog { XamlRoot = XamlRoot };
                await dialog.ShowAsync();
                if (dialog.TotalImportedOrUpdated > 0)
                {
                    await LoadCustomersAsync(_selectedCustomer?.Id);
                    ShowProfileStatus($"✓ 微信标签批量导入完成，共新增/更新 {dialog.TotalImportedOrUpdated} 位客户。");
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

            if (!res.IsSuccess || res.Data is null)
            {
                EmptyChatTitle.Text = $"加载消息失败：{res.ErrorMessage}";
                EmptyChatHint.Visibility = Visibility.Visible;
                MessagesListView.ItemsSource = null;
                return;
            }

            var bubbles = res.Data.Items
                .AsEnumerable()
                .Reverse()
                .Select(m => new ChatBubbleItem
                {
                    Id = m.Id,
                    SenderInitial = customer.AvatarInitial,
                    SenderAvatar = customer.AvatarImage,
                    Content = m.Content,
                    TimeLabel = m.TimeLabel,
                    IsOutgoing = m.IsOutgoing
                })
                .ToList();

            MessagesListView.ItemsSource = bubbles;
            EmptyChatTitle.Text = $"与「{customer.DisplayName}」暂无聊天记录";
            EmptyChatHint.Visibility = bubbles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (bubbles.Count > 0)
            {
                MessagesListView.ScrollIntoView(bubbles[^1]);
            }
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
