using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Client.Models
{
    public sealed class AutoReplyExamplePayload
    {
        [JsonPropertyName("category")]
        public string Category { get; set; } = string.Empty;

        [JsonPropertyName("question_pattern")]
        public string QuestionPattern { get; set; } = string.Empty;

        [JsonPropertyName("answer_example")]
        public string AnswerExample { get; set; } = string.Empty;
    }

    public sealed class AutoReplyExampleItem : INotifyPropertyChanged
    {
        private bool _enabled = true;
        private string _category = string.Empty;
        private string _questionPattern = string.Empty;
        private string _answerExample = string.Empty;
        private bool _directMatch;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled != value)
                {
                    _enabled = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CardOpacity));
                }
            }
        }

        public string Category
        {
            get => _category;
            set
            {
                if (_category != value)
                {
                    _category = value ?? string.Empty;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CategoryDisplay));
                }
            }
        }

        public string QuestionPattern
        {
            get => _questionPattern;
            set
            {
                if (_questionPattern != value)
                {
                    _questionPattern = value ?? string.Empty;
                    OnPropertyChanged();
                }
            }
        }

        public string AnswerExample
        {
            get => _answerExample;
            set
            {
                if (_answerExample != value)
                {
                    _answerExample = value ?? string.Empty;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// When true and the incoming message matches keywords in QuestionPattern,
        /// replies with AnswerExample directly without waiting for the LLM.
        /// When false (default), injects this pair as a high-priority few-shot example for AI.
        /// </summary>
        public bool DirectMatch
        {
            get => _directMatch;
            set
            {
                if (_directMatch != value)
                {
                    _directMatch = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(MatchModeLabel));
                }
            }
        }

        [JsonIgnore]
        public string CategoryDisplay => string.IsNullOrWhiteSpace(Category) ? "通用" : Category;

        [JsonIgnore]
        public string MatchModeLabel => DirectMatch ? "关键词直出" : "AI 参考示例";

        [JsonIgnore]
        public double CardOpacity => Enabled ? 1.0 : 0.56;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class AutoReplyConfigData
    {
        public bool GlobalEnabled { get; set; } = true;
        public bool AutoSendAfterPaste { get; set; } = true;
        public bool ShowClickOverlay { get; set; } = true;
        public bool VerifyGroupTitleBeforePaste { get; set; } = true;
        public bool IgnoreAtAll { get; set; } = true;
        public bool PrefixMentionSender { get; set; } = true;
        public bool RestoreClipboardAfterSend { get; set; } = true;
        public bool SlowStableMode { get; set; } = false;
        public int CooldownSecondsPerGroup { get; set; } = 15;
        public int MaxMessageAgeSeconds { get; set; } = 180;
        public int MaxReplyChars { get; set; } = 260;
        public string ExtraTriggerNames { get; set; } = string.Empty;
        public string CustomSystemPrompt { get; set; } =
            "你是专业、亲切、高效的群内客服助理。请用自然简洁的中文直接回答群友问题，语气友善礼貌，避免官腔和长篇大论。";
        public string FallbackReply { get; set; } = string.Empty;
        public string BlockedKeywords { get; set; } = "转账,汇款,验证码,密码,银行卡";
        public List<AutoReplyExampleItem> Examples { get; set; } = [];
    }

    public sealed class AutoReplyExecutionRecord
    {
        public DateTime Timestamp { get; init; } = DateTime.Now;
        public long GroupId { get; init; }
        public string GroupName { get; init; } = string.Empty;
        public string SenderName { get; init; } = string.Empty;
        public string TriggerMessage { get; init; } = string.Empty;
        public string GeneratedReply { get; init; } = string.Empty;
        public string SourceMode { get; init; } = "AI 示例生成";
        public string StatusLabel { get; init; } = "已发送";
        public bool IsSuccess { get; init; }
        public bool IsSkipped { get; init; }
        public string Detail { get; init; } = string.Empty;
        public long ElapsedMs { get; init; }

        public string TimeLabel => Timestamp.ToString("HH:mm:ss");
        public string HeaderLine => $"{GroupName} · {SenderName}";
        public string MetaLine => ElapsedMs > 0 ? $"{SourceMode} · {ElapsedMs} ms" : SourceMode;
        public Visibility ReplyVisibility => string.IsNullOrWhiteSpace(GeneratedReply) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility DetailVisibility => string.IsNullOrWhiteSpace(Detail) ? Visibility.Collapsed : Visibility.Visible;

        public Brush StatusBgBrush => (Brush)Application.Current.Resources[
            IsSuccess ? "SuccessBgBrush" : (IsSkipped ? "WarningBgBrush" : "DangerBgBrush")];

        public Brush StatusFgBrush => (Brush)Application.Current.Resources[
            IsSuccess ? "SuccessTextBrush" : (IsSkipped ? "WarningTextBrush" : "DangerTextBrush")];
    }
}
