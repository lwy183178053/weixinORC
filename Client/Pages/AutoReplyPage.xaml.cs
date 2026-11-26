using System.Collections.ObjectModel;
using Client.Dialogs;
using Client.Models;
using Client.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Client.Pages
{
    public sealed partial class AutoReplyPage : Page
    {
        private readonly ObservableCollection<AutoReplyExampleItem> _examples = [];
        private readonly List<GroupDto> _activeGroups = [];
        private readonly ObservableCollection<AutoReplyExecutionRecord> _records = [];
        private readonly ObservableCollection<CompactLogRow> _diagLogs = [];

        private string _activeSubTab = "examples";
        private string? _editingExampleId;
        private bool _isInitializingUi = true;
        private bool _isTesting;

        public AutoReplyPage()
        {
            InitializeComponent();
            NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
            ExamplesListView.ItemsSource = _examples;
            RecordsListView.ItemsSource = _records;
            DiagLogsListView.ItemsSource = _diagLogs;
            Loaded += AutoReplyPage_Loaded;
            Unloaded += AutoReplyPage_Unloaded;
        }

        private async void AutoReplyPage_Loaded(object sender, RoutedEventArgs e)
        {
            AppServices.GroupAutoReply.EnsureInitialized();

            AppServices.Settings.ThemeChanged -= OnThemeChanged;
            AppServices.WeChat4Sync.AccountsUpdated -= OnWeChatAccountsUpdated;
            AppServices.GroupAutoReply.StateChanged -= OnAutoReplyStateChanged;
            AppServices.GroupAutoReply.ExecutionRecorded -= OnExecutionRecorded;
            AppServices.GroupAutoReply.GroupAutoReplyToggled -= OnGroupAutoReplyToggled;
            AppServices.Logs.LogAdded -= OnLogAdded;
            AppServices.Logs.LogsCleared -= OnLogsCleared;

            AppServices.Settings.ThemeChanged += OnThemeChanged;
            AppServices.WeChat4Sync.AccountsUpdated += OnWeChatAccountsUpdated;
            AppServices.GroupAutoReply.StateChanged += OnAutoReplyStateChanged;
            AppServices.GroupAutoReply.ExecutionRecorded += OnExecutionRecorded;
            AppServices.GroupAutoReply.GroupAutoReplyToggled += OnGroupAutoReplyToggled;
            AppServices.Logs.LogAdded += OnLogAdded;
            AppServices.Logs.LogsCleared += OnLogsCleared;

            LoadConfigIntoUi();
            ReloadRecords();
            ReloadDiagLogs();
            SelectSubTab(_activeSubTab);
            await RefreshTestGroupsAsync();
            UpdateHeaderStatus();
        }

        private void AutoReplyPage_Unloaded(object sender, RoutedEventArgs e)
        {
            AppServices.Settings.ThemeChanged -= OnThemeChanged;
            AppServices.WeChat4Sync.AccountsUpdated -= OnWeChatAccountsUpdated;
            AppServices.GroupAutoReply.StateChanged -= OnAutoReplyStateChanged;
            AppServices.GroupAutoReply.ExecutionRecorded -= OnExecutionRecorded;
            AppServices.GroupAutoReply.GroupAutoReplyToggled -= OnGroupAutoReplyToggled;
            AppServices.Logs.LogAdded -= OnLogAdded;
            AppServices.Logs.LogsCleared -= OnLogsCleared;
        }

        private void OnWeChatAccountsUpdated()
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                await RefreshTestGroupsAsync();
                UpdateHeaderStatus();
            });
        }

        private void OnThemeChanged(bool _)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateSubTabVisuals();
                UpdateHeaderStatus();
                ReloadDiagLogs();
            });
        }

        private void OnAutoReplyStateChanged()
        {
            DispatcherQueue.TryEnqueue(UpdateHeaderStatus);
        }

        private void OnExecutionRecorded(AutoReplyExecutionRecord record)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _records.Insert(0, record);
                while (_records.Count > 100) _records.RemoveAt(_records.Count - 1);
                UpdateRecordsEmptyState();
                UpdateHeaderStatus();
            });
        }

        private void OnGroupAutoReplyToggled(long groupId, bool enabled)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                var found = _activeGroups.FirstOrDefault(g => g.Id == groupId);
                if (found is not null)
                {
                    found.AutoReplyEnabled = enabled;
                }
                RebuildTestGroupCombo();
                UpdateHeaderStatus();
            });
        }

        private void LoadConfigIntoUi()
        {
            _isInitializingUi = true;
            try
            {
                var cfg = AppServices.GroupAutoReply.Config;
                GlobalEnabledToggle.IsOn = cfg.GlobalEnabled;
                ExtraTriggerNamesBox.Text = cfg.ExtraTriggerNames;
                BlockedKeywordsBox.Text = cfg.BlockedKeywords;
                CooldownSecondsBox.Text = cfg.CooldownSecondsPerGroup.ToString();
                MaxMessageAgeBox.Text = cfg.MaxMessageAgeSeconds.ToString();
                MaxReplyCharsBox.Text = cfg.MaxReplyChars.ToString();
                IgnoreAtAllCheck.IsChecked = cfg.IgnoreAtAll;
                PrefixMentionSenderCheck.IsChecked = cfg.PrefixMentionSender;
                CustomPromptBox.Text = cfg.CustomSystemPrompt;
                FallbackReplyBox.Text = cfg.FallbackReply;
                AutoSendAfterPasteCheck.IsChecked = cfg.AutoSendAfterPaste;
                PreOpenGroupWhileGeneratingCheck.IsChecked = cfg.PreOpenGroupWhileGenerating;
                ShowClickOverlayCheck.IsChecked = cfg.ShowClickOverlay;
                VerifyGroupTitleCheck.IsChecked = cfg.VerifyGroupTitleBeforePaste;
                RestoreClipboardCheck.IsChecked = cfg.RestoreClipboardAfterSend;
                SlowStableModeCheck.IsChecked = cfg.SlowStableMode;

                _examples.Clear();
                foreach (var ex in cfg.Examples)
                {
                    _examples.Add(ex);
                }
                UpdateExamplesHeader();
            }
            finally
            {
                _isInitializingUi = false;
            }
        }

        private void SaveAllConfigFieldsSilently()
        {
            var cfg = AppServices.GroupAutoReply.Config;
            cfg.GlobalEnabled = GlobalEnabledToggle.IsOn;
            ApplyTriggerRulesToConfig(cfg);
            ApplyPersonaToConfig(cfg);
            ApplyAutomationOptionsToConfig(cfg);
            cfg.Examples = _examples.ToList();
            AppServices.GroupAutoReply.SaveConfig();
            UpdateHeaderStatus();
        }

        private void ApplyTriggerRulesToConfig(AutoReplyConfigData cfg)
        {
            cfg.ExtraTriggerNames = ExtraTriggerNamesBox.Text.Trim();
            cfg.BlockedKeywords = BlockedKeywordsBox.Text.Trim();
            cfg.CooldownSecondsPerGroup = int.TryParse(CooldownSecondsBox.Text.Trim(), out int cd)
                ? Math.Clamp(cd, 0, 600) : 10;
            cfg.MaxMessageAgeSeconds = int.TryParse(MaxMessageAgeBox.Text.Trim(), out int age)
                ? Math.Clamp(age, 30, 1800) : 180;
            cfg.MaxReplyChars = int.TryParse(MaxReplyCharsBox.Text.Trim(), out int maxC)
                ? Math.Clamp(maxC, 40, 800) : 260;
            cfg.IgnoreAtAll = IgnoreAtAllCheck.IsChecked == true;
            cfg.PrefixMentionSender = PrefixMentionSenderCheck.IsChecked == true;

            CooldownSecondsBox.Text = cfg.CooldownSecondsPerGroup.ToString();
            MaxMessageAgeBox.Text = cfg.MaxMessageAgeSeconds.ToString();
            MaxReplyCharsBox.Text = cfg.MaxReplyChars.ToString();
        }

        private void ApplyPersonaToConfig(AutoReplyConfigData cfg)
        {
            cfg.CustomSystemPrompt = CustomPromptBox.Text.Trim();
            cfg.FallbackReply = FallbackReplyBox.Text.Trim();
        }

        private void ApplyAutomationOptionsToConfig(AutoReplyConfigData cfg)
        {
            cfg.AutoSendAfterPaste = AutoSendAfterPasteCheck.IsChecked == true;
            cfg.PreOpenGroupWhileGenerating = PreOpenGroupWhileGeneratingCheck.IsChecked == true;
            cfg.ShowClickOverlay = ShowClickOverlayCheck.IsChecked == true;
            cfg.VerifyGroupTitleBeforePaste = VerifyGroupTitleCheck.IsChecked == true;
            cfg.RestoreClipboardAfterSend = RestoreClipboardCheck.IsChecked == true;
            cfg.SlowStableMode = SlowStableModeCheck.IsChecked == true;
        }

        private void SaveTriggerRulesButton_Click(object sender, RoutedEventArgs e)
        {
            var cfg = AppServices.GroupAutoReply.Config;
            ApplyTriggerRulesToConfig(cfg);
            AppServices.GroupAutoReply.SaveConfig();
            AppServices.Logs.Info("REPLY",
                $"已保存触发与防骚扰规则：冷却={cfg.CooldownSecondsPerGroup}s，时限={cfg.MaxMessageAgeSeconds}s，字数上限={cfg.MaxReplyChars}，忽略@所有人={cfg.IgnoreAtAll}，回复@提问者={cfg.PrefixMentionSender}");
            FlashSavedHint(TriggerRulesSavedHint);
        }

        private void SavePersonaButton_Click(object sender, RoutedEventArgs e)
        {
            var cfg = AppServices.GroupAutoReply.Config;
            ApplyPersonaToConfig(cfg);
            AppServices.GroupAutoReply.SaveConfig();
            AppServices.Logs.Info("REPLY",
                $"已保存 AI 回复人设与兜底策略（人设长度={cfg.CustomSystemPrompt.Length} 字，兜底={(string.IsNullOrWhiteSpace(cfg.FallbackReply) ? "跳过" : "已配置")}）。");
            FlashSavedHint(PersonaSavedHint);
        }

        private void AutomationOptionCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializingUi) return;
            var cfg = AppServices.GroupAutoReply.Config;
            ApplyAutomationOptionsToConfig(cfg);
            AppServices.GroupAutoReply.SaveConfig();
            AppServices.Logs.Info("REPLY",
                $"已更新微信操控选项：自动发送={(cfg.AutoSendAfterPaste ? "开" : "仅粘贴")}，并行预开={(cfg.PreOpenGroupWhileGenerating ? "开" : "关")}，点击遮罩={(cfg.ShowClickOverlay ? "开" : "关")}，标题校验={(cfg.VerifyGroupTitleBeforePaste ? "开" : "关")}，慢速模式={(cfg.SlowStableMode ? "开" : "关")}");
        }

        private void FlashSavedHint(TextBlock hint)
        {
            hint.Visibility = Visibility.Visible;
            _ = Task.Run(async () =>
            {
                await Task.Delay(2200);
                DispatcherQueue.TryEnqueue(() => hint.Visibility = Visibility.Collapsed);
            });
        }

        private void GlobalEnabledToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializingUi) return;
            AppServices.GroupAutoReply.Config.GlobalEnabled = GlobalEnabledToggle.IsOn;
            AppServices.GroupAutoReply.SaveConfig();
            AppServices.Logs.Info("REPLY", GlobalEnabledToggle.IsOn ? "用户已全局开启群 @ 自动回复。" : "用户已全局暂停群 @ 自动回复。");
            UpdateHeaderStatus();
        }

        // ---- Sub-Tab Switching ----

        private void SubTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                SelectSubTab(tag);
            }
        }

        private void SelectSubTab(string tag)
        {
            _activeSubTab = tag;
            ExamplesTabView.Visibility = tag == "examples" ? Visibility.Visible : Visibility.Collapsed;
            SettingsTabView.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
            LogsTabView.Visibility = tag == "logs" ? Visibility.Visible : Visibility.Collapsed;
            UpdateSubTabVisuals();
        }

        private void UpdateSubTabVisuals()
        {
            var activeFg = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
            var inactiveFg = (Brush)Application.Current.Resources["TextSecondaryBrush"];
            var transparent = new SolidColorBrush(Colors.Transparent);

            SetTabStyle(TabExamplesIcon, TabExamplesText, TabExamplesLine, _activeSubTab == "examples", activeFg, inactiveFg, transparent);
            SetTabStyle(TabSettingsIcon, TabSettingsText, TabSettingsLine, _activeSubTab == "settings", activeFg, inactiveFg, transparent);
            SetTabStyle(TabLogsIcon, TabLogsText, TabLogsLine, _activeSubTab == "logs", activeFg, inactiveFg, transparent);
        }

        private static void SetTabStyle(FontIcon icon, TextBlock text, Border line, bool active, Brush activeFg, Brush inactiveFg, Brush transparent)
        {
            icon.Foreground = active ? activeFg : inactiveFg;
            text.Foreground = active ? activeFg : inactiveFg;
            text.FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            line.Background = active ? activeFg : transparent;
        }

        private void UpdateHeaderStatus()
        {
            var svc = AppServices.GroupAutoReply;
            int enabledCount = svc.GetEnabledGroupCount();
            bool globalOn = svc.Config.GlobalEnabled;

            if (svc.IsExecuting || svc.PendingQueueCount > 0)
            {
                StatusDot.Fill = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
                StatusBadge.Background = (Brush)Application.Current.Resources["BrandSoftBgBrush"];
                StatusBadgeText.Foreground = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
                StatusBadgeText.Text = svc.LiveStatusText;
            }
            else if (globalOn && enabledCount > 0)
            {
                StatusDot.Fill = (Brush)Application.Current.Resources["SuccessTextBrush"];
                StatusBadge.Background = (Brush)Application.Current.Resources["SuccessBgBrush"];
                StatusBadgeText.Foreground = (Brush)Application.Current.Resources["SuccessTextBrush"];
                StatusBadgeText.Text = $"监听中 · 已托管 {enabledCount} 个群";
            }
            else if (globalOn)
            {
                StatusDot.Fill = (Brush)Application.Current.Resources["WarningTextBrush"];
                StatusBadge.Background = (Brush)Application.Current.Resources["WarningBgBrush"];
                StatusBadgeText.Foreground = (Brush)Application.Current.Resources["WarningTextBrush"];
                StatusBadgeText.Text = "已开启 · 尚未勾选生效群组";
            }
            else
            {
                StatusDot.Fill = (Brush)Application.Current.Resources["TextMutedBrush"];
                StatusBadge.Background = (Brush)Application.Current.Resources["AppInputBgBrush"];
                StatusBadgeText.Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"];
                StatusBadgeText.Text = "全局已暂停";
            }
        }

        // ---- Q&A Examples CRUD ----

        private void UpdateExamplesHeader()
        {
            int enabled = _examples.Count(x => x.Enabled);
            ExamplesCountText.Text = $"共 {_examples.Count} 条示例 · {enabled} 条已启用";
            ExamplesListView.Visibility = _examples.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyExamplesHint.Visibility = _examples.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void NewExampleButton_Click(object sender, RoutedEventArgs e)
        {
            _editingExampleId = null;
            ExampleEditorTitleText.Text = "新增问答示例";
            EditorCategoryBox.Text = string.Empty;
            EditorMatchModeCombo.SelectedIndex = 0;
            EditorQuestionBox.Text = string.Empty;
            EditorAnswerBox.Text = string.Empty;
            ExampleEditorCard.Visibility = Visibility.Visible;
            EditorQuestionBox.Focus(FocusState.Programmatic);
        }

        private void EditExampleButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string id) return;
            var item = _examples.FirstOrDefault(x => x.Id == id);
            if (item is null) return;

            _editingExampleId = item.Id;
            ExampleEditorTitleText.Text = "编辑问答示例";
            EditorCategoryBox.Text = item.Category;
            EditorMatchModeCombo.SelectedIndex = item.DirectMatch ? 1 : 0;
            EditorQuestionBox.Text = item.QuestionPattern;
            EditorAnswerBox.Text = item.AnswerExample;
            ExampleEditorCard.Visibility = Visibility.Visible;
            EditorQuestionBox.Focus(FocusState.Programmatic);
        }

        private void CancelEditExampleButton_Click(object sender, RoutedEventArgs e)
        {
            _editingExampleId = null;
            ExampleEditorCard.Visibility = Visibility.Collapsed;
        }

        private void SaveExampleButton_Click(object sender, RoutedEventArgs e)
        {
            string question = EditorQuestionBox.Text.Trim();
            string answer = EditorAnswerBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(answer))
            {
                ShowBanner("请完整填写「典型问题 / 触发关键词」和「标准回答示例」。", isError: true);
                return;
            }

            string category = EditorCategoryBox.Text.Trim();
            bool direct = ((EditorMatchModeCombo.SelectedItem as ComboBoxItem)?.Tag as string) == "direct";

            if (!string.IsNullOrEmpty(_editingExampleId))
            {
                var existing = _examples.FirstOrDefault(x => x.Id == _editingExampleId);
                if (existing is not null)
                {
                    existing.Category = category;
                    existing.QuestionPattern = question;
                    existing.AnswerExample = answer;
                    existing.DirectMatch = direct;
                }
            }
            else
            {
                _examples.Insert(0, new AutoReplyExampleItem
                {
                    Enabled = true,
                    Category = category,
                    QuestionPattern = question,
                    AnswerExample = answer,
                    DirectMatch = direct
                });
            }

            _editingExampleId = null;
            ExampleEditorCard.Visibility = Visibility.Collapsed;
            UpdateExamplesHeader();
            AppServices.GroupAutoReply.Config.Examples = _examples.ToList();
            AppServices.GroupAutoReply.SaveConfig();
            ShowBanner("✓ 问答示例已保存。", isError: false);
        }

        private void ExampleEnabledCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox cb || cb.Tag is not string id) return;
            var item = _examples.FirstOrDefault(x => x.Id == id);
            if (item is null) return;

            item.Enabled = cb.IsChecked == true;
            UpdateExamplesHeader();
            AppServices.GroupAutoReply.Config.Examples = _examples.ToList();
            AppServices.GroupAutoReply.SaveConfig();
        }

        private void DeleteExampleButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string id) return;
            var item = _examples.FirstOrDefault(x => x.Id == id);
            if (item is null) return;

            _examples.Remove(item);
            if (_editingExampleId == id)
            {
                _editingExampleId = null;
                ExampleEditorCard.Visibility = Visibility.Collapsed;
            }
            UpdateExamplesHeader();
            AppServices.GroupAutoReply.Config.Examples = _examples.ToList();
            AppServices.GroupAutoReply.SaveConfig();
            ShowBanner("已删除该条问答示例。", isError: false);
        }

        private void LoadStarterTemplatesButton_Click(object sender, RoutedEventArgs e)
        {
            var starters = AppServices.GroupAutoReply.BuildStarterTemplates();
            int added = 0;
            foreach (var tpl in starters)
            {
                if (_examples.Any(x => x.QuestionPattern.Equals(tpl.QuestionPattern, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                _examples.Add(tpl);
                added++;
            }
            UpdateExamplesHeader();
            AppServices.GroupAutoReply.Config.Examples = _examples.ToList();
            AppServices.GroupAutoReply.SaveConfig();
            ShowBanner(added > 0 ? $"✓ 已载入 {added} 条常用问答模板，可按需修改内容。" : "常用示例模板均已存在。", isError: false);
        }

        // ---- Active Account Groups & Manual Test ----

        private async Task RefreshTestGroupsAsync()
        {
            if (!AppServices.Backend.IsAuthenticated) return;
            try
            {
                var allGroups = await AppServices.Backend.ListAllGroupsAsync(1000);
                AppServices.GroupAutoReply.SyncGroupsSnapshot(allGroups);

                string activeWxid = AppServices.WeChat4Sync.ActiveOwnerWxid;
                var scoped = string.IsNullOrWhiteSpace(activeWxid)
                    ? allGroups
                    : allGroups.Where(g =>
                        string.IsNullOrWhiteSpace(g.OwnerWxid) ||
                        string.Equals(g.OwnerWxid, activeWxid, StringComparison.OrdinalIgnoreCase)).ToList();

                _activeGroups.Clear();
                _activeGroups.AddRange(scoped);
                RebuildTestGroupCombo();
                UpdateHeaderStatus();
            }
            catch (Exception ex)
            {
                AppServices.Logs.Warn("REPLY", $"刷新当前微信群列表失败：{ex.Message}");
            }
        }

        private void RebuildTestGroupCombo()
        {
            long? prevSelectedTestId = (TestGroupCombo.SelectedItem as ComboBoxItem)?.Tag as long?;
            TestGroupCombo.Items.Clear();

            int targetIdx = 0;
            for (int i = 0; i < _activeGroups.Count; i++)
            {
                var g = _activeGroups[i];
                TestGroupCombo.Items.Add(new ComboBoxItem
                {
                    Tag = g.Id,
                    Content = g.AutoReplyEnabled ? $"● {g.DisplayName}" : g.DisplayName
                });
                if (prevSelectedTestId.HasValue && prevSelectedTestId.Value == g.Id)
                {
                    targetIdx = i;
                }
                else if (!prevSelectedTestId.HasValue && g.AutoReplyEnabled && targetIdx == 0)
                {
                    targetIdx = i;
                }
            }

            if (TestGroupCombo.Items.Count > 0)
            {
                TestGroupCombo.SelectedIndex = Math.Clamp(targetIdx, 0, TestGroupCombo.Items.Count - 1);
            }
        }

        private async void RunTestButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isTesting) return;
            if ((TestGroupCombo.SelectedItem as ComboBoxItem)?.Tag is not long groupId)
            {
                ShowBanner("请先在「群组档案」导入当前微信的群聊后再进行测试。", isError: true);
                return;
            }
            var target = _activeGroups.FirstOrDefault(g => g.Id == groupId);
            if (target is null)
            {
                ShowBanner("未找到所选群聊，请重新进入本页面后再试。", isError: true);
                return;
            }

            SaveAllConfigFieldsSilently();
            _isTesting = true;
            RunTestButton.IsEnabled = false;
            bool sendNow = TestSendImmediatelyCheck.IsChecked == true;
            string question = TestQuestionBox.Text.Trim();

            try
            {
                ShowBanner($"正在执行端到端测试（群「{target.DisplayName}」）：生成回答并模拟微信界面操作，按 ESC 可随时中断...", isError: false);
                var rec = await AppServices.GroupAutoReply.RunManualTestAsync(target, question, sendNow);
                ShowBanner(rec.IsSuccess
                    ? $"✓ 测试完成（{rec.StatusLabel}，耗时 {rec.ElapsedMs} ms）：{rec.Detail}"
                    : $"✕ 测试未完成（{rec.StatusLabel}）：{rec.Detail}",
                    isError: !rec.IsSuccess);
            }
            catch (Exception ex)
            {
                ShowBanner($"测试执行异常：{ex.Message}", isError: true);
            }
            finally
            {
                _isTesting = false;
                RunTestButton.IsEnabled = true;
            }
        }

        // ---- Records & Diagnostic Logs ----

        private void ReloadRecords()
        {
            _records.Clear();
            foreach (var rec in AppServices.GroupAutoReply.GetRecentRecords())
            {
                _records.Add(rec);
            }
            UpdateRecordsEmptyState();
        }

        private void UpdateRecordsEmptyState()
        {
            RecordsCountText.Text = $"{_records.Count} 条";
            RecordsListView.Visibility = _records.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyRecordsHint.Visibility = _records.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ClearRecordsButton_Click(object sender, RoutedEventArgs e)
        {
            AppServices.GroupAutoReply.ClearRecentRecords();
            _records.Clear();
            UpdateRecordsEmptyState();
        }

        private void OnLogAdded(object? sender, AppLogEntry entry)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!MatchesDiagFilter(entry)) return;
                var row = new CompactLogRow(entry);
                _diagLogs.Add(row);
                while (_diagLogs.Count > 500) _diagLogs.RemoveAt(0);
                if (_activeSubTab == "logs")
                {
                    DiagLogsListView.ScrollIntoView(row);
                }
            });
        }

        private void OnLogsCleared(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() => _diagLogs.Clear());
        }

        private void ReloadDiagLogs()
        {
            if (DiagLogsListView is null) return;
            _diagLogs.Clear();
            var filtered = AppServices.Logs.GetSnapshot().Where(MatchesDiagFilter).TakeLast(500).ToList();
            foreach (var entry in filtered)
            {
                _diagLogs.Add(new CompactLogRow(entry));
            }
            if (_diagLogs.Count > 0)
            {
                DiagLogsListView.ScrollIntoView(_diagLogs[^1]);
            }
        }

        private bool MatchesDiagFilter(AppLogEntry entry)
        {
            string lvl = (DiagLevelCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
            bool levelOk = lvl switch
            {
                "info" => entry.Level == AppLogLevel.Info,
                "ok" => entry.Level == AppLogLevel.Success,
                "warn" => entry.Level == AppLogLevel.Warning,
                "err" => entry.Level == AppLogLevel.Error,
                _ => true
            };
            if (!levelOk) return false;

            string mod = (DiagModuleCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "AUTOREPLY_CHAIN";
            if (mod != "all")
            {
                bool modOk = mod == "AUTOREPLY_CHAIN"
                    ? entry.Module is "REPLY" or "AUTO" or "OCR" or "CLICK" or "WIN" or "AI" or "WX4"
                    : string.Equals(entry.Module, mod, StringComparison.OrdinalIgnoreCase);
                if (!modOk) return false;
            }

            string q = DiagSearchBox?.Text.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(q)) return true;

            return entry.Message.Contains(q, StringComparison.OrdinalIgnoreCase)
                || entry.Module.Contains(q, StringComparison.OrdinalIgnoreCase);
        }

        private void DiagLogFilter_Changed(object sender, SelectionChangedEventArgs e) => ReloadDiagLogs();

        private void DiagSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ReloadDiagLogs();

        private void OpenLogFolderButton_Click(object sender, RoutedEventArgs e)
        {
            AppServices.Logs.OpenLogDirectory();
        }

        private void CopyDiagLogsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_diagLogs.Count == 0) return;
            try
            {
                var pkg = new DataPackage();
                pkg.SetText(string.Join(Environment.NewLine, _diagLogs.Select(x => x.Entry.FormattedLine)));
                Clipboard.SetContent(pkg);
                ShowBanner($"✓ 已复制 {_diagLogs.Count} 条诊断日志到剪贴板。", isError: false);
            }
            catch (Exception ex)
            {
                ShowBanner($"复制日志失败：{ex.Message}", isError: true);
            }
        }

        private void ClearDiagLogsButton_Click(object sender, RoutedEventArgs e)
        {
            AppServices.Logs.Clear();
            _diagLogs.Clear();
        }

        private static void ShowBanner(string message, bool isError)
        {
            WorkbenchPage.Current?.ShowToast(message, isError);
        }
    }
}
