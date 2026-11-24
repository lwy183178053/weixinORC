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
        private const int DefaultLogicalWidth = 1280;
        private const int DefaultLogicalHeight = 720;
        private const int MinLogicalWidth = 1040;
        private const int MinLogicalHeight = 640;

        private uint _lastAppliedDpi;

        public static MainWindow? Instance { get; private set; }

        public MainWindow()
        {
            InitializeComponent();
            Instance = this;

            AppServices.MainWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            try { AppServices.Backend.SetBaseUrl(AppServices.Settings.Current.BackendUrl); } catch { }

            ConfigureTitleBar();
            AppServices.Settings.ApplyTheme(AppServices.Settings.IsDarkMode, save: false);
            ApplyMinimumWindowSize();
            ResizeAndCenter(DefaultLogicalWidth, DefaultLogicalHeight);
            AppWindow.Changed += AppWindow_Changed;

            AppServices.Logs.Info("SYS", $"客户端启动完成，默认窗口尺寸 {DefaultLogicalWidth}x{DefaultLogicalHeight}，最小尺寸 {MinLogicalWidth}x{MinLogicalHeight}。");
            ShowLoginView();
        }

        private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (args.DidPositionChange || args.DidSizeChange)
            {
                uint dpi = Win32WindowHelper.GetDpiForWindow(AppServices.MainWindowHandle);
                if (dpi > 0 && dpi != _lastAppliedDpi)
                {
                    ApplyMinimumWindowSize();
                }
            }
        }

        private void ApplyMinimumWindowSize()
        {
            if (AppWindow.Presenter is not OverlappedPresenter presenter)
            {
                return;
            }

            uint dpi = Win32WindowHelper.GetDpiForWindow(AppServices.MainWindowHandle);
            _lastAppliedDpi = dpi;
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
            RootFrame.Navigate(typeof(LoginPage), null, new EntranceNavigationTransitionInfo());
        }

        public void EnterWorkbench()
        {
            RootFrame.Navigate(typeof(WorkbenchPage), null, new DrillInNavigationTransitionInfo());
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
                width = Math.Min(width, Math.Max(640, work.Width - 40));
                height = Math.Min(height, Math.Max(480, work.Height - 40));

                AppWindow.Resize(new SizeInt32(width, height));
                int x = work.X + Math.Max(0, (work.Width - width) / 2);
                int y = work.Y + Math.Max(0, (work.Height - height) / 2);
                AppWindow.Move(new PointInt32(x, y));
            }
            else
            {
                AppWindow.Resize(new SizeInt32(width, height));
            }
        }
    }
}
