using Client.Models;
using Client.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;

namespace Client.Dialogs
{
    public sealed partial class ImportCustomerDialog : ContentDialog
    {
        private SoftwareBitmap? _bitmap;
        private byte[]? _extractedAvatarPng;
        private CustomerDto? _duplicateMatch;
        private CreateCustomerRequest? _pendingRequest;
        private bool _isBusyOcr;
        private bool _isSubmitting;

        public CustomerDto? CreatedCustomer { get; private set; }
        public bool WasOverwritten { get; private set; }
        public bool RequestedBatchTagImport { get; private set; }

        public ImportCustomerDialog()
        {
            InitializeComponent();
            RequestedTheme = AppServices.Settings.CurrentElementTheme;
            Closed += (_, _) =>
            {
                _bitmap?.Dispose();
                _bitmap = null;
            };
        }

        public async Task TriggerClipboardImportAsync()
        {
            await ImportFromClipboardAsync();
        }

        private void BatchTagImportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isSubmitting) return;
            RequestedBatchTagImport = true;
            Hide();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isSubmitting)
            {
                Hide();
            }
        }

        private void CloseAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            if (!_isSubmitting)
            {
                args.Handled = true;
                Hide();
            }
        }

        private async void PasteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
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
            await ImportFromClipboardAsync();
        }

        private async void PasteButton_Click(object sender, RoutedEventArgs e) => await ImportFromClipboardAsync();

        private async void UploadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusyOcr) return;
            string? filePath = Win32WindowHelper.PickImageFilePath(AppServices.MainWindowHandle);
            if (string.IsNullOrWhiteSpace(filePath)) return;

            try
            {
                var bmp = await Task.Run(() => AppServices.Ocr.LoadImageFileFromPathAsync(filePath));
                await RunOcrAsync(bmp, Path.GetFileName(filePath));
            }
            catch (Exception ex)
            {
                AppServices.Logs.Error("OCR", $"读取图片失败：{ex.Message}");
                ShowStatus($"读取图片失败：{ex.Message}", isError: true);
            }
        }

        private async Task ImportFromClipboardAsync()
        {
            if (_isBusyOcr) return;
            try
            {
                var (bmp, label) = await AppServices.Ocr.TryLoadImageFromClipboardAsync();
                if (bmp is null)
                {
                    ShowStatus("剪贴板中没有检测到图片，请先截微信资料卡再按 Ctrl+V。", isError: true);
                    return;
                }
                await RunOcrAsync(bmp, label);
            }
            catch (Exception ex)
            {
                AppServices.Logs.Error("OCR", $"读取剪贴板失败：{ex.Message}");
                ShowStatus($"读取剪贴板失败：{ex.Message}", isError: true);
            }
        }

        private async Task RunOcrAsync(SoftwareBitmap bmp, string label)
        {
            if (!AppServices.Ocr.RapidEngine.IsAvailable)
            {
                bmp.Dispose();
                ShowStatus(AppServices.Ocr.RapidEngine.AvailabilityNote, isError: true);
                return;
            }

            _isBusyOcr = true;
            OcrSpinner.IsActive = true;
            OcrSpinner.Visibility = Visibility.Visible;
            PasteButton.IsEnabled = false;
            UploadButton.IsEnabled = false;
            ShowStatus($"正在识别「{label}」...");

            try
            {
                _bitmap?.Dispose();
                _bitmap = bmp;

                var src = new SoftwareBitmapSource();
                await src.SetBitmapAsync(bmp);
                PreviewImage.Source = src;
                PreviewImage.Visibility = Visibility.Visible;
                EmptyPreviewHint.Visibility = Visibility.Collapsed;

                var ocr = await AppServices.Ocr.RapidEngine.RecognizeAsync(bmp);
                var parsed = WeChatOcrAnalyzer.Analyze(null, ocr);
                var (avatarPng, detectedGender) = await AvatarImageService.TryExtractProfileAvatarAndGenderAsync(bmp, ocr);

                RemarkBox.Text = parsed.CandidateWechatRemark;
                NameBox.Text = parsed.CandidateWechatName;
                AccountBox.Text = parsed.CandidateWechatAccount;
                TagsBox.Text = parsed.CandidateTags;
                RegionBox.Text = parsed.CandidateRegion;
                PhoneBox.Text = parsed.CandidatePhone;
                SignatureBox.Text = parsed.CandidateSignature;
                SourceBox.Text = parsed.CandidateSource;
                AddedDateBox.Text = parsed.CandidateAddedDate;
                SelectGender(detectedGender);

                _extractedAvatarPng = avatarPng;
                if (avatarPng is not null)
                {
                    ExtractedAvatarImage.Source = await AvatarImageService.BytesToBitmapImageAsync(avatarPng);
                    ExtractedAvatarBadge.Visibility = Visibility.Visible;
                }
                else
                {
                    ExtractedAvatarImage.Source = null;
                    ExtractedAvatarBadge.Visibility = Visibility.Collapsed;
                }

                string extra = avatarPng is not null ? "，已自动裁剪头像" : "";
                if (!string.IsNullOrEmpty(detectedGender)) extra += $"，性别识别为「{detectedGender}」";
                ShowStatus($"识别完成（{ocr.ElapsedMilliseconds} ms，共 {ocr.Lines.Count} 行{extra}），请核对后导入。");
            }
            catch (Exception ex)
            {
                AppServices.Logs.Error("OCR", $"客户截图识别失败：{ex.Message}");
                ShowStatus($"识别失败：{ex.Message}", isError: true);
            }
            finally
            {
                _isBusyOcr = false;
                OcrSpinner.IsActive = false;
                OcrSpinner.Visibility = Visibility.Collapsed;
                PasteButton.IsEnabled = true;
                UploadButton.IsEnabled = true;
            }
        }

        private async void SubmitButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isSubmitting) return;

            string remark = RemarkBox.Text.Trim();
            string name = NameBox.Text.Trim();
            string account = AccountBox.Text.Trim();
            if (string.IsNullOrEmpty(remark) && string.IsNullOrEmpty(name) && string.IsNullOrEmpty(account))
            {
                ShowStatus("微信备注、微信昵称和微信号不能同时为空。", isError: true);
                return;
            }

            _pendingRequest = new CreateCustomerRequest
            {
                WechatRemark = remark,
                WechatName = name,
                WechatAccount = account,
                Gender = GetSelectedGender(),
                Tags = TagsBox.Text.Trim(),
                Region = RegionBox.Text.Trim(),
                Phone = PhoneBox.Text.Trim(),
                Signature = SignatureBox.Text.Trim(),
                Source = SourceBox.Text.Trim(),
                AddedDate = AddedDateBox.Text.Trim(),
                Note = string.Empty
            };

            SetSubmitting(true);
            try
            {
                // Check for existing duplicate customer (match by wechat_account first, then exact display name / nickname)
                var listRes = await AppServices.Backend.ListCustomersAsync(100);
                CustomerDto? dup = null;
                if (listRes.IsSuccess && listRes.Data is not null)
                {
                    if (!string.IsNullOrEmpty(account))
                    {
                        dup = listRes.Data.Items.FirstOrDefault(c =>
                            !string.IsNullOrWhiteSpace(c.WechatAccount) &&
                            c.WechatAccount.Equals(account, StringComparison.OrdinalIgnoreCase));
                    }
                    if (dup is null && !string.IsNullOrEmpty(name))
                    {
                        dup = listRes.Data.Items.FirstOrDefault(c =>
                            !string.IsNullOrWhiteSpace(c.WechatName) &&
                            c.WechatName.Equals(name, StringComparison.OrdinalIgnoreCase));
                    }
                    if (dup is null && !string.IsNullOrEmpty(remark))
                    {
                        dup = listRes.Data.Items.FirstOrDefault(c =>
                            !string.IsNullOrWhiteSpace(c.WechatRemark) &&
                            c.WechatRemark.Equals(remark, StringComparison.OrdinalIgnoreCase));
                    }
                }

                if (dup is not null)
                {
                    _duplicateMatch = dup;
                    DuplicateSummaryText.Text =
                        $"系统中已存在客户「{dup.DisplayName}」" +
                        $"{(string.IsNullOrWhiteSpace(dup.WechatAccount) ? "" : $"（微信号：{dup.WechatAccount}）")}。\n" +
                        $"· 选择「覆盖已有客户」将用本次识别资料更新原档案（保留原员工备注）；\n" +
                        $"· 选择「仍要新建」将另建一条独立客户档案。";
                    DuplicateOverlay.Visibility = Visibility.Visible;
                    return;
                }

                await ExecuteSaveAsync(overwriteExisting: false, allowDuplicate: false);
            }
            finally
            {
                SetSubmitting(false);
            }
        }

        private async void DuplicateOverwriteButton_Click(object sender, RoutedEventArgs e)
        {
            DuplicateOverlay.Visibility = Visibility.Collapsed;
            SetSubmitting(true);
            try
            {
                await ExecuteSaveAsync(overwriteExisting: true, allowDuplicate: false);
            }
            finally
            {
                SetSubmitting(false);
            }
        }

        private async void DuplicateCreateNewButton_Click(object sender, RoutedEventArgs e)
        {
            DuplicateOverlay.Visibility = Visibility.Collapsed;
            SetSubmitting(true);
            try
            {
                await ExecuteSaveAsync(overwriteExisting: false, allowDuplicate: true);
            }
            finally
            {
                SetSubmitting(false);
            }
        }

        private void DuplicateCancelButton_Click(object sender, RoutedEventArgs e)
        {
            DuplicateOverlay.Visibility = Visibility.Collapsed;
            _duplicateMatch = null;
        }

        private async Task ExecuteSaveAsync(bool overwriteExisting, bool allowDuplicate)
        {
            if (_pendingRequest is null) return;

            ApiResult<CustomerDto> res;
            if (overwriteExisting && _duplicateMatch is not null)
            {
                // Preserve existing employee follow-up note & CRM fields unless overwritten
                var patch = new CreateCustomerRequest
                {
                    WechatRemark = string.IsNullOrEmpty(_pendingRequest.WechatRemark) ? _duplicateMatch.WechatRemark : _pendingRequest.WechatRemark,
                    WechatName = string.IsNullOrEmpty(_pendingRequest.WechatName) ? _duplicateMatch.WechatName : _pendingRequest.WechatName,
                    WechatAccount = string.IsNullOrEmpty(_pendingRequest.WechatAccount) ? _duplicateMatch.WechatAccount : _pendingRequest.WechatAccount,
                    Gender = string.IsNullOrEmpty(_pendingRequest.Gender) ? _duplicateMatch.Gender : _pendingRequest.Gender,
                    Tags = string.IsNullOrEmpty(_pendingRequest.Tags) ? _duplicateMatch.Tags : _pendingRequest.Tags,
                    Region = string.IsNullOrEmpty(_pendingRequest.Region) ? _duplicateMatch.Region : _pendingRequest.Region,
                    Phone = string.IsNullOrEmpty(_pendingRequest.Phone) ? _duplicateMatch.Phone : _pendingRequest.Phone,
                    Signature = string.IsNullOrEmpty(_pendingRequest.Signature) ? _duplicateMatch.Signature : _pendingRequest.Signature,
                    Source = string.IsNullOrEmpty(_pendingRequest.Source) ? _duplicateMatch.Source : _pendingRequest.Source,
                    AddedDate = string.IsNullOrEmpty(_pendingRequest.AddedDate) ? _duplicateMatch.AddedDate : _pendingRequest.AddedDate,
                    Description = _duplicateMatch.Description,
                    FollowStatus = _duplicateMatch.FollowStatus,
                    LocalKey = _duplicateMatch.LocalKey,
                    NativeId = _duplicateMatch.NativeId,
                    Note = _duplicateMatch.Note
                };
                res = await AppServices.Backend.UpdateCustomerAsync(_duplicateMatch.Id, patch);
                WasOverwritten = true;
            }
            else
            {
                _pendingRequest.AllowDuplicate = allowDuplicate;
                res = await AppServices.Backend.CreateCustomerAsync(_pendingRequest);
                WasOverwritten = false;
            }

            if (res.IsSuccess && res.Data is not null)
            {
                var saved = res.Data;
                if (_extractedAvatarPng is not null)
                {
                    var avRes = await AppServices.Backend.UploadCustomerAvatarAsync(saved.Id, _extractedAvatarPng);
                    if (avRes.IsSuccess && avRes.Data is not null)
                    {
                        saved = avRes.Data;
                    }
                }
                CreatedCustomer = saved;
                Hide();
            }
            else
            {
                ShowStatus($"保存失败：{res.ErrorMessage}", isError: true);
            }
        }

        private void SetSubmitting(bool busy)
        {
            _isSubmitting = busy;
            SubmitButton.IsEnabled = !busy;
            SubmitSpinner.IsActive = busy;
            SubmitSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            SubmitButtonText.Text = busy ? "正在保存..." : "核验确认并导入";
        }

        private void SelectGender(string gender)
        {
            GenderBox.SelectedIndex = gender switch
            {
                "男" => 1,
                "女" => 2,
                _ => 0
            };
        }

        private string GetSelectedGender()
        {
            return GenderBox.SelectedIndex switch
            {
                1 => "男",
                2 => "女",
                _ => string.Empty
            };
        }

        private void ShowStatus(string message, bool isError = false)
        {
            StatusBanner.Background = (Brush)Application.Current.Resources[isError ? "DangerBgBrush" : "BrandSoftBgBrush"];
            StatusText.Foreground = (Brush)Application.Current.Resources[isError ? "DangerTextBrush" : "BrandPrimaryBrush"];
            StatusText.Text = message;
            StatusBanner.Visibility = Visibility.Visible;
        }
    }
}
