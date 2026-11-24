using System.Collections.ObjectModel;
using System.Text;
using Client.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Client.Dialogs
{
    public sealed partial class BatchTagImportDialog : ContentDialog
    {
        private readonly WeChatTagBatchImporter _importer = new();
        private readonly ObservableCollection<WeChatTagItem> _tags = [];
        private readonly StringBuilder _logBuilder = new();

        private nint _contactManagerHwnd;
        private int _wechatPid;
        private CancellationTokenSource? _cts;
        private bool _isBusy;
        private bool _isClosed;

        public int TotalImportedOrUpdated { get; private set; }

        public BatchTagImportDialog()
        {
            InitializeComponent();
            RequestedTheme = AppServices.Settings.CurrentElementTheme;
            TagsListView.ItemsSource = _tags;
            Closed += (_, _) =>
            {
                _isClosed = true;
                _cts?.Cancel();
            };
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
            {
                _cts?.Cancel();
                return;
            }
            Hide();
        }

        private async void ScanTagsButton_Click(object sender, RoutedEventArgs e)
        {
            await ScanTagsAsync(scrollDown: false);
        }

        private async void ScrollMoreTagsButton_Click(object sender, RoutedEventArgs e)
        {
            await ScanTagsAsync(scrollDown: true);
        }

        private async Task ScanTagsAsync(bool scrollDown)
        {
            if (_isBusy) return;
            _isBusy = true;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            ScanTagsButton.IsEnabled = false;
            ScrollMoreTagsButton.IsEnabled = false;
            StartImportButton.IsEnabled = false;
            ScanStatusTitle.Text = "正在定位微信「通讯录管理」并识别标签...";
            FooterStatusText.Text = "正在执行 OCR 扫描，请稍候...";
            AppendLog(scrollDown ? "正在向下滚动左侧标签栏并重扫..." : "正在定位微信「通讯录管理」窗口并规范尺寸...");

            try
            {
                var result = await Task.Run(() => _importer.LocateAndScanTagsAsync(scrollLeftSidebarDown: scrollDown, ct: ct), ct);
                if (_isClosed) return;

                _contactManagerHwnd = result.ContactManagerHwnd;
                _wechatPid = result.ProcessId;

                if (!scrollDown)
                {
                    _tags.Clear();
                }

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

                // Bring our workbench back to front so the user sees the scanned tag list immediately
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
                _isBusy = false;
                _cts?.Dispose();
                _cts = null;
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
            if (_tags.Count == 0 || _isBusy) return;
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
            if (_isBusy || _contactManagerHwnd == nint.Zero) return;
            var chosen = _tags.Where(t => t.IsSelected).ToList();
            if (chosen.Count == 0)
            {
                FooterStatusText.Text = "请先在左侧至少勾选一个微信标签。";
                return;
            }

            bool overwrite = DedupOverwriteRadio.IsChecked == true;
            const int defaultSmartDelayMs = 480;

            _isBusy = true;
            _cts = new CancellationTokenSource();
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

            var ct = _cts.Token;
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

                TotalImportedOrUpdated += summary.CreatedCount + summary.UpdatedCount;
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
                _isBusy = false;
                _cts?.Dispose();
                _cts = null;
                if (!_isClosed)
                {
                    ScanTagsButton.IsEnabled = true;
                    ScrollMoreTagsButton.IsEnabled = _contactManagerHwnd != nint.Zero;
                    StartImportButton.Visibility = Visibility.Visible;
                    StartImportButton.IsEnabled = _contactManagerHwnd != nint.Zero && _tags.Count > 0;
                    StopImportButton.Visibility = Visibility.Collapsed;
                    DoneButton.IsEnabled = true;
                    CloseButton.IsEnabled = true;

                    // Restore our workbench window to foreground so the user immediately sees the summary
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
            _cts?.Cancel();
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
