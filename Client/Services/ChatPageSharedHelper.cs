using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Client.Dialogs;
using Client.Models;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace Client.Services
{
    /// <summary>
    /// Shared UI &amp; media helpers used by CustomersPage, GroupsPage, and AiAssistantPage:
    /// - ScrollViewer discovery &amp; near-bottom detection (non-disruptive incremental sync)
    /// - Controlled-concurrency avatar &amp; chat image hydration (up to 4 parallel HTTP downloads)
    /// - Unified bubble context-menu configuration, image viewer launch, clipboard copy, voice transcription
    /// - Unified right-panel 3-tab visual state &amp; AI tool-trace summary formatting
    /// </summary>
    public static class ChatPageSharedHelper
    {
        private sealed class ScrollWatcherToken
        {
            public Action? OnNearBottom { get; set; }
            public double ThresholdPx { get; set; }
        }

        private static readonly ConditionalWeakTable<ScrollViewer, ScrollWatcherToken> AttachedScrollWatchers = new();

        public static ScrollViewer? FindScrollViewer(DependencyObject? root)
        {
            if (root is null) return null;
            if (root is ScrollViewer sv) return sv;

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var found = FindScrollViewer(child);
                if (found is not null) return found;
            }
            return null;
        }

        public static bool IsListNearBottom(ListView listView, double thresholdPx = 120.0)
        {
            var sv = FindScrollViewer(listView);
            if (sv is null) return true;
            if (sv.ScrollableHeight <= 4.0) return true;
            return (sv.ScrollableHeight - sv.VerticalOffset) <= thresholdPx;
        }

        public static void AttachScrollNearBottomWatcher(ListView listView, Action onNearBottom, double thresholdPx = 120.0)
        {
            var sv = FindScrollViewer(listView);
            if (sv is null) return;

            if (AttachedScrollWatchers.TryGetValue(sv, out var existing))
            {
                existing.OnNearBottom = onNearBottom;
                existing.ThresholdPx = thresholdPx;
                return;
            }

            var token = new ScrollWatcherToken
            {
                OnNearBottom = onNearBottom,
                ThresholdPx = thresholdPx
            };
            AttachedScrollWatchers.Add(sv, token);
            sv.ViewChanged += (sender, _) =>
            {
                if (sender is ScrollViewer viewer &&
                    AttachedScrollWatchers.TryGetValue(viewer, out var t) &&
                    (viewer.ScrollableHeight <= 4.0 || (viewer.ScrollableHeight - viewer.VerticalOffset) <= t.ThresholdPx))
                {
                    t.OnNearBottom?.Invoke();
                }
            };
        }

        /// <summary>
        /// Downloads avatars with bounded concurrency (default 4) off the UI thread,
        /// then decodes and applies BitmapImages safely on the provided DispatcherQueue.
        /// </summary>
        public static async Task HydrateAvatarsConcurrentlyAsync<T>(
            IReadOnlyList<T> items,
            Func<T, string?> getAvatarUrl,
            Func<T, bool> hasImage,
            Action<T, BitmapImage> applyOnUiThread,
            DispatcherQueue dispatcher,
            CancellationToken ct,
            int maxConcurrency = 4,
            string logLabel = "头像")
        {
            if (items.Count == 0) return;

            var pending = new List<(T Item, string Url)>();
            foreach (var item in items)
            {
                if (hasImage(item)) continue;
                string? url = getAvatarUrl(item);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    pending.Add((item, url));
                }
            }
            if (pending.Count == 0) return;

            using var gate = new SemaphoreSlim(Math.Max(1, maxConcurrency));
            var tasks = pending.Select(async entry =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    if (ct.IsCancellationRequested || hasImage(entry.Item)) return;
                    byte[]? bytes = await AppServices.Backend.DownloadAvatarBytesAsync(entry.Url, ct);
                    if (bytes is null || bytes.Length == 0 || ct.IsCancellationRequested) return;

                    await RunOnUiAsync(dispatcher, async () =>
                    {
                        if (ct.IsCancellationRequested || hasImage(entry.Item)) return;
                        var img = await AvatarImageService.BytesToBitmapImageAsync(bytes);
                        if (img is not null && !ct.IsCancellationRequested)
                        {
                            applyOnUiThread(entry.Item, img);
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    AppServices.Logs.Warn("UI", $"加载{logLabel}异常：{ex.Message}");
                }
                finally
                {
                    gate.Release();
                }
            });

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>
        /// Downloads chat bubble images with bounded concurrency (default 4) off the UI thread,
        /// then decodes BitmapImages safely on the provided DispatcherQueue.
        /// </summary>
        public static async Task HydrateChatBubbleImagesConcurrentlyAsync(
            IReadOnlyList<ChatBubbleItem> bubbles,
            DispatcherQueue dispatcher,
            CancellationToken ct,
            int maxConcurrency = 4,
            string logLabel = "聊天图片")
        {
            if (bubbles.Count == 0) return;

            var pending = bubbles
                .Where(b => !string.IsNullOrWhiteSpace(b.ImageUrl) && b.BubbleImage is null)
                .ToList();
            if (pending.Count == 0) return;

            using var gate = new SemaphoreSlim(Math.Max(1, maxConcurrency));
            var tasks = pending.Select(async bubble =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    if (ct.IsCancellationRequested || bubble.BubbleImage is not null) return;
                    byte[]? bytes = await AppServices.Backend.DownloadChatImageBytesAsync(bubble.ImageUrl, ct);
                    if (bytes is null || bytes.Length == 0 || ct.IsCancellationRequested) return;

                    await RunOnUiAsync(dispatcher, async () =>
                    {
                        if (ct.IsCancellationRequested || bubble.BubbleImage is not null) return;
                        var img = await ChatImageHelper.BytesToBitmapImageAsync(bytes);
                        if (img is not null && !ct.IsCancellationRequested)
                        {
                            bubble.BubbleImageBytes = bytes;
                            bubble.BubbleImage = img;
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    AppServices.Logs.Warn("UI", $"加载{logLabel}异常：{ex.Message}");
                }
                finally
                {
                    gate.Release();
                }
            });

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static Task RunOnUiAsync(DispatcherQueue dispatcher, Func<Task> action)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    await action();
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
            {
                tcs.TrySetResult(false);
            }
            return tcs.Task;
        }

        public static void ConfigureBubbleContextFlyout(object sender)
        {
            if (sender is not MenuFlyout flyout ||
                flyout.Target is not FrameworkElement target ||
                target.DataContext is not ChatBubbleItem item)
            {
                return;
            }

            foreach (var mi in flyout.Items.OfType<MenuFlyoutItem>())
            {
                if (mi.Tag as string == "VoiceTranscribe")
                {
                    mi.Visibility = item.VoiceTranscribeMenuVisibility;
                    mi.Text = VoiceTranscriptionService.HasSavedTranscript(item.Content) ? "重新转文字" : "语音转文字";
                }
            }
        }

        public static async Task OpenBubbleImageViewerAsync(ChatBubbleItem item, string? entityDisplayName, string defaultImageKind)
        {
            byte[]? bytes = item.BubbleImageBytes;
            if ((bytes is null || bytes.Length == 0) && !string.IsNullOrWhiteSpace(item.ImageUrl))
            {
                bytes = await AppServices.Backend.DownloadChatImageBytesAsync(item.ImageUrl);
                if (bytes is { Length: > 0 })
                {
                    item.BubbleImageBytes = bytes;
                }
            }

            if (bytes is { Length: > 0 })
            {
                string kind = item.IsVideoMessage ? "视频封面" : defaultImageKind;
                string title = !string.IsNullOrWhiteSpace(entityDisplayName)
                    ? $"{entityDisplayName} · {kind}"
                    : $"{kind}查看器";
                ImageViewerWindow.ShowImage(bytes, title);
            }
        }

        public static async Task<string> CopyBubbleToClipboardAsync(ChatBubbleItem item)
        {
            try
            {
                var pkg = new DataPackage();
                if (!string.IsNullOrWhiteSpace(item.Content))
                {
                    pkg.SetText(item.Content);
                    Clipboard.SetContent(pkg);
                    return "已复制该条消息内容。";
                }
                if (item.BubbleImageBytes is { Length: > 0 } bytes)
                {
                    var stream = new InMemoryRandomAccessStream();
                    await stream.WriteAsync(bytes.AsBuffer());
                    stream.Seek(0);
                    pkg.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
                    Clipboard.SetContent(pkg);
                    return "已复制该条消息图片。";
                }
                return "该条消息暂无可复制的内容。";
            }
            catch (Exception ex)
            {
                return $"复制失败：{ex.Message}";
            }
        }

        public static (bool Ok, string ErrorMessage) CopyTextToClipboard(string text)
        {
            try
            {
                var pkg = new DataPackage();
                pkg.SetText(text);
                Clipboard.SetContent(pkg);
                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public static async Task<string> TranscribeVoiceBubbleAsync(
            ChatBubbleItem item,
            Func<string, Task<(bool Ok, string ErrorMessage)>>? persistMergedContentAsync)
        {
            if (string.IsNullOrWhiteSpace(item.AudioUrl))
            {
                return "该条语音暂无音频文件，请先点击顶部「刷新同步」。";
            }

            var res = await VoiceTranscriptionService.TranscribeAsync(item.AudioUrl);
            if (!res.Success || string.IsNullOrWhiteSpace(res.Text))
            {
                return res.ErrorMessage;
            }

            string mergedContent = VoiceTranscriptionService.MergeTranscriptIntoContent(item.Content, res.Text);
            item.Content = mergedContent;

            if (item.Id > 0 && persistMergedContentAsync is not null)
            {
                var (ok, err) = await persistMergedContentAsync(mergedContent);
                return ok
                    ? $"✓ 语音转文字已完成并永久保存（{res.EngineLabel}）。"
                    : $"已识别文字，但保存到服务端失败：{err}";
            }

            return $"✓ 语音转文字已完成（{res.EngineLabel}）。";
        }

        public static void ApplyRightTabVisuals(
            string activeTab,
            FontIcon profileIcon, TextBlock profileText, Border profileLine,
            FontIcon summaryIcon, TextBlock summaryText, Border summaryLine,
            FontIcon chatIcon, TextBlock chatText, Border chatLine)
        {
            var activeFg = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
            var inactiveFg = (Brush)Application.Current.Resources["TextSecondaryBrush"];
            var transparent = new SolidColorBrush(Colors.Transparent);

            ApplyTabVisual(profileIcon, profileText, profileLine, activeTab == "profile", activeFg, inactiveFg, transparent);
            ApplyTabVisual(summaryIcon, summaryText, summaryLine, activeTab == "summary", activeFg, inactiveFg, transparent);
            ApplyTabVisual(chatIcon, chatText, chatLine, activeTab == "aichat", activeFg, inactiveFg, transparent);
        }

        private static void ApplyTabVisual(
            FontIcon icon, TextBlock text, Border line,
            bool active, Brush activeFg, Brush inactiveFg, Brush transparent)
        {
            icon.Foreground = active ? activeFg : inactiveFg;
            text.Foreground = active ? activeFg : inactiveFg;
            text.FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            line.Background = active ? activeFg : transparent;
        }

        public static string FormatToolTraceSummary(IReadOnlyList<AiToolTraceDto>? trace)
        {
            if (trace is not { Count: > 0 }) return string.Empty;
            var labels = trace.Select(t =>
                string.IsNullOrWhiteSpace(t.Summary) ? t.SkillName : $"{t.SkillName}（{t.Summary}）");
            return "已调用工具：" + string.Join(" · ", labels);
        }
    }
}
