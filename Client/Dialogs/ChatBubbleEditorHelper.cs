using Client.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Client.Dialogs
{
    internal static class ChatBubbleEditorHelper
    {
        internal sealed record Result(
            bool Confirmed,
            bool IsOutgoing,
            string SenderName,
            string Content,
            string ImageUrl,
            byte[]? NewImageBytes,
            ImageSource? ImagePreview,
            bool ImageRemoved);

        internal static async Task<Result> ShowAsync(
            XamlRoot xamlRoot,
            string title,
            string primaryButtonText,
            bool initialIsOutgoing,
            string initialContent,
            bool isGroupChat = false,
            string initialSenderName = "",
            string initialImageUrl = "",
            ImageSource? initialImagePreview = null,
            byte[]? initialImageBytes = null)
        {
            if (!AppServices.TryBeginModalDialog())
            {
                return new Result(false, initialIsOutgoing, initialSenderName, initialContent, initialImageUrl, null, initialImagePreview, false);
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
                    PlaceholderText = "输入或修改聊天内容（可留空仅发送图片）...",
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    MinHeight = 110,
                    MaxHeight = 240,
                    Style = (Style)Application.Current.Resources["SoftTextBoxStyle"]
                };
                // Must assign Text AFTER AcceptsReturn = true, otherwise WinUI 3 truncates at the first newline.
                contentBox.Text = initialContent.Replace("\r\n", "\n").Replace("\n", "\r");
                ScrollViewer.SetVerticalScrollBarVisibility(contentBox, ScrollBarVisibility.Auto);

                // ---- Optional Chat Image Section ----
                string currentImageUrl = initialImageUrl;
                byte[]? newImageBytes = null;
                byte[]? previewBytes = initialImageBytes;
                ImageSource? currentPreview = initialImagePreview;
                bool imageRemoved = false;

                var previewImageCtrl = new Image
                {
                    Width = 64,
                    Height = 64,
                    Stretch = Stretch.UniformToFill,
                    Source = currentPreview
                };
                var previewThumbBtn = new Button
                {
                    Padding = new Thickness(0),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(8),
                    Content = new Border
                    {
                        Width = 64,
                        Height = 64,
                        CornerRadius = new CornerRadius(8),
                        BorderBrush = (Brush)Application.Current.Resources["AppBorderBrush"],
                        BorderThickness = new Thickness(1),
                        Child = previewImageCtrl
                    }
                };
                ToolTipService.SetToolTip(previewThumbBtn, "点击放大查看图片");

                var imageNameText = new TextBlock
                {
                    FontSize = 12,
                    Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Text = !string.IsNullOrWhiteSpace(initialImageUrl) || initialImagePreview is not null
                        ? "已附带图片（点击缩略图可放大查看）"
                        : string.Empty
                };

                var removeImgBtn = new Button
                {
                    Height = 28,
                    Padding = new Thickness(9, 0, 9, 0),
                    Content = "移除图片",
                    FontSize = 11.5,
                    Style = (Style)Application.Current.Resources["DangerOutlineButtonStyle"]
                };

                var previewCardGrid = new Grid { ColumnSpacing = 10 };
                previewCardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                previewCardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                previewCardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(previewThumbBtn, 0);
                Grid.SetColumn(imageNameText, 1);
                Grid.SetColumn(removeImgBtn, 2);
                previewCardGrid.Children.Add(previewThumbBtn);
                previewCardGrid.Children.Add(imageNameText);
                previewCardGrid.Children.Add(removeImgBtn);

                var previewCard = new Border
                {
                    Padding = new Thickness(8),
                    Background = (Brush)Application.Current.Resources["AppCanvasBgBrush"],
                    BorderBrush = (Brush)Application.Current.Resources["AppBorderBrush"],
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(9),
                    Child = previewCardGrid,
                    Visibility = currentPreview is not null || !string.IsNullOrWhiteSpace(currentImageUrl)
                        ? Visibility.Visible
                        : Visibility.Collapsed
                };

                var pickImageBtn = new Button
                {
                    Height = 30,
                    Padding = new Thickness(11, 0, 11, 0),
                    FontSize = 12,
                    Style = (Style)Application.Current.Resources["SecondaryOutlineButtonStyle"],
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 5,
                        Children =
                        {
                            new FontIcon { FontSize = 12.5, Glyph = "\uE91B" },
                            new TextBlock { Text = "上传图片" }
                        }
                    }
                };

                var pasteImageBtn = new Button
                {
                    Height = 30,
                    Padding = new Thickness(11, 0, 11, 0),
                    FontSize = 12,
                    Style = (Style)Application.Current.Resources["SecondaryOutlineButtonStyle"],
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 5,
                        Children =
                        {
                            new FontIcon { FontSize = 12.5, Glyph = "\uE77F" },
                            new TextBlock { Text = "粘贴图片" }
                        }
                    }
                };

                var errText = new TextBlock
                {
                    FontSize = 11.5,
                    Foreground = (Brush)Application.Current.Resources["DangerTextBrush"],
                    Visibility = Visibility.Collapsed
                };

                void ApplySelectedImage(PendingChatImage loaded)
                {
                    newImageBytes = loaded.Bytes;
                    previewBytes = loaded.Bytes;
                    currentPreview = loaded.Preview;
                    currentImageUrl = string.Empty;
                    imageRemoved = false;
                    previewImageCtrl.Source = loaded.Preview;
                    imageNameText.Text = $"{loaded.DisplayName}（点击放大查看）";
                    previewCard.Visibility = Visibility.Visible;
                    errText.Visibility = Visibility.Collapsed;
                }

                pickImageBtn.Click += async (_, _) =>
                {
                    string? path = Win32WindowHelper.PickImageFilePath(AppServices.MainWindowHandle);
                    if (string.IsNullOrWhiteSpace(path)) return;
                    try
                    {
                        var loaded = await ChatImageHelper.TryLoadFromFileAsync(path);
                        if (loaded is not null)
                        {
                            ApplySelectedImage(loaded);
                        }
                        else
                        {
                            errText.Text = "无法读取该图片文件，请尝试 PNG / JPG / WebP 格式。";
                            errText.Visibility = Visibility.Visible;
                        }
                    }
                    catch (Exception ex)
                    {
                        errText.Text = $"加载图片失败：{ex.Message}";
                        errText.Visibility = Visibility.Visible;
                    }
                };

                pasteImageBtn.Click += async (_, _) =>
                {
                    try
                    {
                        var loaded = await ChatImageHelper.TryLoadFromClipboardAsync();
                        if (loaded is not null)
                        {
                            ApplySelectedImage(loaded);
                        }
                        else
                        {
                            errText.Text = "剪贴板中暂无图片。";
                            errText.Visibility = Visibility.Visible;
                        }
                    }
                    catch (Exception ex)
                    {
                        errText.Text = $"粘贴图片失败：{ex.Message}";
                        errText.Visibility = Visibility.Visible;
                    }
                };

                removeImgBtn.Click += (_, _) =>
                {
                    newImageBytes = null;
                    previewBytes = null;
                    currentPreview = null;
                    currentImageUrl = string.Empty;
                    imageRemoved = true;
                    previewImageCtrl.Source = null;
                    previewCard.Visibility = Visibility.Collapsed;
                };

                previewThumbBtn.Click += async (_, _) =>
                {
                    byte[]? bytesToShow = previewBytes;
                    if (bytesToShow is null && !string.IsNullOrWhiteSpace(currentImageUrl))
                    {
                        bytesToShow = await AppServices.Backend.DownloadChatImageBytesAsync(currentImageUrl);
                        previewBytes = bytesToShow;
                    }
                    if (bytesToShow is { Length: > 0 })
                    {
                        ImageViewerWindow.ShowImage(bytesToShow, "聊天图片预览");
                    }
                };

                var imageHeaderRow = new Grid { ColumnSpacing = 8 };
                imageHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                imageHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var imgLabel = new TextBlock
                {
                    Text = "附带图片（可选）",
                    FontSize = 12.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"]
                };
                var imgBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                imgBtns.Children.Add(pickImageBtn);
                imgBtns.Children.Add(pasteImageBtn);
                Grid.SetColumn(imgLabel, 0);
                Grid.SetColumn(imgBtns, 1);
                imageHeaderRow.Children.Add(imgLabel);
                imageHeaderRow.Children.Add(imgBtns);

                var imageSection = new StackPanel { Spacing = 8 };
                imageSection.Children.Add(imageHeaderRow);
                imageSection.Children.Add(previewCard);

                var panel = new StackPanel
                {
                    Width = 440,
                    Spacing = 10
                };
                panel.Children.Add(dirRow);
                if (isGroupChat) panel.Children.Add(senderBox);
                panel.Children.Add(contentBox);
                panel.Children.Add(imageSection);
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
                    bool hasText = !string.IsNullOrWhiteSpace(contentBox.Text);
                    bool hasImg = newImageBytes is { Length: > 0 } || !string.IsNullOrWhiteSpace(currentImageUrl);
                    if (!hasText && !hasImg)
                    {
                        args.Cancel = true;
                        errText.Text = "消息内容和图片不能同时为空。";
                        errText.Visibility = Visibility.Visible;
                    }
                };

                var choice = await dialog.ShowAsync();
                if (choice != ContentDialogResult.Primary)
                {
                    return new Result(false, initialIsOutgoing, initialSenderName, initialContent, initialImageUrl, null, initialImagePreview, false);
                }

                bool isOut = outgoingRadio.IsChecked == true;
                string sender = isGroupChat && !isOut
                    ? (string.IsNullOrWhiteSpace(senderBox.Text) ? "群成员" : senderBox.Text.Trim())
                    : string.Empty;

                string normalizedContent = contentBox.Text
                    .Replace("\r\n", "\n")
                    .Replace('\r', '\n')
                    .Trim();

                return new Result(
                    Confirmed: true,
                    IsOutgoing: isOut,
                    SenderName: sender,
                    Content: normalizedContent,
                    ImageUrl: currentImageUrl,
                    NewImageBytes: newImageBytes,
                    ImagePreview: currentPreview,
                    ImageRemoved: imageRemoved);
            }
            finally
            {
                AppServices.EndModalDialog();
            }
        }
    }
}
