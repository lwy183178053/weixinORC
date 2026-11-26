using System.Runtime.InteropServices.WindowsRuntime;
using Client.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;

namespace Client.Dialogs
{
    public sealed partial class ImageViewerWindow : Window
    {
        private const int DefaultLogicalWidth = 1220;
        private const int DefaultLogicalHeight = 840;
        private const int MinLogicalWidth = 720;
        private const int MinLogicalHeight = 520;
        private const double MinZoom = 0.10;
        private const double MaxZoom = 8.00;

        private static ImageViewerWindow? _activeInstance;

        private byte[]? _imageBytes;
        private double _imageWidth;
        private double _imageHeight;
        private double _zoom = 1.0;
        private int _rotationDegrees;
        private double _translateX;
        private double _translateY;
        private bool _hasFittedOnce;
        private bool _isDragging;
        private Point _dragStartPoint;
        private double _dragStartTranslateX;
        private double _dragStartTranslateY;
        private CancellationTokenSource? _toastCts;

        public ImageViewerWindow()
        {
            InitializeComponent();
            ConfigureWindowChromeAndSize();
            Closed += ImageViewerWindow_Closed;
        }

        public static void ShowImage(byte[] imageBytes, string title = "图片查看器")
        {
            if (imageBytes is null || imageBytes.Length == 0) return;

            if (_activeInstance is not null)
            {
                try
                {
                    _activeInstance.Activate();
                    _ = _activeInstance.LoadImageBytesAsync(imageBytes, title);
                    return;
                }
                catch
                {
                    _activeInstance = null;
                }
            }

            var win = new ImageViewerWindow();
            _activeInstance = win;
            win.Activate();
            _ = win.LoadImageBytesAsync(imageBytes, title);
        }

        private void ImageViewerWindow_Closed(object sender, WindowEventArgs args)
        {
            _toastCts?.Cancel();
            _toastCts?.Dispose();
            _toastCts = null;
            if (ReferenceEquals(_activeInstance, this))
            {
                _activeInstance = null;
            }
        }

        private void ConfigureWindowChromeAndSize()
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBarGrid);

            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }

            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                var titleBar = AppWindow.TitleBar;
                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                titleBar.ButtonForegroundColor = Color.FromArgb(255, 224, 228, 238);
                titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 130, 138, 156);
                titleBar.ButtonHoverForegroundColor = Color.FromArgb(255, 255, 255, 255);
                titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 42, 48, 66);
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(255, 56, 64, 86);
            }

            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            uint dpi = Win32WindowHelper.GetDpiForWindow(hwnd);
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;

            int targetW = (int)Math.Round(DefaultLogicalWidth * scale);
            int targetH = (int)Math.Round(DefaultLogicalHeight * scale);
            int minW = (int)Math.Round(MinLogicalWidth * scale);
            int minH = (int)Math.Round(MinLogicalHeight * scale);

            var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            if (displayArea is not null)
            {
                var work = displayArea.WorkArea;
                targetW = Math.Min(targetW, Math.Max(760, (int)(work.Width * 0.90)));
                targetH = Math.Min(targetH, Math.Max(540, (int)(work.Height * 0.90)));
                minW = Math.Min(minW, Math.Max(600, work.Width - 60));
                minH = Math.Min(minH, Math.Max(420, work.Height - 60));

                int x = work.X + Math.Max(0, (work.Width - targetW) / 2);
                int y = work.Y + Math.Max(0, (work.Height - targetH) / 2);
                AppWindow.MoveAndResize(new RectInt32(x, y, targetW, targetH));
            }
            else
            {
                AppWindow.Resize(new SizeInt32(targetW, targetH));
            }

            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = true;
                presenter.IsMaximizable = true;
                presenter.PreferredMinimumWidth = minW;
                presenter.PreferredMinimumHeight = minH;
            }
        }

        public async Task LoadImageBytesAsync(byte[] imageBytes, string title)
        {
            _imageBytes = imageBytes;
            Title = string.IsNullOrWhiteSpace(title) ? "图片查看器" : title;
            TitleText.Text = Title;
            EmptyHintText.Text = "正在加载图片...";
            EmptyHintText.Visibility = Visibility.Visible;
            PreviewImage.Source = null;
            _rotationDegrees = 0;
            _translateX = 0;
            _translateY = 0;
            _hasFittedOnce = false;

            try
            {
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(imageBytes.AsBuffer());
                stream.Seek(0);

                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(stream);

                _imageWidth = bmp.PixelWidth > 0 ? bmp.PixelWidth : 600;
                _imageHeight = bmp.PixelHeight > 0 ? bmp.PixelHeight : 400;

                PreviewImage.Width = _imageWidth;
                PreviewImage.Height = _imageHeight;
                Canvas.SetLeft(PreviewImage, -_imageWidth / 2.0);
                Canvas.SetTop(PreviewImage, -_imageHeight / 2.0);
                PreviewImage.Source = bmp;
                EmptyHintText.Visibility = Visibility.Collapsed;

                string sizeKb = imageBytes.Length >= 1024 * 1024
                    ? $"{imageBytes.Length / (1024.0 * 1024.0):F2} MB"
                    : $"{Math.Max(1, imageBytes.Length / 1024)} KB";
                MetaInfoText.Text = $"{(int)_imageWidth} × {(int)_imageHeight} · {sizeKb}";

                FitToWindow();
            }
            catch (Exception ex)
            {
                EmptyHintText.Text = $"图片加载失败：{ex.Message}";
                EmptyHintText.Visibility = Visibility.Visible;
            }
        }

        private double ComputeFitZoom()
        {
            if (_imageWidth <= 0 || _imageHeight <= 0) return 1.0;
            double vpW = ViewportGrid.ActualWidth;
            double vpH = ViewportGrid.ActualHeight;
            if (vpW < 80 || vpH < 80) return 1.0;

            bool swapped = Math.Abs(_rotationDegrees % 180) == 90;
            double effW = swapped ? _imageHeight : _imageWidth;
            double effH = swapped ? _imageWidth : _imageHeight;

            double availW = Math.Max(120, vpW - 64);
            double availH = Math.Max(120, vpH - 110);
            double fit = Math.Min(availW / effW, availH / effH);
            return Math.Clamp(fit, MinZoom, 1.0);
        }

        private void FitToWindow()
        {
            _zoom = ComputeFitZoom();
            _translateX = 0;
            _translateY = 0;
            _hasFittedOnce = ViewportGrid.ActualWidth >= 80 && ViewportGrid.ActualHeight >= 80;
            ApplyTransform();
        }

        private void ApplyTransform()
        {
            ImageTransform.ScaleX = _zoom;
            ImageTransform.ScaleY = _zoom;
            ImageTransform.Rotation = _rotationDegrees;
            ImageTransform.TranslateX = _translateX;
            ImageTransform.TranslateY = _translateY;

            ZoomPercentText.Text = $"{Math.Round(_zoom * 100)}%";

            int normalizedDeg = ((_rotationDegrees % 360) + 360) % 360;
            RotationBadgeText.Text = $"{normalizedDeg}°";
            RotationBadge.Visibility = normalizedDeg != 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ZoomByFactor(double factor, Point? anchorInViewport = null)
        {
            double newZoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
            if (Math.Abs(newZoom - _zoom) < 0.0005) return;

            if (anchorInViewport.HasValue && ViewportGrid.ActualWidth > 0 && ViewportGrid.ActualHeight > 0)
            {
                double dx = anchorInViewport.Value.X - ViewportGrid.ActualWidth / 2.0;
                double dy = anchorInViewport.Value.Y - ViewportGrid.ActualHeight / 2.0;
                double ratio = newZoom / _zoom;
                _translateX = dx - (dx - _translateX) * ratio;
                _translateY = dy - (dy - _translateY) * ratio;
            }

            _zoom = newZoom;
            ApplyTransform();
        }

        private void RotateBy(int deltaDegrees)
        {
            _rotationDegrees = (_rotationDegrees + deltaDegrees) % 360;
            FitToWindow();
            int normalized = ((_rotationDegrees % 360) + 360) % 360;
            ShowToast($"已旋转至 {normalized}°");
        }

        private void ViewportGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ViewportGrid.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, Math.Max(0, e.NewSize.Width), Math.Max(0, e.NewSize.Height))
            };

            if (!_hasFittedOnce && _imageWidth > 0 && e.NewSize.Width >= 80 && e.NewSize.Height >= 80)
            {
                FitToWindow();
            }
        }

        private void ViewportGrid_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var pt = e.GetCurrentPoint(ViewportGrid);
            int delta = pt.Properties.MouseWheelDelta;
            if (delta == 0) return;

            double factor = delta > 0 ? 1.16 : 1.0 / 1.16;
            ZoomByFactor(factor, pt.Position);
            e.Handled = true;
        }

        private void ViewportGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var pt = e.GetCurrentPoint(ViewportGrid);
            if (!pt.Properties.IsLeftButtonPressed) return;

            _isDragging = true;
            _dragStartPoint = pt.Position;
            _dragStartTranslateX = _translateX;
            _dragStartTranslateY = _translateY;
            ViewportGrid.CapturePointer(e.Pointer);
        }

        private void ViewportGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging) return;
            var pos = e.GetCurrentPoint(ViewportGrid).Position;
            _translateX = _dragStartTranslateX + (pos.X - _dragStartPoint.X);
            _translateY = _dragStartTranslateY + (pos.Y - _dragStartPoint.Y);
            ApplyTransform();
        }

        private void ViewportGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging) return;
            _isDragging = false;
            ViewportGrid.ReleasePointerCapture(e.Pointer);
        }

        private void ViewportGrid_PointerCanceled(object sender, PointerRoutedEventArgs e) => _isDragging = false;
        private void ViewportGrid_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => _isDragging = false;

        private void ViewportGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            double fit = ComputeFitZoom();
            if (Math.Abs(_zoom - fit) < 0.04)
            {
                double target = fit < 0.92 ? 1.0 : 1.8;
                _zoom = target;
                _translateX = 0;
                _translateY = 0;
                ApplyTransform();
            }
            else
            {
                FitToWindow();
            }
        }

        private void ZoomOutButton_Click(object sender, RoutedEventArgs e) => ZoomByFactor(1.0 / 1.25);
        private void ZoomInButton_Click(object sender, RoutedEventArgs e) => ZoomByFactor(1.25);
        private void FitWindowButton_Click(object sender, RoutedEventArgs e) => FitToWindow();

        private void ActualSizeButton_Click(object sender, RoutedEventArgs e)
        {
            _zoom = 1.0;
            _translateX = 0;
            _translateY = 0;
            ApplyTransform();
            ShowToast("原始尺寸 100%");
        }

        private void RotateLeftButton_Click(object sender, RoutedEventArgs e) => RotateBy(-90);
        private void RotateRightButton_Click(object sender, RoutedEventArgs e) => RotateBy(90);

        private void ResetViewButton_Click(object sender, RoutedEventArgs e)
        {
            _rotationDegrees = 0;
            FitToWindow();
            ShowToast("已重置视图");
        }

        private async void CopyImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_imageBytes is null || _imageBytes.Length == 0) return;
            try
            {
                var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(_imageBytes.AsBuffer());
                stream.Seek(0);
                var pkg = new DataPackage();
                pkg.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
                Clipboard.SetContent(pkg);
                ShowToast("已复制图片到剪贴板");
            }
            catch (Exception ex)
            {
                ShowToast($"复制失败：{ex.Message}");
            }
        }

        private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            switch (e.Key)
            {
                case VirtualKey.Escape:
                    Close();
                    e.Handled = true;
                    break;
                case VirtualKey.Add:
                case (VirtualKey)187: // '+' / '='
                    ZoomByFactor(1.25);
                    e.Handled = true;
                    break;
                case VirtualKey.Subtract:
                case (VirtualKey)189: // '-'
                    ZoomByFactor(1.0 / 1.25);
                    e.Handled = true;
                    break;
                case VirtualKey.Number0:
                case VirtualKey.NumberPad0:
                    FitToWindow();
                    e.Handled = true;
                    break;
                case VirtualKey.Number1:
                case VirtualKey.NumberPad1:
                    _zoom = 1.0;
                    _translateX = 0;
                    _translateY = 0;
                    ApplyTransform();
                    e.Handled = true;
                    break;
                case VirtualKey.Q:
                case VirtualKey.Left:
                    RotateBy(-90);
                    e.Handled = true;
                    break;
                case VirtualKey.E:
                case VirtualKey.Right:
                    RotateBy(90);
                    e.Handled = true;
                    break;
            }
        }

        private async void ShowToast(string message)
        {
            _toastCts?.Cancel();
            _toastCts?.Dispose();
            var cts = new CancellationTokenSource();
            _toastCts = cts;

            ToastText.Text = message;
            ToastBanner.Visibility = Visibility.Visible;
            try
            {
                await Task.Delay(1400, cts.Token);
                ToastBanner.Visibility = Visibility.Collapsed;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
