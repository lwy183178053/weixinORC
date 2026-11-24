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
        private bool _isBusy;
        private bool _isInitialized;

        public WeChatOcrPage()
        {
            InitializeComponent();
            _isInitialized = true;
            InitializeEngines();
            Loaded += WeChatOcrPage_Loaded;
        }

        private async void WeChatOcrPage_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= WeChatOcrPage_Loaded;
            await RefreshWindowListAsync();
        }

        private void InitializeEngines()
        {
            OcrEngineCombo.ItemsSource = AppServices.Ocr.Engines;
            OcrEngineCombo.SelectedItem = AppServices.Ocr.GetDefaultEngine();
            UpdateSelectedEngineNote();
        }

        private IOcrEngine SelectedEngine =>
            OcrEngineCombo.SelectedItem as IOcrEngine ?? AppServices.Ocr.GetDefaultEngine();

        private WeChatWindowInfo? SelectedWindow =>
            WindowsListView.SelectedItem as WeChatWindowInfo;

        private void OcrEngineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized)
            {
                return;
            }

            UpdateSelectedEngineNote();
        }

        private void UpdateSelectedEngineNote()
        {
            if (EngineNoteText is not null)
            {
                EngineNoteText.Text = SelectedEngine.AvailabilityNote;
            }
        }

        private async void ShowAllWindowsCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized)
            {
                return;
            }

            await RefreshWindowListAsync();
        }

        private async void RefreshWindowsButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshWindowListAsync();
        }

        private void WindowSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isInitialized)
            {
                return;
            }

            ApplyWindowFilter();
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
            if (target is null)
            {
                return;
            }

            if (AppServices.WeChatWindows.ShowWindow(target))
            {
                ShowBanner($"已恢复显示窗口：{target.DisplayTitle}。重新 OCR 复核后可继续后续任务。", BannerKind.Success);
                await RefreshWindowListAsync();
            }
            else
            {
                ShowBanner("恢复窗口失败：窗口句柄可能已关闭，请刷新窗口列表。", BannerKind.Danger);
            }
        }

        private async void HideWindowButton_Click(object sender, RoutedEventArgs e)
        {
            var target = SelectedWindow;
            if (target is null)
            {
                return;
            }

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
            if (target is null)
            {
                return;
            }

            if (!SelectedEngine.IsAvailable)
            {
                ShowBanner(SelectedEngine.AvailabilityNote, BannerKind.Warning);
                return;
            }

            _isBusy = true;
            UpdateWindowActionButtons();
            try
            {
                SoftwareBitmap bitmap = await Task.Run(() => AppServices.WeChatWindows.CaptureWindow(target));
                await RunOcrPipelineAsync(bitmap, target, $"窗口截图：{target.DisplayTitle}");
            }
            catch (Exception ex)
            {
                ShowBanner($"窗口截取或 OCR 失败：{ex.Message}", BannerKind.Danger);
            }
            finally
            {
                _isBusy = false;
                UpdateWindowActionButtons();
            }
        }

        private async void LoadImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (!SelectedEngine.IsAvailable)
            {
                ShowBanner(SelectedEngine.AvailabilityNote, BannerKind.Warning);
                return;
            }

            string? filePath = Win32WindowHelper.PickImageFilePath(AppServices.MainWindowHandle);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            _isBusy = true;
            UpdateWindowActionButtons();
            try
            {
                SoftwareBitmap bitmap = await Task.Run(() => AppServices.Ocr.LoadImageFileFromPathAsync(filePath));
                await RunOcrPipelineAsync(bitmap, null, $"本地图片：{System.IO.Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                ShowBanner($"加载图片或 OCR 失败：{ex.Message}", BannerKind.Danger);
            }
            finally
            {
                _isBusy = false;
                UpdateWindowActionButtons();
            }
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

            var engine = SelectedEngine;
            var ocrResult = await Task.Run(() => engine.RecognizeAsync(bitmap));
            _lastOcrResult = ocrResult;

            var analysis = WeChatOcrAnalyzer.Analyze(window, ocrResult);
            if (window is not null)
            {
                window.DetectedState = analysis.InferredState;
            }

            PreviewMetaText.Text = $"{sourceLabel} · {ocrResult.ImageWidth}×{ocrResult.ImageHeight}";
            TabLinesText.Text = $"OCR 文本行 ({ocrResult.Lines.Count})";
            OcrStatsText.Text = $"共 {ocrResult.Lines.Count} 行 · 耗时 {ocrResult.ElapsedMilliseconds} ms";
            OcrTimingText.Text = $"耗时: {ocrResult.ElapsedMilliseconds} ms · 文本行: {ocrResult.Lines.Count} · 引擎: {ocrResult.EngineId}";
            OcrLinesListView.ItemsSource = ocrResult.Lines;

            ApplyStateBadge(analysis.InferredState);
            InferredReasonText.Text = analysis.StateReason;

            if (!string.IsNullOrWhiteSpace(analysis.CandidateWechatName))
            {
                CandidateNameBox.Text = analysis.CandidateWechatName;
            }

            if (!string.IsNullOrWhiteSpace(analysis.CandidateWechatAccount))
            {
                CandidateAccountBox.Text = analysis.CandidateWechatAccount;
            }

            if (!string.IsNullOrWhiteSpace(analysis.CandidateNote) || !string.IsNullOrWhiteSpace(analysis.CandidateRegion))
            {
                var parts = new[] { analysis.CandidateNote, analysis.CandidateRegion }
                    .Where(p => !string.IsNullOrWhiteSpace(p));
                CandidateNoteBox.Text = string.Join(" | ", parts);
            }

            RenderOverlayBoxes();

            if (analysis.InferredState == WeChatLoginState.Disconnected)
            {
                ShowBanner("检测到微信掉线/退出提示：按安全策略应立即停止该账号的同步与键鼠任务！", BannerKind.Danger);
            }
            else
            {
                ShowBanner($"OCR 识别完成：耗时 {ocrResult.ElapsedMilliseconds} ms，共识别 {ocrResult.Lines.Count} 行文本。", BannerKind.Success);
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
                _ => ("未判定 / 常规画面", "BrandSoftBgBrush", "BrandPrimaryBrush")
            };

            InferredStateText.Text = label;
            StatePillBorder.Background = (Brush)Application.Current.Resources[bgKey];
            InferredStateText.Foreground = (Brush)Application.Current.Resources[fgKey];
        }

        private void ShowOverlayCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized)
            {
                return;
            }

            RenderOverlayBoxes();
        }

        private void RenderOverlayBoxes()
        {
            if (OverlayCanvas is null)
            {
                return;
            }

            OverlayCanvas.Children.Clear();
            if (ShowOverlayCheck.IsChecked != true || _lastOcrResult is null)
            {
                return;
            }

            var strokeBrush = new SolidColorBrush(Color.FromArgb(220, 124, 92, 252));
            var fillBrush = new SolidColorBrush(Color.FromArgb(34, 124, 92, 252));

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
            if (sender is not Button btn || btn.Tag is not string tab)
            {
                return;
            }

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
            string note = CandidateNoteBox.Text.Trim();

            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(account))
            {
                ShowBanner("请至少填写微信昵称或微信号后再保存。", BannerKind.Warning);
                return;
            }

            SaveCustomerButton.IsEnabled = false;
            try
            {
                var result = await AppServices.Backend.CreateCustomerAsync(name, account, note);
                if (result.IsSuccess && result.Data is not null)
                {
                    ShowBanner($"保存成功：客户 #{result.Data.Id}（{result.Data.WechatName} / {result.Data.WechatAccount}）已落库到当前员工名下。", BannerKind.Success);
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
                ShowBanner("暂无可复制的 OCR 文本。", BannerKind.Info);
                return;
            }

            var pkg = new DataPackage();
            pkg.SetText(_lastOcrResult.FullText);
            Clipboard.SetContent(pkg);
            ShowBanner("OCR 识别全文已复制到剪贴板。", BannerKind.Success);
        }

        private void CloseBannerButton_Click(object sender, RoutedEventArgs e)
        {
            StatusBanner.Visibility = Visibility.Collapsed;
        }

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
