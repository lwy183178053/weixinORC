using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Client.Services
{
    /// <summary>
    /// Lightweight single-active-track voice message player using native WinRT MediaPlayer.
    /// Plays WAV/MP3 voice audio fetched from the backend and notifies UI bubbles on state changes.
    /// </summary>
    public sealed class VoicePlaybackService : IDisposable
    {
        public static VoicePlaybackService Shared { get; } = new();

        private readonly object _lock = new();
        private MediaPlayer? _player;
        private InMemoryRandomAccessStream? _currentStream;
        private string? _currentUrl;
        private bool _isPlaying;

        public string? CurrentUrl => _currentUrl;
        public bool IsPlaying => _isPlaying;

        public event Action<string?, bool>? PlaybackStateChanged;

        private VoicePlaybackService() { }

        public async Task<(bool Ok, string Error)> TogglePlayAsync(string audioUrl)
        {
            if (string.IsNullOrWhiteSpace(audioUrl))
            {
                return (false, "暂无可播放的语音文件。");
            }

            if (_isPlaying && string.Equals(_currentUrl, audioUrl, StringComparison.Ordinal))
            {
                Stop();
                return (true, string.Empty);
            }

            Stop();

            byte[]? bytes = await AppServices.Backend.DownloadChatAudioBytesAsync(audioUrl);
            if (bytes is null || bytes.Length < 44)
            {
                return (false, "语音文件加载失败或暂未同步。");
            }
            bytes = WeChat4Db.WeChat4MediaDecoder.RepairMalformedKonataWav(bytes);

            try
            {
                var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(bytes.AsBuffer());
                stream.Seek(0);

                string contentType = (bytes[0] == 'R' && bytes[1] == 'I') ? "audio/wav" : "audio/mpeg";
                var source = MediaSource.CreateFromStream(stream, contentType);

                lock (_lock)
                {
                    _currentStream = stream;
                    _currentUrl = audioUrl;
                    _isPlaying = true;

                    _player ??= CreatePlayer();
                    _player.Source = source;
                    _player.Play();
                }

                PlaybackStateChanged?.Invoke(_currentUrl, true);
                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                Stop();
                return (false, $"语音播放失败：{ex.Message}");
            }
        }

        private MediaPlayer CreatePlayer()
        {
            var player = new MediaPlayer
            {
                AudioCategory = MediaPlayerAudioCategory.Media
            };
            player.MediaEnded += (_, _) => Stop();
            player.MediaFailed += (_, args) =>
            {
                AppServices.Logs.Warn("AUDIO", $"语音播放失败：{args.Error} ({args.ErrorMessage})");
                Stop();
            };
            return player;
        }

        public void Stop()
        {
            string? prevUrl;
            lock (_lock)
            {
                prevUrl = _currentUrl;
                _currentUrl = null;
                _isPlaying = false;
                if (_player is not null)
                {
                    try
                    {
                        _player.Pause();
                        _player.Source = null;
                    }
                    catch { }
                }
                if (_currentStream is not null)
                {
                    try { _currentStream.Dispose(); } catch { }
                    _currentStream = null;
                }
            }

            if (prevUrl is not null)
            {
                PlaybackStateChanged?.Invoke(null, false);
            }
        }

        public void Dispose()
        {
            Stop();
            lock (_lock)
            {
                _player?.Dispose();
                _player = null;
            }
        }
    }
}
