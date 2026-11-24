using System.Collections.ObjectModel;
using Client.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace Client.Pages
{
    public sealed class LogRowViewModel
    {
        private static readonly Brush InfoBrush = new SolidColorBrush(Color.FromArgb(255, 88, 166, 255));
        private static readonly Brush OkBrush = new SolidColorBrush(Color.FromArgb(255, 63, 185, 80));
        private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromArgb(255, 210, 153, 34));
        private static readonly Brush ErrBrush = new SolidColorBrush(Color.FromArgb(255, 248, 81, 73));

        public LogRowViewModel(AppLogEntry entry)
        {
            Entry = entry;
            TimeLabel = $"[{entry.TimeText}]";
            LevelLabel = $"[{entry.LevelTag}]";
            ModuleLabel = $"[{entry.Module,-4}]";
            Message = entry.Message;
            LevelBrush = entry.Level switch
            {
                AppLogLevel.Success => OkBrush,
                AppLogLevel.Warning => WarnBrush,
                AppLogLevel.Error => ErrBrush,
                _ => InfoBrush
            };
        }

        public AppLogEntry Entry { get; }
        public string TimeLabel { get; }
        public string LevelLabel { get; }
        public string ModuleLabel { get; }
        public string Message { get; }
        public Brush LevelBrush { get; }
    }

    public sealed partial class AboutPage : Page
    {
        private readonly ObservableCollection<LogRowViewModel> _visibleLogs = new();
        private bool _isSubscribed;

        public AboutPage()
        {
            InitializeComponent();
            LogsListView.ItemsSource = _visibleLogs;
            Unloaded += (_, _) => Unsubscribe();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Subscribe();
            UpdateServiceBadge();
            ReloadLogs();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            Unsubscribe();
        }

        private void Subscribe()
        {
            if (_isSubscribed) return;
            AppServices.Logs.LogAdded += Logs_LogAdded;
            AppServices.Logs.LogsCleared += Logs_LogsCleared;
            _isSubscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_isSubscribed) return;
            AppServices.Logs.LogAdded -= Logs_LogAdded;
            AppServices.Logs.LogsCleared -= Logs_LogsCleared;
            _isSubscribed = false;
        }

        private void Logs_LogAdded(object? sender, AppLogEntry entry)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (MatchesFilter(entry))
                {
                    var vm = new LogRowViewModel(entry);
                    _visibleLogs.Add(vm);
                    UpdateLogCount();
                    if (AutoScrollCheck.IsChecked == true)
                    {
                        LogsListView.ScrollIntoView(vm);
                    }
                }
            });
        }

        private void Logs_LogsCleared(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _visibleLogs.Clear();
                UpdateLogCount();
            });
        }

        private void ReloadLogs()
        {
            _visibleLogs.Clear();
            foreach (var entry in AppServices.Logs.GetSnapshot())
            {
                if (MatchesFilter(entry))
                {
                    _visibleLogs.Add(new LogRowViewModel(entry));
                }
            }

            UpdateLogCount();
            if (AutoScrollCheck.IsChecked == true && _visibleLogs.Count > 0)
            {
                LogsListView.ScrollIntoView(_visibleLogs[^1]);
            }
        }

        private bool MatchesFilter(AppLogEntry entry)
        {
            string levelTag = (LevelFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
            bool levelOk = levelTag switch
            {
                "info" => entry.Level == AppLogLevel.Info,
                "ok" => entry.Level == AppLogLevel.Success,
                "warn" => entry.Level == AppLogLevel.Warning,
                "err" => entry.Level == AppLogLevel.Error,
                _ => true
            };

            if (!levelOk) return false;

            string query = LogSearchBox.Text.Trim();
            if (string.IsNullOrEmpty(query)) return true;

            return entry.Message.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Module.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.LevelTag.Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateLogCount()
        {
            LogCountText.Text = $"{_visibleLogs.Count} lines";
            ConsolePromptText.Text = _visibleLogs.Count == 0
                ? "> 暂无匹配日志"
                : $"> 监听中 · 最新事件 {DateTime.Now:HH:mm:ss}";
        }

        private void UpdateServiceBadge()
        {
            bool ok = AppServices.Ocr.RapidEngine.IsAvailable;
            ServiceStatePill.Background = (Brush)Application.Current.Resources[ok ? "SuccessBgBrush" : "DangerBgBrush"];
            ServiceStateDot.Fill = (Brush)Application.Current.Resources[ok ? "SuccessTextBrush" : "DangerTextBrush"];
            ServiceStateText.Foreground = (Brush)Application.Current.Resources[ok ? "SuccessTextBrush" : "DangerTextBrush"];
            ServiceStateText.Text = ok ? "运行正常" : "组件缺失";
        }

        private void FilterChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LogsListView is not null) ReloadLogs();
        }

        private void LogSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (LogsListView is not null) ReloadLogs();
        }

        private async void RunSelfCheckButton_Click(object sender, RoutedEventArgs e)
        {
            AppServices.Logs.Info("SYS", "开始执行运行环境自检...");
            if (AppServices.Ocr.RapidEngine.IsAvailable)
            {
                AppServices.Logs.Success("OCR", "本地识别组件检查通过，状态正常。");
            }
            else
            {
                AppServices.Logs.Error("OCR", AppServices.Ocr.RapidEngine.AvailabilityNote);
            }

            UpdateServiceBadge();
            _ = await AppServices.Backend.CheckHealthAsync();
        }

        private void CopyLogsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_visibleLogs.Count == 0) return;

            var text = string.Join(Environment.NewLine, _visibleLogs.Select(x => x.Entry.FormattedLine));
            var pkg = new DataPackage();
            pkg.SetText(text);
            Clipboard.SetContent(pkg);

            CopyFeedbackText.Text = $"已复制 {_visibleLogs.Count} 条日志";
            CopyFeedbackText.Visibility = Visibility.Visible;
        }

        private void ClearLogsButton_Click(object sender, RoutedEventArgs e)
        {
            CopyFeedbackText.Visibility = Visibility.Collapsed;
            AppServices.Logs.Clear();
        }
    }
}
