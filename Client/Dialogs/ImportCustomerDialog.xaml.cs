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
        private bool _isBusyOcr;
        private bool _isSubmitting;

        public CustomerDto? CreatedCustomer { get; private set; }

        public ImportCustomerDialog()
        {
            InitializeComponent();
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

                RemarkBox.Text = parsed.CandidateWechatRemark;
                NameBox.Text = parsed.CandidateWechatName;
                AccountBox.Text = parsed.CandidateWechatAccount;
                RegionBox.Text = parsed.CandidateRegion;
                PhoneBox.Text = parsed.CandidatePhone;
                SignatureBox.Text = parsed.CandidateSignature;
                SourceBox.Text = parsed.CandidateSource;
                AddedDateBox.Text = parsed.CandidateAddedDate;

                ShowStatus($"识别完成（{ocr.ElapsedMilliseconds} ms，共 {ocr.Lines.Count} 行），请核对右侧信息后点击导入。");
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

            _isSubmitting = true;
            SubmitButton.IsEnabled = false;
            SubmitSpinner.IsActive = true;
            SubmitSpinner.Visibility = Visibility.Visible;
            SubmitButtonText.Text = "正在导入...";
            try
            {
                var res = await AppServices.Backend.CreateCustomerAsync(new CreateCustomerRequest
                {
                    WechatRemark = remark,
                    WechatName = name,
                    WechatAccount = account,
                    Region = RegionBox.Text.Trim(),
                    Phone = PhoneBox.Text.Trim(),
                    Signature = SignatureBox.Text.Trim(),
                    Source = SourceBox.Text.Trim(),
                    AddedDate = AddedDateBox.Text.Trim(),
                    Note = string.Empty
                });

                if (res.IsSuccess && res.Data is not null)
                {
                    CreatedCustomer = res.Data;
                    Hide();
                }
                else
                {
                    ShowStatus($"保存失败：{res.ErrorMessage}", isError: true);
                }
            }
            finally
            {
                _isSubmitting = false;
                SubmitButton.IsEnabled = true;
                SubmitSpinner.IsActive = false;
                SubmitSpinner.Visibility = Visibility.Collapsed;
                SubmitButtonText.Text = "核验确认并导入";
            }
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
