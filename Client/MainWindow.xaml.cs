using Client.Pages;
using Client.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using Windows.UI;

namespace Client
{
    public sealed partial class MainWindow : Window
    {
        private const int LoginLogicalWidth = 740;
        private const int LoginLogicalHeight = 450;
        private const int DefaultLogicalWidth = 1440;
        private const int DefaultLogicalHeight = 900;
        private const int MinLogicalWidth = 1040;
        private const int MinLogicalHeight = 640;

        private uint _lastAppliedDpi;
        private bool _isLoginMode = true;
        private bool _isClosed;
        private int _transitionVersion;
        private Storyboard? _revealStoryboard;

        public static MainWindow? Instance { get; private set; }

        public MainWindow()
        {
            InitializeComponent();
            Instance = this;

            AppServices.MainWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            try { AppServices.Backend.SetBaseUrl(AppServices.Settings.Current.BackendUrl); } catch { }

            Closed += (_, _) =>
            {
                _isClosed = true;
                try { VoicePlaybackService.Shared.Dispose(); } catch { }
                try { AppServices.WeChat4Sync.Dispose(); } catch { }
                AppServices.Logs.Info("SYS", "客户端窗口关闭，已释放后台监听与媒体资源。");
                AppServices.Logs.Flush();
            };
            RootFrame.ContentTransitions = [];

            ConfigureTitleBar();
            AppServices.Settings.ApplyTheme(AppServices.Settings.IsDarkMode, save: false);
            AppWindow.Changed += AppWindow_Changed;

            AppServices.Logs.Info("SYS", $"客户端启动完成，登录窗口 {LoginLogicalWidth}x{LoginLogicalHeight}，工作台默认 {DefaultLogicalWidth}x{DefaultLogicalHeight}。");
            ShowLoginView();
        }

        private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (args.DidPositionChange || args.DidSizeChange)
            {
                uint dpi = Win32WindowHelper.GetDpiForWindow(AppServices.MainWindowHandle);
                if (dpi > 0 && dpi != _lastAppliedDpi)
                {
                    ApplyWindowConstraints();
                }
            }
        }

        private void ApplyWindowConstraints()
        {
            if (AppWindow.Presenter is not OverlappedPresenter presenter)
            {
                return;
            }

            uint dpi = Win32WindowHelper.GetDpiForWindow(AppServices.MainWindowHandle);
            _lastAppliedDpi = dpi;

            if (_isLoginMode)
            {
                if (presenter.State == OverlappedPresenterState.Maximized)
                {
                    presenter.Restore();
                }
                presenter.PreferredMinimumWidth = null;
                presenter.PreferredMinimumHeight = null;
                presenter.IsMaximizable = false;
                presenter.IsResizable = false;
                return;
            }

            presenter.IsResizable = true;
            presenter.IsMaximizable = true;

            double scale = dpi > 0 ? dpi / 96.0 : 1.0;
            int minW = (int)Math.Round(MinLogicalWidth * scale);
            int minH = (int)Math.Round(MinLogicalHeight * scale);

            var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            if (displayArea is not null)
            {
                var work = displayArea.WorkArea;
                minW = Math.Min(minW, Math.Max(800, work.Width - 40));
                minH = Math.Min(minH, Math.Max(560, work.Height - 40));
            }

            presenter.PreferredMinimumWidth = minW;
            presenter.PreferredMinimumHeight = minH;
        }

        private void ConfigureTitleBar()
        {
            ExtendsContentIntoTitleBar = true;

            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }

            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                var titleBar = AppWindow.TitleBar;
                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                UpdateTitleBarTheme(AppServices.Settings.IsDarkMode);
            }
        }

        public void ApplyWindowTheme(bool isDark)
        {
            RootGrid.RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;
            UpdateTitleBarTheme(isDark);
        }

        private void UpdateTitleBarTheme(bool isDark)
        {
            if (!AppWindowTitleBar.IsCustomizationSupported())
            {
                return;
            }

            var titleBar = AppWindow.TitleBar;
            if (isDark)
            {
                titleBar.ButtonForegroundColor = Color.FromArgb(255, 224, 228, 238);
                titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 130, 138, 156);
                titleBar.ButtonHoverForegroundColor = Color.FromArgb(255, 255, 255, 255);
                titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 36, 41, 56);
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(255, 48, 55, 74);
            }
            else
            {
                titleBar.ButtonForegroundColor = Color.FromArgb(255, 31, 35, 41);
                titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 130, 136, 148);
                titleBar.ButtonHoverForegroundColor = Color.FromArgb(255, 31, 35, 41);
                titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 232, 234, 242);
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(255, 220, 223, 235);
            }
        }

        public void SetDragRegion(UIElement element)
        {
            SetTitleBar(element);
        }

        public void ShowLoginView()
        {
            int version = ++_transitionVersion;
            _isLoginMode = true;

            bool cloaked = BeginCloakedTransition();
            ApplyWindowConstraints();
            ResizeClientAndCenter(LoginLogicalWidth, LoginLogicalHeight);
            RootFrame.Navigate(typeof(LoginPage), null, new SuppressNavigationTransitionInfo());
            _ = FinishCloakedTransitionAsync(version, LoginLogicalWidth, LoginLogicalHeight, cloaked, logEnterWorkbench: false);
        }

        public void EnterWorkbench()
        {
            int version = ++_transitionVersion;
            _isLoginMode = false;

            bool cloaked = BeginCloakedTransition();
            ApplyWindowConstraints();
            ResizeClientAndCenter(DefaultLogicalWidth, DefaultLogicalHeight);
            RootFrame.Navigate(typeof(WorkbenchPage), null, new SuppressNavigationTransitionInfo());
            _ = FinishCloakedTransitionAsync(version, DefaultLogicalWidth, DefaultLogicalHeight, cloaked, logEnterWorkbench: true);
        }

        private bool BeginCloakedTransition()
        {
            _revealStoryboard?.Stop();
            // Cloak the DWM surface BEFORE resizing so the outgoing page (e.g. LoginPage)
            // is never stretched to the new dimensions and cold startup never flashes an oversized frame.
            bool cloaked = Win32WindowHelper.SetWindowCloaked(AppServices.MainWindowHandle, true);
            if (!cloaked)
            {
                // Fallback when DWM cloaking is unavailable: hide content during resize.
                RootFrame.Opacity = 0;
            }
            return cloaked;
        }

        private async Task FinishCloakedTransitionAsync(int version, int targetLogicalWidth, int targetLogicalHeight, bool cloaked, bool logEnterWorkbench)
        {
            try
            {
                if (RootFrame.Content is FrameworkElement page && !page.IsLoaded)
                {
                    var loadedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    void OnPageLoaded(object sender, RoutedEventArgs e)
                    {
                        page.Loaded -= OnPageLoaded;
                        loadedTcs.TrySetResult(true);
                    }

                    page.Loaded += OnPageLoaded;
                    await Task.WhenAny(loadedTcs.Task, Task.Delay(350));
                    page.Loaded -= OnPageLoaded;
                }

                if (_isClosed || version != _transitionVersion)
                {
                    return;
                }

                // Let Page.Loaded handlers (SetDragRegion, initial tab navigation, layout pass) complete first.
                await WaitForLowPriorityUiTurnAsync();
                if (_isClosed || version != _transitionVersion)
                {
                    return;
                }

                // Verify final client size while STILL cloaked so the window never visibly jumps twice.
                var (targetW, targetH) = ComputeTargetClientPixels(targetLogicalWidth, targetLogicalHeight);
                var currentClient = AppWindow.ClientSize;
                if (Math.Abs(currentClient.Width - targetW) > 3 || Math.Abs(currentClient.Height - targetH) > 3)
                {
                    ResizeClientAndCenter(targetLogicalWidth, targetLogicalHeight);
                    await WaitForLowPriorityUiTurnAsync();
                }

                if (_isClosed || version != _transitionVersion)
                {
                    return;
                }

                // Give DWM ~2 frames to commit the fully painted page surface before uncloaking.
                await Task.Delay(32);
                if (_isClosed || version != _transitionVersion)
                {
                    return;
                }

                if (logEnterWorkbench && !_isLoginMode)
                {
                    uint dpi = Win32WindowHelper.GetDpiForWindow(AppServices.MainWindowHandle);
                    var clientSize = AppWindow.ClientSize;
                    var outerSize = AppWindow.Size;
                    AppServices.Logs.Info(
                        "SYS",
                        $"进入工作台：目标 {DefaultLogicalWidth}x{DefaultLogicalHeight} (DPI {dpi})，实际客户区 {clientSize.Width}x{clientSize.Height}，窗口外框 {outerSize.Width}x{outerSize.Height}。");
                }

                if (cloaked)
                {
                    Win32WindowHelper.SetWindowCloaked(AppServices.MainWindowHandle, false);
                }

                PlayRevealFadeIn(cloaked ? 0.90 : 0.0);
            }
            catch
            {
                if (!_isClosed && version == _transitionVersion)
                {
                    Win32WindowHelper.SetWindowCloaked(AppServices.MainWindowHandle, false);
                    RootFrame.Opacity = 1;
                }
            }
        }

        private Task WaitForLowPriorityUiTurnAsync()
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => tcs.TrySetResult(true)))
            {
                tcs.TrySetResult(true);
            }
            return tcs.Task;
        }

        private void PlayRevealFadeIn(double fromOpacity)
        {
            _revealStoryboard?.Stop();
            RootFrame.Opacity = fromOpacity;
            var anim = new DoubleAnimation
            {
                From = fromOpacity,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(95)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(anim, RootFrame);
            Storyboard.SetTargetProperty(anim, "Opacity");

            var sb = new Storyboard();
            sb.Children.Add(anim);
            sb.Completed += (_, _) => RootFrame.Opacity = 1;
            _revealStoryboard = sb;
            sb.Begin();
        }

        private (int width, int height) ComputeTargetClientPixels(int logicalWidth, int logicalHeight)
        {
            uint dpi = Win32WindowHelper.GetDpiForWindow(AppServices.MainWindowHandle);
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;

            int width = (int)Math.Round(logicalWidth * scale);
            int height = (int)Math.Round(logicalHeight * scale);

            var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            if (displayArea is not null)
            {
                var work = displayArea.WorkArea;
                width = Math.Min(width, Math.Max(540, work.Width - 40));
                height = Math.Min(height, Math.Max(380, work.Height - 40));
            }

            return (width, height);
        }

        private void ResizeClientAndCenter(int logicalWidth, int logicalHeight)
        {
            var (targetClientW, targetClientH) = ComputeTargetClientPixels(logicalWidth, logicalHeight);
            AppWindow.ResizeClient(new SizeInt32(targetClientW, targetClientH));

            var currentClient = AppWindow.ClientSize;
            var currentOuter = AppWindow.Size;
            int borderW = currentClient.Width > 0 ? Math.Clamp(currentOuter.Width - currentClient.Width, 0, 48) : 0;
            int borderH = currentClient.Height > 0 ? Math.Clamp(currentOuter.Height - currentClient.Height, 0, 64) : 0;

            int desiredOuterW = targetClientW + borderW;
            int desiredOuterH = targetClientH + borderH;
            bool needsOuterFix = currentClient.Width <= 0
                || Math.Abs(currentClient.Width - targetClientW) > 2
                || Math.Abs(currentClient.Height - targetClientH) > 2;

            var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            if (displayArea is not null)
            {
                var work = displayArea.WorkArea;
                int finalOuterW = Math.Min(needsOuterFix ? desiredOuterW : currentOuter.Width, work.Width);
                int finalOuterH = Math.Min(needsOuterFix ? desiredOuterH : currentOuter.Height, work.Height);
                int x = work.X + Math.Max(0, (work.Width - finalOuterW) / 2);
                int y = work.Y + Math.Max(0, (work.Height - finalOuterH) / 2);

                if (needsOuterFix)
                {
                    AppWindow.MoveAndResize(new RectInt32(x, y, finalOuterW, finalOuterH));
                }
                else
                {
                    AppWindow.Move(new PointInt32(x, y));
                }
            }
            else if (needsOuterFix)
            {
                AppWindow.Resize(new SizeInt32(desiredOuterW, desiredOuterH));
            }
        }
    }
}
