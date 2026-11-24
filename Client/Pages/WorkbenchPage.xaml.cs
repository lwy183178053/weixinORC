using Client.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Client.Pages
{
    public sealed partial class WorkbenchPage : Page
    {
        private string _activeTag = "ocr";

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
            SwitchSection("ocr");
        }

        private void RefreshHeaderAndFooter()
        {
            var employee = AppServices.Backend.CurrentEmployee;
            string host = AppServices.Backend.BaseUrl.Replace("http://", "").Replace("https://", "");
            TopBackendText.Text = $"Go 后端 · {host}";

            if (employee is not null)
            {
                string display = string.IsNullOrWhiteSpace(employee.DisplayName) ? employee.Account : employee.DisplayName;
                TopEmployeeNameText.Text = $"{display} ({employee.Account})";
                EmployeeInitialText.Text = display.Length > 0 ? display[..1].ToUpperInvariant() : "E";
                FooterEmployeeText.Text = $"员工 ID: {employee.EmployeeId}";
            }

            FooterOcrText.Text = $"OCR 引擎: {AppServices.Ocr.GetDefaultEngine().DisplayName}";
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
                "customers" => (typeof(CustomersPage), "客户档案管理"),
                "about" => (typeof(AboutPage), "OCR 引擎与环境状态"),
                _ => (typeof(WeChatOcrPage), "微信监控与 OCR 识别")
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

            SetRailItemState(RailOcrButton, RailOcrIcon, RailOcrLabel, _activeTag == "ocr", activeBg, activeFg, inactiveBg, inactiveFg);
            SetRailItemState(RailCustomersButton, RailCustomersIcon, RailCustomersLabel, _activeTag == "customers", activeBg, activeFg, inactiveBg, inactiveFg);
            SetRailItemState(RailAboutButton, RailAboutIcon, RailAboutLabel, _activeTag == "about", activeBg, activeFg, inactiveBg, inactiveFg);
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
