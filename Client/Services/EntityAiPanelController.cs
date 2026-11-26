using System.Collections.ObjectModel;
using Client.Controls;
using Client.Models;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace Client.Services
{
    public sealed class EntityAiPanelControls
    {
        public required Border SummaryErrorBanner { get; init; }
        public required TextBlock SummaryErrorText { get; init; }
        public required TextBlock SummaryNoticeText { get; init; }
        public required UIElement SummaryEmptyPanel { get; init; }
        public required UIElement SummaryResultScroll { get; init; }
        public required UIElement SummaryFooterBar { get; init; }
        public required UIElement SummaryLoadingPanel { get; init; }
        public required ProgressRing SummarySpinner { get; init; }
        public required MarkdownTextView SummaryMarkdownView { get; init; }
        public required TextBlock SummaryTimeText { get; init; }

        public required UIElement ChatEmptyPanel { get; init; }
        public required ListView ChatListView { get; init; }
        public required Button ClearChatButton { get; init; }
        public required UIElement PendingImagesScroll { get; init; }
        public required ItemsControl PendingImagesItems { get; init; }
        public required TextBox ChatInputBox { get; init; }
        public required Button ChatSendButton { get; init; }
        public required Button ChatAttachButton { get; init; }
        public required UIElement ChatSendIcon { get; init; }
        public required ProgressRing ChatSendBusyRing { get; init; }
    }

    public sealed class EntityAiPanelHooks
    {
        public required Func<long?> GetActiveEntityId { get; init; }
        public required Func<long, IEntityAiWorkspace> LoadWorkspace { get; init; }
        public required Action<IEntityAiWorkspace> SaveWorkspace { get; init; }
        public required Func<long, Task<ApiResult<AiCustomerSummaryResponse>>> SummarizeAsync { get; init; }
        public required Func<long, IEnumerable<AiChatTurnDto>, string, IEnumerable<string>, Task<ApiResult<AiChatResponse>>> ChatAsync { get; init; }
        public required Action UpdateContextLabels { get; init; }
        public string PendingThinkingText { get; init; } = "正在查阅记录并思考回复...";
        public string LogPrefix { get; init; } = "AI";
    }

    /// <summary>
    /// Shared controller for the right-pane AI Summary &amp; AI Chat workspace used identically
    /// by both CustomersPage and GroupsPage, eliminating ~600 lines of duplicated code.
    /// </summary>
    public sealed class EntityAiPanelController
    {
        private readonly EntityAiPanelControls _ui;
        private readonly EntityAiPanelHooks _hooks;
        private readonly ObservableCollection<AiChatMessageItem> _chatMessages = [];
        private readonly ObservableCollection<AiImageAttachment> _pendingImages = [];

        private IEntityAiWorkspace? _currentWorkspace;
        private bool _isSummaryBusy;
        private bool _isChatBusy;

        public EntityAiPanelController(EntityAiPanelControls controls, EntityAiPanelHooks hooks)
        {
            _ui = controls;
            _hooks = hooks;
            _ui.ChatListView.ItemsSource = _chatMessages;
            _ui.PendingImagesItems.ItemsSource = _pendingImages;
        }

        public void LoadForEntity(long entityId)
        {
            _currentWorkspace = _hooks.LoadWorkspace(entityId);
            _pendingImages.Clear();
            UpdatePendingImagesUi();
            _ui.SummaryErrorBanner.Visibility = Visibility.Collapsed;
            _ui.SummaryNoticeText.Visibility = Visibility.Collapsed;
            RenderSummaryState();

            _chatMessages.Clear();
            foreach (var msg in _currentWorkspace.Messages)
            {
                _chatMessages.Add(msg);
            }
            UpdateChatEmptyState();
            _hooks.UpdateContextLabels();
        }

        public void ClearForNoEntity()
        {
            _currentWorkspace = null;
            _chatMessages.Clear();
            _pendingImages.Clear();
            UpdatePendingImagesUi();
            UpdateChatEmptyState();
        }

        public void RenderSummaryState()
        {
            bool hasSummary = _currentWorkspace is not null && !string.IsNullOrWhiteSpace(_currentWorkspace.SummaryText);
            _ui.SummaryLoadingPanel.Visibility = Visibility.Collapsed;
            _ui.SummarySpinner.IsActive = false;
            _ui.SummaryEmptyPanel.Visibility = hasSummary ? Visibility.Collapsed : Visibility.Visible;
            _ui.SummaryResultScroll.Visibility = hasSummary ? Visibility.Visible : Visibility.Collapsed;
            _ui.SummaryFooterBar.Visibility = hasSummary ? Visibility.Visible : Visibility.Collapsed;

            if (hasSummary && _currentWorkspace is not null)
            {
                _ui.SummaryMarkdownView.Text = _currentWorkspace.SummaryText;
                _ui.SummaryTimeText.Text = string.IsNullOrWhiteSpace(_currentWorkspace.SummaryUpdatedAt)
                    ? string.Empty
                    : $"更新于 {_currentWorkspace.SummaryUpdatedAt}";
            }
        }

        public async Task GenerateSummaryAsync()
        {
            long? entityId = _hooks.GetActiveEntityId();
            if (!entityId.HasValue || _isSummaryBusy) return;
            long targetId = entityId.Value;

            _isSummaryBusy = true;
            _ui.SummaryErrorBanner.Visibility = Visibility.Collapsed;
            _ui.SummaryNoticeText.Visibility = Visibility.Collapsed;
            _ui.SummaryEmptyPanel.Visibility = Visibility.Collapsed;
            _ui.SummaryResultScroll.Visibility = Visibility.Collapsed;
            _ui.SummaryFooterBar.Visibility = Visibility.Collapsed;
            _ui.SummaryLoadingPanel.Visibility = Visibility.Visible;
            _ui.SummarySpinner.IsActive = true;

            try
            {
                var res = await _hooks.SummarizeAsync(targetId);
                if (_hooks.GetActiveEntityId() != targetId) return;

                if (res.IsSuccess && res.Data is not null && !string.IsNullOrWhiteSpace(res.Data.Summary))
                {
                    _currentWorkspace ??= _hooks.LoadWorkspace(targetId);
                    _currentWorkspace.SummaryText = res.Data.Summary;
                    _currentWorkspace.SummaryUpdatedAt = DateTime.Now.ToString("MM-dd HH:mm");
                    _currentWorkspace.SummaryContextMeta = $"{res.Data.MessageCount} 条消息";
                    _hooks.SaveWorkspace(_currentWorkspace);
                }
                else
                {
                    _ui.SummaryErrorText.Text = $"生成失败：{res.ErrorMessage}";
                    _ui.SummaryErrorBanner.Visibility = Visibility.Visible;
                    _ui.SummaryNoticeText.Foreground = (Brush)Application.Current.Resources["DangerTextBrush"];
                    _ui.SummaryNoticeText.Text = $"生成失败：{res.ErrorMessage}";
                    _ui.SummaryNoticeText.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                if (_hooks.GetActiveEntityId() == targetId)
                {
                    _ui.SummaryErrorText.Text = $"生成异常：{ex.Message}";
                    _ui.SummaryErrorBanner.Visibility = Visibility.Visible;
                }
            }
            finally
            {
                _isSummaryBusy = false;
                if (_hooks.GetActiveEntityId() == targetId)
                {
                    RenderSummaryState();
                }
            }
        }

        public void CopySummary()
        {
            if (_currentWorkspace is null || string.IsNullOrWhiteSpace(_currentWorkspace.SummaryText)) return;
            var (ok, err) = ChatPageSharedHelper.CopyTextToClipboard(_currentWorkspace.SummaryText);
            if (ok)
            {
                _ui.SummaryNoticeText.Foreground = (Brush)Application.Current.Resources["SuccessTextBrush"];
                _ui.SummaryNoticeText.Text = "已复制到剪贴板";
            }
            else
            {
                _ui.SummaryNoticeText.Foreground = (Brush)Application.Current.Resources["DangerTextBrush"];
                _ui.SummaryNoticeText.Text = $"复制失败：{err}";
            }
            _ui.SummaryNoticeText.Visibility = Visibility.Visible;
        }

        public void CopyChatMessage(object sender)
        {
            if ((sender as FrameworkElement)?.DataContext is not AiChatMessageItem item) return;
            var (ok, err) = ChatPageSharedHelper.CopyTextToClipboard(item.Content);
            if (!ok)
            {
                AppServices.Logs.Warn("AI", $"复制{_hooks.LogPrefix}回复失败：{err}");
            }
        }

        public async Task UploadImageAsync()
        {
            if (_isChatBusy || _pendingImages.Count >= AiImageAttachmentHelper.MaxAttachmentsPerMessage) return;
            string? path = Win32WindowHelper.PickImageFilePath(AppServices.MainWindowHandle);
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var item = await AiImageAttachmentHelper.TryCreateFromFileAsync(path);
                if (item is not null)
                {
                    _pendingImages.Add(item);
                    UpdatePendingImagesUi();
                }
            }
            catch (Exception ex)
            {
                AppServices.Logs.Warn("AI", $"加载图片失败：{ex.Message}");
            }
        }

        public async Task HandleInputPasteAsync(TextControlPasteEventArgs e)
        {
            try
            {
                var content = Clipboard.GetContent();
                if (content.Contains(StandardDataFormats.Bitmap))
                {
                    e.Handled = true;
                    await PasteClipboardImageAsync();
                }
            }
            catch
            {
            }
        }

        public async Task PasteClipboardImageAsync()
        {
            if (_isChatBusy || _pendingImages.Count >= AiImageAttachmentHelper.MaxAttachmentsPerMessage) return;
            try
            {
                var item = await AiImageAttachmentHelper.TryCreateFromClipboardAsync();
                if (item is not null)
                {
                    _pendingImages.Add(item);
                    UpdatePendingImagesUi();
                }
            }
            catch (Exception ex)
            {
                AppServices.Logs.Warn("AI", $"粘贴图片失败：{ex.Message}");
            }
        }

        public void RemovePendingImage(object sender)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                var target = _pendingImages.FirstOrDefault(x => x.Id == id);
                if (target is not null)
                {
                    _pendingImages.Remove(target);
                    UpdatePendingImagesUi();
                }
            }
        }

        public async Task HandleInputKeyDownAsync(KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter) return;
            var shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
            if ((shiftState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down) return;

            e.Handled = true;
            await SendMessageAsync(_ui.ChatInputBox.Text);
        }

        public async Task SendMessageAsync(string rawText)
        {
            long? entityId = _hooks.GetActiveEntityId();
            string prompt = rawText.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            var attachedUris = _pendingImages.Select(x => x.DataUri).ToList();
            if (!entityId.HasValue || (string.IsNullOrEmpty(prompt) && attachedUris.Count == 0) || _isChatBusy) return;
            long targetId = entityId.Value;

            _isChatBusy = true;
            _ui.ChatSendButton.IsEnabled = false;
            _ui.ChatAttachButton.IsEnabled = false;
            _ui.ChatSendIcon.Visibility = Visibility.Collapsed;
            _ui.ChatSendBusyRing.Visibility = Visibility.Visible;
            _ui.ChatSendBusyRing.IsActive = true;
            _ui.ClearChatButton.IsEnabled = false;

            _ui.ChatInputBox.Text = string.Empty;
            _pendingImages.Clear();
            UpdatePendingImagesUi();

            AiChatMessageItem? pendingBubble = null;
            try
            {
                var workspace = _currentWorkspace ?? _hooks.LoadWorkspace(targetId);
                _currentWorkspace = workspace;

                var historyTurns = workspace.Messages
                    .TakeLast(12)
                    .Select(m => new AiChatTurnDto { Role = m.Role, Content = m.Content })
                    .ToList();

                var userMsg = new AiChatMessageItem
                {
                    Role = "user",
                    Content = prompt,
                    Images = attachedUris,
                    Timestamp = DateTime.Now.ToString("HH:mm")
                };
                workspace.Messages.Add(userMsg);
                _chatMessages.Add(userMsg);

                pendingBubble = new AiChatMessageItem
                {
                    Role = "assistant",
                    IsPending = true,
                    Content = _hooks.PendingThinkingText
                };
                _chatMessages.Add(pendingBubble);

                UpdateChatEmptyState();
                _ui.ChatListView.ScrollIntoView(pendingBubble);
                _hooks.SaveWorkspace(workspace);

                var res = await _hooks.ChatAsync(targetId, historyTurns, prompt, attachedUris);

                if (pendingBubble is not null)
                {
                    _chatMessages.Remove(pendingBubble);
                    pendingBubble = null;
                }

                if (_hooks.GetActiveEntityId() != targetId) return;

                string replyText = res.IsSuccess && res.Data is not null && !string.IsNullOrWhiteSpace(res.Data.Reply)
                    ? res.Data.Reply
                    : $"请求失败：{res.ErrorMessage}";

                List<AiToolTraceDto> traceList = res.IsSuccess && res.Data?.ToolTrace is { Count: > 0 }
                    ? res.Data.ToolTrace
                    : [];

                var reply = new AiChatMessageItem
                {
                    Role = "assistant",
                    Content = replyText,
                    ToolTrace = traceList,
                    Timestamp = DateTime.Now.ToString("HH:mm")
                };
                workspace.Messages.Add(reply);
                _chatMessages.Add(reply);
                _ui.ChatListView.ScrollIntoView(reply);
                _hooks.SaveWorkspace(workspace);
            }
            catch (Exception ex)
            {
                if (pendingBubble is not null)
                {
                    _chatMessages.Remove(pendingBubble);
                }
                AppServices.Logs.Error("AI", $"{_hooks.LogPrefix}对话异常：{ex.Message}");
            }
            finally
            {
                _isChatBusy = false;
                _ui.ChatSendButton.IsEnabled = true;
                _ui.ChatAttachButton.IsEnabled = true;
                _ui.ChatSendBusyRing.IsActive = false;
                _ui.ChatSendBusyRing.Visibility = Visibility.Collapsed;
                _ui.ChatSendIcon.Visibility = Visibility.Visible;
                UpdateChatEmptyState();
            }
        }

        public void ClearChat()
        {
            if (!_hooks.GetActiveEntityId().HasValue || _currentWorkspace is null) return;
            _currentWorkspace.Messages.Clear();
            _chatMessages.Clear();
            _hooks.SaveWorkspace(_currentWorkspace);
            UpdateChatEmptyState();
        }

        private void UpdateChatEmptyState()
        {
            bool hasMsgs = _chatMessages.Count > 0;
            _ui.ChatEmptyPanel.Visibility = hasMsgs ? Visibility.Collapsed : Visibility.Visible;
            _ui.ChatListView.Visibility = hasMsgs ? Visibility.Visible : Visibility.Collapsed;
            _ui.ClearChatButton.IsEnabled = hasMsgs && !_isChatBusy;
        }

        private void UpdatePendingImagesUi()
        {
            _ui.PendingImagesScroll.Visibility = _pendingImages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
