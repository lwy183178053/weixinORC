using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Client.Models
{
    public sealed class CustomerListRow : INotifyPropertyChanged
    {
        private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);

        private bool _isCollapsed;
        private bool _isMultiSelectMode;
        private bool _isChecked;
        private bool _isGroupAllSelected;
        private bool _isActive;

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool IsGroupHeader { get; init; }
        public string TagName { get; init; } = string.Empty;
        public int GroupCount { get; init; }
        public CustomerDto? Customer { get; init; }

        public Visibility GroupHeaderVisibility => IsGroupHeader ? Visibility.Visible : Visibility.Collapsed;
        public Visibility CustomerRowVisibility => IsGroupHeader ? Visibility.Collapsed : Visibility.Visible;

        public string GroupCountText => GroupCount.ToString();

        public bool IsCollapsed
        {
            get => _isCollapsed;
            set
            {
                if (_isCollapsed != value)
                {
                    _isCollapsed = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ChevronGlyph));
                }
            }
        }

        public string ChevronGlyph => _isCollapsed ? "\uE76C" : "\uE70D";

        public bool IsMultiSelectMode
        {
            get => _isMultiSelectMode;
            set
            {
                if (_isMultiSelectMode != value)
                {
                    _isMultiSelectMode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(GroupCheckVisibility));
                    OnPropertyChanged(nameof(CustomerCheckVisibility));
                }
            }
        }

        public Visibility GroupCheckVisibility => IsGroupHeader && _isMultiSelectMode ? Visibility.Visible : Visibility.Collapsed;
        public Visibility CustomerCheckVisibility => !IsGroupHeader && _isMultiSelectMode ? Visibility.Visible : Visibility.Collapsed;

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked != value)
                {
                    _isChecked = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsGroupAllSelected
        {
            get => _isGroupAllSelected;
            set
            {
                if (_isGroupAllSelected != value)
                {
                    _isGroupAllSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive != value)
                {
                    _isActive = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(RowBackground));
                    OnPropertyChanged(nameof(RowBorderBrush));
                }
            }
        }

        public Brush RowBackground => _isActive
            ? (Brush)Application.Current.Resources["BrandSoftBgBrush"]
            : TransparentBrush;

        public Brush RowBorderBrush => _isActive
            ? (Brush)Application.Current.Resources["BrandSoftBorderBrush"]
            : TransparentBrush;

        // Safe customer display projections (never null-ref on group header rows)
        public string DisplayName => Customer?.DisplayName ?? string.Empty;
        public string AvatarInitial => Customer?.AvatarInitial ?? "客";
        public string TimeShort => Customer?.TimeShort ?? string.Empty;
        public string PreviewLine => Customer?.PreviewLine ?? string.Empty;
        public ImageSource? AvatarImage => Customer?.AvatarImage;
        public Visibility AvatarImageVisibility => Customer?.AvatarImage is not null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AvatarInitialVisibility => Customer?.AvatarImage is null ? Visibility.Visible : Visibility.Collapsed;
        public string UnreadBadgeText => Customer?.UnreadBadgeText ?? string.Empty;
        public Visibility UnreadBadgeVisibility => Customer?.UnreadBadgeVisibility ?? Visibility.Collapsed;
        public Visibility PinnedBadgeVisibility => Customer?.PinnedBadgeVisibility ?? Visibility.Collapsed;

        public void NotifyAvatarChanged()
        {
            OnPropertyChanged(nameof(AvatarImage));
            OnPropertyChanged(nameof(AvatarImageVisibility));
            OnPropertyChanged(nameof(AvatarInitialVisibility));
        }

        public void NotifySessionStateChanged()
        {
            OnPropertyChanged(nameof(UnreadBadgeText));
            OnPropertyChanged(nameof(UnreadBadgeVisibility));
            OnPropertyChanged(nameof(PinnedBadgeVisibility));
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
