using Client.Models;
using Client.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Client.Controls
{
    /// <summary>
    /// Codex-style minimal, collapsible tool execution trace block shown above AI assistant replies.
    /// Displays each automatically invoked tool call with status dot, icon, tool label, target pill, and result summary.
    /// </summary>
    public sealed class CodexToolTraceView : UserControl
    {
        public static readonly DependencyProperty TraceItemsProperty = DependencyProperty.Register(
            nameof(TraceItems), typeof(List<AiToolTraceDto>), typeof(CodexToolTraceView),
            new PropertyMetadata(null, OnVisualPropertyChanged));

        public static readonly DependencyProperty LegacySummaryProperty = DependencyProperty.Register(
            nameof(LegacySummary), typeof(string), typeof(CodexToolTraceView),
            new PropertyMetadata(string.Empty, OnVisualPropertyChanged));

        private readonly Border _container = new();
        private bool _isExpanded = true;
        private bool _hasExplicitToggle;
        private bool _subscribed;

        public CodexToolTraceView()
        {
            Content = _container;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        public List<AiToolTraceDto>? TraceItems
        {
            get => (List<AiToolTraceDto>?)GetValue(TraceItemsProperty);
            set => SetValue(TraceItemsProperty, value);
        }

        public string LegacySummary
        {
            get => (string)GetValue(LegacySummaryProperty);
            set => SetValue(LegacySummaryProperty, value);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_subscribed)
            {
                AppServices.Settings.ThemeChanged += OnThemeChanged;
                _subscribed = true;
            }
            Render();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_subscribed)
            {
                AppServices.Settings.ThemeChanged -= OnThemeChanged;
                _subscribed = false;
            }
        }

        private void OnThemeChanged(bool _) => DispatcherQueue?.TryEnqueue(Render);

        private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CodexToolTraceView view)
            {
                if (!view._hasExplicitToggle && view.TraceItems is { Count: > 0 } items)
                {
                    view._isExpanded = items.Count <= 4;
                }
                view.Render();
            }
        }

        private static Brush GetBrush(string key, Color fallback)
        {
            if (Application.Current?.Resources.TryGetValue(key, out var val) == true && val is Brush b)
            {
                return b;
            }
            return new SolidColorBrush(fallback);
        }

        private static string GetSkillGlyph(string skillId) => skillId switch
        {
            "search_chat_messages" => "\uE721",      // Search
            "get_customer_detail" => "\uE77B",       // Contact
            "list_customers" => "\uE716",            // People
            "get_group_messages" => "\uE902",        // Group chat
            "list_groups" => "\uE902",               // Groups
            "get_recent_conversations" => "\uE8BD",  // Messages
            "get_tag_customers_chats" => "\uE8EC",   // Tag
            "get_workspace_stats" => "\uE9D2",       // Bar chart
            "search_knowledge" => "\uE82D",          // Dictionary / book
            _ => "\uE945"                            // Lightning / tool
        };

        private static string GetFriendlyActionVerb(string skillId, string fallbackName) => skillId switch
        {
            "search_chat_messages" => "检索聊天",
            "get_customer_detail" => "查阅客户",
            "list_customers" => "筛选客户",
            "get_group_messages" => "查阅群聊",
            "list_groups" => "筛选群组",
            "get_recent_conversations" => "查看会话",
            "get_tag_customers_chats" => "按标签查阅",
            "get_workspace_stats" => "工作台统计",
            "search_knowledge" => "检索知识库",
            _ => string.IsNullOrWhiteSpace(fallbackName) ? "调用工具" : fallbackName
        };

        private void Render()
        {
            var items = TraceItems;
            string legacy = (LegacySummary ?? string.Empty).Trim();

            if ((items is null || items.Count == 0) && string.IsNullOrEmpty(legacy))
            {
                _container.Child = null;
                Visibility = Visibility.Collapsed;
                return;
            }

            Visibility = Visibility.Visible;
            _container.Background = GetBrush("AppSurfaceAltBrush", Color.FromArgb(255, 246, 247, 250));
            _container.BorderBrush = GetBrush("AppBorderSubtleBrush", Color.FromArgb(255, 229, 232, 239));
            _container.BorderThickness = new Thickness(1);
            _container.CornerRadius = new CornerRadius(10);
            _container.Padding = new Thickness(10, 7, 10, 7);

            // Legacy string fallback for previously persisted sessions
            if (items is null || items.Count == 0)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(new Border
                {
                    Width = 6,
                    Height = 6,
                    CornerRadius = new CornerRadius(3),
                    Background = GetBrush("SuccessTextBrush", Color.FromArgb(255, 22, 163, 74)),
                    VerticalAlignment = VerticalAlignment.Center
                });
                row.Children.Add(new TextBlock
                {
                    Text = legacy,
                    FontSize = 11.5,
                    Foreground = GetBrush("TextSecondaryBrush", Color.FromArgb(255, 95, 104, 118)),
                    TextWrapping = TextWrapping.Wrap
                });
                _container.Child = row;
                return;
            }

            var root = new StackPanel { Spacing = 6 };

            // Codex-style compact clickable header row
            var headerGrid = new Grid { ColumnSpacing = 7 };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var iconGlyph = new FontIcon
            {
                Glyph = "\uE756", // Command prompt / tool execution icon
                FontSize = 11,
                Foreground = GetBrush("BrandPrimaryBrush", Color.FromArgb(255, 124, 92, 252)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(iconGlyph, 0);

            int okCount = items.Count(x => x.Ok);
            string titleText = items.Count == 1
                ? $"已调用 1 项工具"
                : $"已调用 {items.Count} 项工具";

            var titleBlock = new TextBlock
            {
                Text = titleText,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = GetBrush("TextSecondaryBrush", Color.FromArgb(255, 78, 89, 105)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleBlock, 1);

            string previewTools = !_isExpanded
                ? string.Join(" · ", items.Select(x => GetFriendlyActionVerb(x.SkillId, x.SkillName)).Distinct().Take(3))
                : (okCount == items.Count ? "全部完成" : $"{okCount}/{items.Count} 成功");

            var metaBlock = new TextBlock
            {
                Text = previewTools,
                FontSize = 11,
                Foreground = GetBrush("TextMutedBrush", Color.FromArgb(255, 134, 144, 156)),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(metaBlock, 2);

            var chevron = new FontIcon
            {
                Glyph = _isExpanded ? "\uE70D" : "\uE76C",
                FontSize = 9.5,
                Foreground = GetBrush("TextMutedBrush", Color.FromArgb(255, 134, 144, 156)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(chevron, 3);

            headerGrid.Children.Add(iconGlyph);
            headerGrid.Children.Add(titleBlock);
            headerGrid.Children.Add(metaBlock);
            headerGrid.Children.Add(chevron);

            var toggleBtn = new Button
            {
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Content = headerGrid
            };
            toggleBtn.Click += (_, _) =>
            {
                _hasExplicitToggle = true;
                _isExpanded = !_isExpanded;
                Render();
            };
            root.Children.Add(toggleBtn);

            if (_isExpanded)
            {
                // Left timeline rail container
                var railBorder = new Border
                {
                    Margin = new Thickness(4, 0, 0, 1),
                    Padding = new Thickness(10, 2, 0, 2),
                    BorderBrush = GetBrush("AppBorderBrush", Color.FromArgb(255, 220, 224, 232)),
                    BorderThickness = new Thickness(1.5, 0, 0, 0)
                };

                var stepsStack = new StackPanel { Spacing = 5 };
                foreach (var item in items)
                {
                    stepsStack.Children.Add(BuildStepRow(item));
                }

                railBorder.Child = stepsStack;
                root.Children.Add(railBorder);
            }

            _container.Child = root;
        }

        private UIElement BuildStepRow(AiToolTraceDto item)
        {
            var grid = new Grid { ColumnSpacing = 7 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // status dot
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // skill icon
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // action verb
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // target + summary

            var dot = new Border
            {
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                VerticalAlignment = VerticalAlignment.Center,
                Background = item.Ok
                    ? GetBrush("SuccessTextBrush", Color.FromArgb(255, 22, 163, 74))
                    : GetBrush("DangerTextBrush", Color.FromArgb(255, 220, 38, 38))
            };
            Grid.SetColumn(dot, 0);

            var glyph = new FontIcon
            {
                Glyph = GetSkillGlyph(item.SkillId),
                FontSize = 11,
                Foreground = GetBrush("TextMutedBrush", Color.FromArgb(255, 134, 144, 156)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(glyph, 1);

            var verb = new TextBlock
            {
                Text = GetFriendlyActionVerb(item.SkillId, item.SkillName),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = GetBrush("TextPrimaryBrush", Color.FromArgb(255, 31, 35, 41)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(verb, 2);

            var detailGrid = new Grid { ColumnSpacing = 6, VerticalAlignment = VerticalAlignment.Center };
            detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (!string.IsNullOrWhiteSpace(item.Target))
            {
                var targetBadge = new Border
                {
                    Padding = new Thickness(5, 1, 5, 1),
                    Background = GetBrush("AppSurfaceBrush", Colors.White),
                    BorderBrush = GetBrush("AppBorderBrush", Color.FromArgb(255, 224, 228, 236)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = item.Target,
                        FontSize = 10.5,
                        Foreground = GetBrush("TextSecondaryBrush", Color.FromArgb(255, 78, 89, 105)),
                        MaxWidth = 112,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                };
                Grid.SetColumn(targetBadge, 0);
                detailGrid.Children.Add(targetBadge);
            }

            if (!string.IsNullOrWhiteSpace(item.Summary))
            {
                var summaryText = new TextBlock
                {
                    Text = item.Summary,
                    FontSize = 11,
                    Foreground = item.Ok
                        ? GetBrush("TextMutedBrush", Color.FromArgb(255, 134, 144, 156))
                        : GetBrush("DangerTextBrush", Color.FromArgb(255, 220, 38, 38)),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(summaryText, 1);
                detailGrid.Children.Add(summaryText);
            }

            Grid.SetColumn(detailGrid, 3);

            string tip = string.IsNullOrWhiteSpace(item.Target)
                ? $"{GetFriendlyActionVerb(item.SkillId, item.SkillName)}：{item.Summary}"
                : $"{GetFriendlyActionVerb(item.SkillId, item.SkillName)} [{item.Target}] · {item.Summary}";
            ToolTipService.SetToolTip(grid, tip);

            grid.Children.Add(dot);
            grid.Children.Add(glyph);
            grid.Children.Add(verb);
            grid.Children.Add(detailGrid);
            return grid;
        }
    }
}
