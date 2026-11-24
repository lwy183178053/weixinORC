using Client.Models;
using Client.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.UI;

namespace Client.Pages
{
    public sealed partial class WeChatOcrPage : Page
    {
        private List<WeChatWindowInfo> _allWindows = [];
        private SoftwareBitmap? _currentBitmap;
        private OcrRecognitionResult? _lastOcrResult;
        private string _candidateAddedDate = string.Empty;
        private bool _isBusy;
        private bool _isInitialized;

        public WeChatOcrPage()
        {
            InitializeComponent();
            _isInitialized = true;
            EngineNoteText.Text = AppServices.Ocr.RapidEngine.AvailabilityNote;
            Loaded += WeChatOcrPage_Loaded;
        }

        private async void WeChatOcrPage_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= WeChatOcrPage_Loaded;
            await RefreshWindowListAsync();
        }

        private WeChatWindowInfo? SelectedWindow => WindowsListView.SelectedItem as WeChatWindowInfo;

        private async void ShowAllWindowsCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitialized) await RefreshWindowListAsync();
        }

        private async void RefreshWindowsButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshWindowListAsync();
        }

        private void WindowSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isInitialized) ApplyWindowFilter();
        }

        private async Task RefreshWindowListAsync()
        {
            bool includeAll = ShowAllWindowsCheck.IsChecked == true;
            _allWindows = await Task.Run(() => AppServices.WeChatWindows.EnumerateWindows(includeAll));
            ApplyWindowFilter();
        }

        private void ApplyWindowFilter()
        {
            string query = WindowSearchBox.Text.Trim();
            var filtered = string.IsNullOrEmpty(query)
                ? _allWindows
                : _allWindows.Where(w =>
                    w.DisplayTitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    w.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

            WindowsListView.ItemsSource = filtered;
            WindowCountPill.Text = filtered.Count.ToString();
            EmptyWindowListHint.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (filtered.Count > 0)
            {
                WindowsListView.SelectedIndex = 0;
            }
            UpdateWindowActionButtons();
        }

        private void WindowsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateWindowActionButtons();
            if (SelectedWindow is { } w)
            {
                PreviewMetaText.Text = $"已选窗口：{w.DisplayTitle} ({w.DimensionsLabel})";
            }
        }

        private void UpdateWindowActionButtons()
        {
            bool hasSelection = SelectedWindow is not null && !_isBusy;
            ShowWindowButton.IsEnabled = hasSelection;
            HideWindowButton.IsEnabled = hasSelection;
            CaptureAndOcrButton.IsEnabled = hasSelection;
        }

        private async void ShowWindowButton_Click(object sender, RoutedEventArgs e)
        {
            var target = SelectedWindow;
            if (target is null) return;
            if (AppServices.WeChatWindows.ShowWindow(target))
            {
                ShowBanner($"已恢复显示窗口：{target.DisplayTitle}。", BannerKind.Success);
                await RefreshWindowListAsync();
            }
            else
            {
                ShowBanner("恢复窗口失败：窗口句柄可能已关闭。", BannerKind.Danger);
            }
        }

        private async void HideWindowButton_Click(object sender, RoutedEventArgs e)
        {
            var target = SelectedWindow;
            if (target is null) return;
            if (AppServices.WeChatWindows.HideWindow(target))
            {
                ShowBanner(AppServices.WeChatWindows.PauseReason, BannerKind.Warning);
                await RefreshWindowListAsync();
            }
            else
            {
                ShowBanner("隐藏窗口失败：窗口句柄可能已关闭。", BannerKind.Danger);
            }
        }

        private async void CaptureAndOcrButton_Click(object sender, RoutedEventArgs e)
        {
            var target = SelectedWindow;
            if (target is null) return;
            if (!EnsureRapidAvailable()) return;

            _isBusy = true;
            UpdateWindowActionButtons();
            try
            {
                SoftwareBitmap bitmap = await Task.Run(() => AppServices.WeChatWindows.CaptureWindow(target));
                await RunOcrPipelineAsync(bitmap, target, $"窗口截图：{target.DisplayTitle}");
            }
            catch (Exception ex)
            {
                AppServices.Logs.Error("OCR", $"窗口截取或识别失败：{ex.Message}");
                ShowBanner($"窗口截取或识别失败：{ex.Message}", BannerKind.Danger);
            }
            finally
            {
                _isBusy = false;
                UpdateWindowActionButtons();
            }
        }

        private async void PasteClipboardButton_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureRapidAvailable()) return;

            _isBusy = true;
            UpdateWindowActionButtons();
            try
            {
                var (bitmap, label) = await AppServices.Ocr.TryLoadImageFromClipboardAsync();
                if (bitmap is null)
                {
                    ShowBanner("剪贴板中未检测到截图或图片文件，请先截图复制后再试。", BannerKind.Warning);
                    return;
                }
                await RunOcrPipelineAsync(bitmap, null, label);
            }
            catch (Exception ex)
            {
                AppServices.Logs.Error("OCR", $"读取剪贴板或识别失败：{ex.Message}");
                ShowBanner($"读取剪贴板或识别失败：{ex.Message}", BannerKind.Danger);
            }
            finally
            {
                _isBusy = false;
                UpdateWindowActionButtons();
            }
        }

        private async void LoadImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureRapidAvailable()) return;

            string? filePath = Win32WindowHelper.PickImageFilePath(AppServices.MainWindowHandle);
            if (string.IsNullOrWhiteSpace(filePath)) return;

            _isBusy = true;
            UpdateWindowActionButtons();
            try
            {
                SoftwareBitmap bitmap = await Task.Run(() => AppServices.Ocr.LoadImageFileFromPathAsync(filePath));
                await RunOcrPipelineAsync(bitmap, null, $"本地图片：{System.IO.Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                AppServices.Logs.Error("OCR", $"加载图片或识别失败：{ex.Message}");
                ShowBanner($"加载图片或识别失败：{ex.Message}", BannerKind.Danger);
            }
            finally
            {
                _isBusy = false;
                UpdateWindowActionButtons();
            }
        }

        private bool EnsureRapidAvailable()
        {
            if (!AppServices.Ocr.RapidEngine.IsAvailable)
            {
                ShowBanner(AppServices.Ocr.RapidEngine.AvailabilityNote, BannerKind.Danger);
                return false;
            }
            return true;
        }

        private async Task RunOcrPipelineAsync(SoftwareBitmap bitmap, WeChatWindowInfo? window, string sourceLabel)
        {
            _currentBitmap?.Dispose();
            _currentBitmap = bitmap;

            var imageSource = new SoftwareBitmapSource();
            await imageSource.SetBitmapAsync(bitmap);
            PreviewImage.Source = imageSource;
            PreviewSurface.Width = bitmap.PixelWidth;
            PreviewSurface.Height = bitmap.PixelHeight;
            OverlayCanvas.Width = bitmap.PixelWidth;
            OverlayCanvas.Height = bitmap.PixelHeight;
            PreviewViewbox.Visibility = Visibility.Visible;
            EmptyPreviewHint.Visibility = Visibility.Collapsed;

            ShowBanner("正在识别中...", BannerKind.Info);
            var ocrResult = await AppServices.Ocr.RapidEngine.RecognizeAsync(bitmap);
            _lastOcrResult = ocrResult;

            var analysis = WeChatOcrAnalyzer.Analyze(window, ocrResult);
            if (window is not null) window.DetectedState = analysis.InferredState;

            PreviewMetaText.Text = $"{sourceLabel} · {ocrResult.ImageWidth}×{ocrResult.ImageHeight}";
            TabLinesText.Text = $"识别文本 ({ocrResult.Lines.Count})";
            OcrStatsText.Text = $"共 {ocrResult.Lines.Count} 行 · 耗时 {ocrResult.ElapsedMilliseconds} ms";
            OcrTimingText.Text = $"耗时: {ocrResult.ElapsedMilliseconds} ms · 文本行: {ocrResult.Lines.Count}";
            OcrLinesListView.ItemsSource = ocrResult.Lines;

            ApplyStateBadge(analysis.InferredState);
            InferredReasonText.Text = analysis.StateReason;

            CandidateNameBox.Text = analysis.CandidateWechatName;
            CandidateAccountBox.Text = analysis.CandidateWechatAccount;
            CandidateRegionBox.Text = analysis.CandidateRegion;
            CandidatePhoneBox.Text = analysis.CandidatePhone;
            CandidateSignatureBox.Text = analysis.CandidateSignature;
            CandidateSourceBox.Text = analysis.CandidateSource;
            CandidateNoteBox.Text = analysis.CandidateNote;
            _candidateAddedDate = analysis.CandidateAddedDate;

            RenderOverlayBoxes();

            if (analysis.InferredState == WeChatLoginState.Disconnected)
            {
                ShowBanner("检测到微信掉线/退出提示：应立即停止该账号任务！", BannerKind.Danger);
            }
            else
            {
                ShowBanner($"识别完成：耗时 {ocrResult.ElapsedMilliseconds} ms，共识别 {ocrResult.Lines.Count} 行文本。", BannerKind.Success);
            }
        }

        private void ApplyStateBadge(WeChatLoginState state)
        {
            (string label, string bgKey, string fgKey) = state switch
            {
                WeChatLoginState.LoggedIn => ("主界面（已登录）", "SuccessBgBrush", "SuccessTextBrush"),
                WeChatLoginState.LoginPage => ("登录页（待扫码）", "WarningBgBrush", "WarningTextBrush"),
                WeChatLoginState.Disconnected => ("掉线 / 已退出", "DangerBgBrush", "DangerTextBrush"),
                WeChatLoginState.HiddenOrMinimized => ("已隐藏/最小化", "WarningBgBrush", "WarningTextBrush"),
                _ => ("资料页 / 常规画面", "BrandSoftBgBrush", "BrandPrimaryBrush")
            };

            InferredStateText.Text = label;
            StatePillBorder.Background = (Brush)Application.Current.Resources[bgKey];
            InferredStateText.Foreground = (Brush)Application.Current.Resources[fgKey];
        }

        private void ShowOverlayCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitialized) RenderOverlayBoxes();
        }

        private void RenderOverlayBoxes()
        {
            if (OverlayCanvas is null) return;
            OverlayCanvas.Children.Clear();
            if (ShowOverlayCheck.IsChecked != true || _lastOcrResult is null) return;

            var strokeBrush = new SolidColorBrush(Color.FromArgb(220, 124, 92, 252));
            var fillBrush = new SolidColorBrush(Color.FromArgb(30, 124, 92, 252));

            foreach (var line in _lastOcrResult.Lines)
            {
                var rect = new Rectangle
                {
                    Width = line.Bounds.Width,
                    Height = line.Bounds.Height,
                    Stroke = strokeBrush,
                    StrokeThickness = 2,
                    Fill = fillBrush,
                    RadiusX = 3,
                    RadiusY = 3
                };
                Canvas.SetLeft(rect, line.Bounds.X);
                Canvas.SetTop(rect, line.Bounds.Y);
                OverlayCanvas.Children.Add(rect);
            }
        }

        private void InspectorTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string tab) return;
            bool isVerify = tab == "verify";
            VerifyPanel.Visibility = isVerify ? Visibility.Visible : Visibility.Collapsed;
            LinesPanel.Visibility = isVerify ? Visibility.Collapsed : Visibility.Visible;

            var activeFg = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
            var inactiveFg = (Brush)Application.Current.Resources["TextSecondaryBrush"];
            var transparent = new SolidColorBrush(Colors.Transparent);

            TabVerifyText.Foreground = isVerify ? activeFg : inactiveFg;
            TabVerifyText.FontWeight = isVerify ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            TabVerifyUnderline.Background = isVerify ? activeFg : transparent;

            TabLinesText.Foreground = !isVerify ? activeFg : inactiveFg;
            TabLinesText.FontWeight = !isVerify ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            TabLinesUnderline.Background = !isVerify ? activeFg : transparent;
        }

        private async void SaveCustomerButton_Click(object sender, RoutedEventArgs e)
        {
            string name = CandidateNameBox.Text.Trim();
            string account = CandidateAccountBox.Text.Trim();
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(account))
            {
                ShowBanner("请至少填写微信昵称或微信号后再保存。", BannerKind.Warning);
                return;
            }

            SaveCustomerButton.IsEnabled = false;
            try
            {
                var result = await AppServices.Backend.CreateCustomerAsync(new CreateCustomerRequest
                {
                    WechatName = name,
                    WechatAccount = account,
                    Region = CandidateRegionBox.Text.Trim(),
                    Phone = CandidatePhoneBox.Text.Trim(),
                    Signature = CandidateSignatureBox.Text.Trim(),
                    Source = CandidateSourceBox.Text.Trim(),
                    Note = CandidateNoteBox.Text.Trim(),
                    AddedDate = _candidateAddedDate
                });

                if (result.IsSuccess && result.Data is not null)
                {
                    ShowBanner($"保存成功：客户 #{result.Data.Id}（{result.Data.DisplayName}）已入库。", BannerKind.Success);
                }
                else
                {
                    ShowBanner($"保存失败：{result.ErrorMessage}", BannerKind.Danger);
                }
            }
            finally
            {
                SaveCustomerButton.IsEnabled = true;
            }
        }

        private void CopyAllTextButton_Click(object sender, RoutedEventArgs e)
        {
            if (_lastOcrResult is null || string.IsNullOrWhiteSpace(_lastOcrResult.FullText))
            {
                ShowBanner("暂无可复制的识别文本。", BannerKind.Info);
                return;
            }
            var pkg = new DataPackage();
            pkg.SetText(_lastOcrResult.FullText);
            Clipboard.SetContent(pkg);
            ShowBanner("识别全文已复制到剪贴板。", BannerKind.Success);
        }

        private void CloseBannerButton_Click(object sender, RoutedEventArgs e) => StatusBanner.Visibility = Visibility.Collapsed;

        private enum BannerKind { Info, Success, Warning, Danger }

        private void ShowBanner(string message, BannerKind kind)
        {
            (string bgKey, string fgKey) = kind switch
            {
                BannerKind.Success => ("SuccessBgBrush", "SuccessTextBrush"),
                BannerKind.Warning => ("WarningBgBrush", "WarningTextBrush"),
                BannerKind.Danger => ("DangerBgBrush", "DangerTextBrush"),
                _ => ("BrandSoftBgBrush", "BrandPrimaryBrush")
            };

            StatusBanner.Background = (Brush)Application.Current.Resources[bgKey];
            StatusBannerText.Foreground = (Brush)Application.Current.Resources[fgKey];
            StatusBannerText.Text = message;
            StatusBanner.Visibility = Visibility.Visible;
        }
    }
}
