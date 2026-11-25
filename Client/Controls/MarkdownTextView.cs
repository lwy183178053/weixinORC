using System.Text.RegularExpressions;
using Client.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace Client.Controls
{
    /// <summary>
    /// Native WinUI 3 Markdown renderer supporting headings, bold/italic/strikethrough,
    /// inline code, fenced code blocks, bullet &amp; numbered lists, blockquotes, horizontal rules,
    /// links, and Markdown tables. Automatically adapts to Light (all palettes) &amp; Dark themes.
    /// </summary>
    public sealed class MarkdownTextView : UserControl
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(MarkdownTextView),
            new PropertyMetadata(string.Empty, OnVisualPropertyChanged));

        public static readonly DependencyProperty BaseFontSizeProperty = DependencyProperty.Register(
            nameof(BaseFontSize),
            typeof(double),
            typeof(MarkdownTextView),
            new PropertyMetadata(13.0, OnVisualPropertyChanged));

        private static readonly Regex HeadingRegex = new(@"^(#{1,6})\s+(.+)$", RegexOptions.Compiled);
        private static readonly Regex UnorderedListRegex = new(@"^(\s*)[-*+•]\s+(.+)$", RegexOptions.Compiled);
        private static readonly Regex OrderedListRegex = new(@"^(\s*)(\d+)[.)]\s+(.+)$", RegexOptions.Compiled);
        private static readonly Regex HorizontalRuleRegex = new(@"^\s*([-*_])(\s*\1){2,}\s*$", RegexOptions.Compiled);
        private static readonly Regex TableSeparatorRegex = new(@"^\s*\|?(\s*:?-{2,}:?\s*\|)+\s*:?-{2,}:?\s*\|?\s*$", RegexOptions.Compiled);

        private readonly StackPanel _rootPanel = new() { Spacing = 6 };
        private bool _themeSubscribed;

        public MarkdownTextView()
        {
            Content = _rootPanel;
            Loaded += MarkdownTextView_Loaded;
            Unloaded += MarkdownTextView_Unloaded;
        }

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public double BaseFontSize
        {
            get => (double)GetValue(BaseFontSizeProperty);
            set => SetValue(BaseFontSizeProperty, value);
        }

        private void MarkdownTextView_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_themeSubscribed)
            {
                AppServices.Settings.ThemeChanged += OnThemeChanged;
                _themeSubscribed = true;
            }
            Render();
        }

        private void MarkdownTextView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_themeSubscribed)
            {
                AppServices.Settings.ThemeChanged -= OnThemeChanged;
                _themeSubscribed = false;
            }
        }

        private void OnThemeChanged(bool _)
        {
            DispatcherQueue?.TryEnqueue(Render);
        }

        private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MarkdownTextView view)
            {
                view.Render();
            }
        }

        private static Brush GetBrush(string key, Windows.UI.Color fallback)
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
            string raw = Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw)) return;

            var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int i = 0;

            while (i < lines.Length)
            {
                string line = lines[i];
                string trimmed = line.Trim();

                if (string.IsNullOrEmpty(trimmed))
                {
                    i++;
                    continue;
                }

                // 1. Fenced code block ```
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    string lang = trimmed[3..].Trim();
                    var codeLines = new List<string>();
                    i++;
                    while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                    {
                        codeLines.Add(lines[i]);
                        i++;
                    }
                    if (i < lines.Length) i++; // skip closing ```
                    _rootPanel.Children.Add(BuildCodeBlock(lang, string.Join("\n", codeLines)));
                    continue;
                }

                // 2. Horizontal rule
                if (HorizontalRuleRegex.IsMatch(trimmed))
                {
                    _rootPanel.Children.Add(new Border
                    {
                        Height = 1,
                        Margin = new Thickness(0, 4, 0, 4),
                        Background = GetBrush("AppBorderBrush", Microsoft.UI.Colors.Gray),
                        Opacity = 0.7
                    });
                    i++;
                    continue;
                }

                // 3. Heading (# .. ######)
                var hMatch = HeadingRegex.Match(trimmed);
                if (hMatch.Success)
                {
                    int level = hMatch.Groups[1].Value.Length;
                    string content = hMatch.Groups[2].Value.Trim().TrimEnd('#').Trim();
                    _rootPanel.Children.Add(BuildHeading(level, content));
                    i++;
                    continue;
                }

                // 4. Markdown Table (header line with | + separator line)
                if (trimmed.Contains('|') && i + 1 < lines.Length && TableSeparatorRegex.IsMatch(lines[i + 1]))
                {
                    var headerCells = SplitTableRow(trimmed);
                    i += 2; // skip header & separator
                    var bodyRows = new List<List<string>>();
                    while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && lines[i].Contains('|'))
                    {
                        bodyRows.Add(SplitTableRow(lines[i]));
                        i++;
                    }
                    _rootPanel.Children.Add(BuildTable(headerCells, bodyRows));
                    continue;
                }

                // 5. Blockquote (> ...)
                if (trimmed.StartsWith(">", StringComparison.Ordinal))
                {
                    var quoteLines = new List<string>();
                    while (i < lines.Length && lines[i].TrimStart().StartsWith(">", StringComparison.Ordinal))
                    {
                        string q = lines[i].TrimStart()[1..];
                        if (q.StartsWith(' ')) q = q[1..];
                        quoteLines.Add(q);
                        i++;
                    }
                    _rootPanel.Children.Add(BuildBlockquote(string.Join("\n", quoteLines)));
                    continue;
                }

                // 6. Unordered / Ordered Lists
                if (UnorderedListRegex.IsMatch(line) || OrderedListRegex.IsMatch(line))
                {
                    var listStack = new StackPanel { Spacing = 4 };
                    while (i < lines.Length)
                    {
                        var ul = UnorderedListRegex.Match(lines[i]);
                        if (ul.Success)
                        {
                            int indent = ul.Groups[1].Value.Length / 2;
                            listStack.Children.Add(BuildListItem("•", ul.Groups[2].Value, indent));
                            i++;
                            continue;
                        }
                        var ol = OrderedListRegex.Match(lines[i]);
                        if (ol.Success)
                        {
                            int indent = ol.Groups[1].Value.Length / 2;
                            listStack.Children.Add(BuildListItem($"{ol.Groups[2].Value}.", ol.Groups[3].Value, indent));
                            i++;
                            continue;
                        }
                        break;
                    }
                    _rootPanel.Children.Add(listStack);
                    continue;
                }

                // 7. Normal Paragraph (coalesce contiguous plain text lines)
                var paraLines = new List<string>();
                while (i < lines.Length)
                {
                    string cur = lines[i];
                    string curTrim = cur.Trim();
                    if (string.IsNullOrEmpty(curTrim)
                        || curTrim.StartsWith("```", StringComparison.Ordinal)
                        || HeadingRegex.IsMatch(curTrim)
                        || HorizontalRuleRegex.IsMatch(curTrim)
                        || curTrim.StartsWith(">", StringComparison.Ordinal)
                        || UnorderedListRegex.IsMatch(cur)
                        || OrderedListRegex.IsMatch(cur)
                        || (curTrim.Contains('|') && i + 1 < lines.Length && TableSeparatorRegex.IsMatch(lines[i + 1])))
                    {
                        break;
                    }
                    paraLines.Add(curTrim);
                    i++;
                }

                if (paraLines.Count > 0)
                {
                    _rootPanel.Children.Add(BuildRichParagraph(string.Join("\n", paraLines), BaseFontSize, FontWeights.Normal));
                }
            }
        }

        private UIElement BuildHeading(int level, string text)
        {
            double size = level switch
            {
                1 => BaseFontSize + 4.5,
                2 => BaseFontSize + 2.5,
                3 => BaseFontSize + 1.0,
                _ => BaseFontSize
            };
            var rtb = BuildRichParagraph(text, size, FontWeights.SemiBold);
            rtb.Margin = new Thickness(0, level <= 2 ? 4 : 2, 0, 1);
            return rtb;
        }

        private UIElement BuildCodeBlock(string language, string code)
        {
            var stack = new StackPanel { Spacing = 4 };
            if (!string.IsNullOrWhiteSpace(language))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = language,
                    FontSize = Math.Max(10.5, BaseFontSize - 2),
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = GetBrush("TextMutedBrush", Microsoft.UI.Colors.Gray)
                });
            }

            stack.Children.Add(new TextBlock
            {
                Text = code,
                FontSize = Math.Max(11.5, BaseFontSize - 1),
                FontFamily = new FontFamily("Consolas"),
                Foreground = GetBrush("TextPrimaryBrush", Microsoft.UI.Colors.Black),
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap
            });

            return new Border
            {
                Padding = new Thickness(10, 8, 10, 8),
                Background = GetBrush("AppCanvasBgBrush", Microsoft.UI.Colors.WhiteSmoke),
                BorderBrush = GetBrush("AppBorderBrush", Microsoft.UI.Colors.LightGray),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Child = stack
            };
        }

        private UIElement BuildBlockquote(string content)
        {
            var rtb = BuildRichParagraph(content, BaseFontSize - 0.5, FontWeights.Normal);
            rtb.Foreground = GetBrush("TextSecondaryBrush", Microsoft.UI.Colors.DimGray);

            return new Border
            {
                Padding = new Thickness(10, 6, 10, 6),
                Background = GetBrush("AppCanvasBgBrush", Microsoft.UI.Colors.WhiteSmoke),
                BorderBrush = GetBrush("BrandPrimaryBrush", Microsoft.UI.Colors.MediumPurple),
                BorderThickness = new Thickness(3, 0, 0, 0),
                CornerRadius = new CornerRadius(0, 6, 6, 0),
                Child = rtb
            };
        }

        private UIElement BuildListItem(string marker, string content, int indentLevel)
        {
            var grid = new Grid
            {
                ColumnSpacing = 6,
                Margin = new Thickness(Math.Min(indentLevel, 4) * 14, 0, 0, 0)
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var bullet = new TextBlock
            {
                Text = marker,
                FontSize = BaseFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = GetBrush("BrandPrimaryBrush", Microsoft.UI.Colors.MediumPurple),
                MinWidth = marker == "•" ? 10 : 18,
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetColumn(bullet, 0);

            var rtb = BuildRichParagraph(content, BaseFontSize, FontWeights.Normal);
            Grid.SetColumn(rtb, 1);

            grid.Children.Add(bullet);
            grid.Children.Add(rtb);
            return grid;
        }

        private UIElement BuildTable(List<string> headers, List<List<string>> rows)
        {
            int colCount = headers.Count;
            foreach (var r in rows)
            {
                if (r.Count > colCount) colCount = r.Count;
            }
            if (colCount == 0) return new Border();

            var grid = new Grid();
            for (int c = 0; c < colCount; c++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            int totalRows = 1 + rows.Count;
            for (int r = 0; r < totalRows; r++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            var borderBrush = GetBrush("AppBorderBrush", Microsoft.UI.Colors.LightGray);
            var headerBg = GetBrush("AppCanvasBgBrush", Microsoft.UI.Colors.WhiteSmoke);

            for (int c = 0; c < colCount; c++)
            {
                string cellText = c < headers.Count ? headers[c] : string.Empty;
                var cell = new Border
                {
                    Padding = new Thickness(8, 6, 8, 6),
                    Background = headerBg,
                    BorderBrush = borderBrush,
                    BorderThickness = new Thickness(c == 0 ? 0 : 1, 0, 0, 1),
                    Child = BuildRichParagraph(cellText, Math.Max(11.5, BaseFontSize - 0.5), FontWeights.SemiBold)
                };
                Grid.SetRow(cell, 0);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }

            for (int r = 0; r < rows.Count; r++)
            {
                for (int c = 0; c < colCount; c++)
                {
                    string cellText = c < rows[r].Count ? rows[r][c] : string.Empty;
                    var cell = new Border
                    {
                        Padding = new Thickness(8, 5, 8, 5),
                        BorderBrush = borderBrush,
                        BorderThickness = new Thickness(c == 0 ? 0 : 1, 0, 0, r == rows.Count - 1 ? 0 : 1),
                        Child = BuildRichParagraph(cellText, Math.Max(11.5, BaseFontSize - 0.5), FontWeights.Normal)
                    };
                    Grid.SetRow(cell, r + 1);
                    Grid.SetColumn(cell, c);
                    grid.Children.Add(cell);
                }
            }

            return new Border
            {
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Child = grid
            };
        }

        private static List<string> SplitTableRow(string line)
        {
            string s = line.Trim();
            if (s.StartsWith('|')) s = s[1..];
            if (s.EndsWith('|')) s = s[..^1];
            return s.Split('|').Select(cell => cell.Trim()).ToList();
        }

        private RichTextBlock BuildRichParagraph(string text, double fontSize, FontWeight weight)
        {
            var rtb = new RichTextBlock
            {
                FontSize = fontSize,
                FontWeight = weight,
                LineHeight = fontSize + 7.5,
                Foreground = GetBrush("TextPrimaryBrush", Microsoft.UI.Colors.Black),
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap
            };

            var paragraph = new Paragraph();
            var splitLines = text.Split('\n');
            for (int idx = 0; idx < splitLines.Length; idx++)
            {
                if (idx > 0)
                {
                    paragraph.Inlines.Add(new LineBreak());
                }
                AppendInlines(paragraph.Inlines, splitLines[idx]);
            }

            rtb.Blocks.Add(paragraph);
            return rtb;
        }

        private void AppendInlines(InlineCollection target, string text)
        {
            int pos = 0;
            int len = text.Length;
            var brandBrush = GetBrush("BrandPrimaryBrush", Microsoft.UI.Colors.MediumPurple);

            while (pos < len)
            {
                // Inline code: `code`
                if (text[pos] == '`')
                {
                    int end = text.IndexOf('`', pos + 1);
                    if (end > pos + 1)
                    {
                        var span = new Span
                        {
                            FontFamily = new FontFamily("Consolas"),
                            Foreground = brandBrush
                        };
                        span.Inlines.Add(new Run { Text = text[(pos + 1)..end] });
                        target.Add(span);
                        pos = end + 1;
                        continue;
                    }
                }

                // Bold + Italic: ***text***
                if (pos + 2 < len && text[pos] == '*' && text[pos + 1] == '*' && text[pos + 2] == '*')
                {
                    int end = text.IndexOf("***", pos + 3, StringComparison.Ordinal);
                    if (end > pos + 3)
                    {
                        var bold = new Bold();
                        var italic = new Italic();
                        AppendInlines(italic.Inlines, text[(pos + 3)..end]);
                        bold.Inlines.Add(italic);
                        target.Add(bold);
                        pos = end + 3;
                        continue;
                    }
                }

                // Bold: **text** or __text__
                if (pos + 1 < len && ((text[pos] == '*' && text[pos + 1] == '*') || (text[pos] == '_' && text[pos + 1] == '_')))
                {
                    string delim = text.Substring(pos, 2);
                    int end = text.IndexOf(delim, pos + 2, StringComparison.Ordinal);
                    if (end > pos + 2)
                    {
                        var bold = new Bold();
                        AppendInlines(bold.Inlines, text[(pos + 2)..end]);
                        target.Add(bold);
                        pos = end + 2;
                        continue;
                    }
                }

                // Strikethrough: ~~text~~
                if (pos + 1 < len && text[pos] == '~' && text[pos + 1] == '~')
                {
                    int end = text.IndexOf("~~", pos + 2, StringComparison.Ordinal);
                    if (end > pos + 2)
                    {
                        target.Add(new Run
                        {
                            Text = text[(pos + 2)..end],
                            TextDecorations = TextDecorations.Strikethrough
                        });
                        pos = end + 2;
                        continue;
                    }
                }

                // Italic: *text* (only when next char is not whitespace)
                if (text[pos] == '*' && pos + 1 < len && !char.IsWhiteSpace(text[pos + 1]))
                {
                    int end = text.IndexOf('*', pos + 1);
                    if (end > pos + 1 && !char.IsWhiteSpace(text[end - 1]))
                    {
                        var italic = new Italic();
                        AppendInlines(italic.Inlines, text[(pos + 1)..end]);
                        target.Add(italic);
                        pos = end + 1;
                        continue;
                    }
                }

                // Markdown Link: [label](url)
                if (text[pos] == '[')
                {
                    int closeBracket = text.IndexOf(']', pos + 1);
                    if (closeBracket > pos + 1 && closeBracket + 1 < len && text[closeBracket + 1] == '(')
                    {
                        int closeParen = text.IndexOf(')', closeBracket + 2);
                        if (closeParen > closeBracket + 2)
                        {
                            string label = text[(pos + 1)..closeBracket];
                            string url = text[(closeBracket + 2)..closeParen].Trim();
                            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                            {
                                var link = new Hyperlink { NavigateUri = uri };
                                link.Inlines.Add(new Run { Text = label, Foreground = brandBrush });
                                target.Add(link);
                            }
                            else
                            {
                                target.Add(new Run { Text = label, Foreground = brandBrush });
                            }
                            pos = closeParen + 1;
                            continue;
                        }
                    }
                }

                // Plain text until next potential markdown trigger
                int next = pos + 1;
                while (next < len && text[next] is not ('`' or '*' or '_' or '~' or '['))
                {
                    next++;
                }
                target.Add(new Run { Text = text[pos..next] });
                pos = next;
            }
        }
    }
}
