using Client.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace Client.Pages
{
    public sealed partial class LoginPage : Page
    {
        private bool _isBusy;

        public LoginPage()
        {
            InitializeComponent();
            ServerUrlBox.Text = AppServices.Backend.BaseUrl;
            Loaded += LoginPage_Loaded;
        }

        private async void LoginPage_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= LoginPage_Loaded;
            MainWindow.Instance?.SetDragRegion(LoginTitleDragBar);
            await CheckBackendHealthAsync();
        }

        private async void BackendStatusButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryApplyServerUrl())
            {
                return;
            }

            await CheckBackendHealthAsync();
        }

        private async Task CheckBackendHealthAsync()
        {
            HealthDot.Fill = new SolidColorBrush(Color.FromArgb(255, 217, 119, 6));
            HealthBadgeText.Text = "检测服务中...";

            var result = await AppServices.Backend.CheckHealthAsync();
            if (result.IsSuccess)
            {
                HealthDot.Fill = new SolidColorBrush(Color.FromArgb(255, 23, 163, 74));
                HealthBadgeText.Text = "服务在线";
            }
            else
            {
                HealthDot.Fill = new SolidColorBrush(Color.FromArgb(255, 220, 38, 38));
                HealthBadgeText.Text = "服务未连接";
            }
        }

        private void ToggleServerConfigButton_Click(object sender, RoutedEventArgs e)
        {
            bool expand = ServerConfigPanel.Visibility != Visibility.Visible;
            ServerConfigPanel.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
            UpdateServerConfigToggleLabel(expand);
        }

        private void UpdateServerConfigToggleLabel(bool expanded)
        {
            string host = ServerUrlBox.Text.Replace("http://", "").Replace("https://", "");
            ServerConfigToggleText.Text = expanded
                ? $"▾ 隐藏服务地址 ({host})"
                : $"▸ 服务地址配置 ({host})";
        }

        private void FillDemoButton_Click(object sender, RoutedEventArgs e)
        {
            AccountBox.Text = "demo";
            PasswordBox.Password = "demo123";
            HideError();
        }

        private void Input_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter && !_isBusy)
            {
                e.Handled = true;
                _ = PerformLoginAsync();
            }
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            await PerformLoginAsync();
        }

        private async Task PerformLoginAsync()
        {
            if (_isBusy)
            {
                return;
            }

            HideError();
            if (!TryApplyServerUrl())
            {
                return;
            }

            string account = AccountBox.Text.Trim();
            string password = PasswordBox.Password;
            if (string.IsNullOrWhiteSpace(account) || string.IsNullOrEmpty(password))
            {
                ShowError("请输入员工账号和登录密码。");
                return;
            }

            SetBusy(true);
            try
            {
                var result = await AppServices.Backend.LoginAsync(account, password);
                if (result.IsSuccess && result.Data is not null)
                {
                    PasswordBox.Password = string.Empty;
                    MainWindow.Instance?.EnterWorkbench();
                }
                else
                {
                    ShowError(result.ErrorMessage);
                    await CheckBackendHealthAsync();
                }
            }
            finally
            {
                SetBusy(false);
            }
        }

        private bool TryApplyServerUrl()
        {
            try
            {
                AppServices.Backend.SetBaseUrl(ServerUrlBox.Text);
                UpdateServerConfigToggleLabel(ServerConfigPanel.Visibility == Visibility.Visible);
                return true;
            }
            catch (ArgumentException ex)
            {
                ShowError(ex.Message);
                return false;
            }
        }

        private void SetBusy(bool busy)
        {
            _isBusy = busy;
            LoginButton.IsEnabled = !busy;
            AccountBox.IsEnabled = !busy;
            PasswordBox.IsEnabled = !busy;
            LoginSpinner.IsActive = busy;
            LoginSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            LoginButtonText.Text = busy ? "正在验证身份..." : "立即登录";
        }

        private void ShowError(string message)
        {
            ErrorMessageText.Text = message;
            ErrorBanner.Visibility = Visibility.Visible;
        }

        private void HideError()
        {
            ErrorBanner.Visibility = Visibility.Collapsed;
            ErrorMessageText.Text = string.Empty;
        }
    }
}
