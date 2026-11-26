using System.Text.RegularExpressions;
using Client.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace Client.Controls
{
    /// <summary>
    /// WeChat-style rich message bubble renderer for WinUI 3:
    /// - Full mouse text selection &amp; copy (IsTextSelectionEnabled = true)
    /// - Clickable URLs that open in the system default browser
    /// - WeChat-style Link cards, Mini-Program cards, File cards, Transfer/Red-Packet cards,
    ///   Merged Forward cards, Quote reply strips, and playable Voice message bars.
    /// </summary>
    public sealed partial class WeChatMessageContentView : UserControl
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(WeChatMessageContentView),
            new PropertyMetadata(string.Empty, OnVisualPropertyChanged));

        public static readonly DependencyProperty AudioUrlProperty = DependencyProperty.Register(
            nameof(AudioUrl), typeof(string), typeof(WeChatMessageContentView),
            new PropertyMetadata(string.Empty, OnVisualPropertyChanged));

        public static readonly DependencyProperty VideoUrlProperty = DependencyProperty.Register(
            nameof(VideoUrl), typeof(string), typeof(WeChatMessageContentView),
            new PropertyMetadata(string.Empty, OnVisualPropertyChanged));

        public static readonly DependencyProperty IsOutgoingProperty = DependencyProperty.Register(
            nameof(IsOutgoing), typeof(bool), typeof(WeChatMessageContentView),
            new PropertyMetadata(false, OnVisualPropertyChanged));

        [GeneratedRegex(@"https?://[^\s<>""'\]）)】]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
        private static partial Regex UrlRegex();

        [GeneratedRegex(@"^🎤\s*\[语音(?:\s*(\d+)""?)?\](.*)$", RegexOptions.Singleline | RegexOptions.Compiled)]
        private static partial Regex VoiceRegex();

        private readonly StackPanel _rootPanel = new() { Spacing = 6 };
        private Button? _voicePlayButton;
        private FontIcon? _voicePlayIcon;
        private TextBlock? _voiceStateText;
        private bool _subscribed;

        public WeChatMessageContentView()
        {
            Content = _rootPanel;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public string AudioUrl
        {
            get => (string)GetValue(AudioUrlProperty);
            set => SetValue(AudioUrlProperty, value);
        }

        public string VideoUrl
        {
            get => (string)GetValue(VideoUrlProperty);
            set => SetValue(VideoUrlProperty, value);
        }

        public bool IsOutgoing
        {
            get => (bool)GetValue(IsOutgoingProperty);
            set => SetValue(IsOutgoingProperty, value);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_subscribed)
            {
                AppServices.Settings.ThemeChanged += OnThemeChanged;
                VoicePlaybackService.Shared.PlaybackStateChanged += OnPlaybackStateChanged;
                _subscribed = true;
            }
            Render();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_subscribed)
            {
                AppServices.Settings.ThemeChanged -= OnThemeChanged;
                VoicePlaybackService.Shared.PlaybackStateChanged -= OnPlaybackStateChanged;
                _subscribed = false;
            }
        }

        private void OnThemeChanged(bool _) => DispatcherQueue?.TryEnqueue(Render);

        private void OnPlaybackStateChanged(string? playingUrl, bool isPlaying)
        {
            DispatcherQueue?.TryEnqueue(() => UpdateVoiceVisualState(playingUrl, isPlaying));
        }

        private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is WeChatMessageContentView view)
            {
                view.Render();
            }
        }

        private Brush TextBrush => IsOutgoing
            ? GetBrush("ChatOutgoingTextBrush", Color.FromArgb(255, 31, 35, 41))
            : GetBrush("TextPrimaryBrush", Color.FromArgb(255, 31, 35, 41));

        private Brush LinkBrush => IsOutgoing && !AppServices.Settings.IsDarkMode
            ? new SolidColorBrush(Color.FromArgb(255, 11, 87, 208))
            : GetBrush("InfoTextBrush", Color.FromArgb(255, 37, 99, 235));

        private static Brush GetBrush(string key, Color fallback)
        {
            if (Application.Current?.Resources.TryGetValue(key, out var val) == true && val is Brush b)
            {
                return b;
            }
            return new SolidColorBrush(fallback);
        }

        private void Render()
        {
            _rootPanel.Children.Clear();
            _voicePlayButton = null;
            _voicePlayIcon = null;
            _voiceStateText = null;

            string raw = (Text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            string audioUrl = (AudioUrl ?? string.Empty).Trim();
            string videoUrl = (VideoUrl ?? string.Empty).Trim();

            if (string.IsNullOrEmpty(raw) && string.IsNullOrEmpty(audioUrl) && string.IsNullOrEmpty(videoUrl))
            {
                return;
            }

            // 0. Video card fallback (when no cover image is rendered above)
            if (!string.IsNullOrEmpty(videoUrl) || raw.StartsWith("🎬 [视频", StringComparison.Ordinal))
            {
                RenderVideoFallbackCard(raw, videoUrl);
                return;
            }

            // 1. Voice message bar (with real playback if AudioUrl is available)
            var voiceMatch = VoiceRegex().Match(raw);
            if (voiceMatch.Success || !string.IsNullOrEmpty(audioUrl))
            {
                string sec = voiceMatch.Success ? voiceMatch.Groups[1].Value : string.Empty;
                string transcript = voiceMatch.Success ? voiceMatch.Groups[2].Value.Trim() : (string.IsNullOrEmpty(raw) ? string.Empty : raw);
                RenderVoiceMessage(audioUrl, sec, transcript);
                return;
            }

            // 2. WeChat Shared Link Card: 🔗 [链接] ...
            if (raw.StartsWith("🔗 [链接]", StringComparison.Ordinal))
            {
                RenderLinkCard(raw["🔗 [链接]".Length..].Trim());
                return;
            }

            // 3. WeChat Mini-Program Card: 🧩 [小程序] ...
            if (raw.StartsWith("🧩 [小程序]", StringComparison.Ordinal))
            {
                RenderMiniProgramCard(raw["🧩 [小程序]".Length..].Trim());
                return;
            }

            // 4. WeChat File Card: 📎 [文件] ...
            if (raw.StartsWith("📎 [文件]", StringComparison.Ordinal))
            {
                RenderFileCard(raw["📎 [文件]".Length..].Trim());
                return;
            }

            // 5. WeChat Transfer / Red Packet Card
            if (raw.StartsWith("💰 [微信转账]", StringComparison.Ordinal) || raw.StartsWith("🧧 [微信红包]", StringComparison.Ordinal))
            {
                bool isTransfer = raw.StartsWith("💰", StringComparison.Ordinal);
                string body = raw[(isTransfer ? "💰 [微信转账]" : "🧧 [微信红包]").Length..].Trim();
                RenderPayCard(isTransfer ? "微信转账" : "微信红包", body, isTransfer ? "\uE8C7" : "\uE8F1");
                return;
            }

            // 6. WeChat Merged Forward Card: 📋 [合并转发] ...
            if (raw.StartsWith("📋 [合并转发]", StringComparison.Ordinal))
            {
                RenderForwardCard(raw["📋 [合并转发]".Length..].Trim());
                return;
            }

            // 7. Quote Reply: main reply + \n「引用 ...」
            int quoteIdx = raw.LastIndexOf("\n「引用 ", StringComparison.Ordinal);
            if (quoteIdx > 0 && raw.EndsWith("」", StringComparison.Ordinal))
            {
                string replyText = raw[..quoteIdx].Trim();
                string quotedText = raw[(quoteIdx + 2)..^1].Trim(); // strip \n「 and 」
                if (!string.IsNullOrEmpty(replyText))
                {
                    _rootPanel.Children.Add(BuildSelectableRichText(replyText));
                }
                RenderQuoteStrip(quotedText);
                return;
            }

            // 8. Standard text (selectable + auto-linked clickable URLs)
            _rootPanel.Children.Add(BuildSelectableRichText(raw));
        }

        private RichTextBlock BuildSelectableRichText(string text, double fontSize = 13.5, Brush? foreground = null)
        {
            var rtb = new RichTextBlock
            {
                FontSize = fontSize,
                Foreground = foreground ?? TextBrush,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                LineHeight = fontSize * 1.48
            };

            var paragraph = new Paragraph();
            int cursor = 0;
            foreach (Match m in UrlRegex().Matches(text))
            {
                if (m.Index > cursor)
                {
                    paragraph.Inlines.Add(new Run { Text = text[cursor..m.Index] });
                }

                string cleanUrl = TrimUrlTrailingPunctuation(m.Value, out string trailing);
                if (Uri.TryCreate(cleanUrl, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    var link = new Hyperlink
                    {
                        Foreground = LinkBrush,
                        UnderlineStyle = UnderlineStyle.Single
                    };
                    link.Inlines.Add(new Run { Text = cleanUrl });
                    var targetUri = uri;
                    link.Click += async (_, _) =>
                    {
                        try { await Launcher.LaunchUriAsync(targetUri); }
                        catch { }
                    };
                    paragraph.Inlines.Add(link);
                }
                else
                {
                    paragraph.Inlines.Add(new Run { Text = cleanUrl });
                }

                if (!string.IsNullOrEmpty(trailing))
                {
                    paragraph.Inlines.Add(new Run { Text = trailing });
                }
                cursor = m.Index + m.Length;
            }

            if (cursor < text.Length)
            {
                paragraph.Inlines.Add(new Run { Text = text[cursor..] });
            }

            rtb.Blocks.Add(paragraph);
            return rtb;
        }

        private static string TrimUrlTrailingPunctuation(string url, out string trailing)
        {
            trailing = string.Empty;
            int end = url.Length;
            while (end > 0 && url[end - 1] is '.' or ',' or ';' or '!' or '?' or '。' or '，' or '；' or '！' or '？' or ')' or '）' or '」' or '”')
            {
                end--;
            }
            if (end < url.Length)
            {
                trailing = url[end..];
                return url[..end];
            }
            return url;
        }

        private void RenderLinkCard(string body)
        {
            var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string url = string.Empty;
            var textLines = new List<string>();
            foreach (var line in lines)
            {
                var m = UrlRegex().Match(line);
                if (string.IsNullOrEmpty(url) && m.Success && m.Index == 0 && m.Length == line.Length)
                {
                    url = TrimUrlTrailingPunctuation(m.Value, out _);
                }
                else
                {
                    if (string.IsNullOrEmpty(url) && m.Success)
                    {
                        url = TrimUrlTrailingPunctuation(m.Value, out _);
                    }
                    textLines.Add(line);
                }
            }

            string title = textLines.Count > 0 ? textLines[0] : "网页链接";
            string desc = textLines.Count > 1 ? string.Join(" ", textLines.Skip(1)) : string.Empty;
            // Support legacy single-line "标题 · 描述" format
            if (string.IsNullOrEmpty(desc) && title.Contains(" · ", StringComparison.Ordinal))
            {
                int sep = title.IndexOf(" · ", StringComparison.Ordinal);
                desc = title[(sep + 3)..].Trim();
                title = title[..sep].Trim();
            }

            Uri? targetUri = Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
                             (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
                ? parsed
                : null;

            var card = new Border
            {
                MinWidth = 220,
                MaxWidth = 340,
                Padding = new Thickness(11, 9, 11, 8),
                Background = GetBrush("AppSurfaceBrush", Colors.White),
                BorderBrush = GetBrush("AppBorderBrush", Color.FromArgb(255, 226, 232, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8)
            };

            var stack = new StackPanel { Spacing = 6 };
            var topGrid = new Grid { ColumnSpacing = 10 };
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textStack = new StackPanel { Spacing = 3 };
            textStack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = GetBrush("TextPrimaryBrush", Color.FromArgb(255, 31, 35, 41)),
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsTextSelectionEnabled = true
            });
            if (!string.IsNullOrWhiteSpace(desc))
            {
                textStack.Children.Add(new TextBlock
                {
                    Text = desc,
                    FontSize = 11.5,
                    Foreground = GetBrush("TextMutedBrush", Color.FromArgb(255, 143, 149, 158)),
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 2,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    IsTextSelectionEnabled = true
                });
            }

            var iconBox = new Border
            {
                Width = 40,
                Height = 40,
                VerticalAlignment = VerticalAlignment.Top,
                Background = GetBrush("InfoBgBrush", Color.FromArgb(255, 234, 243, 255)),
                CornerRadius = new CornerRadius(7),
                Child = new FontIcon
                {
                    Glyph = "\uE71B",
                    FontSize = 16,
                    Foreground = GetBrush("InfoTextBrush", Color.FromArgb(255, 37, 99, 235)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            Grid.SetColumn(textStack, 0);
            Grid.SetColumn(iconBox, 1);
            topGrid.Children.Add(textStack);
            topGrid.Children.Add(iconBox);
            stack.Children.Add(topGrid);

            stack.Children.Add(new Border
            {
                Height = 1,
                Background = GetBrush("AppBorderSubtleBrush", Color.FromArgb(255, 238, 240, 246))
            });

            var footer = new Grid { ColumnSpacing = 8 };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string hostLabel = targetUri is not null ? targetUri.Host : "网页链接";
            var hostText = new TextBlock
            {
                Text = hostLabel,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = GetBrush("TextMutedBrush", Color.FromArgb(255, 143, 149, 158)),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(hostText, 0);
            footer.Children.Add(hostText);

            if (targetUri is not null)
            {
                var openBtn = new HyperlinkButton
                {
                    Content = "在浏览器打开 ↗",
                    FontSize = 11.5,
                    Padding = new Thickness(4, 1, 4, 1),
                    NavigateUri = targetUri
                };
                Grid.SetColumn(openBtn, 1);
                footer.Children.Add(openBtn);
            }
            else
            {
                var hintText = new TextBlock
                {
                    Text = "点击「立即同步」可补全链接",
                    FontSize = 10.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = GetBrush("TextMutedBrush", Color.FromArgb(255, 143, 149, 158))
                };
                Grid.SetColumn(hintText, 1);
                footer.Children.Add(hintText);
            }

            stack.Children.Add(footer);
            card.Child = stack;
            _rootPanel.Children.Add(card);
        }

        private void RenderMiniProgramCard(string body)
        {
            var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string main = lines.Length > 0 ? lines[0] : "小程序";
            string url = lines.Skip(1).FirstOrDefault(l => l.StartsWith("http", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;

            string appName = "小程序";
            string title = main;
            int sep = main.IndexOf(" · ", StringComparison.Ordinal);
            if (sep > 0)
            {
                appName = main[..sep].Trim();
                title = main[(sep + 3)..].Trim();
            }

            var card = new Border
            {
                MinWidth = 220,
                MaxWidth = 330,
                Padding = new Thickness(11, 9, 11, 8),
                Background = GetBrush("AppSurfaceBrush", Colors.White),
                BorderBrush = GetBrush("AppBorderBrush", Color.FromArgb(255, 226, 232, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8)
            };

            var stack = new StackPanel { Spacing = 6 };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            head.Children.Add(new FontIcon
            {
                Glyph = "\uE74C",
                FontSize = 12,
                Foreground = GetBrush("BrandPrimaryBrush", Color.FromArgb(255, 124, 92, 252))
            });
            head.Children.Add(new TextBlock
            {
                Text = appName,
                FontSize = 11.5,
                Foreground = GetBrush("TextMutedBrush", Color.FromArgb(255, 143, 149, 158)),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            stack.Children.Add(head);

            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = GetBrush("TextPrimaryBrush", Color.FromArgb(255, 31, 35, 41)),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            });

            stack.Children.Add(new Border
            {
                Height = 1,
                Background = GetBrush("AppBorderSubtleBrush", Color.FromArgb(255, 238, 240, 246))
            });

            var foot = new Grid();
            foot.Children.Add(new TextBlock
            {
                Text = "微信小程序",
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = GetBrush("TextMutedBrush", Color.FromArgb(255, 143, 149, 158))
            });
            if (Uri.TryCreate(url, UriKind.Absolute, out var targetUri) &&
                (targetUri.Scheme == Uri.UriSchemeHttp || targetUri.Scheme == Uri.UriSchemeHttps))
            {
                foot.Children.Add(new HyperlinkButton
                {
                    Content = "打开链接 ↗",
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Padding = new Thickness(4, 0, 4, 0),
                    NavigateUri = targetUri
                });
            }
            stack.Children.Add(foot);
            card.Child = stack;
            _rootPanel.Children.Add(card);
        }

        private void RenderFileCard(string fileInfo)
        {
            var card = new Border
            {
                MinWidth = 210,
                MaxWidth = 330,
                Padding = new Thickness(11, 9, 11, 9),
                Background = GetBrush("AppSurfaceBrush", Colors.White),
                BorderBrush = GetBrush("AppBorderBrush", Color.FromArgb(255, 226, 232, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8)
            };

            var grid = new Grid { ColumnSpacing = 10 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameText = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(fileInfo) ? "文件" : fileInfo,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = GetBrush("TextPrimaryBrush", Color.FromArgb(255, 31, 35, 41)),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                VerticalAlignment = VerticalAlignment.Center
            };
            var iconBox = new Border
            {
                Width = 36,
                Height = 36,
                Background = GetBrush("BrandSoftBgBrush", Color.FromArgb(255, 241, 238, 255)),
                CornerRadius = new CornerRadius(7),
                Child = new FontIcon
                {
                    Glyph = "\uE8A5",
                    FontSize = 16,
                    Foreground = GetBrush("BrandPrimaryBrush", Color.FromArgb(255, 124, 92, 252)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetColumn(nameText, 0);
            Grid.SetColumn(iconBox, 1);
            grid.Children.Add(nameText);
            grid.Children.Add(iconBox);
            card.Child = grid;
            _rootPanel.Children.Add(card);
        }

        private void RenderPayCard(string label, string detail, string glyph)
        {
            var card = new Border
            {
                MinWidth = 210,
                MaxWidth = 300,
                Padding = new Thickness(12, 10, 12, 8),
                Background = new SolidColorBrush(Color.FromArgb(255, 249, 157, 59)),
                CornerRadius = new CornerRadius(8)
            };
            var stack = new StackPanel { Spacing = 6 };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(17),
                Background = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)),
                Child = new FontIcon
                {
                    Glyph = glyph,
                    FontSize = 16,
                    Foreground = new SolidColorBrush(Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            textStack.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(detail) ? label : detail,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.White),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            });
            textStack.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255))
            });
            row.Children.Add(textStack);
            stack.Children.Add(row);
            card.Child = stack;
            _rootPanel.Children.Add(card);
        }

        private void RenderForwardCard(string body)
        {
            var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            string title = lines.Length > 0 ? lines[0].Trim() : "聊天记录";
            var card = new Border
            {
                MinWidth = 220,
                MaxWidth = 340,
                Padding = new Thickness(11, 9, 11, 8),
                Background = GetBrush("AppSurfaceBrush", Colors.White),
                BorderBrush = GetBrush("AppBorderBrush", Color.FromArgb(255, 226, 232, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8)
            };
            var stack = new StackPanel { Spacing = 4 };
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = GetBrush("TextPrimaryBrush", Color.FromArgb(255, 31, 35, 41)),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            });
            foreach (var preview in lines.Skip(1).Take(4))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = preview.Trim(),
                    FontSize = 11.5,
                    Foreground = GetBrush("TextMutedBrush", Color.FromArgb(255, 143, 149, 158)),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxLines = 1,
                    IsTextSelectionEnabled = true
                });
            }
            stack.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(0, 3, 0, 1),
                Background = GetBrush("AppBorderSubtleBrush", Color.FromArgb(255, 238, 240, 246))
            });
            stack.Children.Add(new TextBlock
            {
                Text = "聊天记录",
                FontSize = 10.5,
                Foreground = GetBrush("TextMutedBrush", Color.FromArgb(255, 143, 149, 158))
            });
            card.Child = stack;
            _rootPanel.Children.Add(card);
        }

        private void RenderQuoteStrip(string quotedText)
        {
            var quoteBox = new Border
            {
                Padding = new Thickness(8, 5, 8, 5),
                Background = new SolidColorBrush(AppServices.Settings.IsDarkMode
                    ? Color.FromArgb(55, 255, 255, 255)
                    : Color.FromArgb(20, 0, 0, 0)),
                BorderBrush = GetBrush("BrandPrimaryBrush", Color.FromArgb(255, 124, 92, 252)),
                BorderThickness = new Thickness(2.5, 0, 0, 0),
                CornerRadius = new CornerRadius(4),
                Child = new TextBlock
                {
                    Text = quotedText,
                    FontSize = 11.5,
                    Foreground = IsOutgoing ? TextBrush : GetBrush("TextSecondaryBrush", Color.FromArgb(255, 100, 106, 115)),
                    Opacity = 0.85,
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 3,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    IsTextSelectionEnabled = true
                }
            };
            _rootPanel.Children.Add(quoteBox);
        }

        private void RenderVoiceMessage(string audioUrl, string durationSec, string transcript)
        {
            bool hasAudio = !string.IsNullOrWhiteSpace(audioUrl);
            int secNum = int.TryParse(durationSec, out int s) && s > 0 ? s : 0;
            double barWidth = Math.Clamp(136 + secNum * 4, 144, 248);

            var icon = new FontIcon
            {
                Glyph = "\uE768", // Play
                FontSize = 13,
                Foreground = TextBrush
            };
            var wave = new FontIcon
            {
                Glyph = "\uE767", // Volume / sound wave
                FontSize = 14,
                Foreground = TextBrush,
                Opacity = hasAudio ? 0.9 : 0.45
            };
            var durText = new TextBlock
            {
                Text = secNum > 0 ? $"{secNum}\"" : "语音",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            var stateText = new TextBlock
            {
                Text = hasAudio ? "点击播放" : "暂无音频",
                FontSize = 11,
                Foreground = TextBrush,
                Opacity = hasAudio ? 0.72 : 0.5,
                VerticalAlignment = VerticalAlignment.Center
            };

            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Grid.SetColumn(icon, 0);
            Grid.SetColumn(wave, 1);
            Grid.SetColumn(durText, 2);
            Grid.SetColumn(stateText, 3);
            stateText.HorizontalAlignment = HorizontalAlignment.Right;

            row.Children.Add(icon);
            row.Children.Add(wave);
            row.Children.Add(durText);
            row.Children.Add(stateText);

            var playBtn = new Button
            {
                MinWidth = barWidth,
                Padding = new Thickness(6, 4, 6, 4),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                IsEnabled = hasAudio,
                Content = row
            };
            ToolTipService.SetToolTip(playBtn, hasAudio ? "点击播放 / 停止语音 · 右键可转文字" : "点击顶部「刷新同步」尝试获取该条语音音频");

            _voicePlayButton = playBtn;
            _voicePlayIcon = icon;
            _voiceStateText = stateText;

            if (hasAudio)
            {
                string targetAudioUrl = audioUrl;
                playBtn.Click += async (_, _) =>
                {
                    stateText.Text = "加载中...";
                    var (ok, err) = await VoicePlaybackService.Shared.TogglePlayAsync(targetAudioUrl);
                    if (!ok && !string.IsNullOrEmpty(err))
                    {
                        stateText.Text = "播放失败";
                        ToolTipService.SetToolTip(playBtn, err);
                    }
                };
            }

            _rootPanel.Children.Add(playBtn);
            UpdateVoiceVisualState(VoicePlaybackService.Shared.CurrentUrl, VoicePlaybackService.Shared.IsPlaying);

            if (!string.IsNullOrWhiteSpace(transcript))
            {
                // WeChat-style clean transcript card directly below the voice bar
                var transBorder = new Border
                {
                    Margin = new Thickness(0, 2, 0, 0),
                    Padding = new Thickness(10, 7, 10, 7),
                    Background = IsOutgoing
                        ? new SolidColorBrush(AppServices.Settings.IsDarkMode
                            ? Color.FromArgb(46, 255, 255, 255)
                            : Color.FromArgb(140, 255, 255, 255))
                        : GetBrush("AppSurfaceAltBrush", Color.FromArgb(255, 244, 245, 247)),
                    BorderBrush = new SolidColorBrush(AppServices.Settings.IsDarkMode
                        ? Color.FromArgb(35, 255, 255, 255)
                        : Color.FromArgb(22, 0, 0, 0)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(7),
                    Child = BuildSelectableRichText(
                        transcript,
                        fontSize: 13,
                        foreground: IsOutgoing
                            ? TextBrush
                            : GetBrush("TextPrimaryBrush", Color.FromArgb(255, 31, 35, 41)))
                };
                _rootPanel.Children.Add(transBorder);
            }
        }

        private void RenderVideoFallbackCard(string raw, string videoUrl)
        {
            var durMatch = Regex.Match(raw, @"\[视频\s*(\d+""?)\]");
            string title = durMatch.Success ? $"视频 · {durMatch.Groups[1].Value}" : "微信视频";

            var row = new Grid { ColumnSpacing = 10, MinWidth = 176 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var iconBox = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(AppServices.Settings.IsDarkMode
                    ? Color.FromArgb(55, 255, 255, 255)
                    : Color.FromArgb(24, 0, 0, 0)),
                Child = new FontIcon
                {
                    Glyph = "\uE714", // Video camera icon
                    FontSize = 15,
                    Foreground = TextBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            textStack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush,
                IsTextSelectionEnabled = true
            });
            textStack.Children.Add(new TextBlock
            {
                Text = "视频记录（未缓存首帧封面）",
                FontSize = 11,
                Foreground = TextBrush,
                Opacity = 0.68
            });

            Grid.SetColumn(iconBox, 0);
            Grid.SetColumn(textStack, 1);
            row.Children.Add(iconBox);
            row.Children.Add(textStack);

            _rootPanel.Children.Add(new Border
            {
                Padding = new Thickness(4, 2, 4, 2),
                Child = row
            });
        }

        private void UpdateVoiceVisualState(string? playingUrl, bool isPlaying)
        {
            if (_voicePlayIcon is null || _voiceStateText is null || _voicePlayButton is null) return;
            string myUrl = (AudioUrl ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(myUrl)) return;

            bool active = isPlaying && string.Equals(playingUrl, myUrl, StringComparison.Ordinal);
            _voicePlayIcon.Glyph = active ? "\uE71A" : "\uE768"; // Stop vs Play
            _voiceStateText.Text = active ? "播放中..." : "点击播放";
        }
    }
}
