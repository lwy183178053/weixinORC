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

        public long Id { get; init; }
        public string SenderName { get; init; } = string.Empty;
        public string SenderInitial { get; init; } = "客";
        public ImageSource? SenderAvatar { get; init; }
        public string Content { get; init; } = string.Empty;
        public string TimeLabel { get; init; } = string.Empty;
        public bool IsOutgoing { get; init; }

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
        public Visibility IncomingVisibility => IsOutgoing ? Visibility.Collapsed : Visibility.Visible;
        public Visibility OutgoingVisibility => IsOutgoing ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SenderAvatarVisibility => SenderAvatar is not null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SenderInitialVisibility => SenderAvatar is null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SenderNameVisibility => !IsOutgoing && !string.IsNullOrWhiteSpace(SenderName)
            ? Visibility.Visible
            : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
