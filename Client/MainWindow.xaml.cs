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
        public static MainWindow? Instance { get; private set; }

        public MainWindow()
        {
            InitializeComponent();
            Instance = this;

            AppServices.MainWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);

            ConfigureTitleBar();
            ResizeAndCenter(1280, 720);
            AppServices.Logs.Info("SYS", "客户端启动完成，默认窗口尺寸 1280x720。");
            ShowLoginView();
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
                titleBar.ButtonForegroundColor = Color.FromArgb(255, 31, 35, 41);
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
