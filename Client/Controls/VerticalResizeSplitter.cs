using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Client.Controls
{
    /// <summary>
    /// 竖向拖拽分隔条（左右调整板块宽度），自带 6px 易抓取热区、1px 细分割线与悬停/拖拽紫色高亮。
    /// </summary>
    public sealed class VerticalResizeSplitter : Grid
    {
        private readonly SolidColorBrush _restLineBrush = new(Color.FromArgb(255, 230, 232, 240));
        private readonly SolidColorBrush _activeLineBrush = new(Color.FromArgb(255, 124, 92, 252));
        private readonly SolidColorBrush _hoverBgBrush = new(Color.FromArgb(20, 124, 92, 252));
        private readonly SolidColorBrush _transparentBrush = new(Colors.Transparent);

        private readonly Border _indicatorLine;
        private bool _isHovering;
        private bool _isDragging;

        public VerticalResizeSplitter()
        {
            Width = 6;
            Background = _transparentBrush;
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

            _indicatorLine = new Border
            {
                Width = 1,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = _restLineBrush,
                IsHitTestVisible = false
            };
            Children.Add(_indicatorLine);

            PointerEntered += (_, _) =>
            {
                _isHovering = true;
                UpdateVisual();
            };
            PointerExited += (_, _) =>
            {
                _isHovering = false;
                UpdateVisual();
            };
        }

        internal void SetDragging(bool dragging)
        {
            _isDragging = dragging;
            UpdateVisual();
        }

        private void UpdateVisual()
        {
            bool active = _isDragging || _isHovering;
            _indicatorLine.Width = active ? 2 : 1;
            _indicatorLine.Background = active ? _activeLineBrush : _restLineBrush;
            Background = active ? _hoverBgBrush : _transparentBrush;
        }
    }

    /// <summary>
    /// 三栏页面横向拖拽缩放控制器：
    /// 1. 左右分隔条拖拽调整左/右板块宽度，严格遵守三栏各自最小/最大宽度；
    /// 2. 窗口缩小时自动按余量等比回缩左右板块，确保中间板块永不被挤没；
    /// 3. 双击分隔条可一键恢复该侧默认宽度。
    /// </summary>
    public sealed class ThreePaneResizeController
    {
        private readonly Grid _rootGrid;
        private readonly ColumnDefinition _leftCol;
        private readonly VerticalResizeSplitter _leftSplitter;
        private readonly ColumnDefinition _centerCol;
        private readonly VerticalResizeSplitter _rightSplitter;
        private readonly ColumnDefinition _rightCol;

        private readonly double _leftDefault;
        private readonly double _leftMin;
        private readonly double _leftMax;
        private readonly double _centerMin;
        private readonly double _rightDefault;
        private readonly double _rightMin;
        private readonly double _rightMax;

        private bool _draggingLeft;
        private bool _draggingRight;
        private double _dragStartX;
        private double _dragStartWidth;

        private ThreePaneResizeController(
            Grid rootGrid,
            ColumnDefinition leftCol,
            VerticalResizeSplitter leftSplitter,
            ColumnDefinition centerCol,
            VerticalResizeSplitter rightSplitter,
            ColumnDefinition rightCol,
            double leftDefault,
            double leftMin,
            double leftMax,
            double centerMin,
            double rightDefault,
            double rightMin,
            double rightMax)
        {
            _rootGrid = rootGrid;
            _leftCol = leftCol;
            _leftSplitter = leftSplitter;
            _centerCol = centerCol;
            _rightSplitter = rightSplitter;
            _rightCol = rightCol;

            _leftDefault = leftDefault;
            _leftMin = leftMin;
            _leftMax = leftMax;
            _centerMin = centerMin;
            _rightDefault = rightDefault;
            _rightMin = rightMin;
            _rightMax = rightMax;

            _leftCol.MinWidth = _leftMin;
            _centerCol.MinWidth = _centerMin;
            _rightCol.MinWidth = _rightMin;

            ToolTipService.SetToolTip(_leftSplitter, "左右拖动调整左侧宽度（双击恢复默认）");
            ToolTipService.SetToolTip(_rightSplitter, "左右拖动调整右侧宽度（双击恢复默认）");

            WireSplitter(_leftSplitter, isLeft: true);
            WireSplitter(_rightSplitter, isLeft: false);
            _rootGrid.SizeChanged += (_, _) => ClampColumnsToAvailableWidth();
        }

        public static ThreePaneResizeController Attach(
            Grid rootGrid,
            ColumnDefinition leftCol,
            VerticalResizeSplitter leftSplitter,
            ColumnDefinition centerCol,
            VerticalResizeSplitter rightSplitter,
            ColumnDefinition rightCol,
            double leftDefault = 268,
            double leftMin = 220,
            double leftMax = 420,
            double centerMin = 450,
            double rightDefault = 356,
            double rightMin = 290,
            double rightMax = 520)
        {
            return new ThreePaneResizeController(
                rootGrid, leftCol, leftSplitter, centerCol, rightSplitter, rightCol,
                leftDefault, leftMin, leftMax, centerMin, rightDefault, rightMin, rightMax);
        }

        private double GetAvailablePaneWidth()
        {
            double splitterSum = (_leftSplitter.ActualWidth > 0 ? _leftSplitter.ActualWidth : _leftSplitter.Width)
                               + (_rightSplitter.ActualWidth > 0 ? _rightSplitter.ActualWidth : _rightSplitter.Width);
            return Math.Max(0, _rootGrid.ActualWidth - splitterSum);
        }

        private void WireSplitter(VerticalResizeSplitter splitter, bool isLeft)
        {
            splitter.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(splitter).Properties.IsLeftButtonPressed) return;
                _dragStartX = e.GetCurrentPoint(_rootGrid).Position.X;
                if (isLeft)
                {
                    _draggingLeft = true;
                    _dragStartWidth = _leftCol.ActualWidth > 0 ? _leftCol.ActualWidth : _leftCol.Width.Value;
                }
                else
                {
                    _draggingRight = true;
                    _dragStartWidth = _rightCol.ActualWidth > 0 ? _rightCol.ActualWidth : _rightCol.Width.Value;
                }

                splitter.SetDragging(true);
                splitter.CapturePointer(e.Pointer);
                e.Handled = true;
            };

            splitter.PointerMoved += (_, e) =>
            {
                if (isLeft && !_draggingLeft) return;
                if (!isLeft && !_draggingRight) return;

                double currentX = e.GetCurrentPoint(_rootGrid).Position.X;
                double delta = currentX - _dragStartX;
                double available = GetAvailablePaneWidth();

                if (isLeft)
                {
                    double rightW = _rightCol.ActualWidth > 0 ? _rightCol.ActualWidth : _rightCol.Width.Value;
                    double maxAllowed = Math.Max(_leftMin, Math.Min(_leftMax, available - rightW - _centerMin));
                    double next = Math.Clamp(_dragStartWidth + delta, _leftMin, maxAllowed);
                    _leftCol.Width = new GridLength(Math.Round(next));
                }
                else
                {
                    double leftW = _leftCol.ActualWidth > 0 ? _leftCol.ActualWidth : _leftCol.Width.Value;
                    double maxAllowed = Math.Max(_rightMin, Math.Min(_rightMax, available - leftW - _centerMin));
                    double next = Math.Clamp(_dragStartWidth - delta, _rightMin, maxAllowed);
                    _rightCol.Width = new GridLength(Math.Round(next));
                }
            };

            void EndDrag(Pointer? pointer)
            {
                if (isLeft) _draggingLeft = false;
                else _draggingRight = false;
                splitter.SetDragging(false);
                if (pointer is not null)
                {
                    splitter.ReleasePointerCapture(pointer);
                }
            }

            splitter.PointerReleased += (_, e) => EndDrag(e.Pointer);
            splitter.PointerCaptureLost += (_, _) => EndDrag(null);
            splitter.PointerCanceled += (_, e) => EndDrag(e.Pointer);

            splitter.DoubleTapped += (_, _) =>
            {
                if (isLeft)
                {
                    _leftCol.Width = new GridLength(_leftDefault);
                }
                else
                {
                    _rightCol.Width = new GridLength(_rightDefault);
                }
                ClampColumnsToAvailableWidth();
            };
        }

        private void ClampColumnsToAvailableWidth()
        {
            double available = GetAvailablePaneWidth();
            if (available <= 0) return;

            double leftW = _leftCol.Width.IsAbsolute ? _leftCol.Width.Value : _leftDefault;
            double rightW = _rightCol.Width.IsAbsolute ? _rightCol.Width.Value : _rightDefault;

            leftW = Math.Clamp(leftW, _leftMin, _leftMax);
            rightW = Math.Clamp(rightW, _rightMin, _rightMax);

            double maxSideTotal = available - _centerMin;
            if (leftW + rightW > maxSideTotal)
            {
                double excess = (leftW + rightW) - maxSideTotal;
                double leftSlack = Math.Max(0, leftW - _leftMin);
                double rightSlack = Math.Max(0, rightW - _rightMin);
                double totalSlack = leftSlack + rightSlack;

                if (totalSlack > 0.5)
                {
                    double trim = Math.Min(excess, totalSlack);
                    double leftTrim = trim * (leftSlack / totalSlack);
                    double rightTrim = trim - leftTrim;
                    leftW = Math.Max(_leftMin, leftW - leftTrim);
                    rightW = Math.Max(_rightMin, rightW - rightTrim);
                }
                else
                {
                    leftW = _leftMin;
                    rightW = _rightMin;
                }
            }

            double roundedLeft = Math.Round(leftW);
            double roundedRight = Math.Round(rightW);

            if (Math.Abs(_leftCol.Width.Value - roundedLeft) >= 1)
            {
                _leftCol.Width = new GridLength(roundedLeft);
            }
            if (Math.Abs(_rightCol.Width.Value - roundedRight) >= 1)
            {
                _rightCol.Width = new GridLength(roundedRight);
            }
        }
    }
}
