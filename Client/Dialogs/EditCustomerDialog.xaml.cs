using Client.Models;
using Client.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Client.Dialogs
{
    public sealed partial class EditCustomerDialog : ContentDialog
    {
        private static readonly string[] FollowStatusOptions =
            ["未设置", "新线索", "跟进中", "高意向", "已报价", "已成交", "暂缓/流失"];

        private readonly CustomerDto _customer;
        private string _activeTab = "profile";
        private byte[]? _pendingAvatarPng;
        private bool _clearAvatarRequested;
        private bool _isSaving;

        public CustomerDto? SavedCustomer { get; private set; }
        public byte[]? UpdatedAvatarBytes { get; private set; }
        public bool AvatarWasCleared { get; private set; }

        public EditCustomerDialog(CustomerDto customer, string initialTab = "profile")
        {
            InitializeComponent();
            _customer = customer;
            _activeTab = initialTab == "notes" ? "notes" : "profile";
            RequestedTheme = AppServices.Settings.CurrentElementTheme;
            Loaded += EditCustomerDialog_Loaded;
        }

        private void EditCustomerDialog_Loaded(object sender, RoutedEventArgs e)
        {
            PopulateFields(_customer);
            SelectTab(_activeTab);
        }

        private void PopulateFields(CustomerDto c)
        {
            HeaderAvatarText.Text = c.AvatarInitial;
            PreviewAvatarText.Text = c.AvatarInitial;
            HeaderSubtitleText.Text = string.IsNullOrWhiteSpace(c.WechatAccount)
                ? c.DisplayName
                : $"{c.DisplayName} · 微信号：{c.WechatAccount}";

            if (c.AvatarImage is not null)
            {
                HeaderAvatarImage.Source = c.AvatarImage;
                HeaderAvatarImage.Visibility = Visibility.Visible;
                HeaderAvatarText.Visibility = Visibility.Collapsed;

                PreviewAvatarImage.Source = c.AvatarImage;
                PreviewAvatarImage.Visibility = Visibility.Visible;
                PreviewAvatarText.Visibility = Visibility.Collapsed;
                ClearAvatarBtn.IsEnabled = true;
            }
            else
            {
                ClearAvatarBtn.IsEnabled = !string.IsNullOrWhiteSpace(c.AvatarUrl);
            }

            EditNameBox.Text = c.WechatName;
            EditRemarkBox.Text = c.WechatRemark;
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

            UpdateNoteCharCount();
        }

        private void TabSwitch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tab)
            {
                SelectTab(tab);
            }
        }

        private void SelectTab(string tab)
        {
            _activeTab = tab;
            bool isProfile = tab == "profile";
            ProfileTabScroll.Visibility = isProfile ? Visibility.Visible : Visibility.Collapsed;
            NotesTabPanel.Visibility = isProfile ? Visibility.Collapsed : Visibility.Visible;

            var activeFg = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
            var inactiveFg = (Brush)Application.Current.Resources["TextSecondaryBrush"];
            var transparent = new SolidColorBrush(Colors.Transparent);

            TabProfileIcon.Foreground = isProfile ? activeFg : inactiveFg;
            TabProfileText.Foreground = isProfile ? activeFg : inactiveFg;
            TabProfileText.FontWeight = isProfile ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            TabProfileLine.Background = isProfile ? activeFg : transparent;

            TabNotesIcon.Foreground = !isProfile ? activeFg : inactiveFg;
            TabNotesText.Foreground = !isProfile ? activeFg : inactiveFg;
            TabNotesText.FontWeight = !isProfile ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            TabNotesLine.Background = !isProfile ? activeFg : transparent;
        }

        private void EditEmployeeNoteBox_TextChanged(object sender, TextChangedEventArgs e) =>
            UpdateNoteCharCount();

        private void UpdateNoteCharCount()
        {
            int count = EditEmployeeNoteBox.Text.Length;
            NoteCharCountText.Text = $"共 {count} 字";
        }

        private void InsertDateStampBtn_Click(object sender, RoutedEventArgs e)
        {
            string stamp = $"[{DateTime.Now:yyyy-MM-dd}] ";
            string existing = EditEmployeeNoteBox.Text;
            EditEmployeeNoteBox.Text = string.IsNullOrWhiteSpace(existing)
                ? stamp
                : $"{existing.TrimEnd()}\n{stamp}";
            EditEmployeeNoteBox.SelectionStart = EditEmployeeNoteBox.Text.Length;
            EditEmployeeNoteBox.Focus(FocusState.Programmatic);
        }

        private async void PasteAvatarBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isSaving) return;
            try
            {
                var (bmp, _) = await AppServices.Ocr.TryLoadImageFromClipboardAsync();
                if (bmp is null)
                {
                    SetStatus("剪贴板中没有图片，请先截图或复制头像。", isError: true);
                    return;
                }
                using (bmp)
                {
                    byte[] pngBytes = await AvatarImageService.EncodeSquarePngAsync(bmp, 256);
                    await StageAvatarPreviewAsync(pngBytes, "已从剪贴板载入新头像，保存后生效");
                }
            }
            catch (Exception ex)
            {
                SetStatus($"粘贴头像失败：{ex.Message}", isError: true);
            }
        }

        private async void UploadAvatarBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isSaving) return;
            string? filePath = Win32WindowHelper.PickImageFilePath(AppServices.MainWindowHandle);
            if (string.IsNullOrWhiteSpace(filePath)) return;

            try
            {
                using var bmp = await Task.Run(() => AppServices.Ocr.LoadImageFileFromPathAsync(filePath));
                byte[] pngBytes = await AvatarImageService.EncodeSquarePngAsync(bmp, 256);
                await StageAvatarPreviewAsync(pngBytes, "已选择本地新头像，保存后生效");
            }
            catch (Exception ex)
            {
                SetStatus($"读取头像失败：{ex.Message}", isError: true);
            }
        }

        private async Task StageAvatarPreviewAsync(byte[] pngBytes, string statusHint)
        {
            _pendingAvatarPng = pngBytes;
            _clearAvatarRequested = false;
            var img = await AvatarImageService.BytesToBitmapImageAsync(pngBytes);
            if (img is not null)
            {
                HeaderAvatarImage.Source = img;
                HeaderAvatarImage.Visibility = Visibility.Visible;
                HeaderAvatarText.Visibility = Visibility.Collapsed;

                PreviewAvatarImage.Source = img;
                PreviewAvatarImage.Visibility = Visibility.Visible;
                PreviewAvatarText.Visibility = Visibility.Collapsed;
            }
            ClearAvatarBtn.IsEnabled = true;
            AvatarStatusText.Text = statusHint;
            SetStatus(statusHint, isError: false);
        }

        private void ClearAvatarBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isSaving) return;
            _pendingAvatarPng = null;
            _clearAvatarRequested = true;

            HeaderAvatarImage.Source = null;
            HeaderAvatarImage.Visibility = Visibility.Collapsed;
            HeaderAvatarText.Visibility = Visibility.Visible;

            PreviewAvatarImage.Source = null;
            PreviewAvatarImage.Visibility = Visibility.Collapsed;
            PreviewAvatarText.Visibility = Visibility.Visible;

            ClearAvatarBtn.IsEnabled = false;
            AvatarStatusText.Text = "已标记清除头像，保存后生效";
            SetStatus("已标记清除头像，点击「保存修改」后生效", isError: false);
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isSaving) return;

            string remark = EditRemarkBox.Text.Trim();
            string name = EditNameBox.Text.Trim();
            string account = EditAccountBox.Text.Trim();
            if (string.IsNullOrEmpty(remark) && string.IsNullOrEmpty(name) && string.IsNullOrEmpty(account))
            {
                SelectTab("profile");
                SetStatus("微信备注、微信昵称和微信号不能同时为空。", isError: true);
                return;
            }

            SetSaving(true);
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

                var res = await AppServices.Backend.UpdateCustomerAsync(_customer.Id, new CreateCustomerRequest
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
                    LocalKey = _customer.LocalKey,
                    NativeId = _customer.NativeId
                });

                if (!res.IsSuccess || res.Data is null)
                {
                    SetStatus($"保存失败：{res.ErrorMessage}", isError: true);
                    return;
                }

                var latest = res.Data;
                if (_pendingAvatarPng is not null)
                {
                    var avRes = await AppServices.Backend.UploadCustomerAvatarAsync(latest.Id, _pendingAvatarPng);
                    if (avRes.IsSuccess && avRes.Data is not null)
                    {
                        latest = avRes.Data;
                        UpdatedAvatarBytes = _pendingAvatarPng;
                    }
                }
                else if (_clearAvatarRequested && !string.IsNullOrWhiteSpace(_customer.AvatarUrl))
                {
                    var delRes = await AppServices.Backend.DeleteCustomerAvatarAsync(latest.Id, _customer.AvatarUrl);
                    if (delRes.IsSuccess && delRes.Data is not null)
                    {
                        latest = delRes.Data;
                        AvatarWasCleared = true;
                    }
                }

                SavedCustomer = latest;
                Hide();
            }
            catch (Exception ex)
            {
                SetStatus($"保存异常：{ex.Message}", isError: true);
            }
            finally
            {
                SetSaving(false);
            }
        }

        private void SetSaving(bool busy)
        {
            _isSaving = busy;
            SaveBtn.IsEnabled = !busy;
            CancelBtn.IsEnabled = !busy;
            SaveSpinner.IsActive = busy;
            SaveSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            SaveBtnText.Text = busy ? "保存中..." : "保存修改";
        }

        private void SetStatus(string message, bool isError)
        {
            StatusText.Text = message;
            StatusText.Foreground = (Brush)Application.Current.Resources[isError ? "DangerTextBrush" : "TextMutedBrush"];
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isSaving) Hide();
        }

        private void CloseAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            if (!_isSaving)
            {
                args.Handled = true;
                Hide();
            }
        }
    }
}
