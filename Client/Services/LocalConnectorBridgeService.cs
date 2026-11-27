using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Client.Services.WeChat4Db;
using Client.Services.WeComDb;

namespace Client.Services
{
    /// <summary>
    /// Local HTTP Connector Bridge (Option B + Route 1):
    /// Drop-in, hook-free, read-only DB + OCR/Win32 RPA replacement for legacy local third-party tools:
    /// - Personal WeChat adapter: http://127.0.0.1:8990/api (+ /health, pushes events to http://127.0.0.1:9001)
    /// - Enterprise WeCom adapter: http://127.0.0.1:8989/api (+ /health, pushes events to http://127.0.0.1:9000)
    /// All JSON encoding uses Utf8JsonWriter (100% PublishTrimmed-safe).
    /// </summary>
    public sealed class LocalConnectorBridgeService : IDisposable
    {
        private static readonly HttpClient PushHttp = new() { Timeout = TimeSpan.FromSeconds(5) };
        private static readonly HttpClient MediaHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

        private readonly object _lifecycleLock = new();
        private readonly SemaphoreSlim _wechatSendGate = new(1, 1);
        private readonly SemaphoreSlim _wecomSendGate = new(1, 1);

        // Short-term outbound send fingerprints (60s) to deduplicate echoed messages back from WAL
        private readonly ConcurrentDictionary<string, DateTimeOffset> _recentOutboundFingerprints = new(StringComparer.OrdinalIgnoreCase);

        private HttpListener? _wechatListener;
        private HttpListener? _wecomListener;
        private CancellationTokenSource? _cts;

        // Personal WeChat type=47 voice auto-save config
        private volatile bool _wechatVoiceAutoSaveEnabled;
        private volatile string _wechatVoiceSaveDir = string.Empty;

        // Lightweight local watcher state for Personal WeChat push (works even before workbench login)
        private readonly Dictionary<string, long> _wechatLastSessionTs = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _wechatPushedExtIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string Name, HashSet<string> Members)> _wechatGroupSnapshots = new(StringComparer.OrdinalIgnoreCase);
        private bool _wechatBaselineReady;
        private bool _wechatWasRunning;
        private bool _disposed;

        public bool IsRunning { get; private set; }
        public bool IsWeChatListening { get; private set; }
        public bool IsWeComListening { get; private set; }
        public string WeChatEndpoint { get; private set; } = string.Empty;
        public string WeComEndpoint { get; private set; } = string.Empty;
        public string StatusSummary { get; private set; } = "本地适配服务：未启动";

        public event Action? StateChanged;

        public void StartIfEnabled()
        {
            if (AppServices.Settings.Current.LocalBridgeEnabled)
            {
                Start();
            }
        }

        public void ConfigureEnabled(bool enabled)
        {
            AppServices.Settings.Current.LocalBridgeEnabled = enabled;
            AppServices.Settings.Save();
            if (enabled) Start();
            else Stop();
        }

        public void Start()
        {
            lock (_lifecycleLock)
            {
                if (IsRunning || _disposed) return;

                var cfg = AppServices.Settings.Current;
                int wxPort = cfg.WeChatBridgePort is > 0 and <= 65535 ? cfg.WeChatBridgePort : 8990;
                int wcPort = cfg.WeComBridgePort is > 0 and <= 65535 ? cfg.WeComBridgePort : 8989;

                _cts = new CancellationTokenSource();
                var ct = _cts.Token;

                (_wechatListener, IsWeChatListening, WeChatEndpoint) = TryStartListener(wxPort, "个人微信");
                (_wecomListener, IsWeComListening, WeComEndpoint) = TryStartListener(wcPort, "企业微信");

                IsRunning = IsWeChatListening || IsWeComListening;
                if (!IsRunning)
                {
                    StatusSummary = $"本地适配端口被占用（个微 :{wxPort} / 企微 :{wcPort}）";
                    StateChanged?.Invoke();
                    return;
                }

                if (IsWeChatListening && _wechatListener is not null)
                {
                    _ = Task.Run(() => ServeLoopAsync(_wechatListener, isWeCom: false, ct), ct);
                    _ = Task.Run(() => PersonalWeChatPushPatrolAsync(ct), ct);
                }

                if (IsWeComListening && _wecomListener is not null)
                {
                    _ = Task.Run(() => ServeLoopAsync(_wecomListener, isWeCom: true, ct), ct);
                    AppServices.WeComSync.EventDetected -= OnWeComEventDetected;
                    AppServices.WeComSync.EventDetected += OnWeComEventDetected;
                    AppServices.WeComSync.StartWatching();
                }

                UpdateSummary();
                AppServices.Logs.Success("BRIDGE", StatusSummary);
                StateChanged?.Invoke();
            }
        }

        public void Stop()
        {
            lock (_lifecycleLock)
            {
                if (!IsRunning && _cts is null) return;
                IsRunning = false;
                IsWeChatListening = false;
                IsWeComListening = false;

                AppServices.WeComSync.EventDetected -= OnWeComEventDetected;

                try { _cts?.Cancel(); _cts?.Dispose(); } catch { }
                _cts = null;

                StopListener(ref _wechatListener);
                StopListener(ref _wecomListener);

                StatusSummary = "本地适配服务：已停止";
                AppServices.Logs.Info("BRIDGE", StatusSummary);
                StateChanged?.Invoke();
            }
        }

        private void UpdateSummary()
        {
            var parts = new List<string>(2);
            if (IsWeChatListening) parts.Add($"个微 {WeChatEndpoint}");
            if (IsWeComListening) parts.Add($"企微 {WeComEndpoint}");
            StatusSummary = parts.Count > 0
                ? $"本地适配运行中：{string.Join(" · ", parts)}"
                : "本地适配服务：未启动";
        }

        private static (HttpListener? Listener, bool Ok, string Endpoint) TryStartListener(int port, string label)
        {
            string prefix = $"http://127.0.0.1:{port}/";
            try
            {
                var l = new HttpListener();
                l.Prefixes.Add(prefix);
                l.Start();
                AppServices.Logs.Info("BRIDGE", $"{label}本地适配服务已监听 {prefix}api");
                return (l, true, $"127.0.0.1:{port}");
            }
            catch (Exception ex)
            {
                AppServices.Logs.Warn("BRIDGE", $"启动{label}本地适配端口 {port} 失败：{ex.Message}");
                return (null, false, $"127.0.0.1:{port} (失败)");
            }
        }

        private static void StopListener(ref HttpListener? listener)
        {
            if (listener is null) return;
            try { listener.Stop(); } catch { }
            try { listener.Close(); } catch { }
            listener = null;
        }

        private async Task ServeLoopAsync(HttpListener listener, bool isWeCom, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync().WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch
                {
                    continue;
                }

                _ = Task.Run(() => HandleRequestAsync(ctx, isWeCom, ct), ct);
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext ctx, bool isWeCom, CancellationToken ct)
        {
            try
            {
                string path = (ctx.Request.Url?.AbsolutePath ?? "/").TrimEnd('/').ToLowerInvariant();
                string method = ctx.Request.HttpMethod.ToUpperInvariant();

                if (method == "GET" && (path == "/health" || path == ""))
                {
                    byte[] body = BuildHealthJson(isWeCom);
                    await WriteResponseAsync(ctx.Response, 200, body);
                    return;
                }

                if (method != "POST" || (path != "/api" && path != ""))
                {
                    await WriteResponseAsync(ctx.Response, 404, BuildSimpleErrorJson(isWeCom, 404, "route_not_found"));
                    return;
                }

                string rawBody;
                using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
                {
                    rawBody = await reader.ReadToEndAsync(ct);
                }

                if (string.IsNullOrWhiteSpace(rawBody))
                {
                    await WriteResponseAsync(ctx.Response, 400, BuildSimpleErrorJson(isWeCom, 400, "empty_body"));
                    return;
                }

                using var doc = JsonDocument.Parse(rawBody);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !TryGetInt(root, "type", out int apiType))
                {
                    await WriteResponseAsync(ctx.Response, 400, BuildSimpleErrorJson(isWeCom, 400, "missing_type"));
                    return;
                }

                var (statusCode, responseBytes) = isWeCom
                    ? await HandleWeComApiAsync(apiType, root, ct)
                    : await HandlePersonalWeChatApiAsync(apiType, root, ct);

                await WriteResponseAsync(ctx.Response, statusCode, responseBytes);
            }
            catch (JsonException ex)
            {
                await WriteResponseAsync(ctx.Response, 400, BuildSimpleErrorJson(isWeCom, 400, $"invalid_json: {ex.Message}"));
            }
            catch (Exception ex)
            {
                AppServices.Logs.Warn("BRIDGE", $"{(isWeCom ? "企微" : "个微")} /api 处理异常：{ex.Message}");
                await WriteResponseAsync(ctx.Response, 500, BuildSimpleErrorJson(isWeCom, 500, ex.Message));
            }
        }

        // =========================================================================
        // 6.2 Enterprise WeCom Adapter (http://127.0.0.1:8989/api)
        // =========================================================================

        private async Task<(int Status, byte[] Body)> HandleWeComApiAsync(int type, JsonElement req, CancellationToken ct)
        {
            switch (type)
            {
                case 1000: // 查询登录状态 -> data.status (0/1)
                {
                    int pid = WeComPaths.GetPrimaryWXWorkPid();
                    bool loggedIn = pid > 0;
                    if (loggedIn)
                    {
                        var (ok, _) = await AppServices.WeComSync.EnsureReadyAsync(forceRefresh: false, ct);
                        loggedIn = ok && AppServices.WeComSync.ActiveReader is not null;
                    }
                    return (200, WriteJson(w =>
                    {
                        w.WriteStartObject();
                        w.WriteNumber("code", 0);
                        w.WriteString("msg", "OK");
                        w.WritePropertyName("data");
                        w.WriteStartObject();
                        w.WriteNumber("status", loggedIn ? 1 : 0);
                        w.WriteEndObject();
                        w.WriteEndObject();
                    }));
                }

                case 10004: // 查询企微进程 PID -> data.pid
                {
                    int pid = WeComPaths.GetPrimaryWXWorkPid();
                    return (200, WriteJson(w =>
                    {
                        w.WriteStartObject();
                        w.WriteNumber("code", 0);
                        w.WriteString("msg", "OK");
                        w.WritePropertyName("data");
                        w.WriteStartObject();
                        w.WriteNumber("pid", pid);
                        w.WriteEndObject();
                        w.WriteEndObject();
                    }));
                }

                case 1002: // 获取个人资料
                {
                    var (ok, err) = await AppServices.WeComSync.EnsureReadyAsync(forceRefresh: false, ct);
                    var reader = AppServices.WeComSync.ActiveReader;
                    if (!ok || reader is null) return (500, BuildSimpleErrorJson(true, 500, err));
                    var me = reader.SelfUser ?? new WeComUser { UserId = reader.AccountId, Name = reader.AccountId };

                    return (200, WriteJson(w =>
                    {
                        w.WriteStartObject();
                        w.WriteNumber("code", 0);
                        w.WriteString("msg", "OK");
                        w.WritePropertyName("data");
                        WriteWeComUserObject(w, me);
                        w.WriteEndObject();
                    }));
                }

                case 2000: // 分页获取联系人 -> data.list / total_page
                {
                    var (ok, err) = await AppServices.WeComSync.EnsureReadyAsync(forceRefresh: false, ct);
                    var reader = AppServices.WeComSync.ActiveReader;
                    if (!ok || reader is null) return (500, BuildSimpleErrorJson(true, 500, err));

                    int pageNum = Math.Max(1, GetIntOrDefault(req, "page_num", 1));
                    int pageSize = Math.Clamp(GetIntOrDefault(req, "page_size", 50), 1, 500);
                    var all = reader.Users;
                    int total = all.Count;
                    int totalPage = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
                    var slice = all.Skip((pageNum - 1) * pageSize).Take(pageSize).ToList();

                    return (200, WriteJson(w =>
                    {
                        w.WriteStartObject();
                        w.WriteNumber("code", 0);
                        w.WriteString("msg", "OK");
                        w.WritePropertyName("data");
                        w.WriteStartObject();
                        w.WriteNumber("page_num", pageNum);
                        w.WriteNumber("page_size", pageSize);
                        w.WriteNumber("total", total);
                        w.WriteNumber("total_page", totalPage);
                        w.WritePropertyName("list");
                        w.WriteStartArray();
                        foreach (var u in slice) WriteWeComUserObject(w, u);
                        w.WriteEndArray();
                        w.WriteEndObject();
                        w.WriteEndObject();
                    }));
                }

                case 2002: // 分页获取群列表 -> data.list / total_page
                {
                    var (ok, err) = await AppServices.WeComSync.EnsureReadyAsync(forceRefresh: false, ct);
                    var reader = AppServices.WeComSync.ActiveReader;
                    if (!ok || reader is null) return (500, BuildSimpleErrorJson(true, 500, err));

                    int pageNum = Math.Max(1, GetIntOrDefault(req, "page_num", 1));
                    int pageSize = Math.Clamp(GetIntOrDefault(req, "page_size", 100), 1, 500);
                    var all = reader.Groups;
                    int total = all.Count;
                    int totalPage = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
                    var slice = all.Skip((pageNum - 1) * pageSize).Take(pageSize).ToList();

                    return (200, WriteJson(w =>
                    {
                        w.WriteStartObject();
                        w.WriteNumber("code", 0);
                        w.WriteString("msg", "OK");
                        w.WritePropertyName("data");
                        w.WriteStartObject();
                        w.WriteNumber("page_num", pageNum);
                        w.WriteNumber("page_size", pageSize);
                        w.WriteNumber("total", total);
                        w.WriteNumber("total_page", totalPage);
                        w.WritePropertyName("list");
                        w.WriteStartArray();
                        foreach (var g in slice) WriteWeComGroupObject(w, g);
                        w.WriteEndArray();
                        w.WriteEndObject();
                        w.WriteEndObject();
                    }));
                }

                case 2003: // 获取群成员
                {
                    string roomId = GetString(req, "chat_room_id");
                    var (ok, err) = await AppServices.WeComSync.EnsureReadyAsync(forceRefresh: false, ct);
                    var reader = AppServices.WeComSync.ActiveReader;
                    if (!ok || reader is null) return (500, BuildSimpleErrorJson(true, 500, err));
                    var grp = reader.FindGroup(roomId);
                    if (grp is null) return (404, BuildSimpleErrorJson(true, 404, $"group_not_found: {roomId}"));

                    return (200, WriteJson(w =>
                    {
                        w.WriteStartObject();
                        w.WriteNumber("code", 0);
                        w.WriteString("msg", "OK");
                        w.WritePropertyName("data");
                        w.WriteStartObject();
                        w.WriteString("chat_room_id", grp.ConversationId);
                        w.WriteString("room_id", grp.RoomId);
                        w.WritePropertyName("list");
                        w.WriteStartArray();
                        foreach (var m in grp.Members)
                        {
                            w.WriteStartObject();
                            w.WriteString("user_id", m.UserId);
                            w.WriteString("name", !string.IsNullOrWhiteSpace(m.Name) ? m.Name : m.DisplayName);
                            w.WriteString("nick_name", m.DisplayName);
                            w.WriteString("group_nickname", m.GroupNickname);
                            w.WriteString("remark", m.Remark);
                            w.WriteString("avatar_url", m.AvatarUrl);
                            w.WriteBoolean("is_admin", m.IsAdmin);
                            w.WriteNumber("join_time", m.JoinTime);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                        w.WriteEndObject();
                        w.WriteEndObject();
                    }));
                }

                case 4007: // 获取联系人详情
                {
                    string uid = GetString(req, "user_id");
                    var (ok, err) = await AppServices.WeComSync.EnsureReadyAsync(forceRefresh: false, ct);
                    var reader = AppServices.WeComSync.ActiveReader;
                    if (!ok || reader is null) return (500, BuildSimpleErrorJson(true, 500, err));
                    var user = reader.FindUser(uid);
                    if (user is null) return (404, BuildSimpleErrorJson(true, 404, $"user_not_found: {uid}"));

                    return (200, WriteJson(w =>
                    {
                        w.WriteStartObject();
                        w.WriteNumber("code", 0);
                        w.WriteString("msg", "OK");
                        w.WritePropertyName("data");
                        WriteWeComUserObject(w, user);
                        w.WriteEndObject();
                    }));
                }

                case 5008: // 获取群详情
                {
                    string roomId = GetString(req, "chat_room_id");
                    var (ok, err) = await AppServices.WeComSync.EnsureReadyAsync(forceRefresh: false, ct);
                    var reader = AppServices.WeComSync.ActiveReader;
                    if (!ok || reader is null) return (500, BuildSimpleErrorJson(true, 500, err));
                    var grp = reader.FindGroup(roomId);
                    if (grp is null) return (404, BuildSimpleErrorJson(true, 404, $"group_not_found: {roomId}"));

                    return (200, WriteJson(w =>
                    {
                        w.WriteStartObject();
                        w.WriteNumber("code", 0);
                        w.WriteString("msg", "OK");
                        w.WritePropertyName("data");
                        WriteWeComGroupObject(w, grp);
                        w.WriteEndObject();
                    }));
                }

                case 3000: // 发送文本（个人或群）
                case 3001: // 发送图片
                case 3002: // 发送视频
                case 3003: // 发送文件
                case 3004: // 发送语音（以文件形式发送）
                case 3009: // 发送群文本并 @ 成员
                {
                    return await HandleWeComSendAsync(type, req, ct);
                }

                case 9001: // 下载图片
                case 9002: // 下载视频
                case 9003: // 下载文件/语音
                case 9004: // URL 资源统一下载
                {
                    return await HandleWeComDownloadAsync(type, req, ct);
                }

                default:
                    return (400, BuildSimpleErrorJson(true, 400, $"unsupported_wecom_type: {type}"));
            }
        }

        private async Task<(int Status, byte[] Body)> HandleWeComSendAsync(int type, JsonElement req, CancellationToken ct)
        {
            string targetId = type == 3009
                ? GetString(req, "chat_room_id")
                : GetString(req, "user_id", "chat_room_id");
            string msg = GetString(req, "msg");
            string filePath = GetString(req, "path");
            var atIds = GetStringArray(req, "at_list");

            if (string.IsNullOrWhiteSpace(targetId))
            {
                return (400, BuildSimpleErrorJson(true, 400, "missing_target_id"));
            }

            if (type is 3001 or 3002 or 3003 or 3004 && (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)))
            {
                return (400, BuildSimpleErrorJson(true, 400, $"file_not_found: {filePath}"));
            }

            var (okReady, errReady) = await AppServices.WeComSync.EnsureReadyAsync(forceRefresh: false, ct);
            var reader = AppServices.WeComSync.ActiveReader;
            if (!okReady || reader is null)
            {
                return (500, BuildSimpleErrorJson(true, 500, errReady));
            }

            bool isGroup = type == 3009 ||
                           targetId.StartsWith("R:", StringComparison.OrdinalIgnoreCase) ||
                           reader.FindGroup(targetId) is not null;

            string searchKey;
            string expectedName;
            string verifyConvId;
            var atDisplayNames = new List<string>();

            if (isGroup)
            {
                var grp = reader.FindGroup(targetId);
                expectedName = grp?.DisplayName ?? targetId;
                searchKey = expectedName;
                verifyConvId = grp?.ConversationId ?? (targetId.StartsWith("R:", StringComparison.OrdinalIgnoreCase) ? targetId : "R:" + targetId);

                foreach (string aid in atIds)
                {
                    if (aid.Equals("notify@all", StringComparison.OrdinalIgnoreCase))
                    {
                        atDisplayNames.Add("所有人");
                    }
                    else
                    {
                        var mem = grp?.Members.FirstOrDefault(m => string.Equals(m.UserId, aid, StringComparison.OrdinalIgnoreCase));
                        string disp = mem?.DisplayName ?? reader.FindUser(aid)?.DisplayName ?? aid;
                        atDisplayNames.Add(disp);
                    }
                }
            }
            else
            {
                var user = reader.FindUser(targetId);
                expectedName = user?.DisplayName ?? targetId;
                searchKey = !string.IsNullOrWhiteSpace(user?.Remark) ? user.Remark : expectedName;
                verifyConvId = reader.BuildSingleConversationId(targetId);
            }

            await _wecomSendGate.WaitAsync(ct);
            try
            {
                RecordOutboundFingerprint("wecom", verifyConvId, !string.IsNullOrWhiteSpace(msg) ? msg : Path.GetFileName(filePath));
                var executor = new WeComAutoReplyExecutor();
                var (sentOk, verifiedWal, detail) = await executor.SendMessageAsync(
                    searchKey: searchKey,
                    expectedDisplayName: expectedName,
                    isGroup: isGroup,
                    text: msg,
                    filePath: filePath,
                    atDisplayNames: atDisplayNames,
                    verifyConversationId: verifyConvId,
                    ct: ct);

                if (!sentOk)
                {
                    return (500, BuildSimpleErrorJson(true, 500, detail));
                }

                return (200, WriteJson(w =>
                {
                    w.WriteStartObject();
                    w.WriteNumber("code", 0);
                    w.WriteString("msg", "OK");
                    w.WritePropertyName("data");
                    w.WriteStartObject();
                    w.WriteBoolean("sent", true);
                    w.WriteBoolean("wal_verified", verifiedWal);
                    w.WriteString("detail", detail);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }));
            }
            finally
            {
                _wecomSendGate.Release();
            }
        }

        private async Task<(int Status, byte[] Body)> HandleWeComDownloadAsync(int type, JsonElement req, CancellationToken ct)
        {
            string savePath = GetString(req, "save_path");
            string cdnKey = GetString(req, "cdn_key", "file_id");
            string md5 = GetString(req, "md5");
            string url = GetString(req, "url");
            string fileName = GetString(req, "file_name", "filename");
            if (string.IsNullOrWhiteSpace(savePath))
            {
                return (400, BuildSimpleErrorJson(true, 400, "missing_save_path"));
            }

            string kind = type switch
            {
                9001 => "image",
                9002 => "video",
                9003 => "file",
                _ => "file"
            };

            string? dir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 1. Direct HTTP URL download (e.g. public wework.qpic.cn emoji/image URLs or type=9004)
            if (!string.IsNullOrWhiteSpace(url) && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    byte[] bytes = await MediaHttp.GetByteArrayAsync(url, ct);
                    if (bytes.Length > 0)
                    {
                        await File.WriteAllBytesAsync(savePath, bytes, ct);
                        return (200, WriteWeComDownloadOk(savePath, bytes.Length, "http"));
                    }
                }
                catch { }
            }

            // 2. Locate from local WeCom Cache (Cache/Image, Cache/Video, Cache/File)
            await AppServices.WeComSync.EnsureReadyAsync(forceRefresh: false, ct);
            var reader = AppServices.WeComSync.ActiveReader;
            string? localFile = reader?.TryLocateLocalMediaFile(md5, fileName, cdnKey, kind);
            if (!string.IsNullOrWhiteSpace(localFile) && File.Exists(localFile))
            {
                File.Copy(localFile, savePath, overwrite: true);
                long size = new FileInfo(savePath).Length;
                if (size > 0)
                {
                    return (200, WriteWeComDownloadOk(savePath, size, "local_cache"));
                }
            }

            return (404, BuildSimpleErrorJson(true, 404, "local_media_not_cached_yet"));
        }

        private static byte[] WriteWeComDownloadOk(string savePath, long size, string source) =>
            WriteJson(w =>
            {
                w.WriteStartObject();
                w.WriteNumber("code", 0);
                w.WriteString("msg", "OK");
                w.WritePropertyName("data");
                w.WriteStartObject();
                w.WriteString("save_path", savePath);
                w.WriteNumber("size", size);
                w.WriteString("source", source);
                w.WriteEndObject();
                w.WriteEndObject();
            });

        // =========================================================================
        // 6.3 Personal WeChat Adapter (http://127.0.0.1:8990/api)
        // Format: {"errno": 0, "errmsg": "OK", "data": {...}}
        // =========================================================================

        private async Task<(int Status, byte[] Body)> HandlePersonalWeChatApiAsync(int type, JsonElement req, CancellationToken ct)
        {
            switch (type)
            {
                case 1: // 查询登录状态 -> data.status (1/0)
                {
                    var procs = WeChat4Paths.FindWeixinProcesses();
                    bool loggedIn = procs.Count > 0;
                    if (loggedIn && AppServices.WeChat4Sync.ActiveReader is null)
                    {
                        var snap = await AppServices.WeChat4Sync.EnsureSnapshotReadyAsync(forceReload: false, ct: ct);
                        loggedIn = snap.Success;
                    }
                    return (200, WritePersonalOk(w =>
                    {
                        w.WriteStartObject();
                        w.WriteNumber("status", loggedIn ? 1 : 0);
                        w.WriteEndObject();
                    }));
                }

                case 48: // 查询微信进程 PID -> data.pid
                {
                    var procs = WeChat4Paths.FindWeixinProcesses();
                    int pid = procs.Count > 0 ? procs[0].Pid : 0;
                    return (200, WritePersonalOk(w =>
                    {
                        w.WriteStartObject();
                        w.WriteNumber("pid", pid);
                        w.WriteEndObject();
                    }));
                }

                case 3: // 获取个人资料 -> data.wx_id/nick_name/avatar_url/mobile
                {
                    var snap = await AppServices.WeChat4Sync.EnsureSnapshotReadyAsync(forceReload: false, ct: ct);
                    var reader = AppServices.WeChat4Sync.ActiveReader;
                    if (!snap.Success || reader is null)
                    {
                        return (200, WritePersonalErr(1, snap.ErrorMessage));
                    }
                    var self = reader.SelfContact;
                    string wxid = !string.IsNullOrWhiteSpace(reader.SelfWxid) ? reader.SelfWxid : (snap.Account?.Wxid ?? string.Empty);

                    return (200, WritePersonalOk(w =>
                    {
                        w.WriteStartObject();
                        w.WriteString("wx_id", wxid);
                        w.WriteString("wxid", wxid);
                        w.WriteString("account", self?.Alias ?? string.Empty);
                        w.WriteString("nick_name", self?.NickName ?? wxid);
                        w.WriteString("avatar_url", self?.AvatarUrl ?? string.Empty);
                        w.WriteString("mobile", self?.Phone ?? string.Empty);
                        w.WriteEndObject();
                    }));
                }

                case 47: // 开启语音自动保存
                {
                    int state = GetIntOrDefault(req, "state", 1);
                    string saveDir = GetString(req, "save_dir");
                    _wechatVoiceAutoSaveEnabled = state != 0;
                    if (!string.IsNullOrWhiteSpace(saveDir))
                    {
                        _wechatVoiceSaveDir = saveDir.Trim();
                        try { Directory.CreateDirectory(_wechatVoiceSaveDir); } catch { }
                    }
                    return (200, WritePersonalOk(w =>
                    {
                        w.WriteStartObject();
                        w.WriteNumber("state", _wechatVoiceAutoSaveEnabled ? 1 : 0);
                        w.WriteString("save_dir", _wechatVoiceSaveDir);
                        w.WriteEndObject();
                    }));
                }

                case 5: // 获取联系人 (pull_type=1) 或群列表 (pull_type=2)
                {
                    int pullType = GetIntOrDefault(req, "pull_type", 1);
                    var snap = await AppServices.WeChat4Sync.EnsureSnapshotReadyAsync(forceReload: false, ct: ct);
                    var reader = AppServices.WeChat4Sync.ActiveReader;
                    if (!snap.Success || reader is null) return (200, WritePersonalErr(1, snap.ErrorMessage));

                    return (200, WritePersonalOk(w =>
                    {
                        w.WriteStartObject();
                        w.WritePropertyName("list");
                        w.WriteStartArray();
                        if (pullType == 2)
                        {
                            foreach (var g in reader.Groups) WritePersonalGroupObject(w, g);
                        }
                        else
                        {
                            foreach (var f in reader.Friends) WritePersonalContactObject(w, f);
                        }
                        w.WriteEndArray();
                        w.WriteEndObject();
                    }));
                }

                case 6: // 获取群成员
                {
                    string roomId = GetString(req, "chat_room_id");
                    var snap = await AppServices.WeChat4Sync.EnsureSnapshotReadyAsync(forceReload: false, ct: ct);
                    var reader = AppServices.WeChat4Sync.ActiveReader;
                    if (!snap.Success || reader is null) return (200, WritePersonalErr(1, snap.ErrorMessage));
                    var grp = reader.Groups.FirstOrDefault(g => string.Equals(g.Username, roomId, StringComparison.OrdinalIgnoreCase));
                    if (grp is null) return (200, WritePersonalErr(404, $"group_not_found: {roomId}"));

                    return (200, WritePersonalOk(w =>
                    {
                        w.WriteStartObject();
                        w.WriteString("chat_room_id", grp.Username);
                        w.WritePropertyName("list");
                        w.WriteStartArray();
                        foreach (var m in grp.Members)
                        {
                            w.WriteStartObject();
                            w.WriteString("wx_id", m.Username);
                            w.WriteString("wxid", m.Username);
                            w.WriteString("account", m.Alias);
                            w.WriteString("nick_name", m.NickName);
                            w.WriteString("remark", m.Remark);
                            w.WriteString("group_nick_name", m.GroupNickname);
                            w.WriteString("display_name", m.DisplayName);
                            w.WriteString("avatar_url", m.AvatarUrl);
                            w.WriteString("role", m.Role);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                        w.WriteEndObject();
                    }));
                }

                case 19: // 获取好友详情
                {
                    string wxid = GetString(req, "wx_id", "wxid");
                    var snap = await AppServices.WeChat4Sync.EnsureSnapshotReadyAsync(forceReload: false, ct: ct);
                    var reader = AppServices.WeChat4Sync.ActiveReader;
                    if (!snap.Success || reader is null) return (200, WritePersonalErr(1, snap.ErrorMessage));

                    var contact = reader.Friends.FirstOrDefault(f => string.Equals(f.Username, wxid, StringComparison.OrdinalIgnoreCase));
                    if (contact is null)
                    {
                        // Also check group members if non-friend
                        var mem = reader.Groups.SelectMany(g => g.Members)
                            .FirstOrDefault(m => string.Equals(m.Username, wxid, StringComparison.OrdinalIgnoreCase));
                        if (mem is not null)
                        {
                            return (200, WritePersonalOk(w =>
                            {
                                w.WriteStartObject();
                                w.WriteString("wx_id", mem.Username);
                                w.WriteString("nick_name", mem.NickName);
                                w.WriteString("remark", mem.Remark);
                                w.WriteString("account", mem.Alias);
                                w.WriteString("avatar_url", mem.AvatarUrl);
                                w.WriteNumber("gender", ParseGenderInt(mem.Gender));
                                w.WriteEndObject();
                            }));
                        }
                        return (200, WritePersonalErr(404, $"contact_not_found: {wxid}"));
                    }

                    return (200, WritePersonalOk(w => WritePersonalContactObject(w, contact)));
                }

                case 29: // 批量查询群昵称
                {
                    string roomId = GetString(req, "chat_room_id");
                    var wxidList = GetStringArray(req, "wx_id_list");
                    var snap = await AppServices.WeChat4Sync.EnsureSnapshotReadyAsync(forceReload: false, ct: ct);
                    var reader = AppServices.WeChat4Sync.ActiveReader;
                    if (!snap.Success || reader is null) return (200, WritePersonalErr(1, snap.ErrorMessage));
                    var grp = reader.Groups.FirstOrDefault(g => string.Equals(g.Username, roomId, StringComparison.OrdinalIgnoreCase));
                    if (grp is null) return (200, WritePersonalErr(404, $"group_not_found: {roomId}"));

                    var memById = grp.Members.ToDictionary(m => m.Username, StringComparer.OrdinalIgnoreCase);
                    return (200, WritePersonalOk(w =>
                    {
                        w.WriteStartObject();
                        w.WritePropertyName("list");
                        w.WriteStartArray();
                        foreach (string id in wxidList)
                        {
                            memById.TryGetValue(id, out var m);
                            w.WriteStartObject();
                            w.WriteString("wx_id", id);
                            w.WriteString("nick_name", m?.DisplayName ?? id);
                            w.WriteString("group_nick_name", m?.GroupNickname ?? string.Empty);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                        w.WriteEndObject();
                    }));
                }

                case 7:  // 发送文本
                case 8:  // 发送图片
                case 9:  // 发送视频/文件
                case 13: // 群文本 @ 指定成员
                case 14: // 群文本 @所有人
                case 55: // 发送语音（降级为音频文件发送并标注说明）
                {
                    return await HandlePersonalWeChatSendAsync(type, req, ct);
                }

                case 73: // 本地导出图片到 save_path
                case 74: // 本地导出视频到 save_path
                case 75: // 本地导出文件/语音到 save_path
                {
                    return await HandlePersonalWeChatDownloadAsync(type, req, ct);
                }

                default:
                    return (200, WritePersonalErr(400, $"unsupported_wechat_type: {type}"));
            }
        }

        private async Task<(int Status, byte[] Body)> HandlePersonalWeChatSendAsync(int type, JsonElement req, CancellationToken ct)
        {
            string targetId = type is 13 or 14
                ? GetString(req, "chat_room_id", "wx_id")
                : GetString(req, "wx_id", "chat_room_id");
            string msg = GetString(req, "msg");
            string filePath = GetString(req, "path");
            var atIds = type == 14 ? new List<string> { "notify@all" } : GetStringArray(req, "at_list");

            if (string.IsNullOrWhiteSpace(targetId))
            {
                return (200, WritePersonalErr(400, "missing_wx_id"));
            }

            if (type is 8 or 9 or 55 && (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)))
            {
                return (200, WritePersonalErr(400, $"file_not_found: {filePath}"));
            }

            var snap = await AppServices.WeChat4Sync.EnsureSnapshotReadyAsync(forceReload: false, ct: ct);
            var reader = AppServices.WeChat4Sync.ActiveReader;
            if (!snap.Success || reader is null)
            {
                return (200, WritePersonalErr(500, snap.ErrorMessage));
            }

            bool isGroup = type is 13 or 14 || targetId.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase);
            string searchKey;
            string displayName;
            var atNames = new List<string>();

            if (isGroup)
            {
                var grp = reader.Groups.FirstOrDefault(g => string.Equals(g.Username, targetId, StringComparison.OrdinalIgnoreCase));
                displayName = grp?.GroupName ?? targetId;
                searchKey = displayName;
                foreach (string aid in atIds)
                {
                    if (aid.Equals("notify@all", StringComparison.OrdinalIgnoreCase))
                    {
                        atNames.Add("notify@all");
                    }
                    else
                    {
                        var mem = grp?.Members.FirstOrDefault(m => string.Equals(m.Username, aid, StringComparison.OrdinalIgnoreCase));
                        atNames.Add(mem?.DisplayName ?? aid);
                    }
                }
            }
            else
            {
                var friend = reader.Friends.FirstOrDefault(f => string.Equals(f.Username, targetId, StringComparison.OrdinalIgnoreCase));
                displayName = friend?.DisplayName ?? targetId;
                searchKey = !string.IsNullOrWhiteSpace(friend?.Alias) ? friend.Alias : displayName;
            }

            await _wechatSendGate.WaitAsync(ct);
            try
            {
                RecordOutboundFingerprint("wechat", targetId, !string.IsNullOrWhiteSpace(msg) ? msg : Path.GetFileName(filePath));
                var executor = new WeChatAutoReplyExecutor();
                var (ok, walConfirmed, detail) = await executor.SendBridgeMessageAsync(
                    searchKey: searchKey,
                    expectedDisplayName: displayName,
                    isGroup: isGroup,
                    text: msg,
                    filePath: filePath,
                    atDisplayNames: atNames,
                    verifySessionKey: targetId,
                    ct: ct);

                if (!ok) return (200, WritePersonalErr(500, detail));

                return (200, WritePersonalOk(w =>
                {
                    w.WriteStartObject();
                    w.WriteBoolean("sent", true);
                    w.WriteBoolean("wal_verified", walConfirmed);
                    w.WriteString("detail", detail);
                    if (type == 55)
                    {
                        w.WriteString("note", "PC 微信原生 UI 不支持导入语音条，已降级通过剪贴板发送音频文件");
                    }
                    w.WriteEndObject();
                }));
            }
            finally
            {
                _wechatSendGate.Release();
            }
        }

        private async Task<(int Status, byte[] Body)> HandlePersonalWeChatDownloadAsync(int type, JsonElement req, CancellationToken ct)
        {
            string savePath = GetString(req, "save_path");
            string fileId = GetString(req, "file_id", "md5", "clientmsgid");
            if (string.IsNullOrWhiteSpace(savePath))
            {
                return (200, WritePersonalErr(400, "missing_save_path"));
            }

            var snap = await AppServices.WeChat4Sync.EnsureSnapshotReadyAsync(forceReload: false, ct: ct);
            var reader = AppServices.WeChat4Sync.ActiveReader;
            if (!snap.Success || reader is null)
            {
                return (200, WritePersonalErr(500, snap.ErrorMessage));
            }

            string? dir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Search locally decrypted media/dat/file cache by file_id / md5
            if (!string.IsNullOrWhiteSpace(fileId) && Directory.Exists(reader.AccountPath))
            {
                string needle = fileId.Trim();
                if (needle.Length > 32) needle = needle[..32];

                try
                {
                    // First try image .dat decode if type=73
                    if (type == 73)
                    {
                        var fakeMsg = new WeChat4Message { Kind = "image", MediaHints = [fileId] };
                        byte[]? img = await reader.TryResolveMessageImageBytesAsync(string.Empty, fakeMsg, ct);
                        if (img is { Length: > 0 })
                        {
                            await File.WriteAllBytesAsync(savePath, img, ct);
                            return (200, WritePersonalOk(w =>
                            {
                                w.WriteStartObject();
                                w.WriteString("save_path", savePath);
                                w.WriteNumber("size", img.Length);
                                w.WriteEndObject();
                            }));
                        }
                    }

                    // Search account msg/attach / FileStorage folders for matching file name or md5
                    string[] searchRoots =
                    [
                        Path.Combine(reader.AccountPath, "msg", "file"),
                        Path.Combine(reader.AccountPath, "msg", "video"),
                        Path.Combine(reader.AccountPath, "msg", "attach")
                    ];

                    foreach (string root in searchRoots)
                    {
                        if (!Directory.Exists(root)) continue;
                        foreach (string f in Directory.EnumerateFiles(root, $"*{needle}*", SearchOption.AllDirectories))
                        {
                            var fi = new FileInfo(f);
                            if (fi.Length == 0) continue;
                            File.Copy(f, savePath, overwrite: true);
                            return (200, WritePersonalOk(w =>
                            {
                                w.WriteStartObject();
                                w.WriteString("save_path", savePath);
                                w.WriteNumber("size", fi.Length);
                                w.WriteEndObject();
                            }));
                        }
                    }
                }
                catch (Exception ex)
                {
                    return (200, WritePersonalErr(500, ex.Message));
                }
            }

            return (200, WritePersonalErr(404, "local_media_not_found_in_readonly_cache"));
        }

        // =========================================================================
        // Push Event Loops (WeCom -> :9000, Personal WeChat -> :9001)
        // =========================================================================

        private void OnWeComEventDetected(WeComEvent ev)
        {
            if (!IsRunning) return;
            string pushUrl = AppServices.Settings.Current.WeComPushUrl;
            if (string.IsNullOrWhiteSpace(pushUrl)) return;

            byte[]? payload = null;
            if (ev.EventKind == "message" && ev.Message is not null)
            {
                var m = ev.Message;
                if (m.IsOutgoing && IsRecentOutboundEcho("wecom", m.ConversationId, m.Content))
                {
                    return; // Deduplicate echo of RPA-sent message
                }
                payload = BuildWeComMessagePushJson(m);
            }
            else if (ev.EventKind == "group_name_changed")
            {
                payload = WriteJson(w =>
                {
                    w.WriteStartObject();
                    w.WriteNumber("type", 300);
                    w.WritePropertyName("data");
                    w.WriteStartObject();
                    w.WriteString("chat_room_id", ev.ConversationId);
                    w.WriteString("room_id", ev.RoomId);
                    w.WriteString("old_name", ev.OldGroupName);
                    w.WriteString("new_name", ev.NewGroupName);
                    w.WriteEndObject();
                    w.WriteEndObject();
                });
            }
            else if (ev.EventKind is "group_member_joined" or "group_member_left")
            {
                int eventType = ev.EventKind == "group_member_joined" ? 333 : 444;
                payload = WriteJson(w =>
                {
                    w.WriteStartObject();
                    w.WriteNumber("type", eventType);
                    w.WritePropertyName("data");
                    w.WriteStartObject();
                    w.WriteString("chat_room_id", ev.ConversationId);
                    w.WritePropertyName("member_list");
                    w.WriteStartArray();
                    foreach (string id in ev.MemberIds) w.WriteStringValue(id);
                    w.WriteEndArray();
                    w.WriteEndObject();
                    w.WriteEndObject();
                });
            }
            else if (ev.EventKind == "logout")
            {
                payload = WriteJson(w =>
                {
                    w.WriteStartObject();
                    w.WriteNumber("type", 900);
                    w.WritePropertyName("data");
                    w.WriteStartObject();
                    w.WriteEndObject();
                    w.WriteEndObject();
                });
            }

            if (payload is not null)
            {
                _ = PostPushBestEffortAsync(pushUrl, payload);
            }
        }

        private async Task PersonalWeChatPushPatrolAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    bool runningNow = WeChat4Paths.FindWeixinProcesses().Count > 0;
                    if (_wechatWasRunning && !runningNow)
                    {
                        _wechatWasRunning = false;
                        string pushUrl = AppServices.Settings.Current.WeChatPushUrl;
                        if (!string.IsNullOrWhiteSpace(pushUrl))
                        {
                            byte[] logoutJson = WriteJson(w =>
                            {
                                w.WriteStartObject();
                                w.WriteNumber("type", 999);
                                w.WritePropertyName("data");
                                w.WriteStartObject();
                                w.WriteEndObject();
                                w.WriteEndObject();
                            });
                            _ = PostPushBestEffortAsync(pushUrl, logoutJson);
                        }
                        continue;
                    }
                    _wechatWasRunning = runningNow;
                    if (!runningNow) continue;

                    var snap = await AppServices.WeChat4Sync.EnsureSnapshotReadyAsync(forceReload: true, ct: ct);
                    var reader = AppServices.WeChat4Sync.ActiveReader;
                    if (!snap.Success || reader is null) continue;

                    ProcessPersonalWeChatDelta(reader);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppServices.Logs.Warn("BRIDGE-WX", $"个人微信推送巡检异常：{ex.Message}");
            }
        }

        private void ProcessPersonalWeChatDelta(WeChat4Reader reader)
        {
            string pushUrl = AppServices.Settings.Current.WeChatPushUrl;

            if (!_wechatBaselineReady)
            {
                foreach (var kv in reader.SessionStates)
                {
                    _wechatLastSessionTs[kv.Key] = kv.Value.LastTimestamp;
                }
                foreach (var g in reader.Groups)
                {
                    _wechatGroupSnapshots[g.Username] = (g.GroupName, g.Members.Select(m => m.Username).ToHashSet(StringComparer.OrdinalIgnoreCase));
                }
                _wechatBaselineReady = true;
                return;
            }

            // Detect sessions whose LastTimestamp advanced
            foreach (var kv in reader.SessionStates)
            {
                string sessionKey = kv.Key;
                long ts = kv.Value.LastTimestamp;
                if (ts <= 0) continue;
                if (_wechatLastSessionTs.TryGetValue(sessionKey, out long prevTs) && ts <= prevTs)
                {
                    continue;
                }
                _wechatLastSessionTs[sessionKey] = ts;

                bool isGroup = sessionKey.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase);
                var recent = reader.ReadMessages(sessionKey, maxCount: 20, skipSystem: false);
                foreach (var m in recent)
                {
                    if (m.CreateTime <= prevTs) continue;
                    if (!_wechatPushedExtIds.Add(m.ExternalId)) continue;
                    if (_wechatPushedExtIds.Count > 10000) _wechatPushedExtIds.Clear();

                    if (m.IsOutgoing && IsRecentOutboundEcho("wechat", sessionKey, m.Content))
                    {
                        continue;
                    }

                    // Auto-save voice WAV/SILK when type=47 was configured
                    if (_wechatVoiceAutoSaveEnabled && m.Kind == "voice" && !string.IsNullOrWhiteSpace(_wechatVoiceSaveDir))
                    {
                        try
                        {
                            byte[]? wav = reader.TryResolveVoiceWavBytes(sessionKey, m);
                            if (wav is { Length: > 0 })
                            {
                                string baseId = !string.IsNullOrWhiteSpace(m.ServerId) ? m.ServerId : m.LocalId.ToString();
                                File.WriteAllBytes(Path.Combine(_wechatVoiceSaveDir, baseId + ".wav"), wav);
                            }
                        }
                        catch { }
                    }

                    if (!string.IsNullOrWhiteSpace(pushUrl))
                    {
                        byte[] json = BuildPersonalMessagePushJson(reader.SelfWxid, sessionKey, isGroup, m);
                        _ = PostPushBestEffortAsync(pushUrl, json);
                    }
                }
            }

            // Group metadata diffs (name changed / member joined / member left)
            foreach (var g in reader.Groups)
            {
                var curMembers = g.Members.Select(m => m.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (_wechatGroupSnapshots.TryGetValue(g.Username, out var prev) && !string.IsNullOrWhiteSpace(pushUrl))
                {
                    if (!string.IsNullOrWhiteSpace(prev.Name) && !string.IsNullOrWhiteSpace(g.GroupName) &&
                        !string.Equals(prev.Name, g.GroupName, StringComparison.Ordinal))
                    {
                        _ = PostPushBestEffortAsync(pushUrl, WriteJson(w =>
                        {
                            w.WriteStartObject();
                            w.WriteNumber("type", 300);
                            w.WritePropertyName("data");
                            w.WriteStartObject();
                            w.WriteString("chat_room_id", g.Username);
                            w.WriteString("old_name", prev.Name);
                            w.WriteString("new_name", g.GroupName);
                            w.WriteEndObject();
                            w.WriteEndObject();
                        }));
                    }

                    var joined = curMembers.Except(prev.Members, StringComparer.OrdinalIgnoreCase).ToList();
                    if (joined.Count > 0)
                    {
                        _ = PostPushBestEffortAsync(pushUrl, WriteJson(w =>
                        {
                            w.WriteStartObject();
                            w.WriteNumber("type", 333);
                            w.WritePropertyName("data");
                            w.WriteStartObject();
                            w.WriteString("chat_room_id", g.Username);
                            w.WritePropertyName("member_list");
                            w.WriteStartArray();
                            foreach (string id in joined) w.WriteStringValue(id);
                            w.WriteEndArray();
                            w.WriteEndObject();
                            w.WriteEndObject();
                        }));
                    }

                    var left = prev.Members.Except(curMembers, StringComparer.OrdinalIgnoreCase).ToList();
                    if (left.Count > 0)
                    {
                        _ = PostPushBestEffortAsync(pushUrl, WriteJson(w =>
                        {
                            w.WriteStartObject();
                            w.WriteNumber("type", 444);
                            w.WritePropertyName("data");
                            w.WriteStartObject();
                            w.WriteString("chat_room_id", g.Username);
                            w.WritePropertyName("member_list");
                            w.WriteStartArray();
                            foreach (string id in left) w.WriteStringValue(id);
                            w.WriteEndArray();
                            w.WriteEndObject();
                            w.WriteEndObject();
                        }));
                    }
                }
                _wechatGroupSnapshots[g.Username] = (g.GroupName, curMembers);
            }
        }

        private static async Task PostPushBestEffortAsync(string url, byte[] jsonBytes)
        {
            try
            {
                using var content = new ByteArrayContent(jsonBytes);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                using var resp = await PushHttp.PostAsync(url, content);
            }
            catch
            {
                // Connector receiver may not be listening yet; ignore quietly
            }
        }

        // =========================================================================
        // JSON Writers (Trim-Safe Utf8JsonWriter)
        // =========================================================================

        private static byte[] BuildWeComMessagePushJson(WeComMessage m) =>
            WriteJson(w =>
            {
                w.WriteStartObject();
                w.WriteNumber("type", m.IsGroup ? 11042 : 11041);
                w.WritePropertyName("data");
                w.WriteStartObject();
                w.WriteString("msg_id", m.ServerId > 0 ? m.ServerId.ToString() : m.MessageId.ToString());
                w.WriteNumber("message_id", m.MessageId);
                w.WriteString("conversation_id", m.ConversationId);
                w.WriteString("external_id", m.IsGroup ? m.ConversationId : m.PeerId);
                w.WriteString("chat_room_id", m.IsGroup ? m.ConversationId : string.Empty);
                w.WriteString("user_id", m.IsGroup ? m.SenderId : m.PeerId);
                w.WriteString("sender_id", m.SenderId);
                w.WriteString("sender_name", m.SenderName);
                w.WriteString("sender_type", m.IsOutgoing ? "user" : "customer");
                w.WriteString("target_type", m.IsGroup ? "group" : "person");
                w.WriteString("content_type", m.Kind);
                w.WriteNumber("raw_content_type", m.ContentType);
                w.WriteString("content", m.Content);
                w.WriteNumber("timestamp", m.SendTime);
                w.WritePropertyName("at_list");
                w.WriteStartArray();
                foreach (string a in m.AtUserIds) w.WriteStringValue(a);
                w.WriteEndArray();
                if (!string.IsNullOrEmpty(m.CdnKey)) w.WriteString("cdn_key", m.CdnKey);
                if (!string.IsNullOrEmpty(m.AesKey)) w.WriteString("aes_key", m.AesKey);
                if (!string.IsNullOrEmpty(m.FileMd5)) w.WriteString("md5", m.FileMd5);
                if (!string.IsNullOrEmpty(m.FileName)) w.WriteString("file_name", m.FileName);
                if (m.FileSize > 0) w.WriteNumber("size", m.FileSize);
                if (!string.IsNullOrEmpty(m.MediaUrl)) w.WriteString("url", m.MediaUrl);
                if (!string.IsNullOrEmpty(m.LocalFilePath)) w.WriteString("local_path", m.LocalFilePath);
                w.WriteEndObject();
                w.WriteEndObject();
            });

        private static byte[] BuildPersonalMessagePushJson(string selfWxid, string sessionKey, bool isGroup, WeChat4Message m) =>
            WriteJson(w =>
            {
                w.WriteStartObject();
                w.WriteNumber("type", isGroup ? 1002 : 1001);
                w.WritePropertyName("data");
                w.WriteStartObject();
                w.WriteString("self_wx_id", selfWxid);
                w.WriteString("msg_id", !string.IsNullOrWhiteSpace(m.ServerId) ? m.ServerId : m.LocalId.ToString());
                w.WriteString("clientmsgid", m.LocalId.ToString());
                w.WriteString("external_id", sessionKey);
                w.WriteString("wx_id", isGroup ? m.SenderWxid : sessionKey);
                w.WriteString("chat_room_id", isGroup ? sessionKey : string.Empty);
                w.WriteString("sender_external_id", isGroup ? m.SenderWxid : string.Empty);
                w.WriteString("sender_name", m.SenderName);
                w.WriteString("sender_type", m.IsOutgoing ? "user" : "customer");
                w.WriteString("target_type", isGroup ? "group" : "person");
                w.WriteString("content_type", m.Kind);
                w.WriteNumber("msg_type", m.BaseType);
                w.WriteString("content", m.Content);
                w.WriteNumber("timestamp", m.CreateTime);
                w.WritePropertyName("at_list");
                w.WriteStartArray();
                if (m.MentionedAll) w.WriteStringValue("notify@all");
                else if (m.MentionedSelf && !string.IsNullOrWhiteSpace(selfWxid)) w.WriteStringValue(selfWxid);
                w.WriteEndArray();
                if (m.MediaHints.Count > 0) w.WriteString("file_id", m.MediaHints[0]);
                w.WriteEndObject();
                w.WriteEndObject();
            });

        private static void WriteWeComUserObject(Utf8JsonWriter w, WeComUser u)
        {
            w.WriteStartObject();
            w.WriteString("user_id", u.UserId);
            w.WriteString("name", u.Name);
            w.WriteString("nick_name", u.DisplayName);
            w.WriteString("real_name", u.RealName);
            w.WriteString("remark", u.Remark);
            w.WriteString("remark_name", u.Remark);
            w.WriteString("desc", u.Description);
            w.WriteString("account", u.Account);
            w.WriteString("mobile", u.Mobile);
            w.WriteString("phone", u.Phone);
            w.WriteString("email", u.Email);
            w.WriteString("avatar_url", u.AvatarUrl);
            w.WriteString("corp_id", u.CorpId);
            w.WriteString("company", u.ExternalCorpName);
            w.WriteString("position", u.Position);
            w.WriteString("unionid", u.UnionId);
            w.WriteNumber("gender", u.Gender);
            w.WriteBoolean("is_external", u.IsExternal);
            w.WriteEndObject();
        }

        private static void WriteWeComGroupObject(Utf8JsonWriter w, WeComGroup g)
        {
            w.WriteStartObject();
            w.WriteString("chat_room_id", g.ConversationId);
            w.WriteString("room_id", g.RoomId);
            w.WriteString("name", g.DisplayName);
            w.WriteString("group_name", g.DisplayName);
            w.WriteString("remark", g.Remark);
            w.WriteString("owner_id", g.OwnerUserId);
            w.WriteString("notice", g.Announcement);
            w.WriteString("avatar_url", g.AvatarUrl);
            w.WriteNumber("member_count", g.Members.Count);
            w.WriteNumber("create_time", g.CreateTime);
            w.WriteNumber("last_message_time", g.LastMessageTime);
            w.WriteEndObject();
        }

        private static void WritePersonalContactObject(Utf8JsonWriter w, WeChat4Contact f)
        {
            w.WriteStartObject();
            w.WriteString("wx_id", f.Username);
            w.WriteString("wxid", f.Username);
            w.WriteString("account", f.Alias);
            w.WriteString("wechat", f.Alias);
            w.WriteString("nick_name", f.NickName);
            w.WriteString("name", f.NickName);
            w.WriteString("remark", f.Remark);
            w.WriteString("remark_name", f.Remark);
            w.WriteString("desc", f.Description);
            w.WriteString("notes", f.Description);
            w.WriteString("avatar_url", f.AvatarUrl);
            w.WriteString("phone", f.Phone);
            w.WriteString("region", f.Region);
            w.WriteString("signature", f.Signature);
            w.WriteString("tags", f.Tags);
            w.WriteNumber("gender", ParseGenderInt(f.Gender));
            w.WriteEndObject();
        }

        private static void WritePersonalGroupObject(Utf8JsonWriter w, WeChat4Group g)
        {
            w.WriteStartObject();
            w.WriteString("chat_room_id", g.Username);
            w.WriteString("wx_id", g.Username);
            w.WriteString("name", g.GroupName);
            w.WriteString("nick_name", g.GroupName);
            w.WriteString("announcement", g.Announcement);
            w.WriteString("avatar_url", g.AvatarUrl);
            w.WriteNumber("member_count", g.MemberCount);
            w.WriteEndObject();
        }

        private byte[] BuildHealthJson(bool isWeCom) =>
            WriteJson(w =>
            {
                w.WriteStartObject();
                w.WriteString("status", "ok");
                w.WriteString("platform", isWeCom ? "wechat_work" : "wechat");
                w.WriteString("mode", "readonly_db_and_rpa");
                if (isWeCom)
                {
                    int pid = WeComPaths.GetPrimaryWXWorkPid();
                    w.WriteNumber("pid", pid);
                    w.WriteString("account_id", AppServices.WeComSync.ActiveAccount?.AccountId ?? string.Empty);
                    w.WriteBoolean("db_ready", AppServices.WeComSync.ActiveReader is not null);
                }
                else
                {
                    var procs = WeChat4Paths.FindWeixinProcesses();
                    w.WriteNumber("pid", procs.Count > 0 ? procs[0].Pid : 0);
                    w.WriteString("wx_id", AppServices.WeChat4Sync.ActiveOwnerWxid);
                    w.WriteBoolean("db_ready", AppServices.WeChat4Sync.ActiveReader is not null);
                }
                w.WriteEndObject();
            });

        private static byte[] BuildSimpleErrorJson(bool isWeCom, int code, string msg) =>
            isWeCom
                ? WriteJson(w =>
                {
                    w.WriteStartObject();
                    w.WriteNumber("code", code);
                    w.WriteString("error", msg);
                    w.WriteString("msg", msg);
                    w.WriteEndObject();
                })
                : WritePersonalErr(code, msg);

        private static byte[] WritePersonalOk(Action<Utf8JsonWriter> writeData) =>
            WriteJson(w =>
            {
                w.WriteStartObject();
                w.WriteNumber("errno", 0);
                w.WriteString("errmsg", "OK");
                w.WritePropertyName("data");
                writeData(w);
                w.WriteEndObject();
            });

        private static byte[] WritePersonalErr(int errno, string errmsg) =>
            WriteJson(w =>
            {
                w.WriteStartObject();
                w.WriteNumber("errno", errno);
                w.WriteString("errmsg", errmsg);
                w.WritePropertyName("data");
                w.WriteStartObject();
                w.WriteEndObject();
                w.WriteEndObject();
            });

        private static byte[] WriteJson(Action<Utf8JsonWriter> writeAction)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                writeAction(w);
            }
            return ms.ToArray();
        }

        private static async Task WriteResponseAsync(HttpListenerResponse resp, int statusCode, byte[] body)
        {
            try
            {
                resp.StatusCode = statusCode;
                resp.ContentType = "application/json; charset=utf-8";
                resp.ContentLength64 = body.Length;
                await resp.OutputStream.WriteAsync(body, 0, body.Length);
                resp.OutputStream.Close();
            }
            catch { }
        }

        // ---- Outbound Fingerprint Dedup ----

        private void RecordOutboundFingerprint(string platform, string targetId, string content)
        {
            string key = $"{platform}|{targetId}|{NormalizeFp(content)}";
            _recentOutboundFingerprints[key] = DateTimeOffset.UtcNow;
            if (_recentOutboundFingerprints.Count > 256)
            {
                var cutoff = DateTimeOffset.UtcNow.AddSeconds(-60);
                foreach (var kv in _recentOutboundFingerprints)
                {
                    if (kv.Value < cutoff) _recentOutboundFingerprints.TryRemove(kv.Key, out _);
                }
            }
        }

        private bool IsRecentOutboundEcho(string platform, string targetId, string content)
        {
            string key = $"{platform}|{targetId}|{NormalizeFp(content)}";
            if (_recentOutboundFingerprints.TryGetValue(key, out var ts))
            {
                if ((DateTimeOffset.UtcNow - ts).TotalSeconds <= 60)
                {
                    return true;
                }
                _recentOutboundFingerprints.TryRemove(key, out _);
            }
            return false;
        }

        private static string NormalizeFp(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            string compact = new(s.Where(c => !char.IsWhiteSpace(c)).Take(48).ToArray());
            return compact.ToLowerInvariant();
        }

        // ---- JsonElement Helpers ----

        private static bool TryGetInt(JsonElement obj, string prop, out int value)
        {
            value = 0;
            if (!obj.TryGetProperty(prop, out var el)) return false;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value)) return true;
            if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out value)) return true;
            return false;
        }

        private static int GetIntOrDefault(JsonElement obj, string prop, int fallback) =>
            TryGetInt(obj, prop, out int v) ? v : fallback;

        private static string GetString(JsonElement obj, params string[] names)
        {
            foreach (string name in names)
            {
                if (!obj.TryGetProperty(name, out var el)) continue;
                if (el.ValueKind == JsonValueKind.String) return (el.GetString() ?? string.Empty).Trim();
                if (el.ValueKind == JsonValueKind.Number) return el.GetRawText();
            }
            return string.Empty;
        }

        private static List<string> GetStringArray(JsonElement obj, string prop)
        {
            var list = new List<string>();
            if (!obj.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    string s = (item.GetString() ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(s)) list.Add(s);
                }
                else if (item.ValueKind == JsonValueKind.Number)
                {
                    list.Add(item.GetRawText());
                }
            }
            return list;
        }

        private static int ParseGenderInt(string? gender) => (gender ?? string.Empty).Trim() switch
        {
            "男" or "1" or "male" or "Male" => 1,
            "女" or "2" or "female" or "Female" => 2,
            _ => 0
        };

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _wechatSendGate.Dispose();
            _wecomSendGate.Dispose();
        }
    }
}
