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
            ResizeAndCenter(1320, 840);
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

        private void ResizeAndCenter(int width, int height)
        {
            AppWindow.Resize(new SizeInt32(width, height));
            var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            if (displayArea is not null)
            {
                var work = displayArea.WorkArea;
                int x = work.X + Math.Max(0, (work.Width - width) / 2);
                int y = work.Y + Math.Max(0, (work.Height - height) / 2);
                AppWindow.Move(new PointInt32(x, y));
            }
        }
    }
}
