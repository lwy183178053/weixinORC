using Client.Dialogs;
using Client.Models;
using Client.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace Client.Pages
{
    public sealed partial class CustomersPage : Page
    {
        private List<CustomerDto> _allCustomers = [];
        private CustomerDto? _selectedCustomer;
        private bool _isEditing;
        private bool _dialogOpen;

        public CustomersPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await LoadCustomersAsync();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadCustomersAsync(_selectedCustomer?.Id);
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (CustomersListView is null) return;
            ApplyFilter(_selectedCustomer?.Id);
        }

        private async Task LoadCustomersAsync(long? selectId = null)
        {
            var result = await AppServices.Backend.ListCustomersAsync();
            if (result.IsSuccess && result.Data is not null)
            {
                _allCustomers = result.Data.Items;
                ApplyFilter(selectId);
            }
            else
            {
                ShowProfileStatus($"拉取客户列表失败：{result.ErrorMessage}", isError: true);
            }
        }

        private void ApplyFilter(long? preferredSelectId = null)
        {
            string q = SearchBox.Text.Trim();
            var filtered = string.IsNullOrEmpty(q)
                ? _allCustomers
                : _allCustomers.Where(c =>
                    c.WechatName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    c.WechatAccount.Contains(q, StringComparison.OrdinalIgnoreCase) ||
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

            if (_selectedCustomer is not null)
            {
                await LoadCustomerMessagesAsync(_selectedCustomer);
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
            EditProfileButton.IsEnabled = hasCustomer;
            DeleteCustomerButton.IsEnabled = hasCustomer;
            EmptyProfileHint.Visibility = hasCustomer ? Visibility.Collapsed : Visibility.Visible;
            ProfileContentScroll.Visibility = hasCustomer ? Visibility.Visible : Visibility.Collapsed;
            ProfileFooterBar.Visibility = hasCustomer ? Visibility.Visible : Visibility.Collapsed;

            if (c is null)
            {
                ActiveCustomerTitle.Text = "请选择左侧客户";
                ActiveCustomerMeta.Text = "点左上角「+」可弹出截图识别导入窗口";
                return;
            }

            ActiveCustomerTitle.Text = c.DisplayName;
            var metaParts = new[]
            {
                string.IsNullOrWhiteSpace(c.WechatAccount) ? "" : $"微信号: {c.WechatAccount}",
                string.IsNullOrWhiteSpace(c.Region) ? "" : $"地区: {c.Region}",
                string.IsNullOrWhiteSpace(c.Phone) ? "" : $"电话: {c.Phone}"
            }.Where(s => s.Length > 0);
            ActiveCustomerMeta.Text = metaParts.Any() ? string.Join("  ·  ", metaParts) : $"客户 ID #{c.Id}";

            ProfileAvatarText.Text = c.AvatarInitial;
            ProfileHeaderNameText.Text = c.DisplayName;
            ProfileHeaderAccountText.Text = $"微信号：{(string.IsNullOrWhiteSpace(c.WechatAccount) ? "-" : c.WechatAccount)}";
            ProfileHeaderRegionText.Text = $"地区：{(string.IsNullOrWhiteSpace(c.Region) ? "-" : c.Region)}";

            ViewPhoneText.Text = string.IsNullOrWhiteSpace(c.Phone) ? "-" : c.Phone;
            ViewSignatureText.Text = string.IsNullOrWhiteSpace(c.Signature) ? "-" : c.Signature;
            ViewSourceText.Text = string.IsNullOrWhiteSpace(c.Source) ? "-" : c.Source;
            ViewAddedDateText.Text = string.IsNullOrWhiteSpace(c.AddedDate) ? "-" : c.AddedDate;

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

        private void PopulateEditBoxes(CustomerDto c)
        {
            EditNameBox.Text = c.WechatName;
            EditAccountBox.Text = c.WechatAccount;
            EditRegionBox.Text = c.Region;
            EditPhoneBox.Text = c.Phone;
            EditSignatureBox.Text = c.Signature;
            EditSourceBox.Text = c.Source;
            EditAddedDateBox.Text = c.AddedDate;
            EditEmployeeNoteBox.Text = c.Note;
        }

        private void SetEditMode(bool editing)
        {
            _isEditing = editing;
            ViewActionsBar.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
            EditActionsBar.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
            ProfileViewModePanel.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
            ProfileEditModePanel.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
            FooterViewButtons.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
            FooterEditButtons.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
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

            string name = EditNameBox.Text.Trim();
            string account = EditAccountBox.Text.Trim();
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(account))
            {
                ShowProfileStatus("微信昵称和微信号不能同时为空。", isError: true);
                return;
            }

            SaveProfileButton.IsEnabled = false;
            FooterSaveButton.IsEnabled = false;
            try
            {
                var res = await AppServices.Backend.UpdateCustomerAsync(_selectedCustomer.Id, new CreateCustomerRequest
                {
                    WechatName = name,
                    WechatAccount = account,
                    Region = EditRegionBox.Text.Trim(),
                    Phone = EditPhoneBox.Text.Trim(),
                    Signature = EditSignatureBox.Text.Trim(),
                    Source = EditSourceBox.Text.Trim(),
                    AddedDate = EditAddedDateBox.Text.Trim(),
                    Note = EditEmployeeNoteBox.Text.Trim()
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
                FooterSaveButton.IsEnabled = true;
            }
        }

        private async void DeleteCustomerButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedCustomer is null || _dialogOpen) return;
            var target = _selectedCustomer;

            _dialogOpen = true;
            try
            {
                var confirm = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    RequestedTheme = ElementTheme.Light,
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
            finally
            {
                _dialogOpen = false;
            }
        }

        private async void AddCustomerButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowImportDialogAsync(autoPasteClipboard: false);
        }

        private async void PasteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            if (_dialogOpen || _isEditing) return;
            var data = Clipboard.GetContent();
            if (data is null) return;

            bool hasImage = data.Contains(StandardDataFormats.Bitmap);
            if (!hasImage && data.Contains(StandardDataFormats.StorageItems))
            {
                try
                {
                    var items = await data.GetStorageItemsAsync();
                    hasImage = items.Any(i =>
                        i.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                        i.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                        i.Name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                        i.Name.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase));
                }
                catch { }
            }

            if (!hasImage) return;

            args.Handled = true;
            await ShowImportDialogAsync(autoPasteClipboard: true);
        }

        private async Task ShowImportDialogAsync(bool autoPasteClipboard)
        {
            if (_dialogOpen) return;
            _dialogOpen = true;
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
                    ShowProfileStatus($"已成功导入新客户「{created.DisplayName}」。");
                }
            }
            finally
            {
                _dialogOpen = false;
            }
        }

        private async Task LoadCustomerMessagesAsync(CustomerDto customer)
        {
            var res = await AppServices.Backend.ListCustomerMessagesAsync(customer.Id);
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
            if (_selectedCustomer is null) return;
            string content = MessageInputBox.Text.Trim();
            if (string.IsNullOrEmpty(content)) return;

            string direction = DirIncomingRadio.IsChecked == true ? "incoming" : "outgoing";
            SendMessageButton.IsEnabled = false;
            try
            {
                var res = await AppServices.Backend.CreateCustomerMessageAsync(_selectedCustomer.Id, direction, content);
                if (res.IsSuccess)
                {
                    MessageInputBox.Text = string.Empty;
                    await LoadCustomerMessagesAsync(_selectedCustomer);
                    await LoadCustomersAsync(_selectedCustomer.Id);
                }
                else
                {
                    ComposerHintText.Text = $"发送失败：{res.ErrorMessage}";
                }
            }
            finally
            {
                SendMessageButton.IsEnabled = _selectedCustomer is not null;
            }
        }

        private async void OcrChatClipboardButton_Click(object sender, RoutedEventArgs e)
        {
            if (!AppServices.Ocr.RapidEngine.IsAvailable)
            {
                ComposerHintText.Text = AppServices.Ocr.RapidEngine.AvailabilityNote;
                return;
            }

            try
            {
                var (bmp, _) = await AppServices.Ocr.TryLoadImageFromClipboardAsync();
                if (bmp is null)
                {
                    ComposerHintText.Text = "剪贴板中没有图片，请先截聊天内容再点击。";
                    return;
                }

                using (bmp)
                {
                    ComposerHintText.Text = "正在提取截图文字...";
                    var ocr = await AppServices.Ocr.RapidEngine.RecognizeAsync(bmp);
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
            }
            catch (Exception ex)
            {
                ComposerHintText.Text = $"识别失败：{ex.Message}";
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
