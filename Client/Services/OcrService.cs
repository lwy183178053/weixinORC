using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Client.Services
{
    public sealed class OcrService
    {
        private readonly RapidOcrEngine _rapidEngine;

        public OcrService()
        {
            _rapidEngine = new RapidOcrEngine();
        }

        public RapidOcrEngine RapidEngine => _rapidEngine;
        public IReadOnlyList<IOcrEngine> Engines => new IOcrEngine[] { _rapidEngine };
        public IOcrEngine GetDefaultEngine() => _rapidEngine;

        public async Task<SoftwareBitmap> LoadImageFileFromPathAsync(string filePath)
        {
            byte[] bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
            using var mem = new MemoryStream(bytes, writable: false);
            using var randomStream = mem.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(randomStream).AsTask().ConfigureAwait(false);
            return await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied).AsTask().ConfigureAwait(false);
        }

        /// <summary>
        /// Reads a screenshot bitmap or copied image file directly from the Windows clipboard.
        /// Must be called on the UI thread (WinRT Clipboard requirement).
        /// </summary>
        public async Task<(SoftwareBitmap? Bitmap, string SourceLabel)> TryLoadImageFromClipboardAsync()
        {
            var data = Clipboard.GetContent();
            if (data is null)
            {
                return (null, string.Empty);
            }

            if (data.Contains(StandardDataFormats.Bitmap))
            {
                var streamRef = await data.GetBitmapAsync();
                using var stream = await streamRef.OpenReadAsync();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                var bmp = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied);
                return (bmp, "剪贴板截图");
            }

            if (data.Contains(StandardDataFormats.StorageItems))
            {
                var items = await data.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    if (item is StorageFile file &&
                        (file.FileType.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                         file.FileType.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                         file.FileType.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                         file.FileType.Equals(".bmp", StringComparison.OrdinalIgnoreCase)))
                    {
                        var bmp = await LoadImageFileFromPathAsync(file.Path);
                        return (bmp, $"剪贴板图片：{file.Name}");
                    }
                }
            }

            return (null, string.Empty);
        }
    }
}
