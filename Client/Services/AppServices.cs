using Client.Services.WeChat4Db;
using Client.Services.WeComDb;

namespace Client.Services
{
    public static class AppServices
    {
        private static readonly Lazy<OcrService> LazyOcr = new(() => new OcrService());
        private static int _modalDialogDepth;

        public static AppSettingsService Settings { get; } = new();
        public static AppLogService Logs { get; } = new();
        public static BackendApiClient Backend { get; } = new();
        public static WeChatWindowService WeChatWindows { get; } = new();
        public static WeChat4SyncService WeChat4Sync { get; } = new();
        public static WeComSyncService WeComSync { get; } = new();
        public static LocalConnectorBridgeService LocalBridge { get; } = new();
        public static GroupAutoReplyService GroupAutoReply { get; } = new();
        public static WeChatFriendAutoAcceptService FriendAutoAccept { get; } = new();
        public static LocalAiWorkspaceStore AiWorkspace { get; } = new();
        public static OcrService Ocr => LazyOcr.Value;
        public static nint MainWindowHandle { get; set; }

        public static bool IsModalDialogOpen => Volatile.Read(ref _modalDialogDepth) > 0;

        public static bool TryBeginModalDialog() =>
            Interlocked.CompareExchange(ref _modalDialogDepth, 1, 0) == 0;

        public static void EndModalDialog() =>
            Interlocked.Exchange(ref _modalDialogDepth, 0);
    }
}
