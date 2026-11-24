using Client.Models;
using Client.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Client.Pages
{
    public sealed partial class CustomersPage : Page
    {
        private List<CustomerDto> _allCustomers = [];

        public CustomersPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await LoadCustomersAsync();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadCustomersAsync();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (CustomersListView is null)
            {
                return;
            }

            ApplyFilter();
        }

        private async Task LoadCustomersAsync()
        {
            var employee = AppServices.Backend.CurrentEmployee;
            if (employee is not null)
            {
                OwnerSubtitleText.Text = $"当前员工：{employee.DisplayName} ({employee.Account}) · 仅展示本人负责客户";
            }

            RefreshButton.IsEnabled = false;
            try
            {
                var result = await AppServices.Backend.ListCustomersAsync();
                if (result.IsSuccess && result.Data is not null)
                {
                    _allCustomers = result.Data.Items;
                    ApplyFilter();
                    ShowStatus($"已同步 {_allCustomers.Count} 条客户档案。");
                }
                else
                {
                    ShowStatus($"加载失败：{result.ErrorMessage}");
                }
            }
            finally
            {
                RefreshButton.IsEnabled = true;
            }
        }

        private void ApplyFilter()
        {
            string q = SearchBox.Text.Trim();
            var filtered = string.IsNullOrEmpty(q)
                ? _allCustomers
                : _allCustomers.Where(c =>
                    c.WechatName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    c.WechatAccount.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    c.Note.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

            CustomersListView.ItemsSource = filtered;
            EmptyTableHint.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            string name = NewNameBox.Text.Trim();
            string account = NewAccountBox.Text.Trim();
            string note = NewNoteBox.Text.Trim();

            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(account))
            {
                ShowStatus("微信昵称和微信号不能同时为空。");
                return;
            }

            CreateButton.IsEnabled = false;
            try
            {
                var result = await AppServices.Backend.CreateCustomerAsync(name, account, note);
                if (result.IsSuccess)
                {
                    NewNameBox.Text = string.Empty;
                    NewAccountBox.Text = string.Empty;
                    NewNoteBox.Text = string.Empty;
                    await LoadCustomersAsync();
                    ShowStatus("已新增客户档案。");
                }
                else
                {
                    ShowStatus($"新增失败：{result.ErrorMessage}");
                }
            }
            finally
            {
                CreateButton.IsEnabled = true;
            }
        }

        private void ShowStatus(string message)
        {
            StatusText.Text = message;
            StatusBanner.Visibility = Visibility.Visible;
        }
    }
}
