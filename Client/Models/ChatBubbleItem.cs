using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Client.Models
{
    public sealed class ChatBubbleItem
    {
        public long Id { get; init; }
        public string SenderName { get; init; } = string.Empty;
        public string SenderInitial { get; init; } = "客";
        public ImageSource? SenderAvatar { get; init; }
        public string Content { get; init; } = string.Empty;
        public string TimeLabel { get; init; } = string.Empty;
        public bool IsOutgoing { get; init; }

        public Visibility IncomingVisibility => IsOutgoing ? Visibility.Collapsed : Visibility.Visible;
        public Visibility OutgoingVisibility => IsOutgoing ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SenderAvatarVisibility => SenderAvatar is not null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SenderInitialVisibility => SenderAvatar is null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SenderNameVisibility => !IsOutgoing && !string.IsNullOrWhiteSpace(SenderName)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
