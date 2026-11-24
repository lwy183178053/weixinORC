using Client.Models;
using Windows.Graphics.Imaging;

namespace Client.Services
{
    public interface IOcrEngine
    {
        string Id { get; }
        string DisplayName { get; }
        string Description { get; }
        bool IsAvailable { get; }
        string AvailabilityNote { get; }

        Task<OcrRecognitionResult> RecognizeAsync(
            SoftwareBitmap bitmap,
            CancellationToken cancellationToken = default);
    }
}
