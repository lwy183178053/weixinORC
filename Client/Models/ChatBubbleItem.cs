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
        private string _timeLabel = string.Empty;
        private bool _isOutgoing;

        public Guid LocalKey { get; } = Guid.NewGuid();
        public long Id { get; init; }
        public string SenderInitial { get; init; } = "客";
        public ImageSource? SenderAvatar { get; init; }

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
                }
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
        public Visibility SenderNameVisibility => !_isOutgoing && !string.IsNullOrWhiteSpace(_senderName)
            ? Visibility.Visible
            : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
