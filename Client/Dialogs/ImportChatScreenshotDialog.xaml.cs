using System.Collections.ObjectModel;
using System.Text;
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
    public sealed partial class ImportChatScreenshotDialog : ContentDialog
    {
        private sealed class EditableRow
        {
            public string Direction { get; set; } = "incoming";
            public TextBox ContentBox { get; init; } = null!;
            public Button DirectionButton { get; init; } = null!;
            public Border Container { get; init; } = null!;
        }

        // Screenshot mode state
        private readonly List<EditableRow> _rows = [];
        private SoftwareBitmap? _bitmap;
        private bool _isScreenshotBusy;

        // Batch chat history mode state
        private readonly WeChatChatHistoryBatchImporter _batchImporter = new();
        private readonly ObservableCollection<BatchCapturedChatMessage> _batchMessages = [];
        private readonly StringBuilder _batchLogBuilder = new();
        private readonly List<string> _fallbackSearchKeywords = [];
        private readonly List<string> _customerNameAliases = [];
        private readonly HashSet<string> _existingSignatures = new(StringComparer.OrdinalIgnoreCase);
        private readonly bool _isGroupSearch;
        private CancellationTokenSource? _batchCts;
        private bool _isBatchBusy;

        private string _activeMode = "screenshot";
        private bool _isClosed;

        public List<ParsedChatMessage> ConfirmedMessages { get; private set; } = [];
        public bool WasBatchImported { get; private set; }

        public ImportChatScreenshotDialog(
            string? defaultSearchKeyword = null,
            IEnumerable<string>? customerNameAliases = null,
            IEnumerable<(string Direction, string Content)>? existingMessages = null,
            bool isGroupSearch = false,
            IEnumerable<string>? fallbackSearchKeywords = null)
        {
            InitializeComponent();
            RequestedTheme = AppServices.Settings.CurrentElementTheme;
            _isGroupSearch = isGroupSearch;

            if (!string.IsNullOrWhiteSpace(defaultSearchKeyword))
            {
                BatchSearchKeywordBox.Text = defaultSearchKeyword.Trim();
            }

            if (fallbackSearchKeywords is not null)
            {
                foreach (string kw in fallbackSearchKeywords)
                {
                    if (!string.IsNullOrWhiteSpace(kw)) _fallbackSearchKeywords.Add(kw.Trim());
                }
            }

            if (customerNameAliases is not null)
            {
                foreach (string alias in customerNameAliases)
                {
                    if (!string.IsNullOrWhiteSpace(alias)) _customerNameAliases.Add(alias.Trim());
                }
            }

            if (existingMessages is not null)
            {
                foreach (var (dir, content) in existingMessages)
                {
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        _existingSignatures.Add(WeChatChatHistoryBatchImporter.BuildExistingSignature(dir, content));
                    }
                }
            }

            BatchMessagesListView.ItemsSource = _batchMessages;
            Loaded += (_, _) => SelectMode(_activeMode);
            Closed += (_, _) =>
            {
                _isClosed = true;
                _batchCts?.Cancel();
                if (!_isScreenshotBusy)
                {
                    _bitmap?.Dispose();
                    _bitmap = null;
                }
            };
        }

        // ---- Mode Switcher ----

        private void ModeTab_Click(object sender, RoutedEventArgs e)
        {
            if (_isScreenshotBusy || _isBatchBusy) return;
            if (sender is Button btn && btn.Tag is string mode)
            {
                SelectMode(mode);
            }
        }

        private void SelectMode(string mode)
        {
            _activeMode = mode == "batch" ? "batch" : "screenshot";
            bool isScreenshot = _activeMode == "screenshot";

            ScreenshotModePanel.Visibility = isScreenshot ? Visibility.Visible : Visibility.Collapsed;
            BatchModePanel.Visibility = isScreenshot ? Visibility.Collapsed : Visibility.Visible;
            DialogSubtitleText.Text = isScreenshot
                ? "粘贴或上传单张聊天窗口截图，自动区分绿/白气泡并核对入库"
                : "自动搜索微信账号并打开「查找聊天内容」窗口，逐屏 OCR 批量导入聊天记录";

            var activeFg = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
            var inactiveFg = (Brush)Application.Current.Resources["TextSecondaryBrush"];
            var transparent = new SolidColorBrush(Colors.Transparent);

            TabScreenshotIcon.Foreground = isScreenshot ? activeFg : inactiveFg;
            TabScreenshotText.Foreground = isScreenshot ? activeFg : inactiveFg;
            TabScreenshotText.FontWeight = isScreenshot ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            TabScreenshotLine.Background = isScreenshot ? activeFg : transparent;

            TabBatchIcon.Foreground = !isScreenshot ? activeFg : inactiveFg;
            TabBatchText.Foreground = !isScreenshot ? activeFg : inactiveFg;
            TabBatchText.FontWeight = !isScreenshot ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            TabBatchLine.Background = !isScreenshot ? activeFg : transparent;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBatchBusy)
            {
                _batchCts?.Cancel();
                return;
            }
            if (!_isScreenshotBusy) Hide();
        }

        private void CloseAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            if (_isBatchBusy)
            {
                args.Handled = true;
                _batchCts?.Cancel();
                return;
            }
            if (!_isScreenshotBusy)
            {
                args.Handled = true;
                Hide();
            }
        }

        // =====================================================================
        // MODE 1: Screenshot Bubble Recognition
        // =====================================================================

        private async void PasteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            if (_activeMode != "screenshot" || _isScreenshotBusy) return;
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
            if (_isScreenshotBusy) return;
            string? path = Win32WindowHelper.PickImageFilePath(AppServices.MainWindowHandle);
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                var bmp = await Task.Run(() => AppServices.Ocr.LoadImageFileFromPathAsync(path));
                await RunParseAsync(bmp, Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                ShowStatus($"读取图片失败：{ex.Message}", isError: true);
            }
        }

        private async Task ImportFromClipboardAsync()
        {
            if (_isScreenshotBusy) return;
            try
            {
                var (bmp, label) = await AppServices.Ocr.TryLoadImageFromClipboardAsync();
                if (bmp is null)
                {
                    ShowStatus("剪贴板中没有图片，请先截微信聊天记录再按 Ctrl+V。", isError: true);
                    return;
                }
                await RunParseAsync(bmp, label);
            }
            catch (Exception ex)
            {
                ShowStatus($"读取剪贴板失败：{ex.Message}", isError: true);
            }
        }

        private async Task RunParseAsync(SoftwareBitmap bmp, string label)
        {
            if (!AppServices.Ocr.RapidEngine.IsAvailable)
            {
                bmp.Dispose();
                ShowStatus(AppServices.Ocr.RapidEngine.AvailabilityNote, isError: true);
                return;
            }

            _isScreenshotBusy = true;
            OcrSpinner.IsActive = true;
            OcrSpinner.Visibility = Visibility.Visible;
            PasteButton.IsEnabled = false;
            UploadButton.IsEnabled = false;
            ShowStatus($"正在识别并解析聊天截图「{label}」...");

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
                var parsed = await Task.Run(() => WeChatChatScreenshotParser.Parse(bmp, ocr));
                if (_isClosed) return;

                RenderParsedRows(parsed);
                ShowStatus($"识别完成（{ocr.ElapsedMilliseconds} ms）：自动解析出 {parsed.Count} 条消息（绿色气泡已标为「我方」，左侧气泡已标为「对方」）。");
            }
            catch (Exception ex)
            {
                if (!_isClosed) ShowStatus($"识别失败：{ex.Message}", isError: true);
            }
            finally
            {
                _isScreenshotBusy = false;
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

        private void RenderParsedRows(List<ParsedChatMessage> items)
        {
            _rows.Clear();
            ParsedListPanel.Children.Clear();

            if (items.Count == 0)
            {
                EmptyListHint.Text = "未解析到有效聊天气泡，请确认截图包含完整聊天消息区域。";
                ParsedListPanel.Children.Add(EmptyListHint);
                UpdateScreenshotCountLabel();
                return;
            }

            foreach (var msg in items)
            {
                var grid = new Grid { ColumnSpacing = 8 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var dirBtn = new Button
                {
                    Height = 30,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    FontSize = 11.5,
                    Padding = new Thickness(6, 0, 6, 0),
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1)
                };

                var contentBox = new TextBox
                {
                    Text = msg.Content,
                    TextWrapping = TextWrapping.Wrap,
                    AcceptsReturn = true,
                    FontSize = 12.5,
                    Style = (Style)Application.Current.Resources["SoftTextBoxStyle"]
                };

                var delBtn = new Button
                {
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    Content = "✕",
                    FontSize = 11,
                    Style = (Style)Application.Current.Resources["DangerOutlineButtonStyle"]
                };

                var container = new Border
                {
                    Padding = new Thickness(8),
                    CornerRadius = new CornerRadius(7),
                    BorderThickness = new Thickness(1),
                    Child = grid
                };

                var row = new EditableRow
                {
                    Direction = msg.Direction,
                    ContentBox = contentBox,
                    DirectionButton = dirBtn,
                    Container = container
                };

                ApplyRowVisual(row);

                dirBtn.Click += (_, _) =>
                {
                    row.Direction = row.Direction == "outgoing" ? "incoming" : "outgoing";
                    ApplyRowVisual(row);
                };

                delBtn.Click += (_, _) =>
                {
                    _rows.Remove(row);
                    ParsedListPanel.Children.Remove(row.Container);
                    UpdateScreenshotCountLabel();
                };

                Grid.SetColumn(dirBtn, 0);
                Grid.SetColumn(contentBox, 1);
                Grid.SetColumn(delBtn, 2);
                grid.Children.Add(dirBtn);
                grid.Children.Add(contentBox);
                grid.Children.Add(delBtn);

                _rows.Add(row);
                ParsedListPanel.Children.Add(container);
            }

            UpdateScreenshotCountLabel();
        }

        private static void ApplyRowVisual(EditableRow row)
        {
            var res = Application.Current.Resources;
            bool isOut = row.Direction == "outgoing";
            row.DirectionButton.Content = isOut ? "我方 (绿)" : "对方 (白)";
            row.DirectionButton.Background = (Brush)res[isOut ? "ChatOutgoingBgBrush" : "ChatIncomingBgBrush"];
            row.DirectionButton.Foreground = (Brush)res[isOut ? "ChatOutgoingTextBrush" : "TextPrimaryBrush"];
            row.DirectionButton.BorderBrush = (Brush)res[isOut ? "SuccessTextBrush" : "ChatIncomingBorderBrush"];

            row.Container.Background = (Brush)res[isOut ? "SuccessBgBrush" : "AppSurfaceAltBrush"];
            row.Container.BorderBrush = (Brush)res[isOut ? "BrandSoftBorderBrush" : "AppBorderBrush"];
        }

        private void UpdateScreenshotCountLabel()
        {
            int count = _rows.Count;
            ParsedCountText.Text = $"已解析消息列表（{count} 条，点左侧标签可切换我方/对方）";
            ConfirmScreenshotButton.Content = count > 0 ? $"确认导入 {count} 条消息" : "确认导入消息";
        }

        private void ConfirmScreenshotButton_Click(object sender, RoutedEventArgs e)
        {
            var results = _rows
                .Select(r => new ParsedChatMessage
                {
                    Direction = r.Direction,
                    Content = r.ContentBox.Text.Trim()
                })
                .Where(m => !string.IsNullOrEmpty(m.Content))
                .ToList();

            if (results.Count == 0)
            {
                ShowStatus("请先粘贴或选择一张聊天截图并解析出消息。", isError: true);
                return;
            }

            ConfirmedMessages = results;
            WasBatchImported = false;
            Hide();
        }

        private void ShowStatus(string message, bool isError = false)
        {
            StatusBanner.Background = (Brush)Application.Current.Resources[isError ? "DangerBgBrush" : "BrandSoftBgBrush"];
            StatusText.Foreground = (Brush)Application.Current.Resources[isError ? "DangerTextBrush" : "BrandPrimaryBrush"];
            StatusText.Text = message;
            StatusBanner.Visibility = Visibility.Visible;
        }

        // =====================================================================
        // MODE 2: WeChat Auto Batch Chat History Import
        // =====================================================================

        private async void StartBatchCaptureButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBatchBusy) return;
            string keyword = BatchSearchKeywordBox.Text.Trim();
            if (string.IsNullOrEmpty(keyword))
            {
                BatchFooterStatusText.Text = "请先填写用于微信搜索的微信号或备注名。";
                BatchSearchKeywordBox.Focus(FocusState.Programmatic);
                return;
            }

            int maxMessages = 50;
            if (MaxMessagesComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tagStr && int.TryParse(tagStr, out int parsedMax))
            {
                maxMessages = parsedMax;
            }

            var searchKeywords = new List<string> { keyword };
            foreach (string fb in _fallbackSearchKeywords)
            {
                if (!searchKeywords.Any(x => x.Equals(fb, StringComparison.OrdinalIgnoreCase)))
                {
                    searchKeywords.Add(fb);
                }
            }

            bool skipDup = SkipDuplicateCheck.IsChecked == true;
            var existingSet = skipDup ? _existingSignatures : null;

            _isBatchBusy = true;
            _batchCts = new CancellationTokenSource();
            var ct = _batchCts.Token;

            _batchMessages.Clear();
            _batchLogBuilder.Clear();
            BatchMessagesListView.Visibility = Visibility.Collapsed;
            BatchEmptyHint.Visibility = Visibility.Visible;
            StartBatchCaptureButton.Visibility = Visibility.Collapsed;
            StopBatchCaptureButton.Visibility = Visibility.Visible;
            ConfirmBatchImportButton.IsEnabled = false;
            BatchCancelButton.IsEnabled = false;
            CloseButton.IsEnabled = false;
            BatchSearchKeywordBox.IsEnabled = false;
            MaxMessagesComboBox.IsEnabled = false;
            BatchProgressBar.Value = 0;
            BatchProgressBar.IsIndeterminate = true;
            BatchProgressLabel.Text = "正在启动自动定位与扫描...";
            BatchFooterStatusText.Text = "正在自动采集，请勿移动鼠标（按 ESC 可随时停止）...";
            string limitDesc = maxMessages > 0 ? $"最多 {maxMessages} 条" : "全部到底";
            AppendBatchLog($"开始自动采集「{keyword}」的微信聊天记录（目标：{limitDesc}）...");

            var progress = new Progress<BatchChatHistoryProgress>(p =>
            {
                if (_isClosed) return;
                BatchProgressLabel.Text = p.StatusMessage;
                BatchFooterStatusText.Text = p.StatusMessage;
                if (p.MaxMessages > 0)
                {
                    BatchProgressBar.IsIndeterminate = false;
                    BatchProgressBar.Value = Math.Min(100, p.CapturedCount * 100.0 / p.MaxMessages);
                }
                if (!string.IsNullOrWhiteSpace(p.LogLine))
                {
                    AppendBatchLog(p.LogLine);
                }
            });

            try
            {
                var result = await Task.Run(() => _batchImporter.CaptureChatHistoryAsync(
                    searchKeywords,
                    _customerNameAliases,
                    _isGroupSearch,
                    maxMessages,
                    existingSet,
                    progress,
                    ct));

                if (_isClosed) return;

                BatchProgressBar.IsIndeterminate = false;
                BatchProgressBar.Value = result.Success ? 100 : 0;

                foreach (var m in result.Messages)
                {
                    _batchMessages.Add(m);
                }

                bool hasMsgs = _batchMessages.Count > 0;
                BatchMessagesListView.Visibility = hasMsgs ? Visibility.Visible : Visibility.Collapsed;
                BatchEmptyHint.Visibility = hasMsgs ? Visibility.Collapsed : Visibility.Visible;

                BatchProgressLabel.Text = result.Success ? "采集完成" : "未完成采集";
                BatchFooterStatusText.Text = result.SummaryMessage;
                AppendBatchLog(result.Success ? $"=== {result.SummaryMessage} ===" : $"✕ {result.SummaryMessage}");
                UpdateBatchSelectionSummary();
            }
            catch (Exception ex)
            {
                if (!_isClosed)
                {
                    BatchProgressBar.IsIndeterminate = false;
                    BatchFooterStatusText.Text = $"采集异常：{ex.Message}";
                    AppendBatchLog($"✕ 异常：{ex.Message}");
                }
            }
            finally
            {
                _isBatchBusy = false;
                _batchCts?.Dispose();
                _batchCts = null;
                if (!_isClosed)
                {
                    StartBatchCaptureButton.Visibility = Visibility.Visible;
                    StopBatchCaptureButton.Visibility = Visibility.Collapsed;
                    BatchCancelButton.IsEnabled = true;
                    CloseButton.IsEnabled = true;
                    BatchSearchKeywordBox.IsEnabled = true;
                    MaxMessagesComboBox.IsEnabled = true;
                    UpdateBatchSelectionSummary();

                    if (AppServices.MainWindowHandle != nint.Zero)
                    {
                        _ = Win32WindowHelper.SetForegroundWindow(AppServices.MainWindowHandle);
                    }
                }
            }
        }

        private void StopBatchCaptureButton_Click(object sender, RoutedEventArgs e)
        {
            BatchFooterStatusText.Text = "正在安全停止采集...";
            _batchCts?.Cancel();
        }

        private void BatchToggleAllBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_batchMessages.Count == 0 || _isBatchBusy) return;
            bool selectAll = _batchMessages.Any(m => !m.IsIncluded);
            foreach (var m in _batchMessages)
            {
                m.IsIncluded = selectAll;
            }
            UpdateBatchSelectionSummary();
        }

        private void BatchItemCheck_Click(object sender, RoutedEventArgs e) => UpdateBatchSelectionSummary();

        private void BatchFlipDirectionBtn_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is BatchCapturedChatMessage msg)
            {
                msg.Direction = msg.IsOutgoing ? "incoming" : "outgoing";
            }
        }

        private void UpdateBatchSelectionSummary()
        {
            int total = _batchMessages.Count;
            int selected = _batchMessages.Count(m => m.IsIncluded);
            BatchCapturedHeaderText.Text = $"已采集消息预览（已选 {selected}/{total} 条 · 时间正序）";
            ConfirmBatchImportButton.IsEnabled = !_isBatchBusy && selected > 0;
            ConfirmBatchImportButton.Content = selected > 0 ? $"确认导入 {selected} 条消息" : "确认导入消息";
        }

        private void ConfirmBatchImportButton_Click(object sender, RoutedEventArgs e)
        {
            var chosen = _batchMessages
                .Where(m => m.IsIncluded && !string.IsNullOrWhiteSpace(m.Content))
                .Select(m => new ParsedChatMessage
                {
                    Direction = m.Direction,
                    Content = m.Content.Trim()
                })
                .ToList();

            if (chosen.Count == 0) return;

            ConfirmedMessages = chosen;
            WasBatchImported = true;
            Hide();
        }

        private void AppendBatchLog(string line)
        {
            if (_batchLogBuilder.Length > 0) _batchLogBuilder.AppendLine();
            _batchLogBuilder.Append($"[{DateTime.Now:HH:mm:ss}] {line}");
            BatchLiveLogText.Text = _batchLogBuilder.ToString();
            BatchLogScrollViewer.ChangeView(null, BatchLogScrollViewer.ScrollableHeight + 100, null);
        }
    }
}
