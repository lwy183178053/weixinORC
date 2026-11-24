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
        private const int DefaultLogicalWidth = 1280;
        private const int DefaultLogicalHeight = 720;
        private const int MinLogicalWidth = 1040;
        private const int MinLogicalHeight = 640;

        private uint _lastAppliedDpi;
        private bool _isLoginMode = true;

        public static MainWindow? Instance { get; private set; }

        public MainWindow()
        {
            InitializeComponent();
            Instance = this;

            AppServices.MainWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            try { AppServices.Backend.SetBaseUrl(AppServices.Settings.Current.BackendUrl); } catch { }

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
            _isLoginMode = true;
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                if (presenter.State == OverlappedPresenterState.Maximized)
                {
                    presenter.Restore();
                }

                presenter.PreferredMinimumWidth = null;
                presenter.PreferredMinimumHeight = null;
                presenter.IsResizable = true;
            }

            ResizeAndCenter(LoginLogicalWidth, LoginLogicalHeight);
            ApplyWindowConstraints();
            RootFrame.Navigate(typeof(LoginPage), null, new EntranceNavigationTransitionInfo());
        }

        public void EnterWorkbench()
        {
            _isLoginMode = false;
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = null;
                presenter.PreferredMinimumHeight = null;
                presenter.IsResizable = true;
                presenter.IsMaximizable = true;
            }

            ResizeAndCenter(DefaultLogicalWidth, DefaultLogicalHeight);
            RootFrame.Navigate(typeof(WorkbenchPage), null, new DrillInNavigationTransitionInfo());

            // Apply minimum size constraints on the next UI tick after the 1280x720 resize settles,
            // avoiding OverlappedPresenter snapping the window to PreferredMinimumWidth (1040) on transition.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isLoginMode)
                {
                    return;
                }

                ResizeAndCenter(DefaultLogicalWidth, DefaultLogicalHeight);
                ApplyWindowConstraints();
            });
        }

        private void ResizeAndCenter(int logicalWidth, int logicalHeight)
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

                int x = work.X + Math.Max(0, (work.Width - width) / 2);
                int y = work.Y + Math.Max(0, (work.Height - height) / 2);
                AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
            }
            else
            {
                AppWindow.Resize(new SizeInt32(width, height));
            }
        }
    }
}
