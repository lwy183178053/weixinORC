using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Client.Controls
{
    public sealed class HorizontalResizeSplitter : Grid
    {
        public HorizontalResizeSplitter()
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
        }
    }

    public sealed class ComposerResizeController
    {
        private readonly Grid _stageGrid;
        private readonly RowDefinition _composerRow;
        private readonly HorizontalResizeSplitter _splitter;
        private readonly double _minHeight;
        private readonly double _maxHeight;
        private readonly double _minChatHeight;

        private bool _isDragging;
        private double _dragStartY;
        private double _dragStartHeight;

        private ComposerResizeController(
            Grid stageGrid,
            RowDefinition composerRow,
            HorizontalResizeSplitter splitter,
            double minHeight,
            double maxHeight,
            double minChatHeight)
        {
            _stageGrid = stageGrid;
            _composerRow = composerRow;
            _splitter = splitter;
            _minHeight = minHeight;
            _maxHeight = maxHeight;
            _minChatHeight = minChatHeight;

            _splitter.PointerPressed += OnPointerPressed;
            _splitter.PointerMoved += OnPointerMoved;
            _splitter.PointerReleased += OnPointerReleased;
            _splitter.PointerCanceled += OnPointerCaptureLost;
            _splitter.PointerCaptureLost += OnPointerCaptureLost;
            _stageGrid.SizeChanged += OnStageSizeChanged;
        }

        public static ComposerResizeController Attach(
            Grid stageGrid,
            RowDefinition composerRow,
            HorizontalResizeSplitter splitter,
            double minHeight = 120,
            double maxHeight = 440,
            double minChatHeight = 240) =>
            new(stageGrid, composerRow, splitter, minHeight, maxHeight, minChatHeight);

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var pt = e.GetCurrentPoint(_stageGrid);
            if (!pt.Properties.IsLeftButtonPressed) return;

            _isDragging = true;
            _dragStartY = pt.Position.Y;
            _dragStartHeight = _composerRow.ActualHeight;
            _splitter.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging) return;
            double currentY = e.GetCurrentPoint(_stageGrid).Position.Y;
            double delta = _dragStartY - currentY; // Dragging up increases composer height
            double maxAllowed = Math.Min(_maxHeight, Math.Max(_minHeight + 32, _stageGrid.ActualHeight - _minChatHeight));
            double nextHeight = Math.Clamp(_dragStartHeight + delta, _minHeight, maxAllowed);
            _composerRow.Height = new GridLength(nextHeight);
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging) return;
            _isDragging = false;
            _splitter.ReleasePointerCapture(e.Pointer);
        }

        private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _isDragging = false;
        }

        private void OnStageSizeChanged(object sender, SizeChangedEventArgs e)
        {
            double maxAllowed = Math.Min(_maxHeight, Math.Max(_minHeight, _stageGrid.ActualHeight - _minChatHeight));
            if (_composerRow.Height.IsAbsolute && _composerRow.Height.Value > maxAllowed)
            {
                _composerRow.Height = new GridLength(Math.Max(_minHeight, maxAllowed));
            }
        }
    }
}
