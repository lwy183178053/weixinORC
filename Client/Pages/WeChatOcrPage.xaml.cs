using System.Collections.ObjectModel;
using Client.Models;
using Client.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
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
        private readonly ObservableCollection<WeChatProcessInfo> _processItems = [];
        private readonly ObservableCollection<WeChatWindowInfo> _windowItems = [];
        private readonly ObservableCollection<WeChatMonitorEvent> _events = [];
        private readonly DispatcherQueueTimer _pollTimer;

        private WeChatMonitorSnapshot? _lastSnapshot;
        private nint _selectedWindowHwnd;
        private SoftwareBitmap? _currentBitmap;
        private OcrRecognitionResult? _lastOcrResult;
        private bool _isPolling;
        private bool _isBusy;
        private bool _isInitialized;
        private bool _isUnloaded;
        private bool _isPreviewTab = true;

        public WeChatOcrPage()
        {
            InitializeComponent();
            ProcessesListView.ItemsSource = _processItems;
            WindowsListView.ItemsSource = _windowItems;
            EventsListView.ItemsSource = _events;
            DiagTimingText.Text = AppServices.Ocr.RapidEngine.AvailabilityNote;

            _pollTimer = DispatcherQueue.CreateTimer();
            _pollTimer.Interval = TimeSpan.FromSeconds(2);
            _pollTimer.Tick += PollTimer_Tick;

            _isInitialized = true;
            Loaded += WeChatOcrPage_Loaded;
            Unloaded += WeChatOcrPage_Unloaded;
        }

        private async void WeChatOcrPage_Loaded(object sender, RoutedEventArgs e)
        {
            _isUnloaded = false;
            AppServices.Settings.ThemeChanged += OnThemeChanged;
            await RefreshMonitorAsync(logInitialState: _lastSnapshot is null);
            if (AutoMonitorToggle.IsOn)
            {
                _pollTimer.Start();
            }
        }

        private void WeChatOcrPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _isUnloaded = true;
            _pollTimer.Stop();
            AppServices.Settings.ThemeChanged -= OnThemeChanged;
            if (!_isBusy)
            {
                _currentBitmap?.Dispose();
                _currentBitmap = null;
            }
        }

        private void OnThemeChanged(bool _)
        {
            UpdateDiagTabVisuals();
            if (_lastSnapshot is not null)
            {
                ApplySnapshotToUi(_lastSnapshot);
            }
        }

        private WeChatWindowInfo? SelectedWindow => WindowsListView.SelectedItem as WeChatWindowInfo;

        private async void PollTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            if (_isUnloaded || _isPolling || _isBusy) return;
            await RefreshMonitorAsync(logInitialState: false);
        }

        private void AutoMonitorToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;
            if (AutoMonitorToggle.IsOn)
            {
                _pollTimer.Start();
                LastCheckText.Text = $"实时监听中（每 2 秒同步 · 上次 {DateTime.Now:HH:mm:ss}）";
                AppendEvent("已开启实时监控轮询。");
            }
            else
            {
                _pollTimer.Stop();
                LastCheckText.Text = "实时监听已暂停（可点击刷新按钮手动检测）";
                AppendEvent("已暂停实时监控轮询。");
            }
        }

        private async void RefreshNowButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshMonitorAsync(logInitialState: false);
        }

        private async Task RefreshMonitorAsync(bool logInitialState)
        {
            if (_isPolling || _isUnloaded) return;
            _isPolling = true;
            try
            {
                var snap = await Task.Run(() => AppServices.WeChatWindows.CaptureMonitorSnapshot());
                if (_isUnloaded) return;

                RecordStateDiffEvents(_lastSnapshot, snap, logInitialState);
                _lastSnapshot = snap;
                ApplySnapshotToUi(snap);
            }
            catch (Exception ex)
            {
                AppServices.Logs.Error("WIN", $"微信监控轮询异常：{ex.Message}");
            }
            finally
            {
                _isPolling = false;
            }
        }

        private void RecordStateDiffEvents(WeChatMonitorSnapshot? prev, WeChatMonitorSnapshot curr, bool logInitial)
        {
            if (prev is null)
            {
                if (logInitial)
                {
                    if (curr.IsAlive)
                    {
                        string pids = string.Join(", ", curr.Processes.Where(p => p.IsMainProcess).Select(p => p.ProcessId));
                        string ver = string.IsNullOrWhiteSpace(curr.PrimaryVersion) ? "" : $" v{curr.PrimaryVersion}";
                        AppendEvent($"检测到微信运行中（主进程 PID {pids}{ver}，{curr.Windows.Count} 个窗口，{curr.OverallStatusLabel}）。");
                    }
                    else
                    {
                        AppendEvent("当前未检测到运行中的微信进程。");
                    }
                }
                return;
            }

            if (!prev.IsAlive && curr.IsAlive)
            {
                string pids = string.Join(", ", curr.Processes.Where(p => p.IsMainProcess).Select(p => p.ProcessId));
                AppendEvent($"微信上线：检测到新进程 PID {pids}。");
            }
            else if (prev.IsAlive && !curr.IsAlive)
            {
                AppendEvent("微信已退出：所有微信进程已停止运行。");
                return;
            }

            if (prev.LoggedInCount != curr.LoggedInCount)
            {
                AppendEvent(curr.LoggedInCount > prev.LoggedInCount
                    ? $"登录状态变化：检测到已登录主窗口（当前 {curr.LoggedInCount} 个）。"
                    : $"登录状态变化：在线主窗口减少（{prev.LoggedInCount} → {curr.LoggedInCount}）。");
            }
            else if (prev.LoginPendingCount != curr.LoginPendingCount && curr.LoginPendingCount > 0)
            {
                AppendEvent("检测到微信处于待扫码登录界面。");
            }

            if (prev.Windows.Count != curr.Windows.Count)
            {
                AppendEvent($"窗口数量变化：{prev.Windows.Count} → {curr.Windows.Count} 个窗口。");
            }
        }

        private void ApplySnapshotToUi(WeChatMonitorSnapshot snap)
        {
            // Top header status
            string dotBrushKey = !snap.IsAlive
                ? "TextMutedBrush"
                : snap.LoggedInCount > 0
                    ? "SuccessTextBrush"
                    : "WarningTextBrush";
            string pillBgKey = !snap.IsAlive
                ? "AppInputBgBrush"
                : snap.LoggedInCount > 0
                    ? "SuccessBgBrush"
                    : "WarningBgBrush";
            string pillFgKey = !snap.IsAlive
                ? "TextSecondaryBrush"
                : snap.LoggedInCount > 0
                    ? "SuccessTextBrush"
                    : "WarningTextBrush";

            HeaderStatusDot.Fill = (Brush)Application.Current.Resources[dotBrushKey];
            HeaderStatusPill.Background = (Brush)Application.Current.Resources[pillBgKey];
            HeaderStatusLabel.Foreground = (Brush)Application.Current.Resources[pillFgKey];
            HeaderStatusLabel.Text = snap.OverallStatusLabel;

            LastCheckText.Text = AutoMonitorToggle.IsOn
                ? $"实时监听中 · 最近检测 {snap.CapturedAt:HH:mm:ss}"
                : $"实时监听已暂停 · 最近检测 {snap.CapturedAt:HH:mm:ss}";

            LaunchWeChatButton.Content = snap.IsPersonalWeChatAlive ? "唤起微信" : "启动微信";
            RestartWeChatButton.IsEnabled = snap.IsPersonalWeChatAlive && !_isBusy;

            // KPI 1: Alive
            KpiAliveText.Text = snap.IsAlive ? "运行中" : "未运行";
            KpiAliveText.Foreground = (Brush)Application.Current.Resources[snap.IsAlive ? "SuccessTextBrush" : "TextSecondaryBrush"];
            int mainProcCount = snap.Processes.Count(p => p.IsMainProcess);
            KpiAliveSubText.Text = snap.IsAlive
                ? $"{snap.Processes.Count} 个进程（主进程 {mainProcCount}）"
                : "未发现微信 / 企微进程";

            // KPI 2: Login State
            if (!snap.IsAlive)
            {
                KpiLoginText.Text = "未启动";
                KpiLoginSubText.Text = "批量导入时会自动启动并弹出微信";
            }
            else if (snap.LoggedInCount > 0)
            {
                KpiLoginText.Text = $"已登录 ({snap.LoggedInCount})";
                KpiLoginSubText.Text = "可直接执行批量导入与截图诊断";
            }
            else if (snap.LoginPendingCount > 0)
            {
                KpiLoginText.Text = "待扫码登录";
                KpiLoginSubText.Text = "请先在微信窗口完成扫码确认";
            }
            else
            {
                KpiLoginText.Text = "托盘 / 后台";
                KpiLoginSubText.Text = "需要 OCR 时将自动从托盘唤起";
            }

            // KPI 3: Windows
            KpiWindowsText.Text = $"{snap.Windows.Count} 个窗口";
            int otherCount = Math.Max(0, snap.Windows.Count - snap.MainWindowCount);
            KpiWindowsSubText.Text = $"主窗口 {snap.MainWindowCount} · 子/管理窗口 {otherCount}";

            // KPI 4: Version & Path
            KpiVersionText.Text = string.IsNullOrWhiteSpace(snap.PrimaryVersion) ? "未检测到" : $"v{snap.PrimaryVersion}";
            KpiPathSubText.Text = string.IsNullOrWhiteSpace(snap.ResolvedExecutablePath)
                ? "未找到安装路径，请点击「配置路径」"
                : snap.ResolvedExecutablePath;
            ToolTipService.SetToolTip(ConfigPathButton, string.IsNullOrWhiteSpace(snap.ResolvedExecutablePath)
                ? "手动选择 Weixin.exe / WeChat.exe 路径"
                : $"当前路径：{snap.ResolvedExecutablePath}\n点击更换");

            // Process list: reconcile in-place by ProcessId (zero UI flicker!)
            ReconcileProcesses(snap.Processes);
            ProcessCountPill.Text = _processItems.Count.ToString();
            EmptyProcessHint.Visibility = _processItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Window list: reconcile in-place by HWND (zero UI flicker & preserves selection!)
            ReconcileWindows(snap.Windows);
            WindowCountPill.Text = _windowItems.Count.ToString();
            EmptyWindowHint.Visibility = _windowItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (_windowItems.Count > 0)
            {
                int keepIdx = _selectedWindowHwnd != nint.Zero
                    ? IndexOfWindow(_selectedWindowHwnd)
                    : -1;
                int desiredIdx = keepIdx >= 0 ? keepIdx : 0;
                if (WindowsListView.SelectedIndex != desiredIdx)
                {
                    WindowsListView.SelectedIndex = desiredIdx;
                }
                else
                {
                    UpdateSelectedWindowDetails();
                }
            }
            else
            {
                _selectedWindowHwnd = nint.Zero;
                UpdateSelectedWindowDetails();
            }
        }

        private void ReconcileProcesses(List<WeChatProcessInfo> incoming)
        {
            var incomingIds = incoming.Select(p => p.ProcessId).ToHashSet();
            for (int i = _processItems.Count - 1; i >= 0; i--)
            {
                if (!incomingIds.Contains(_processItems[i].ProcessId))
                {
                    _processItems.RemoveAt(i);
                }
            }

            for (int targetIdx = 0; targetIdx < incoming.Count; targetIdx++)
            {
                var inc = incoming[targetIdx];
                int existingIdx = -1;
                for (int j = 0; j < _processItems.Count; j++)
                {
                    if (_processItems[j].ProcessId == inc.ProcessId)
                    {
                        existingIdx = j;
                        break;
                    }
                }

                if (existingIdx >= 0)
                {
                    _processItems[existingIdx].UpdateFrom(inc);
                    if (existingIdx != targetIdx)
                    {
                        _processItems.Move(existingIdx, targetIdx);
                    }
                }
                else
                {
                    _processItems.Insert(targetIdx, inc);
                }
            }
        }

        private void ReconcileWindows(List<WeChatWindowInfo> incoming)
        {
            var incomingHwnds = incoming.Select(w => w.Hwnd).ToHashSet();
            for (int i = _windowItems.Count - 1; i >= 0; i--)
            {
                if (!incomingHwnds.Contains(_windowItems[i].Hwnd))
                {
                    _windowItems.RemoveAt(i);
                }
            }

            for (int targetIdx = 0; targetIdx < incoming.Count; targetIdx++)
            {
                var inc = incoming[targetIdx];
                int existingIdx = IndexOfWindow(inc.Hwnd);
                if (existingIdx >= 0)
                {
                    _windowItems[existingIdx].UpdateFrom(inc);
                    if (existingIdx != targetIdx)
                    {
                        _windowItems.Move(existingIdx, targetIdx);
                    }
                }
                else
                {
                    _windowItems.Insert(targetIdx, inc);
                }
            }
        }

        private int IndexOfWindow(nint hwnd)
        {
            for (int i = 0; i < _windowItems.Count; i++)
            {
                if (_windowItems[i].Hwnd == hwnd) return i;
            }
            return -1;
        }

        private void WindowsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedWindowHwnd = SelectedWindow?.Hwnd ?? nint.Zero;
            UpdateSelectedWindowDetails();
        }

        private void UpdateSelectedWindowDetails()
        {
            var w = SelectedWindow;
            bool hasSelection = w is not null && !_isBusy;
            ShowWindowButton.IsEnabled = hasSelection;
            HideWindowButton.IsEnabled = hasSelection;
            DiagnoseWindowButton.IsEnabled = hasSelection;

            if (w is null)
            {
                DetailTitleText.Text = "未选择窗口";
                DetailPidText.Text = "-";
                DetailHwndText.Text = "-";
                DetailClassText.Text = "-";
                DetailSizeStateText.Text = "-";
                return;
            }

            DetailTitleText.Text = w.DisplayTitle;
            DetailPidText.Text = $"{w.ProcessName} (PID {w.ProcessId})";
            DetailHwndText.Text = w.HwndHex;
            DetailClassText.Text = string.IsNullOrWhiteSpace(w.ClassName) ? "-" : w.ClassName;
            DetailSizeStateText.Text = $"{w.DimensionsLabel} · {w.VisibilityLabel} · {w.StateLabel}";
        }

        private async void LaunchWeChatButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            SetBusy(true);
            ShowBanner("正在启动或唤起微信主窗口...", BannerKind.Info);
            try
            {
                var res = await Task.Run(() => AppServices.WeChatWindows.LaunchOrActivateWeChatAsync());
                if (_isUnloaded) return;

                ShowBanner(res.Message, res.Success ? BannerKind.Success : BannerKind.Warning);
                AppendEvent(res.Message);
                await RefreshMonitorAsync(logInitialState: false);
            }
            catch (Exception ex)
            {
                if (!_isUnloaded) ShowBanner($"启动/唤起微信失败：{ex.Message}", BannerKind.Danger);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void RestartWeChatButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy || !AppServices.TryBeginModalDialog()) return;

            bool confirmed = false;
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    RequestedTheme = AppServices.Settings.CurrentElementTheme,
                    Title = "确认重启微信",
                    Content = "将关闭当前运行中的个人微信进程并重新启动（未发送的聊天草稿可能会丢失）。是否继续？",
                    PrimaryButtonText = "确认重启",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    PrimaryButtonStyle = (Style)Application.Current.Resources["DangerOutlineButtonStyle"]
                };
                confirmed = await dialog.ShowAsync() == ContentDialogResult.Primary;
            }
            finally
            {
                AppServices.EndModalDialog();
            }

            if (!confirmed) return;

            SetBusy(true);
            ShowBanner("正在安全关闭并重新启动微信...", BannerKind.Info);
            AppendEvent("开始执行重启微信...");
            try
            {
                var res = await Task.Run(() => AppServices.WeChatWindows.RestartWeChatAsync());
                if (_isUnloaded) return;

                ShowBanner(res.Message, res.Success ? BannerKind.Success : BannerKind.Danger);
                AppendEvent(res.Message);
                await RefreshMonitorAsync(logInitialState: false);
            }
            catch (Exception ex)
            {
                if (!_isUnloaded) ShowBanner($"重启微信异常：{ex.Message}", BannerKind.Danger);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void ConfigPathButton_Click(object sender, RoutedEventArgs e)
        {
            string? picked = Win32WindowHelper.PickExecutableFilePath(AppServices.MainWindowHandle);
            if (string.IsNullOrWhiteSpace(picked)) return;

            AppServices.Settings.Current.WeChatExecutablePath = picked;
            AppServices.Settings.Save();
            ShowBanner($"已保存微信程序路径：{picked}", BannerKind.Success);
            AppendEvent($"已更新微信程序路径：{picked}");
            await RefreshMonitorAsync(logInitialState: false);
        }

        private async void ShowWindowButton_Click(object sender, RoutedEventArgs e)
        {
            var target = SelectedWindow;
            if (target is null) return;
            if (AppServices.WeChatWindows.ShowWindow(target))
            {
                ShowBanner($"已唤起窗口至前台：{target.DisplayTitle}", BannerKind.Success);
                AppendEvent($"手动唤起窗口：{target.DisplayTitle} ({target.HwndHex})");
                await RefreshMonitorAsync(logInitialState: false);
            }
            else
            {
                ShowBanner("唤起失败：窗口句柄已失效。", BannerKind.Danger);
            }
        }

        private async void HideWindowButton_Click(object sender, RoutedEventArgs e)
        {
            var target = SelectedWindow;
            if (target is null) return;
            if (AppServices.WeChatWindows.HideWindow(target))
            {
                ShowBanner(AppServices.WeChatWindows.PauseReason, BannerKind.Warning);
                AppendEvent($"已最小化窗口：{target.DisplayTitle}");
                await RefreshMonitorAsync(logInitialState: false);
            }
            else
            {
                ShowBanner("最小化失败：窗口句柄已失效。", BannerKind.Danger);
            }
        }

        private async void DiagnoseWindowButton_Click(object sender, RoutedEventArgs e)
        {
            var target = SelectedWindow;
            if (target is null || _isBusy) return;
            if (!AppServices.Ocr.RapidEngine.IsAvailable)
            {
                ShowBanner(AppServices.Ocr.RapidEngine.AvailabilityNote, BannerKind.Danger);
                return;
            }

            SetBusy(true);
            try
            {
                ShowBanner($"正在自动唤起并截取窗口「{target.DisplayTitle}」进行 OCR 状态诊断...", BannerKind.Info);
                SoftwareBitmap bitmap = await Task.Run(() => AppServices.WeChatWindows.CaptureWindow(target));
                if (_isUnloaded)
                {
                    bitmap.Dispose();
                    return;
                }

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

                var ocrResult = await AppServices.Ocr.RapidEngine.RecognizeAsync(bitmap);
                if (_isUnloaded) return;

                _lastOcrResult = ocrResult;
                var analysis = WeChatOcrAnalyzer.Analyze(target, ocrResult);
                target.DetectedState = analysis.InferredState;

                TabLinesText.Text = $"识别文本 ({ocrResult.Lines.Count})";
                DiagTimingText.Text = $"耗时 {ocrResult.ElapsedMilliseconds} ms · 识别 {ocrResult.Lines.Count} 行 · {ocrResult.ImageWidth}×{ocrResult.ImageHeight}";
                OcrLinesListView.ItemsSource = ocrResult.Lines;
                ApplyDiagStateBadge(analysis.InferredState);
                DiagReasonText.Text = analysis.StateReason;
                RenderOverlayBoxes();

                if (analysis.InferredState == WeChatLoginState.Disconnected)
                {
                    ShowBanner("检测到微信掉线/退出提示，请重新登录！", BannerKind.Danger);
                    AppendEvent($"⚠ OCR 诊断预警：{target.DisplayTitle} 检测到掉线/退出。");
                }
                else
                {
                    ShowBanner($"诊断完成：{DiagStateText.Text}（{ocrResult.ElapsedMilliseconds} ms，{ocrResult.Lines.Count} 行文本）。", BannerKind.Success);
                    AppendEvent($"OCR 诊断完成：{target.DisplayTitle} → {DiagStateText.Text}。");
                }

                // Restore our workbench window cleanly so the employee sees the diagnostic output right away
                if (AppServices.MainWindowHandle != nint.Zero)
                {
                    _ = Win32WindowHelper.SetForegroundWindow(AppServices.MainWindowHandle);
                }
            }
            catch (Exception ex)
            {
                AppServices.Logs.Error("OCR", $"窗口诊断失败：{ex.Message}");
                if (!_isUnloaded) ShowBanner($"窗口诊断失败：{ex.Message}", BannerKind.Danger);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void ApplyDiagStateBadge(WeChatLoginState state)
        {
            (string label, string bgKey, string fgKey) = state switch
            {
                WeChatLoginState.LoggedIn => ("主界面（已登录）", "SuccessBgBrush", "SuccessTextBrush"),
                WeChatLoginState.LoginPage => ("登录页（待扫码）", "WarningBgBrush", "WarningTextBrush"),
                WeChatLoginState.Disconnected => ("掉线 / 已退出", "DangerBgBrush", "DangerTextBrush"),
                WeChatLoginState.HiddenOrMinimized => ("最小化/隐藏", "WarningBgBrush", "WarningTextBrush"),
                _ => ("常规界面", "BrandSoftBgBrush", "BrandPrimaryBrush")
            };

            DiagStateText.Text = label;
            DiagStatePillBorder.Background = (Brush)Application.Current.Resources[bgKey];
            DiagStateText.Foreground = (Brush)Application.Current.Resources[fgKey];
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
            var fillBrush = new SolidColorBrush(Color.FromArgb(28, 124, 92, 252));

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

        private void DiagTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string tag) return;
            _isPreviewTab = tag == "preview";
            PreviewTabContainer.Visibility = _isPreviewTab ? Visibility.Visible : Visibility.Collapsed;
            OcrLinesListView.Visibility = _isPreviewTab ? Visibility.Collapsed : Visibility.Visible;
            ShowOverlayCheck.Visibility = _isPreviewTab ? Visibility.Visible : Visibility.Collapsed;
            CopyAllTextButton.Visibility = _isPreviewTab ? Visibility.Collapsed : Visibility.Visible;
            UpdateDiagTabVisuals();
        }

        private void UpdateDiagTabVisuals()
        {
            var activeFg = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
            var inactiveFg = (Brush)Application.Current.Resources["TextSecondaryBrush"];
            var transparent = new SolidColorBrush(Colors.Transparent);

            TabPreviewText.Foreground = _isPreviewTab ? activeFg : inactiveFg;
            TabPreviewText.FontWeight = _isPreviewTab ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            TabPreviewLine.Background = _isPreviewTab ? activeFg : transparent;

            TabLinesText.Foreground = !_isPreviewTab ? activeFg : inactiveFg;
            TabLinesText.FontWeight = !_isPreviewTab ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            TabLinesLine.Background = !_isPreviewTab ? activeFg : transparent;
        }

        private void CopyAllTextButton_Click(object sender, RoutedEventArgs e)
        {
            if (_lastOcrResult is null || string.IsNullOrWhiteSpace(_lastOcrResult.FullText))
            {
                ShowBanner("暂无可复制的诊断文本。", BannerKind.Info);
                return;
            }
            try
            {
                var pkg = new DataPackage();
                pkg.SetText(_lastOcrResult.FullText);
                Clipboard.SetContent(pkg);
                ShowBanner("诊断识别文本已复制到剪贴板。", BannerKind.Success);
            }
            catch (Exception ex)
            {
                ShowBanner($"复制失败：{ex.Message}", BannerKind.Danger);
            }
        }

        private void ClearEventsButton_Click(object sender, RoutedEventArgs e) => _events.Clear();

        private void AppendEvent(string message)
        {
            _events.Insert(0, new WeChatMonitorEvent
            {
                Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                Message = message
            });
            while (_events.Count > 60)
            {
                _events.RemoveAt(_events.Count - 1);
            }
        }

        private void SetBusy(bool busy)
        {
            _isBusy = busy;
            if (_isUnloaded)
            {
                if (!busy)
                {
                    _currentBitmap?.Dispose();
                    _currentBitmap = null;
                }
                return;
            }

            LaunchWeChatButton.IsEnabled = !busy;
            RestartWeChatButton.IsEnabled = !busy && (_lastSnapshot?.IsPersonalWeChatAlive ?? false);
            RefreshNowButton.IsEnabled = !busy;
            ConfigPathButton.IsEnabled = !busy;
            UpdateSelectedWindowDetails();
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
