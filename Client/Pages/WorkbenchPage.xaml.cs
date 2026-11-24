using Client.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Client.Pages
{
    public sealed partial class WorkbenchPage : Page
    {
        private string _activeTag = "customers";

        public WorkbenchPage()
        {
            InitializeComponent();
            Loaded += WorkbenchPage_Loaded;
        }

        private void WorkbenchPage_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= WorkbenchPage_Loaded;
            MainWindow.Instance?.SetDragRegion(WorkbenchDragRegion);
            RefreshHeaderAndFooter();
            SwitchSection("customers");
        }

        private void RefreshHeaderAndFooter()
        {
            var employee = AppServices.Backend.CurrentEmployee;
            string host = AppServices.Backend.BaseUrl.Replace("http://", "").Replace("https://", "");
            TopBackendText.Text = "服务已连接";
            ToolTipService.SetToolTip(TopServerBadge, $"当前服务地址：{host}");

            if (employee is not null)
            {
                string display = string.IsNullOrWhiteSpace(employee.DisplayName) ? employee.Account : employee.DisplayName;
                TopEmployeeNameText.Text = $"{display} ({employee.Account})";
                EmployeeInitialText.Text = display.Length > 0 ? display[..1].ToUpperInvariant() : "E";
                FooterEmployeeText.Text = $"员工 ID: {employee.EmployeeId}";
            }

            FooterOcrText.Text = AppServices.Ocr.RapidEngine.IsAvailable ? "识别服务: 就绪" : "识别服务: 未就绪";
        }

        private void RailNav_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                SwitchSection(tag);
            }
        }

        public void SwitchSection(string tag)
        {
            _activeTag = tag;
            UpdateRailVisuals();

            (Type pageType, string title) = tag switch
            {
                "customers" => (typeof(CustomersPage), "客户档案"),
                "groups" => (typeof(GroupsPage), "群组档案"),
                "logs" or "about" => (typeof(AboutPage), "运行日志"),
                "guide" => (typeof(GuidePage), "使用介绍"),
                _ => (typeof(WeChatOcrPage), "窗口识别")
            };

            ActiveTabTitleText.Text = title;
            if (WorkspaceFrame.CurrentSourcePageType != pageType)
            {
                WorkspaceFrame.Navigate(pageType);
            }
        }

        private void UpdateRailVisuals()
        {
            var activeBg = (Brush)Application.Current.Resources["BrandSoftBgBrush"];
            var activeFg = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
            var inactiveBg = new SolidColorBrush(Colors.Transparent);
            var inactiveFg = (Brush)Application.Current.Resources["TextSecondaryBrush"];

            SetRailItemState(RailCustomersButton, RailCustomersIcon, RailCustomersLabel, _activeTag == "customers", activeBg, activeFg, inactiveBg, inactiveFg);
            SetRailItemState(RailGroupsButton, RailGroupsIcon, RailGroupsLabel, _activeTag == "groups", activeBg, activeFg, inactiveBg, inactiveFg);
            SetRailItemState(RailOcrButton, RailOcrIcon, RailOcrLabel, _activeTag == "ocr", activeBg, activeFg, inactiveBg, inactiveFg);
            SetRailItemState(RailLogsButton, RailLogsIcon, RailLogsLabel, _activeTag is "logs" or "about", activeBg, activeFg, inactiveBg, inactiveFg);
            SetRailItemState(RailGuideButton, RailGuideIcon, RailGuideLabel, _activeTag == "guide", activeBg, activeFg, inactiveBg, inactiveFg);
        }

        private static void SetRailItemState(
            Button button,
            FontIcon icon,
            TextBlock label,
            bool isActive,
            Brush activeBg,
            Brush activeFg,
            Brush inactiveBg,
            Brush inactiveFg)
        {
            button.Background = isActive ? activeBg : inactiveBg;
            icon.Foreground = isActive ? activeFg : inactiveFg;
            label.Foreground = isActive ? activeFg : inactiveFg;
            label.FontWeight = isActive ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }

        private async void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            await AppServices.Backend.LogoutAsync();
            MainWindow.Instance?.ShowLoginView();
        }
    }
}
