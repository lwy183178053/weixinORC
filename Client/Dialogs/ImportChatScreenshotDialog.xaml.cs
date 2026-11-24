using Client.Services;
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

        private readonly List<EditableRow> _rows = [];
        private SoftwareBitmap? _bitmap;
        private bool _isBusy;
        private bool _isClosed;

        public List<ParsedChatMessage> ConfirmedMessages { get; private set; } = [];

        public ImportChatScreenshotDialog()
        {
            InitializeComponent();
            RequestedTheme = AppServices.Settings.CurrentElementTheme;
            Closed += (_, _) =>
            {
                _isClosed = true;
                if (!_isBusy)
                {
                    _bitmap?.Dispose();
                    _bitmap = null;
                }
            };
        }

        private async void PasteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
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
            if (_isBusy) return;
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
            if (_isBusy) return;
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

            _isBusy = true;
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
                _isBusy = false;
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
                UpdateCountLabel();
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
                    UpdateCountLabel();
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

            UpdateCountLabel();
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

        private void UpdateCountLabel()
        {
            int count = _rows.Count;
            ParsedCountText.Text = $"已解析消息列表（{count} 条，点左侧标签可切换我方/对方）";
            PrimaryButtonText = count > 0 ? $"确认导入 {count} 条消息" : "确认导入消息";
        }

        private void Dialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
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
                args.Cancel = true;
                ShowStatus("请先粘贴或选择一张聊天截图并解析出消息。", isError: true);
                return;
            }

            ConfirmedMessages = results;
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
