using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Client.Models
{
    public sealed class ChatBubbleItem : INotifyPropertyChanged
    {
        private bool _isSelectionMode;
        private bool _isSelected;
        private string _senderName = string.Empty;
        private string _content = string.Empty;
        private string _imageUrl = string.Empty;
        private string _audioUrl = string.Empty;
        private string _videoUrl = string.Empty;
        private ImageSource? _bubbleImage;
        private byte[]? _bubbleImageBytes;
        private ImageSource? _senderAvatar;
        private string _timeLabel = string.Empty;
        private bool _isOutgoing;

        public Guid LocalKey { get; } = Guid.NewGuid();
        public long Id { get; init; }
        public string SenderInitial { get; init; } = "客";

        public ImageSource? SenderAvatar
        {
            get => _senderAvatar;
            set
            {
                if (!ReferenceEquals(_senderAvatar, value))
                {
                    _senderAvatar = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SenderAvatarVisibility));
                    OnPropertyChanged(nameof(SenderInitialVisibility));
                }
            }
        }

        public string SenderName
        {
            get => _senderName;
            set
            {
                if (_senderName != value)
                {
                    _senderName = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SenderNameVisibility));
                }
            }
        }

        public string Content
        {
            get => _content;
            set
            {
                if (_content != value)
                {
                    _content = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsVideoMessage));
                    OnPropertyChanged(nameof(IsVoiceMessage));
                    OnPropertyChanged(nameof(VoiceTranscribeMenuVisibility));
                    OnPropertyChanged(nameof(VideoDurationBadge));
                    OnPropertyChanged(nameof(VideoPlayOverlayVisibility));
                    OnPropertyChanged(nameof(ContentVisibility));
                    OnPropertyChanged(nameof(ContentOrAudioVisibility));
                }
            }
        }

        public string ImageUrl
        {
            get => _imageUrl;
            set
            {
                if (_imageUrl != value)
                {
                    _imageUrl = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasImage));
                }
            }
        }

        public string AudioUrl
        {
            get => _audioUrl;
            set
            {
                if (_audioUrl != value)
                {
                    _audioUrl = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsVoiceMessage));
                    OnPropertyChanged(nameof(VoiceTranscribeMenuVisibility));
                    OnPropertyChanged(nameof(ContentOrAudioVisibility));
                }
            }
        }

        public string VideoUrl
        {
            get => _videoUrl;
            set
            {
                if (_videoUrl != value)
                {
                    _videoUrl = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasVideo));
                    OnPropertyChanged(nameof(VideoPlayOverlayVisibility));
                    OnPropertyChanged(nameof(ContentOrAudioVisibility));
                }
            }
        }

        public ImageSource? BubbleImage
        {
            get => _bubbleImage;
            set
            {
                if (!ReferenceEquals(_bubbleImage, value))
                {
                    _bubbleImage = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(BubbleImageVisibility));
                    OnPropertyChanged(nameof(VideoPlayOverlayVisibility));
                    OnPropertyChanged(nameof(HasImage));
                    OnPropertyChanged(nameof(ContentOrAudioVisibility));
                }
            }
        }

        public byte[]? BubbleImageBytes
        {
            get => _bubbleImageBytes;
            set => _bubbleImageBytes = value;
        }

        public bool HasImage => _bubbleImage is not null || !string.IsNullOrWhiteSpace(_imageUrl);
        public bool HasVideo => !string.IsNullOrWhiteSpace(_videoUrl);
        public bool IsVideoMessage => !string.IsNullOrWhiteSpace(_content) && _content.StartsWith("🎬 [视频", StringComparison.Ordinal);
        public bool IsVoiceMessage => !string.IsNullOrWhiteSpace(_audioUrl) ||
            (!string.IsNullOrWhiteSpace(_content) && _content.StartsWith("🎤 [语音", StringComparison.Ordinal));

        public string VideoDurationBadge
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_content)) return "视频";
                var m = System.Text.RegularExpressions.Regex.Match(_content, @"\[视频\s*(\d+""?)\]");
                return m.Success ? $"视频 · {m.Groups[1].Value}" : "视频";
            }
        }

        public string TimeLabel
        {
            get => _timeLabel;
            set
            {
                if (_timeLabel != value)
                {
                    _timeLabel = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsOutgoing
        {
            get => _isOutgoing;
            set
            {
                if (_isOutgoing != value)
                {
                    _isOutgoing = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IncomingVisibility));
                    OnPropertyChanged(nameof(OutgoingVisibility));
                    OnPropertyChanged(nameof(SenderNameVisibility));
                }
            }
        }

        public bool IsSelectionMode
        {
            get => _isSelectionMode;
            set
            {
                if (_isSelectionMode != value)
                {
                    _isSelectionMode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SelectionCheckVisibility));
                }
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public Visibility SelectionCheckVisibility => IsSelectionMode ? Visibility.Visible : Visibility.Collapsed;
        public Visibility IncomingVisibility => _isOutgoing ? Visibility.Collapsed : Visibility.Visible;
        public Visibility OutgoingVisibility => _isOutgoing ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SenderAvatarVisibility => SenderAvatar is not null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SenderInitialVisibility => SenderAvatar is null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ContentVisibility => !string.IsNullOrWhiteSpace(_content) ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ContentOrAudioVisibility =>
            ((!string.IsNullOrWhiteSpace(_content) && !((IsVideoMessage || HasVideo) && _bubbleImage is not null)) ||
             !string.IsNullOrWhiteSpace(_audioUrl) ||
             ((IsVideoMessage || HasVideo) && _bubbleImage is null))
                ? Visibility.Visible
                : Visibility.Collapsed;
        public Visibility BubbleImageVisibility => _bubbleImage is not null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility VideoPlayOverlayVisibility => (IsVideoMessage || HasVideo) && _bubbleImage is not null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility VoiceTranscribeMenuVisibility => IsVoiceMessage && !string.IsNullOrWhiteSpace(_audioUrl) ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SenderNameVisibility => !_isOutgoing && !string.IsNullOrWhiteSpace(_senderName)
            ? Visibility.Visible
            : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
