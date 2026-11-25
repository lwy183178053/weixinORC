using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Client.Services
{
    public sealed class AiImageAttachment
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
        public string DataUri { get; init; } = string.Empty;
        public BitmapImage? Preview { get; init; }
        public string DisplayName { get; init; } = "图片";
    }

    public static class AiImageAttachmentHelper
    {
        public const int MaxAttachmentsPerMessage = 4;

        public static async Task<AiImageAttachment?> TryCreateFromClipboardAsync()
        {
            var (bmp, _) = await AppServices.Ocr.TryLoadImageFromClipboardAsync();
            if (bmp is null) return null;
            using (bmp)
            {
                return await EncodeBitmapAsync(bmp, "剪贴板截图");
            }
        }

        public static async Task<AiImageAttachment?> TryCreateFromFileAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;
            using var bmp = await Task.Run(() => AppServices.Ocr.LoadImageFileFromPathAsync(filePath));
            return await EncodeBitmapAsync(bmp, Path.GetFileName(filePath));
        }

        private static async Task<AiImageAttachment> EncodeBitmapAsync(SoftwareBitmap source, string displayName)
        {
            using var bgra = source.BitmapPixelFormat == BitmapPixelFormat.Bgra8 && source.BitmapAlphaMode == BitmapAlphaMode.Premultiplied
                ? SoftwareBitmap.Copy(source)
                : SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            uint w = (uint)bgra.PixelWidth;
            uint h = (uint)bgra.PixelHeight;
            const uint maxEdge = 1280;
            uint targetW = w;
            uint targetH = h;
            if (w > maxEdge || h > maxEdge)
            {
                double scale = Math.Min((double)maxEdge / w, (double)maxEdge / h);
                targetW = Math.Max(1, (uint)Math.Round(w * scale));
                targetH = Math.Max(1, (uint)Math.Round(h * scale));
            }

            using var stream = new InMemoryRandomAccessStream();
            var props = new BitmapPropertySet
            {
                { "ImageQuality", new BitmapTypedValue(0.84, Windows.Foundation.PropertyType.Single) }
            };
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, props);
            encoder.SetSoftwareBitmap(bgra);
            if (targetW != w || targetH != h)
            {
                encoder.BitmapTransform.ScaledWidth = targetW;
                encoder.BitmapTransform.ScaledHeight = targetH;
                encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            }
            await encoder.FlushAsync();

            byte[] bytes = new byte[stream.Size];
            stream.Seek(0);
            await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);

            string dataUri = "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
            var preview = await DataUriToBitmapImageAsync(dataUri);

            return new AiImageAttachment
            {
                DataUri = dataUri,
                Preview = preview,
                DisplayName = displayName
            };
        }

        public static async Task<BitmapImage?> DataUriToBitmapImageAsync(string dataUri)
        {
            if (string.IsNullOrWhiteSpace(dataUri)) return null;
            int comma = dataUri.IndexOf(',');
            if (comma < 0 || comma + 1 >= dataUri.Length) return null;

            try
            {
                byte[] bytes = Convert.FromBase64String(dataUri[(comma + 1)..]);
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(bytes.AsBuffer());
                stream.Seek(0);
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(stream);
                return bmp;
            }
            catch
            {
                return null;
            }
        }
    }
}

namespace Client.Controls
{
    /// <summary>
    /// Lightweight image thumbnail control bound directly to a base64 data URI string.
    /// </summary>
    public sealed class DataUriImage : UserControl
    {
        public static readonly DependencyProperty DataUriProperty = DependencyProperty.Register(
            nameof(DataUri),
            typeof(string),
            typeof(DataUriImage),
            new PropertyMetadata(string.Empty, OnDataUriChanged));

        private readonly Image _image = new() { Stretch = Stretch.UniformToFill };

        public DataUriImage()
        {
            Content = _image;
        }

        public string DataUri
        {
            get => (string)GetValue(DataUriProperty);
            set => SetValue(DataUriProperty, value);
        }

        private static async void OnDataUriChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not DataUriImage ctrl) return;
            string uri = e.NewValue as string ?? string.Empty;
            if (string.IsNullOrWhiteSpace(uri))
            {
                ctrl._image.Source = null;
                return;
            }
            ctrl._image.Source = await Client.Services.AiImageAttachmentHelper.DataUriToBitmapImageAsync(uri);
        }
    }
}
