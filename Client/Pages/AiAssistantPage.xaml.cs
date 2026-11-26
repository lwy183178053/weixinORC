using System.Collections.ObjectModel;
using Client.Models;
using Client.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace Client.Pages
{
    public sealed partial class AiAssistantPage : Page
    {
        private readonly ObservableCollection<AgentSessionData> _sessions = [];
        private readonly ObservableCollection<AiChatMessageItem> _messages = [];
        private readonly ObservableCollection<AiImageAttachment> _pendingImages = [];

        private AgentSessionData? _activeSession;
        private bool _hasLoadedOnce;
        private bool _isBusy;
        private bool _suppressSelectionChanged;

        public AiAssistantPage()
        {
            InitializeComponent();
            NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
            SessionsListView.ItemsSource = _sessions;
            AgentChatListView.ItemsSource = _messages;
            PendingImagesItemsControl.ItemsSource = _pendingImages;

            Loaded += AiAssistantPage_Loaded;
        }

        private async void AiAssistantPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_hasLoadedOnce) return;
            _hasLoadedOnce = true;
            LoadLocalSessions();
            await CheckServerAiConfiguredAsync();
        }

        private void LoadLocalSessions()
        {
            var saved = AppServices.AiWorkspace.LoadAgentSessions();
            _sessions.Clear();
            foreach (var s in saved)
            {
                _sessions.Add(s);
            }

            if (_sessions.Count == 0)
            {
                var initial = new AgentSessionData
                {
                    Title = "新对话",
                    UpdatedAt = DateTime.Now.ToString("MM-dd HH:mm")
                };
                _sessions.Add(initial);
                PersistSessions();
            }

            string lastSessionId = AppServices.Settings.Current.LastAgentSessionId;
            var initialTarget = !string.IsNullOrWhiteSpace(lastSessionId)
                ? _sessions.FirstOrDefault(s => string.Equals(s.Id, lastSessionId, StringComparison.Ordinal))
                : null;
            SelectSession(initialTarget ?? _sessions[0]);
            if (initialTarget is not null)
            {
                SessionsListView.ScrollIntoView(initialTarget);
            }
            UpdateSessionCount();
        }

        private void SelectSession(AgentSessionData session)
        {
            _activeSession = session;
            if (!string.Equals(AppServices.Settings.Current.LastAgentSessionId, session.Id, StringComparison.Ordinal))
            {
                AppServices.Settings.Current.LastAgentSessionId = session.Id;
                AppServices.Settings.Save();
            }
            _suppressSelectionChanged = true;
            SessionsListView.SelectedItem = session;
            _suppressSelectionChanged = false;

            ActiveSessionTitleText.Text = string.IsNullOrWhiteSpace(session.Title) ? "新对话" : session.Title;

            _messages.Clear();
            foreach (var m in session.Messages)
            {
                _messages.Add(m);
            }

            UpdateChatVisibility();
            if (_messages.Count > 0)
            {
                AgentChatListView.ScrollIntoView(_messages[^1]);
            }
        }

        private void PersistSessions()
        {
            AppServices.AiWorkspace.SaveAgentSessions(_sessions.ToList());
            UpdateSessionCount();
        }

        private void UpdateSessionCount()
        {
            SessionCountText.Text = _sessions.Count.ToString();
        }

        private void UpdateChatVisibility()
        {
            bool hasMsgs = _messages.Count > 0;
            StarterPanel.Visibility = hasMsgs ? Visibility.Collapsed : Visibility.Visible;
            AgentChatListView.Visibility = hasMsgs ? Visibility.Visible : Visibility.Collapsed;
            ClearSessionButton.IsEnabled = hasMsgs && !_isBusy;
        }

        private void UpdatePendingImagesUi()
        {
            bool hasImgs = _pendingImages.Count > 0;
            PendingImagesScroll.Visibility = hasImgs ? Visibility.Visible : Visibility.Collapsed;
            AttachmentStatusText.Visibility = hasImgs ? Visibility.Visible : Visibility.Collapsed;
            if (hasImgs)
            {
                AttachmentStatusText.Text = $"已添加 {_pendingImages.Count}/{AiImageAttachmentHelper.MaxAttachmentsPerMessage} 张图片";
            }
        }

        private async Task CheckServerAiConfiguredAsync()
        {
            try
            {
                var res = await AppServices.Backend.GetAiStatusAsync();
                bool configured = res.IsSuccess && res.Data is not null && res.Data.Configured;
                UnconfiguredHintBanner.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
            }
            catch
            {
            }
        }

        private void NewSessionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            var session = new AgentSessionData
            {
                Title = "新对话",
                UpdatedAt = DateTime.Now.ToString("MM-dd HH:mm")
            };
            _sessions.Insert(0, session);
            PersistSessions();
            SelectSession(session);
            AgentInputBox.Focus(FocusState.Programmatic);
        }

        private void SessionsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionChanged || _isBusy) return;
            if (SessionsListView.SelectedItem is AgentSessionData session && session != _activeSession)
            {
                SelectSession(session);
            }
        }

        private async void RenameSessionMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            if ((sender as FrameworkElement)?.DataContext is not AgentSessionData session) return;
            if (!AppServices.TryBeginModalDialog()) return;

            try
            {
                var inputBox = new TextBox
                {
                    Text = session.Title,
                    PlaceholderText = "输入对话名称",
                    MaxLength = 40,
                    Style = Application.Current.Resources["SoftTextBoxStyle"] as Style
                };

                var dialog = new ContentDialog
                {
                    Title = "重命名对话",
                    Content = inputBox,
                    PrimaryButtonText = "保存",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot,
                    RequestedTheme = AppServices.Settings.CurrentElementTheme
                };

                inputBox.Loaded += (_, _) =>
                {
                    inputBox.Focus(FocusState.Programmatic);
                    inputBox.SelectAll();
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    string nextTitle = inputBox.Text.Trim();
                    if (!string.IsNullOrEmpty(nextTitle))
                    {
                        session.Title = nextTitle;
                        if (session == _activeSession)
                        {
                            ActiveSessionTitleText.Text = nextTitle;
                        }
                        RefreshSessionRowInList(session);
                        PersistSessions();
                    }
                }
            }
            catch (Exception ex)
            {
                AppServices.Logs.Warn("AI", $"重命名会话失败：{ex.Message}");
            }
            finally
            {
                AppServices.EndModalDialog();
            }
        }

        private void DeleteSessionMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is AgentSessionData session)
            {
                DeleteSession(session);
            }
        }

        private void DeleteSessionInlineButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is AgentSessionData session)
            {
                DeleteSession(session);
            }
        }

        private void DeleteSession(AgentSessionData session)
        {
            if (_isBusy) return;
            bool wasActive = session == _activeSession;
            _sessions.Remove(session);

            if (_sessions.Count == 0)
            {
                var fresh = new AgentSessionData
                {
                    Title = "新对话",
                    UpdatedAt = DateTime.Now.ToString("MM-dd HH:mm")
                };
                _sessions.Add(fresh);
                SelectSession(fresh);
            }
            else if (wasActive)
            {
                SelectSession(_sessions[0]);
            }

            PersistSessions();
        }

        private void ClearSessionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession is null || _isBusy) return;
            _activeSession.Messages.Clear();
            _activeSession.Title = "新对话";
            _activeSession.UpdatedAt = DateTime.Now.ToString("MM-dd HH:mm");
            _messages.Clear();
            ActiveSessionTitleText.Text = _activeSession.Title;
            RefreshSessionRowInList(_activeSession);
            PersistSessions();
            UpdateChatVisibility();
        }

        // ---- Image Attachment (Local Picker & Clipboard Ctrl+V) ----

        private async void UploadImageMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy || _pendingImages.Count >= AiImageAttachmentHelper.MaxAttachmentsPerMessage) return;
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

        private async void PasteImageMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await TryAttachClipboardImageAsync();
        }

        private async void AgentInputBox_Paste(object sender, TextControlPasteEventArgs e)
        {
            try
            {
                var content = Clipboard.GetContent();
                if (content.Contains(StandardDataFormats.Bitmap))
                {
                    e.Handled = true;
                    await TryAttachClipboardImageAsync();
                }
            }
            catch
            {
            }
        }

        private async Task TryAttachClipboardImageAsync()
        {
            if (_isBusy || _pendingImages.Count >= AiImageAttachmentHelper.MaxAttachmentsPerMessage) return;
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

        private void RemovePendingImage_Click(object sender, RoutedEventArgs e)
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

        // ---- Messaging ----

        private async void StarterPromptCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string prompt && !string.IsNullOrWhiteSpace(prompt))
            {
                await SendPromptAsync(prompt);
            }
        }

        private async void SendAgentButton_Click(object sender, RoutedEventArgs e)
        {
            await SendPromptAsync(AgentInputBox.Text);
        }

        private async void AgentInputBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter) return;
            var shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
            if ((shiftState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down) return;

            e.Handled = true;
            await SendPromptAsync(AgentInputBox.Text);
        }

        private async Task SendPromptAsync(string rawPrompt)
        {
            string prompt = rawPrompt.Trim();
            var attachedUris = _pendingImages.Select(x => x.DataUri).ToList();
            if ((string.IsNullOrEmpty(prompt) && attachedUris.Count == 0) || _isBusy) return;

            if (_activeSession is null)
            {
                var fresh = new AgentSessionData { Title = "新对话" };
                _sessions.Insert(0, fresh);
                SelectSession(fresh);
            }
            var session = _activeSession!;

            _isBusy = true;
            SendAgentButton.IsEnabled = false;
            AttachMenuButton.IsEnabled = false;
            SendAgentIcon.Visibility = Visibility.Collapsed;
            SendAgentBusyRing.Visibility = Visibility.Visible;
            SendAgentBusyRing.IsActive = true;
            ClearSessionButton.IsEnabled = false;

            AgentInputBox.Text = string.Empty;
            _pendingImages.Clear();
            UpdatePendingImagesUi();

            AiChatMessageItem? pendingBubble = null;
            try
            {
                var historyTurns = session.Messages
                    .TakeLast(14)
                    .Select(m => new AiChatTurnDto { Role = m.Role, Content = m.Content })
                    .ToList();

                var userMsg = new AiChatMessageItem
                {
                    Role = "user",
                    Content = prompt,
                    Images = attachedUris,
                    Timestamp = DateTime.Now.ToString("HH:mm")
                };

                if (session.Messages.Count == 0 || session.Title == "新对话")
                {
                    string autoTitle = string.IsNullOrEmpty(prompt) ? "图片分析" : (prompt.Length > 16 ? prompt[..16] + "…" : prompt);
                    session.Title = autoTitle;
                    ActiveSessionTitleText.Text = autoTitle;
                }
                session.UpdatedAt = DateTime.Now.ToString("MM-dd HH:mm");
                session.Messages.Add(userMsg);
                _messages.Add(userMsg);

                pendingBubble = new AiChatMessageItem
                {
                    Role = "assistant",
                    IsPending = true,
                    Content = "正在调用工具检索并生成回复..."
                };
                _messages.Add(pendingBubble);

                MoveSessionToTop(session);
                PersistSessions();
                UpdateChatVisibility();
                AgentChatListView.ScrollIntoView(pendingBubble);

                var res = await AppServices.Backend.AgentAiChatAsync(historyTurns, prompt, attachedUris);

                if (pendingBubble is not null)
                {
                    _messages.Remove(pendingBubble);
                    pendingBubble = null;
                }

                string replyText;
                string traceText = string.Empty;
                List<AiToolTraceDto> traceList = [];

                if (res.IsSuccess && res.Data is not null && !string.IsNullOrWhiteSpace(res.Data.Reply))
                {
                    replyText = res.Data.Reply;
                    if (res.Data.ToolTrace is { Count: > 0 })
                    {
                        traceList = res.Data.ToolTrace;
                        traceText = ChatPageSharedHelper.FormatToolTraceSummary(res.Data.ToolTrace);
                    }
                }
                else
                {
                    replyText = $"请求失败：{res.ErrorMessage}";
                }

                var assistantMsg = new AiChatMessageItem
                {
                    Role = "assistant",
                    Content = replyText,
                    ToolTrace = traceList,
                    ToolTraceSummary = traceText,
                    Timestamp = DateTime.Now.ToString("HH:mm")
                };

                session.UpdatedAt = DateTime.Now.ToString("MM-dd HH:mm");
                session.Messages.Add(assistantMsg);
                if (session == _activeSession)
                {
                    _messages.Add(assistantMsg);
                    AgentChatListView.ScrollIntoView(assistantMsg);
                }

                RefreshSessionRowInList(session);
                PersistSessions();
            }
            catch (Exception ex)
            {
                if (pendingBubble is not null)
                {
                    _messages.Remove(pendingBubble);
                }
                AppServices.Logs.Error("AI", $"AI 助手对话异常：{ex.Message}");
            }
            finally
            {
                _isBusy = false;
                SendAgentButton.IsEnabled = true;
                AttachMenuButton.IsEnabled = true;
                SendAgentBusyRing.IsActive = false;
                SendAgentBusyRing.Visibility = Visibility.Collapsed;
                SendAgentIcon.Visibility = Visibility.Visible;
                UpdateChatVisibility();
            }
        }

        private void MoveSessionToTop(AgentSessionData session)
        {
            int idx = _sessions.IndexOf(session);
            if (idx > 0)
            {
                _suppressSelectionChanged = true;
                _sessions.RemoveAt(idx);
                _sessions.Insert(0, session);
                SessionsListView.SelectedItem = session;
                _suppressSelectionChanged = false;
            }
            else
            {
                RefreshSessionRowInList(session);
            }
        }

        private void RefreshSessionRowInList(AgentSessionData session)
        {
            int idx = _sessions.IndexOf(session);
            if (idx < 0) return;
            _suppressSelectionChanged = true;
            _sessions.RemoveAt(idx);
            _sessions.Insert(idx, session);
            if (session == _activeSession)
            {
                SessionsListView.SelectedItem = session;
            }
            _suppressSelectionChanged = false;
        }

        private void CopyMessageMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not AiChatMessageItem item) return;
            try
            {
                var pkg = new DataPackage();
                pkg.SetText(item.Content);
                Clipboard.SetContent(pkg);
            }
            catch (Exception ex)
            {
                AppServices.Logs.Warn("AI", $"复制消息失败：{ex.Message}");
            }
        }
    }
}
