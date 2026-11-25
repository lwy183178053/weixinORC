using Client.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Client.Pages
{
    public sealed partial class GuidePage : Page
    {
        private string _activeSection = "quickstart";

        public GuidePage()
        {
            InitializeComponent();
            Loaded += GuidePage_Loaded;
            Unloaded += GuidePage_Unloaded;
        }

        private void GuidePage_Loaded(object sender, RoutedEventArgs e)
        {
            AppServices.Settings.ThemeChanged += OnThemeChanged;
            SelectSection(_activeSection);
        }

        private void GuidePage_Unloaded(object sender, RoutedEventArgs e)
        {
            AppServices.Settings.ThemeChanged -= OnThemeChanged;
        }

        private void OnThemeChanged(bool _)
        {
            UpdateNavVisuals();
        }

        private void GuideNav_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string section)
            {
                SelectSection(section);
            }
        }

        private void SelectSection(string section)
        {
            _activeSection = section;

            PanelQuickStart.Visibility = section == "quickstart" ? Visibility.Visible : Visibility.Collapsed;
            PanelCustomers.Visibility = section == "customers" ? Visibility.Visible : Visibility.Collapsed;
            PanelChat.Visibility = section == "chat" ? Visibility.Visible : Visibility.Collapsed;
            PanelGroups.Visibility = section == "groups" ? Visibility.Visible : Visibility.Collapsed;
            PanelOcr.Visibility = section == "ocr" ? Visibility.Visible : Visibility.Collapsed;
            PanelSettings.Visibility = section == "settings" ? Visibility.Visible : Visibility.Collapsed;

            (ActiveSectionIcon.Glyph, ActiveSectionTitle.Text, ActiveSectionSubtitle.Text) = section switch
            {
                "customers" => ("\uE77B", "客户档案与导入", "单条截图导入、微信标签批量导入与右侧三选项卡说明"),
                "chat" => ("\uE8BD", "聊天记录管理", "整图气泡识别、单条提字、气泡右键操作与多选批量删除"),
                "groups" => ("\uE716", "群组档案", "群信息截图建群、成员清单维护与群聊记录管理"),
                "ocr" => ("\uE7F4", "微信监控", "微信进程存活监听、启停控制、自动唤起与窗口 OCR 诊断"),
                "settings" => ("\uE713", "外观、快捷键与设置", "明亮/黑夜模式、柔和配色偏好、服务配置与常用快捷键"),
                _ => ("\uE945", "快速上手", "三步了解工作台的核心流程与三栏布局")
            };

            GuideContentScroll.ChangeView(null, 0, null, disableAnimation: true);
            UpdateNavVisuals();
        }

        private void UpdateNavVisuals()
        {
            var activeBg = (Brush)Application.Current.Resources["BrandSoftBgBrush"];
            var activeFg = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
            var inactiveBg = new SolidColorBrush(Colors.Transparent);
            var inactiveFg = (Brush)Application.Current.Resources["TextSecondaryBrush"];

            SetNavState(NavQuickStartBtn, NavQuickStartIcon, NavQuickStartText, _activeSection == "quickstart", activeBg, activeFg, inactiveBg, inactiveFg);
            SetNavState(NavCustomersBtn, NavCustomersIcon, NavCustomersText, _activeSection == "customers", activeBg, activeFg, inactiveBg, inactiveFg);
            SetNavState(NavChatBtn, NavChatIcon, NavChatText, _activeSection == "chat", activeBg, activeFg, inactiveBg, inactiveFg);
            SetNavState(NavGroupsBtn, NavGroupsIcon, NavGroupsText, _activeSection == "groups", activeBg, activeFg, inactiveBg, inactiveFg);
            SetNavState(NavOcrBtn, NavOcrIcon, NavOcrText, _activeSection == "ocr", activeBg, activeFg, inactiveBg, inactiveFg);
            SetNavState(NavSettingsBtn, NavSettingsIcon, NavSettingsText, _activeSection == "settings", activeBg, activeFg, inactiveBg, inactiveFg);
        }

        private static void SetNavState(
            Button btn,
            FontIcon icon,
            TextBlock label,
            bool active,
            Brush activeBg,
            Brush activeFg,
            Brush inactiveBg,
            Brush inactiveFg)
        {
            btn.Background = active ? activeBg : inactiveBg;
            icon.Foreground = active ? activeFg : inactiveFg;
            label.Foreground = active ? activeFg : inactiveFg;
            label.FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
    }
}
