using Client.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Client.Dialogs
{
    internal static class ChatBubbleEditorHelper
    {
        internal sealed record Result(bool Confirmed, bool IsOutgoing, string SenderName, string Content);

        internal static async Task<Result> ShowAsync(
            XamlRoot xamlRoot,
            string title,
            string primaryButtonText,
            bool initialIsOutgoing,
            string initialContent,
            bool isGroupChat = false,
            string initialSenderName = "")
        {
            if (!AppServices.TryBeginModalDialog())
            {
                return new Result(false, initialIsOutgoing, initialSenderName, initialContent);
            }

            try
            {
                var outgoingRadio = new RadioButton
                {
                    Content = isGroupChat ? "我方发出 (绿)" : "我方回复 (绿)",
                    GroupName = "BubbleEditDir",
                    IsChecked = initialIsOutgoing
                };
                var incomingRadio = new RadioButton
                {
                    Content = isGroupChat ? "群成员发言 (白)" : "客户发来 (白)",
                    GroupName = "BubbleEditDir",
                    IsChecked = !initialIsOutgoing
                };

                var dirRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12
                };
                dirRow.Children.Add(incomingRadio);
                dirRow.Children.Add(outgoingRadio);

                var senderBox = new TextBox
                {
                    Header = "发言人",
                    PlaceholderText = "输入群成员名称（留空默认群成员）",
                    Text = initialSenderName,
                    Style = (Style)Application.Current.Resources["SoftTextBoxStyle"],
                    Visibility = isGroupChat && !initialIsOutgoing ? Visibility.Visible : Visibility.Collapsed
                };

                outgoingRadio.Checked += (_, _) => senderBox.Visibility = Visibility.Collapsed;
                incomingRadio.Checked += (_, _) =>
                {
                    if (isGroupChat) senderBox.Visibility = Visibility.Visible;
                };

                var contentBox = new TextBox
                {
                    Header = "对话内容",
                    PlaceholderText = "输入或修改聊天内容...",
                    Text = initialContent,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    MinHeight = 110,
                    MaxHeight = 220,
                    Style = (Style)Application.Current.Resources["SoftTextBoxStyle"]
                };
                ScrollViewer.SetVerticalScrollBarVisibility(contentBox, ScrollBarVisibility.Auto);

                var errText = new TextBlock
                {
                    FontSize = 11.5,
                    Foreground = (Brush)Application.Current.Resources["DangerTextBrush"],
                    Visibility = Visibility.Collapsed
                };

                var panel = new StackPanel
                {
                    Width = 420,
                    Spacing = 10
                };
                panel.Children.Add(dirRow);
                if (isGroupChat) panel.Children.Add(senderBox);
                panel.Children.Add(contentBox);
                panel.Children.Add(errText);

                var dialog = new ContentDialog
                {
                    XamlRoot = xamlRoot,
                    RequestedTheme = AppServices.Settings.CurrentElementTheme,
                    Title = title,
                    Content = panel,
                    PrimaryButtonText = primaryButtonText,
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    PrimaryButtonStyle = (Style)Application.Current.Resources["PrimaryVioletButtonStyle"]
                };

                dialog.PrimaryButtonClick += (_, args) =>
                {
                    if (string.IsNullOrWhiteSpace(contentBox.Text))
                    {
                        args.Cancel = true;
                        errText.Text = "消息内容不能为空。";
                        errText.Visibility = Visibility.Visible;
                    }
                };

                var choice = await dialog.ShowAsync();
                if (choice != ContentDialogResult.Primary)
                {
                    return new Result(false, initialIsOutgoing, initialSenderName, initialContent);
                }

                bool isOut = outgoingRadio.IsChecked == true;
                string sender = isGroupChat && !isOut
                    ? (string.IsNullOrWhiteSpace(senderBox.Text) ? "群成员" : senderBox.Text.Trim())
                    : string.Empty;

                return new Result(true, isOut, sender, contentBox.Text.Trim());
            }
            finally
            {
                AppServices.EndModalDialog();
            }
        }
    }
}
