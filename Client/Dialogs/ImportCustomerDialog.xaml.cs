using System.Collections.ObjectModel;
using System.Text;
using Client.Models;
using Client.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace Client.Dialogs
{
    public sealed partial class ImportCustomerDialog : ContentDialog
    {
        // Single-mode state
        private SoftwareBitmap? _bitmap;
        private byte[]? _extractedAvatarPng;
        private CustomerDto? _duplicateMatch;
        private CreateCustomerRequest? _pendingRequest;
        private bool _isBusyOcr;
        private bool _isSubmitting;

        // Batch-mode state
        private readonly WeChatTagBatchImporter _importer = new();
        private readonly ObservableCollection<WeChatTagItem> _tags = [];
        private readonly StringBuilder _logBuilder = new();
        private nint _contactManagerHwnd;
        private int _wechatPid;
        private CancellationTokenSource? _batchCts;
        private bool _isBatchBusy;

        private string _activeMode = "single";
        private bool _isClosed;

        public CustomerDto? CreatedCustomer { get; private set; }
        public bool WasOverwritten { get; private set; }
        public int TotalBatchImportedOrUpdated { get; private set; }

        public ImportCustomerDialog()
        {
            InitializeComponent();
            RequestedTheme = AppServices.Settings.CurrentElementTheme;
            TagsListView.ItemsSource = _tags;
            Loaded += (_, _) => SelectMode(_activeMode);
            Closed += (_, _) =>
            {
                _isClosed = true;
                _batchCts?.Cancel();
                if (!_isBusyOcr)
                {
                    _bitmap?.Dispose();
                    _bitmap = null;
                }
            };
        }

        public async Task TriggerClipboardImportAsync()
        {
            SelectMode("single");
            await ImportFromClipboardAsync();
        }

        // ---- Mode Switcher (Single | Batch) ----

        private void ModeTab_Click(object sender, RoutedEventArgs e)
        {
            if (_isSubmitting || _isBusyOcr || _isBatchBusy) return;
            if (sender is Button btn && btn.Tag is string mode)
            {
                SelectMode(mode);
            }
        }

        private void SelectMode(string mode)
        {
            _activeMode = mode == "batch" ? "batch" : "single";
            bool isSingle = _activeMode == "single";

            SingleModePanel.Visibility = isSingle ? Visibility.Visible : Visibility.Collapsed;
            BatchModePanel.Visibility = isSingle ? Visibility.Collapsed : Visibility.Visible;
            DialogSubtitleText.Text = isSingle
                ? "粘贴或上传微信资料页截图，自动提取信息并核对入库"
                : "自动唤起微信「通讯录管理」，按微信标签批量采集并去重入库";

            var activeFg = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
            var inactiveFg = (Brush)Application.Current.Resources["TextSecondaryBrush"];
            var transparent = new SolidColorBrush(Colors.Transparent);

            TabSingleIcon.Foreground = isSingle ? activeFg : inactiveFg;
            TabSingleText.Foreground = isSingle ? activeFg : inactiveFg;
            TabSingleText.FontWeight = isSingle ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            TabSingleLine.Background = isSingle ? activeFg : transparent;

            TabBatchIcon.Foreground = !isSingle ? activeFg : inactiveFg;
            TabBatchText.Foreground = !isSingle ? activeFg : inactiveFg;
            TabBatchText.FontWeight = !isSingle ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            TabBatchLine.Background = !isSingle ? activeFg : transparent;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBatchBusy)
            {
                _batchCts?.Cancel();
                return;
            }
            if (!_isSubmitting)
            {
                Hide();
            }
        }

        private void CloseAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            if (_isBatchBusy)
            {
                args.Handled = true;
                _batchCts?.Cancel();
                return;
            }
            if (!_isSubmitting)
            {
                args.Handled = true;
                Hide();
            }
        }

        // =====================================================================
        // MODE 1: Single Customer Screenshot OCR / Manual Creation
        // =====================================================================

        private async void PasteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            if (_activeMode != "single" || _isSubmitting || _isBusyOcr) return;
            if (OcrService.HasClipboardBitmapFast())
            {
                args.Handled = true;
                await ImportFromClipboardAsync();
                return;
            }
            if (!await OcrService.HasClipboardImageAsync()) return;

            args.Handled = true;
            await ImportFromClipboardAsync();
        }

        private async void PasteButton_Click(object sender, RoutedEventArgs e) => await ImportFromClipboardAsync();

        private async void UploadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusyOcr || _isSubmitting) return;
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
            if (_isBusyOcr || _isSubmitting) return;
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
                if (_isClosed) return;
                var parsed = WeChatOcrAnalyzer.Analyze(null, ocr);
                var (avatarPng, detectedGender) = await AvatarImageService.TryExtractProfileAvatarAndGenderAsync(bmp, ocr);
                if (_isClosed) return;

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
                if (!_isClosed) ShowStatus($"识别失败：{ex.Message}", isError: true);
            }
            finally
            {
                _isBusyOcr = false;
                if (_isClosed)
                {
                    _bitmap?.Dispose();
                    _bitmap = null;
                }
                else
                {
                    OcrSpinner.IsActive = false;
                    OcrSpinner.Visibility = Visibility.Collapsed;
                    PasteButton.IsEnabled = true;
                    UploadButton.IsEnabled = true;
                }
            }
        }

        private async void SubmitButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isSubmitting || _isBusyOcr) return;

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
                var allCustomers = await AppServices.Backend.ListAllCustomersAsync();
                if (_isClosed) return;
                CustomerDto? dup = BackendApiClient.FindDuplicateCustomer(allCustomers, account, name, remark);

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
            catch (Exception ex)
            {
                if (!_isClosed) ShowStatus($"保存异常：{ex.Message}", isError: true);
            }
            finally
            {
                if (!_isClosed) SetSubmitting(false);
            }
        }

        private async void DuplicateOverwriteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isSubmitting) return;
            DuplicateOverlay.Visibility = Visibility.Collapsed;
            SetSubmitting(true);
            try { await ExecuteSaveAsync(overwriteExisting: true, allowDuplicate: false); }
            catch (Exception ex) { if (!_isClosed) ShowStatus($"保存异常：{ex.Message}", isError: true); }
            finally { if (!_isClosed) SetSubmitting(false); }
        }

        private async void DuplicateCreateNewButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isSubmitting) return;
            DuplicateOverlay.Visibility = Visibility.Collapsed;
            SetSubmitting(true);
            try { await ExecuteSaveAsync(overwriteExisting: false, allowDuplicate: true); }
            catch (Exception ex) { if (!_isClosed) ShowStatus($"保存异常：{ex.Message}", isError: true); }
            finally { if (!_isClosed) SetSubmitting(false); }
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
            }
            else
            {
                _pendingRequest.AllowDuplicate = allowDuplicate;
                res = await AppServices.Backend.CreateCustomerAsync(_pendingRequest);
            }

            if (res.IsSuccess && res.Data is not null)
            {
                WasOverwritten = overwriteExisting && _duplicateMatch is not null;
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

        private string GetSelectedGender() => GenderBox.SelectedIndex switch
        {
            1 => "男",
            2 => "女",
            _ => string.Empty
        };

        private void ShowStatus(string message, bool isError = false)
        {
            StatusBanner.Background = (Brush)Application.Current.Resources[isError ? "DangerBgBrush" : "BrandSoftBgBrush"];
            StatusText.Foreground = (Brush)Application.Current.Resources[isError ? "DangerTextBrush" : "BrandPrimaryBrush"];
            StatusText.Text = message;
            StatusBanner.Visibility = Visibility.Visible;
        }

        // =====================================================================
        // MODE 2: Batch Import by WeChat Contact Manager Tags
        // =====================================================================

        private async void ScanTagsButton_Click(object sender, RoutedEventArgs e) =>
            await ScanTagsAsync(scrollDown: false);

        private async void ScrollMoreTagsButton_Click(object sender, RoutedEventArgs e) =>
            await ScanTagsAsync(scrollDown: true);

        private async Task ScanTagsAsync(bool scrollDown)
        {
            if (_isBatchBusy) return;
            _isBatchBusy = true;
            _batchCts = new CancellationTokenSource();
            var ct = _batchCts.Token;

            ScanTagsButton.IsEnabled = false;
            ScrollMoreTagsButton.IsEnabled = false;
            StartImportButton.IsEnabled = false;
            ScanStatusTitle.Text = "正在自动唤起微信「通讯录管理」并识别标签...";
            FooterStatusText.Text = "正在执行 OCR 扫描，请稍候...";
            AppendLog(scrollDown ? "正在向下滚动左侧标签栏并重扫..." : "正在检测/唤起微信并定位「通讯录管理」窗口...");

            try
            {
                var result = await Task.Run(() => _importer.LocateAndScanTagsAsync(scrollLeftSidebarDown: scrollDown, ct: ct), ct);
                if (_isClosed) return;

                _contactManagerHwnd = result.ContactManagerHwnd;
                _wechatPid = result.ProcessId;

                if (!scrollDown) _tags.Clear();
                foreach (var t in result.Tags)
                {
                    if (!_tags.Any(x => x.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        _tags.Add(t);
                    }
                }

                bool hasTags = _tags.Count > 0;
                TagsListView.Visibility = hasTags ? Visibility.Visible : Visibility.Collapsed;
                EmptyTagsHint.Visibility = hasTags ? Visibility.Collapsed : Visibility.Visible;
                ScrollMoreTagsButton.IsEnabled = _contactManagerHwnd != nint.Zero;
                StartImportButton.IsEnabled = _contactManagerHwnd != nint.Zero && hasTags;

                ScanStatusTitle.Text = hasTags
                    ? $"已识别 {_tags.Count} 个微信标签（窗口已规范为 900×666）"
                    : "已定位窗口，但未识别到标签";
                ScanStatusSubText.Text = result.Message;
                FooterStatusText.Text = hasTags
                    ? "请勾选要导入的标签，点击「开始批量导入」"
                    : "可在左下角手动补充标签名后再导入";
                AppendLog(result.Message);

                if (!_isClosed && AppServices.MainWindowHandle != nint.Zero)
                {
                    _ = Win32WindowHelper.SetForegroundWindow(AppServices.MainWindowHandle);
                }
            }
            catch (OperationCanceledException)
            {
                if (!_isClosed)
                {
                    FooterStatusText.Text = "扫描已取消。";
                    AppendLog("扫描已取消。");
                }
            }
            catch (Exception ex)
            {
                if (!_isClosed)
                {
                    ScanStatusTitle.Text = "定位或扫描失败";
                    ScanStatusSubText.Text = ex.Message;
                    FooterStatusText.Text = ex.Message;
                    AppendLog($"✕ {ex.Message}");
                }
            }
            finally
            {
                _isBatchBusy = false;
                _batchCts?.Dispose();
                _batchCts = null;
                if (!_isClosed)
                {
                    ScanTagsButton.IsEnabled = true;
                    ScrollMoreTagsButton.IsEnabled = _contactManagerHwnd != nint.Zero;
                    StartImportButton.IsEnabled = _contactManagerHwnd != nint.Zero && _tags.Count > 0;
                }
            }
        }

        private void ToggleSelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (_tags.Count == 0 || _isBatchBusy) return;
            bool selectAll = _tags.Any(t => !t.IsSelected);
            var snapshot = _tags.ToList();
            _tags.Clear();
            foreach (var item in snapshot)
            {
                item.IsSelected = selectAll;
                _tags.Add(item);
            }
        }

        private void AddManualTagButton_Click(object sender, RoutedEventArgs e)
        {
            string name = ManualTagBox.Text.Trim();
            if (string.IsNullOrEmpty(name)) return;
            if (!_tags.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                _tags.Add(new WeChatTagItem { Name = name, Count = -1, IsSelected = true });
            }
            ManualTagBox.Text = string.Empty;
            TagsListView.Visibility = Visibility.Visible;
            EmptyTagsHint.Visibility = Visibility.Collapsed;
            StartImportButton.IsEnabled = _contactManagerHwnd != nint.Zero && _tags.Count > 0;
        }

        private async void StartImportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBatchBusy || _contactManagerHwnd == nint.Zero) return;
            var chosen = _tags.Where(t => t.IsSelected).ToList();
            if (chosen.Count == 0)
            {
                FooterStatusText.Text = "请先在左侧至少勾选一个微信标签。";
                return;
            }

            bool overwrite = DedupOverwriteRadio.IsChecked == true;
            const int defaultSmartDelayMs = 480;

            _isBatchBusy = true;
            _batchCts = new CancellationTokenSource();
            ScanTagsButton.IsEnabled = false;
            ScrollMoreTagsButton.IsEnabled = false;
            StartImportButton.Visibility = Visibility.Collapsed;
            StopImportButton.Visibility = Visibility.Visible;
            DoneButton.IsEnabled = false;
            CloseButton.IsEnabled = false;
            ImportProgressBar.IsIndeterminate = chosen.Sum(t => Math.Max(0, t.Count)) == 0;
            ImportProgressBar.Value = 0;

            _logBuilder.Clear();
            AppendLog($"开始按标签批量导入（共 {chosen.Count} 个标签，策略：{(overwrite ? "覆盖更新" : "自动去重跳过")}）...");

            var progress = new Progress<BatchTagImportProgress>(p =>
            {
                if (_isClosed) return;
                StatProcessedText.Text = p.Processed.ToString();
                StatCreatedText.Text = p.CreatedCount.ToString();
                StatUpdatedText.Text = p.UpdatedCount.ToString();
                StatSkippedText.Text = p.SkippedCount.ToString();
                FooterStatusText.Text = p.StatusMessage;

                if (p.ExpectedTotal > 0)
                {
                    ImportProgressBar.IsIndeterminate = false;
                    ImportProgressBar.Value = Math.Min(100, p.Processed * 100.0 / p.ExpectedTotal);
                }
                if (!string.IsNullOrWhiteSpace(p.LogLine))
                {
                    AppendLog(p.LogLine);
                }
            });

            var ct = _batchCts.Token;
            var hwnd = _contactManagerHwnd;
            var pid = _wechatPid;
            try
            {
                var summary = await Task.Run(() => _importer.RunBatchImportAsync(
                    hwnd,
                    pid,
                    chosen,
                    overwrite,
                    defaultSmartDelayMs,
                    progress,
                    ct));

                TotalBatchImportedOrUpdated += summary.CreatedCount + summary.UpdatedCount;
                if (_isClosed) return;

                ImportProgressBar.IsIndeterminate = false;
                if (!summary.WasCancelled) ImportProgressBar.Value = 100;

                string resultText = $"{summary.StopReason} 新增 {summary.CreatedCount} · 更新 {summary.UpdatedCount} · 跳过 {summary.SkippedCount}" +
                                    (summary.FailedCount > 0 ? $" · 失败 {summary.FailedCount}" : "");
                FooterStatusText.Text = resultText;
                AppendLog($"=== {resultText} ===");
            }
            catch (Exception ex)
            {
                if (!_isClosed)
                {
                    FooterStatusText.Text = $"批量导入异常：{ex.Message}";
                    AppendLog($"✕ 异常：{ex.Message}");
                }
            }
            finally
            {
                _isBatchBusy = false;
                _batchCts?.Dispose();
                _batchCts = null;
                if (!_isClosed)
                {
                    ScanTagsButton.IsEnabled = true;
                    ScrollMoreTagsButton.IsEnabled = _contactManagerHwnd != nint.Zero;
                    StartImportButton.Visibility = Visibility.Visible;
                    StartImportButton.IsEnabled = _contactManagerHwnd != nint.Zero && _tags.Count > 0;
                    StopImportButton.Visibility = Visibility.Collapsed;
                    DoneButton.IsEnabled = true;
                    CloseButton.IsEnabled = true;

                    if (AppServices.MainWindowHandle != nint.Zero)
                    {
                        _ = Win32WindowHelper.SetForegroundWindow(AppServices.MainWindowHandle);
                    }
                }
            }
        }

        private void StopImportButton_Click(object sender, RoutedEventArgs e)
        {
            FooterStatusText.Text = "正在安全停止...";
            _batchCts?.Cancel();
        }

        private void AppendLog(string line)
        {
            if (_logBuilder.Length > 0) _logBuilder.AppendLine();
            _logBuilder.Append($"[{DateTime.Now:HH:mm:ss}] {line}");
            LiveLogText.Text = _logBuilder.ToString();
            LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight + 100, null);
        }
    }
}
