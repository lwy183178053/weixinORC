using System.Diagnostics;
using Client.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Client.Dialogs
{
    public sealed partial class SettingsDialog : ContentDialog
    {
        private string _activeSection = "appearance";
        private bool _initializing = true;

        public bool RequestedNavigateLogs { get; private set; }

        public SettingsDialog()
        {
            InitializeComponent();
            Loaded += SettingsDialog_Loaded;
        }

        private void SettingsDialog_Loaded(object sender, RoutedEventArgs e)
        {
            _initializing = true;

            var s = AppServices.Settings.Current;
            CtrlVPasteToggle.IsOn = s.EnableCtrlVPaste;
            ShowOverlayToggle.IsOn = s.DefaultShowOcrOverlay;
            BackendUrlBox.Text = AppServices.Backend.BaseUrl;
            SettingsPathText.Text = AppServices.Settings.SettingsFilePath;

            var emp = AppServices.Backend.CurrentEmployee;
            if (emp is not null)
            {
                string display = string.IsNullOrWhiteSpace(emp.DisplayName) ? emp.Account : emp.DisplayName;
                SidebarAvatarText.Text = display.Length > 0 ? display[..1].ToUpperInvariant() : "E";
                SidebarEmployeeName.Text = display;
                SidebarEmployeeAccount.Text = emp.Account;
                AccountNameText.Text = display;
                AccountLoginText.Text = emp.Account;
                AccountIdText.Text = emp.EmployeeId;
            }

            bool ocrReady = AppServices.Ocr.RapidEngine.IsAvailable;
            OcrStatusPillText.Text = ocrReady ? "已就绪" : "模型缺失";
            OcrEngineDetailText.Text = ocrReady
                ? "RapidOCR PP-OCRv5（全本地离线推理，支持多行自动拼接与头像定位）"
                : "未检测到 RapidOCR 模型文件，请检查安装目录";

            UpdateThemeCardVisuals();
            UpdateDedupSegmentVisuals();
            SelectCategory("appearance");

            _initializing = false;
        }

        private void NavCategory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                SelectCategory(tag);
            }
        }

        private void SelectCategory(string tag)
        {
            _activeSection = tag;

            AppearancePanel.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
            GeneralPanel.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
            ServerPanel.Visibility = tag == "server" ? Visibility.Visible : Visibility.Collapsed;
            AboutPanel.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;

            (SectionTitleText.Text, SectionSubtitleText.Text) = tag switch
            {
                "general" => ("通用与导入", "配置批量导入默认去重方式、快捷粘贴与窗口识别默认偏好。"),
                "server" => ("服务与账号", "配置后端服务地址、检测实时连通性并查看当前员工隔离会话。"),
                "about" => ("关于工作台", "版本信息、合规说明与本地配置文件快速入口。"),
                _ => ("外观与主题", "自由切换明亮 / 黑夜界面风格，修改后实时生效并自动保存。")
            };

            UpdateNavVisuals();
        }

        private void UpdateNavVisuals()
        {
            var activeBg = (Brush)Application.Current.Resources["BrandSoftBgBrush"];
            var activeFg = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
            var inactiveBg = new SolidColorBrush(Colors.Transparent);
            var inactiveFg = (Brush)Application.Current.Resources["TextSecondaryBrush"];

            SetNavState(NavAppearanceButton, NavAppearanceIcon, NavAppearanceText, _activeSection == "appearance", activeBg, activeFg, inactiveBg, inactiveFg);
            SetNavState(NavGeneralButton, NavGeneralIcon, NavGeneralText, _activeSection == "general", activeBg, activeFg, inactiveBg, inactiveFg);
            SetNavState(NavServerButton, NavServerIcon, NavServerText, _activeSection == "server", activeBg, activeFg, inactiveBg, inactiveFg);
            SetNavState(NavAboutButton, NavAboutIcon, NavAboutText, _activeSection == "about", activeBg, activeFg, inactiveBg, inactiveFg);
        }

        private static void SetNavState(Button btn, FontIcon icon, TextBlock text, bool active, Brush activeBg, Brush activeFg, Brush inactiveBg, Brush inactiveFg)
        {
            btn.Background = active ? activeBg : inactiveBg;
            icon.Foreground = active ? activeFg : inactiveFg;
            text.Foreground = active ? activeFg : inactiveFg;
            text.FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }

        private void SelectLightTheme_Click(object sender, RoutedEventArgs e)
        {
            ApplyThemeLive(isDark: false);
        }

        private void SelectDarkTheme_Click(object sender, RoutedEventArgs e)
        {
            ApplyThemeLive(isDark: true);
        }

        private void QuickFlipTheme_Click(object sender, RoutedEventArgs e)
        {
            ApplyThemeLive(!AppServices.Settings.IsDarkMode);
        }

        private void ApplyThemeLive(bool isDark)
        {
            AppServices.Settings.ApplyTheme(isDark, save: true);
            RequestedTheme = AppServices.Settings.CurrentElementTheme;
            UpdateThemeCardVisuals();
            UpdateNavVisuals();
            UpdateDedupSegmentVisuals();
        }

        private void UpdateThemeCardVisuals()
        {
            bool isDark = AppServices.Settings.IsDarkMode;
            var activeBorder = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
            var inactiveBorder = (Brush)Application.Current.Resources["AppBorderBrush"];

            LightThemeCardBorder.BorderBrush = isDark ? inactiveBorder : activeBorder;
            DarkThemeCardBorder.BorderBrush = isDark ? activeBorder : inactiveBorder;
            LightActiveBadge.Visibility = isDark ? Visibility.Collapsed : Visibility.Visible;
            DarkActiveBadge.Visibility = isDark ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SegBackfillBtn_Click(object sender, RoutedEventArgs e)
        {
            AppServices.Settings.Current.DefaultBatchOverwrite = false;
            AppServices.Settings.Save();
            UpdateDedupSegmentVisuals();
        }

        private void SegOverwriteBtn_Click(object sender, RoutedEventArgs e)
        {
            AppServices.Settings.Current.DefaultBatchOverwrite = true;
            AppServices.Settings.Save();
            UpdateDedupSegmentVisuals();
        }

        private void UpdateDedupSegmentVisuals()
        {
            bool overwrite = AppServices.Settings.Current.DefaultBatchOverwrite;
            var activeBg = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
            var activeFg = new SolidColorBrush(Colors.White);
            var inactiveBg = new SolidColorBrush(Colors.Transparent);
            var inactiveFg = (Brush)Application.Current.Resources["TextSecondaryBrush"];

            SegBackfillBtn.Background = overwrite ? inactiveBg : activeBg;
            SegBackfillBtn.Foreground = overwrite ? inactiveFg : activeFg;
            SegOverwriteBtn.Background = overwrite ? activeBg : inactiveBg;
            SegOverwriteBtn.Foreground = overwrite ? activeFg : inactiveFg;
        }

        private void GeneralToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_initializing) return;
            AppServices.Settings.Current.EnableCtrlVPaste = CtrlVPasteToggle.IsOn;
            AppServices.Settings.Current.DefaultShowOcrOverlay = ShowOverlayToggle.IsOn;
            AppServices.Settings.Save();
        }

        private async void TestBackendBtn_Click(object sender, RoutedEventArgs e)
        {
            string url = BackendUrlBox.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                ServerPingText.Text = "请输入有效的服务端地址（例如 http://127.0.0.1:28765）。";
                return;
            }

            try
            {
                AppServices.Backend.SetBaseUrl(url);
            }
            catch (Exception ex)
            {
                ServerPingText.Text = $"✕ {ex.Message}";
                ServerPingBanner.Background = (Brush)Application.Current.Resources["DangerBgBrush"];
                ServerPingText.Foreground = (Brush)Application.Current.Resources["DangerTextBrush"];
                return;
            }
            AppServices.Settings.Current.BackendUrl = AppServices.Backend.BaseUrl;
            AppServices.Settings.Save();

            TestBackendBtn.IsEnabled = false;
            ServerPingText.Text = "正在检测后端服务连通性...";
            var sw = Stopwatch.StartNew();
            try
            {
                var res = await AppServices.Backend.CheckHealthAsync();
                sw.Stop();
                if (res.IsSuccess)
                {
                    ServerPingText.Text = $"✓ 服务连接正常（{AppServices.Backend.BaseUrl}，耗时 {sw.ElapsedMilliseconds} ms）";
                    ServerPingBanner.Background = (Brush)Application.Current.Resources["SuccessBgBrush"];
                    ServerPingText.Foreground = (Brush)Application.Current.Resources["SuccessTextBrush"];
                }
                else
                {
                    ServerPingText.Text = $"✕ 连接失败：{res.ErrorMessage}";
                    ServerPingBanner.Background = (Brush)Application.Current.Resources["DangerBgBrush"];
                    ServerPingText.Foreground = (Brush)Application.Current.Resources["DangerTextBrush"];
                }
            }
            catch (Exception ex)
            {
                ServerPingText.Text = $"✕ 连接异常：{ex.Message}";
                ServerPingBanner.Background = (Brush)Application.Current.Resources["DangerBgBrush"];
                ServerPingText.Foreground = (Brush)Application.Current.Resources["DangerTextBrush"];
            }
            finally
            {
                TestBackendBtn.IsEnabled = true;
            }
        }

        private void OpenSettingsFolderBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string? dir = Path.GetDirectoryName(AppServices.Settings.SettingsFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                    Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
                }
            }
            catch
            {
                // Ignore shell error
            }
        }

        private void JumpToLogsBtn_Click(object sender, RoutedEventArgs e)
        {
            RequestedNavigateLogs = true;
            Hide();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void CloseAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            Hide();
        }
    }
}
