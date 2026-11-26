using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Client.Models;

namespace Client.Services
{
    public sealed class ApiResult<T>
    {
        public bool IsSuccess { get; init; }
        public T? Data { get; init; }
        public HttpStatusCode StatusCode { get; init; }
        public string ErrorMessage { get; init; } = string.Empty;

        public static ApiResult<T> Ok(T data, HttpStatusCode statusCode = HttpStatusCode.OK) =>
            new() { IsSuccess = true, Data = data, StatusCode = statusCode };

        public static ApiResult<T> Fail(string message, HttpStatusCode statusCode = 0) =>
            new() { IsSuccess = false, ErrorMessage = message, StatusCode = statusCode };
    }

    public sealed class BackendApiClient
    {
        private readonly HttpClient _httpClient;
        private readonly HttpClient _aiHttpClient;
        private readonly ConcurrentDictionary<string, byte[]> _avatarCache = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte[]> _chatImageCache = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte[]> _chatAudioCache = new(StringComparer.Ordinal);
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public BackendApiClient()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            _aiHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(130) };
        }

        public string BaseUrl { get; private set; } = "http://127.0.0.1:28765";
        public string AdminWebUrl => $"{BaseUrl}/admin";
        public string? AccessToken { get; private set; }
        public EmployeeIdentity? CurrentEmployee { get; private set; }
        public bool IsAuthenticated => !string.IsNullOrEmpty(AccessToken) && CurrentEmployee is not null;

        public void SetBaseUrl(string rawUrl)
        {
            var trimmed = rawUrl.Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                throw new ArgumentException("服务地址不能为空。");
            }
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException("请输入有效的 http:// 或 https:// 服务地址。");
            }
            BaseUrl = trimmed;
        }

        public async Task<ApiResult<string>> CheckHealthAsync(CancellationToken ct = default)
        {
            try
            {
                using var resp = await _httpClient.GetAsync($"{BaseUrl}/healthz", ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode)
                {
                    AppServices.Logs.Success("API", $"服务连接正常 ({BaseUrl})。");
                    return ApiResult<string>.Ok($"服务正常 ({(int)resp.StatusCode})", resp.StatusCode);
                }
                AppServices.Logs.Warn("API", $"服务状态异常 ({(int)resp.StatusCode})：{BaseUrl}");
                return ApiResult<string>.Fail($"服务检查失败 ({(int)resp.StatusCode}): {body}", resp.StatusCode);
            }
            catch (Exception ex)
            {
                AppServices.Logs.Error("API", $"无法连接服务 ({BaseUrl})：{ex.Message}");
                return ApiResult<string>.Fail($"无法连接服务 ({BaseUrl})：{ex.Message}");
            }
        }

        public async Task<ApiResult<LoginResponse>> LoginAsync(string account, string password, CancellationToken ct = default)
        {
            try
            {
                var payload = new LoginRequest { Account = account.Trim(), Password = password };
                AppServices.Logs.Info("AUTH", $"正在验证账号「{payload.Account}」...");
                using var resp = await _httpClient.PostAsJsonAsync($"{BaseUrl}/api/v1/login", payload, JsonOptions, ct);
                if (resp.IsSuccessStatusCode)
                {
                    var data = await resp.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions, ct);
                    if (data is null || string.IsNullOrWhiteSpace(data.Token))
                    {
                        AppServices.Logs.Error("AUTH", "登录失败：返回缺少有效令牌。");
                        return ApiResult<LoginResponse>.Fail("登录响应缺少有效令牌。", resp.StatusCode);
                    }
                    AccessToken = data.Token;
                    CurrentEmployee = data.Employee;
                    AppServices.Logs.Success("AUTH", $"账号「{payload.Account}」登录成功。");
                    return ApiResult<LoginResponse>.Ok(data, resp.StatusCode);
                }
                string err = await ReadFriendlyErrorAsync(resp, ct);
                AppServices.Logs.Warn("AUTH", $"账号「{payload.Account}」登录被拒绝：{err}");
                return ApiResult<LoginResponse>.Fail(err, resp.StatusCode);
            }
            catch (Exception ex)
            {
                AppServices.Logs.Error("AUTH", $"登录请求异常：{ex.Message}");
                return ApiResult<LoginResponse>.Fail($"登录请求失败：{ex.Message}");
            }
        }

        public async Task<ApiResult<bool>> LogoutAsync(CancellationToken ct = default)
        {
            string? account = CurrentEmployee?.Account;
            try
            {
                if (!string.IsNullOrEmpty(AccessToken))
                {
                    using var req = CreateAuthedRequest(HttpMethod.Post, "/api/v1/logout");
                    using var resp = await _httpClient.SendAsync(req, ct);
                    ClearSession();
                    AppServices.Logs.Info("AUTH", $"账号「{account ?? "-"}」已退出登录。");
                    return ApiResult<bool>.Ok(true, resp.StatusCode);
                }
                ClearSession();
                return ApiResult<bool>.Ok(true);
            }
            catch (Exception ex)
            {
                ClearSession();
                AppServices.Logs.Warn("AUTH", $"退出登录时通知服务端异常：{ex.Message}");
                return ApiResult<bool>.Fail($"本地会话已清除，通知服务端登出失败：{ex.Message}");
            }
        }

        // ---- Customers ----

        public Task<ApiResult<CustomerListResponse>> ListCustomersAsync(int limit = 100, long cursor = 0, CancellationToken ct = default)
        {
            string qs = cursor > 0 ? $"?limit={limit}&cursor={cursor}" : $"?limit={limit}";
            return SendAuthedAsync<CustomerListResponse>(HttpMethod.Get, $"/api/v1/customers{qs}", null, ct);
        }

        public async Task<List<CustomerDto>> ListAllCustomersAsync(int maxItems = 2000, CancellationToken ct = default)
        {
            var all = new List<CustomerDto>();
            long cursor = 0;
            while (all.Count < maxItems)
            {
                var res = await ListCustomersAsync(limit: 100, cursor: cursor, ct: ct);
                if (!res.IsSuccess || res.Data is null || res.Data.Items.Count == 0)
                {
                    break;
                }
                all.AddRange(res.Data.Items);
                if (res.Data.NextCursor <= 0)
                {
                    break;
                }
                cursor = res.Data.NextCursor;
            }
            return all;
        }

        /// <summary>
        /// Finds an existing duplicate customer safely:
        /// 1. Matches exact WechatAccount first.
        /// 2. Falls back to WechatName / WechatRemark ONLY when incoming account is empty OR the existing record has no WechatAccount,
        ///    preventing false collisions between two different contacts who share a common nickname/remark.
        /// </summary>
        public static CustomerDto? FindDuplicateCustomer(
            IEnumerable<CustomerDto> customers,
            string account,
            string nickname,
            string remark)
        {
            string acc = account.Trim();
            string name = nickname.Trim();
            string rem = remark.Trim();

            if (!string.IsNullOrEmpty(acc))
            {
                var byAcc = customers.FirstOrDefault(c =>
                    !string.IsNullOrWhiteSpace(c.WechatAccount) &&
                    c.WechatAccount.Equals(acc, StringComparison.OrdinalIgnoreCase));
                if (byAcc is not null) return byAcc;
            }

            bool CanMatchByNameOrRemark(CustomerDto c) =>
                string.IsNullOrEmpty(acc) || string.IsNullOrWhiteSpace(c.WechatAccount);

            if (!string.IsNullOrEmpty(name))
            {
                var byName = customers.FirstOrDefault(c =>
                    CanMatchByNameOrRemark(c) &&
                    !string.IsNullOrWhiteSpace(c.WechatName) &&
                    c.WechatName.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (byName is not null) return byName;
            }

            if (!string.IsNullOrEmpty(rem))
            {
                var byRem = customers.FirstOrDefault(c =>
                    CanMatchByNameOrRemark(c) &&
                    !string.IsNullOrWhiteSpace(c.WechatRemark) &&
                    c.WechatRemark.Equals(rem, StringComparison.OrdinalIgnoreCase));
                if (byRem is not null) return byRem;
            }

            return null;
        }

        public Task<ApiResult<CustomerDto>> CreateCustomerAsync(CreateCustomerRequest payload, CancellationToken ct = default) =>
            SendAuthedAsync<CustomerDto>(HttpMethod.Post, "/api/v1/customers", payload, ct);

        public Task<ApiResult<CustomerDto>> UpdateCustomerAsync(long customerId, CreateCustomerRequest payload, CancellationToken ct = default) =>
            SendAuthedAsync<CustomerDto>(HttpMethod.Patch, $"/api/v1/customers/{customerId}", payload, ct);

        public Task<ApiResult<bool>> DeleteCustomerAsync(long customerId, CancellationToken ct = default) =>
            SendAuthedNoContentAsync(HttpMethod.Delete, $"/api/v1/customers/{customerId}", ct);

        public async Task<ApiResult<CustomerDto>> UploadCustomerAvatarAsync(long customerId, byte[] imageBytes, CancellationToken ct = default)
        {
            string b64 = Convert.ToBase64String(imageBytes);
            var res = await SendAuthedAsync<CustomerDto>(
                HttpMethod.Post,
                $"/api/v1/customers/{customerId}/avatar",
                new UploadAvatarRequest { ImageBase64 = b64 },
                ct);
            if (res.IsSuccess && res.Data is not null && !string.IsNullOrWhiteSpace(res.Data.AvatarUrl))
            {
                _avatarCache[res.Data.AvatarUrl] = imageBytes;
            }
            return res;
        }

        public async Task<ApiResult<CustomerDto>> DeleteCustomerAvatarAsync(long customerId, string? oldAvatarUrl = null, CancellationToken ct = default)
        {
            var res = await SendAuthedAsync<CustomerDto>(HttpMethod.Delete, $"/api/v1/customers/{customerId}/avatar", null, ct);
            if (res.IsSuccess && !string.IsNullOrWhiteSpace(oldAvatarUrl))
            {
                _avatarCache.TryRemove(oldAvatarUrl, out _);
            }
            return res;
        }

        public async Task<byte[]?> DownloadAvatarBytesAsync(string? avatarUrl, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(avatarUrl) || !IsAuthenticated) return null;
            if (_avatarCache.TryGetValue(avatarUrl, out var cached)) return cached;

            try
            {
                // Only send Bearer token to relative paths on our configured backend host.
                if (avatarUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    avatarUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    if (!avatarUrl.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }
                }
                string path = avatarUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? avatarUrl
                    : $"{BaseUrl}{(avatarUrl.StartsWith('/') ? avatarUrl : "/" + avatarUrl)}";
                using var req = new HttpRequestMessage(HttpMethod.Get, path);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
                using var resp = await _httpClient.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) return null;

                byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length > 0)
                {
                    _avatarCache[avatarUrl] = bytes;
                }
                return bytes;
            }
            catch
            {
                return null;
            }
        }

        public async Task<ApiResult<UploadChatImageResponse>> UploadChatImageAsync(byte[] imageBytes, CancellationToken ct = default)
        {
            string b64 = Convert.ToBase64String(imageBytes);
            var res = await SendAuthedAsync<UploadChatImageResponse>(
                HttpMethod.Post,
                "/api/v1/chat-images",
                new UploadAvatarRequest { ImageBase64 = b64 },
                ct);
            if (res.IsSuccess && res.Data is not null && !string.IsNullOrWhiteSpace(res.Data.ImageUrl))
            {
                _chatImageCache[res.Data.ImageUrl] = imageBytes;
            }
            return res;
        }

        public async Task<byte[]?> DownloadChatImageBytesAsync(string? imageUrl, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(imageUrl) || !IsAuthenticated) return null;
            if (_chatImageCache.TryGetValue(imageUrl, out var cached)) return cached;

            try
            {
                if (imageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    if (!imageUrl.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }
                }
                string path = imageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? imageUrl
                    : $"{BaseUrl}{(imageUrl.StartsWith('/') ? imageUrl : "/" + imageUrl)}";
                using var req = new HttpRequestMessage(HttpMethod.Get, path);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
                using var resp = await _httpClient.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) return null;

                byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length > 0)
                {
                    _chatImageCache[imageUrl] = bytes;
                }
                return bytes;
            }
            catch
            {
                return null;
            }
        }

        public async Task<ApiResult<UploadChatAudioResponse>> UploadChatAudioAsync(byte[] audioBytes, CancellationToken ct = default)
        {
            string b64 = Convert.ToBase64String(audioBytes);
            var res = await SendAuthedAsync<UploadChatAudioResponse>(
                HttpMethod.Post,
                "/api/v1/chat-audio",
                new UploadAudioRequest { AudioBase64 = b64 },
                ct);
            if (res.IsSuccess && res.Data is not null && !string.IsNullOrWhiteSpace(res.Data.AudioUrl))
            {
                _chatAudioCache[res.Data.AudioUrl] = audioBytes;
            }
            return res;
        }

        public async Task<byte[]?> DownloadChatAudioBytesAsync(string? audioUrl, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(audioUrl) || !IsAuthenticated) return null;
            if (_chatAudioCache.TryGetValue(audioUrl, out var cached)) return cached;

            try
            {
                if (audioUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    audioUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    if (!audioUrl.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }
                }
                string path = audioUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? audioUrl
                    : $"{BaseUrl}{(audioUrl.StartsWith('/') ? audioUrl : "/" + audioUrl)}";
                using var req = new HttpRequestMessage(HttpMethod.Get, path);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
                using var resp = await _httpClient.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) return null;

                byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length > 0)
                {
                    _chatAudioCache[audioUrl] = bytes;
                }
                return bytes;
            }
            catch
            {
                return null;
            }
        }

        public async Task<ApiResult<UploadChatVideoResponse>> UploadChatVideoFileAsync(string localFilePath, CancellationToken ct = default)
        {
            if (!IsAuthenticated || string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath))
            {
                return ApiResult<UploadChatVideoResponse>.Fail("本地视频文件不存在。");
            }

            try
            {
                var fi = new FileInfo(localFilePath);
                if (fi.Length < 16 || fi.Length > 75L * 1024 * 1024)
                {
                    return ApiResult<UploadChatVideoResponse>.Fail("视频文件过大（上限 75 MB）或无效。");
                }

                using var fs = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 256 * 1024);
                using var content = new StreamContent(fs);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/v1/chat-video") { Content = content };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

                using var resp = await _httpClient.SendAsync(req, ct);
                string body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    return ApiResult<UploadChatVideoResponse>.Fail($"HTTP {(int)resp.StatusCode}: {body}");
                }
                var data = JsonSerializer.Deserialize<UploadChatVideoResponse>(body, JsonOptions);
                return data is not null
                    ? ApiResult<UploadChatVideoResponse>.Ok(data)
                    : ApiResult<UploadChatVideoResponse>.Fail("上传视频响应为空。");
            }
            catch (Exception ex)
            {
                return ApiResult<UploadChatVideoResponse>.Fail(ex.Message);
            }
        }

        public async Task<string?> DownloadChatVideoToTempFileAsync(string? videoUrl, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(videoUrl) || !IsAuthenticated) return null;
            try
            {
                if ((videoUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     videoUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
                    !videoUrl.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                string safeName = Path.GetFileName(videoUrl.Split('?')[0]);
                if (string.IsNullOrWhiteSpace(safeName)) safeName = "video.mp4";
                string cacheDir = Path.Combine(Path.GetTempPath(), "weixinORC_video_cache");
                Directory.CreateDirectory(cacheDir);
                string localPath = Path.Combine(cacheDir, safeName);
                if (File.Exists(localPath) && new FileInfo(localPath).Length > 1024)
                {
                    return localPath;
                }

                string url = videoUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? videoUrl
                    : $"{BaseUrl}{(videoUrl.StartsWith('/') ? videoUrl : "/" + videoUrl)}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
                using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode) return null;

                string tmpPath = localPath + ".part";
                await using (var src = await resp.Content.ReadAsStreamAsync(ct))
                await using (var dst = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024))
                {
                    await src.CopyToAsync(dst, ct);
                }
                File.Move(tmpPath, localPath, overwrite: true);
                return localPath;
            }
            catch
            {
                return null;
            }
        }

        public Task<ApiResult<MessageListResponse>> ListCustomerMessagesAsync(long customerId, int limit = 100, long cursor = 0, CancellationToken ct = default)
        {
            string qs = cursor > 0 ? $"?limit={limit}&cursor={cursor}" : $"?limit={limit}";
            return SendAuthedAsync<MessageListResponse>(HttpMethod.Get, $"/api/v1/customers/{customerId}/messages{qs}", null, ct);
        }

        public Task<ApiResult<MessageDto>> CreateCustomerMessageAsync(
            long customerId, string direction, string content, string? imageUrl = null, string? sentAtIso = null, CancellationToken ct = default) =>
            SendAuthedAsync<MessageDto>(HttpMethod.Post, $"/api/v1/customers/{customerId}/messages",
                new CreateMessageRequest
                {
                    Direction = direction,
                    Content = content.Trim(),
                    ImageUrl = imageUrl?.Trim() ?? string.Empty,
                    SentAt = string.IsNullOrWhiteSpace(sentAtIso) ? null : sentAtIso.Trim()
                }, ct);

        public Task<ApiResult<MessageDto>> UpdateCustomerMessageAsync(
            long customerId, long messageId, string direction, string content, string? imageUrl = null, CancellationToken ct = default) =>
            SendAuthedAsync<MessageDto>(HttpMethod.Patch, $"/api/v1/customers/{customerId}/messages/{messageId}",
                new CreateMessageRequest
                {
                    Direction = direction,
                    Content = content.Trim(),
                    ImageUrl = imageUrl?.Trim() ?? string.Empty
                }, ct);

        // ---- Groups ----

        public Task<ApiResult<GroupListResponse>> ListGroupsAsync(int limit = 100, long cursor = 0, CancellationToken ct = default)
        {
            string qs = cursor > 0 ? $"?limit={limit}&cursor={cursor}" : $"?limit={limit}";
            return SendAuthedAsync<GroupListResponse>(HttpMethod.Get, $"/api/v1/groups{qs}", null, ct);
        }

        public async Task<List<GroupDto>> ListAllGroupsAsync(int maxItems = 1000, CancellationToken ct = default)
        {
            var all = new List<GroupDto>();
            long cursor = 0;
            while (all.Count < maxItems)
            {
                var res = await ListGroupsAsync(limit: 100, cursor: cursor, ct: ct);
                if (!res.IsSuccess || res.Data is null || res.Data.Items.Count == 0)
                {
                    break;
                }
                all.AddRange(res.Data.Items);
                if (res.Data.NextCursor <= 0)
                {
                    break;
                }
                cursor = res.Data.NextCursor;
            }
            return all;
        }

        public async Task<ApiResult<GroupDto>> UploadGroupAvatarAsync(long groupId, byte[] imageBytes, CancellationToken ct = default)
        {
            string b64 = Convert.ToBase64String(imageBytes);
            var res = await SendAuthedAsync<GroupDto>(
                HttpMethod.Post,
                $"/api/v1/groups/{groupId}/avatar",
                new UploadAvatarRequest { ImageBase64 = b64 },
                ct);
            if (res.IsSuccess && res.Data is not null && !string.IsNullOrWhiteSpace(res.Data.AvatarUrl))
            {
                _avatarCache[res.Data.AvatarUrl] = imageBytes;
            }
            return res;
        }

        public Task<ApiResult<GroupDto>> CreateGroupAsync(CreateGroupRequest payload, CancellationToken ct = default) =>
            SendAuthedAsync<GroupDto>(HttpMethod.Post, "/api/v1/groups", payload, ct);

        public Task<ApiResult<GroupDto>> UpdateGroupAsync(long groupId, CreateGroupRequest payload, CancellationToken ct = default) =>
            SendAuthedAsync<GroupDto>(HttpMethod.Patch, $"/api/v1/groups/{groupId}", payload, ct);

        public Task<ApiResult<bool>> DeleteGroupAsync(long groupId, CancellationToken ct = default) =>
            SendAuthedNoContentAsync(HttpMethod.Delete, $"/api/v1/groups/{groupId}", ct);

        public Task<ApiResult<GroupMemberListResponse>> ListGroupMembersAsync(long groupId, int limit = 100, long cursor = 0, CancellationToken ct = default)
        {
            string qs = cursor > 0 ? $"?limit={limit}&cursor={cursor}" : $"?limit={limit}";
            return SendAuthedAsync<GroupMemberListResponse>(HttpMethod.Get, $"/api/v1/groups/{groupId}/members{qs}", null, ct);
        }

        public async Task<List<GroupMemberDto>> ListAllGroupMembersAsync(long groupId, int maxItems = 500, CancellationToken ct = default)
        {
            var all = new List<GroupMemberDto>();
            long cursor = 0;
            while (all.Count < maxItems)
            {
                var res = await ListGroupMembersAsync(groupId, limit: 100, cursor: cursor, ct: ct);
                if (!res.IsSuccess || res.Data is null || res.Data.Items.Count == 0) break;
                all.AddRange(res.Data.Items);
                if (res.Data.NextCursor <= 0) break;
                cursor = res.Data.NextCursor;
            }
            return all;
        }

        public Task<ApiResult<GroupMemberDto>> CreateGroupMemberAsync(long groupId, CreateGroupMemberRequest payload, CancellationToken ct = default) =>
            SendAuthedAsync<GroupMemberDto>(HttpMethod.Post, $"/api/v1/groups/{groupId}/members", payload, ct);

        public Task<ApiResult<GroupMemberDto>> UpdateGroupMemberAsync(long groupId, long memberId, CreateGroupMemberRequest payload, CancellationToken ct = default) =>
            SendAuthedAsync<GroupMemberDto>(HttpMethod.Patch, $"/api/v1/groups/{groupId}/members/{memberId}", payload, ct);

        public Task<ApiResult<bool>> DeleteGroupMemberAsync(long groupId, long memberId, CancellationToken ct = default) =>
            SendAuthedNoContentAsync(HttpMethod.Delete, $"/api/v1/groups/{groupId}/members/{memberId}", ct);

        public async Task<ApiResult<GroupMemberDto>> UploadGroupMemberAvatarAsync(long groupId, long memberId, byte[] imageBytes, CancellationToken ct = default)
        {
            string b64 = Convert.ToBase64String(imageBytes);
            var res = await SendAuthedAsync<GroupMemberDto>(
                HttpMethod.Post,
                $"/api/v1/groups/{groupId}/members/{memberId}/avatar",
                new UploadAvatarRequest { ImageBase64 = b64 },
                ct);
            if (res.IsSuccess && res.Data is not null && !string.IsNullOrWhiteSpace(res.Data.AvatarUrl))
            {
                _avatarCache[res.Data.AvatarUrl] = imageBytes;
            }
            return res;
        }

        public Task<ApiResult<GroupMessageListResponse>> ListGroupMessagesAsync(long groupId, int limit = 100, long cursor = 0, CancellationToken ct = default)
        {
            string qs = cursor > 0 ? $"?limit={limit}&cursor={cursor}" : $"?limit={limit}";
            return SendAuthedAsync<GroupMessageListResponse>(HttpMethod.Get, $"/api/v1/groups/{groupId}/messages{qs}", null, ct);
        }

        public Task<ApiResult<GroupMessageDto>> CreateGroupMessageAsync(
            long groupId, string senderName, string direction, string content, string? imageUrl = null, string? sentAtIso = null, CancellationToken ct = default) =>
            SendAuthedAsync<GroupMessageDto>(HttpMethod.Post, $"/api/v1/groups/{groupId}/messages",
                new CreateGroupMessageRequest
                {
                    SenderName = senderName.Trim(),
                    Direction = direction,
                    Content = content.Trim(),
                    ImageUrl = imageUrl?.Trim() ?? string.Empty,
                    SentAt = string.IsNullOrWhiteSpace(sentAtIso) ? null : sentAtIso.Trim()
                }, ct);

        public Task<ApiResult<GroupMessageDto>> UpdateGroupMessageAsync(
            long groupId, long messageId, string senderName, string direction, string content, string? imageUrl = null, CancellationToken ct = default) =>
            SendAuthedAsync<GroupMessageDto>(HttpMethod.Patch, $"/api/v1/groups/{groupId}/messages/{messageId}",
                new CreateGroupMessageRequest
                {
                    SenderName = senderName.Trim(),
                    Direction = direction,
                    Content = content.Trim(),
                    ImageUrl = imageUrl?.Trim() ?? string.Empty
                }, ct);

        // ---- Audit Logs ----

        public Task<ApiResult<AuditLogDto>> CreateAuditLogAsync(string action, string targetType, string targetId, string detail, CancellationToken ct = default) =>
            SendAuthedAsync<AuditLogDto>(HttpMethod.Post, "/api/v1/audit-logs",
                new CreateAuditLogRequest { Action = action, TargetType = targetType, TargetId = targetId, Detail = detail }, ct);

        // ---- AI Gateway & Built-in Agent Skills ----

        public Task<ApiResult<AiStatusResponse>> GetAiStatusAsync(CancellationToken ct = default) =>
            SendAuthedAsync<AiStatusResponse>(HttpMethod.Get, "/api/v1/ai/status", null, ct);

        public Task<ApiResult<AiCustomerSummaryResponse>> SummarizeCustomerWithAiAsync(long customerId, CancellationToken ct = default) =>
            SendAuthedLongRunningAsync<AiCustomerSummaryResponse>(HttpMethod.Post, "/api/v1/ai/customer-summary",
                new { customer_id = customerId }, ct);

        public Task<ApiResult<AiChatResponse>> CustomerAiChatAsync(
            long customerId,
            IEnumerable<AiChatTurnDto> history,
            string message,
            IEnumerable<string>? images = null,
            CancellationToken ct = default) =>
            SendAuthedLongRunningAsync<AiChatResponse>(HttpMethod.Post, "/api/v1/ai/customer-chat",
                new
                {
                    customer_id = customerId,
                    history = history.ToList(),
                    message = message.Trim(),
                    images = images?.ToList() ?? []
                }, ct);

        public Task<ApiResult<AiCustomerSummaryResponse>> SummarizeGroupWithAiAsync(long groupId, CancellationToken ct = default) =>
            SendAuthedLongRunningAsync<AiCustomerSummaryResponse>(HttpMethod.Post, "/api/v1/ai/group-summary",
                new { group_id = groupId }, ct);

        public Task<ApiResult<AiChatResponse>> GroupAiChatAsync(
            long groupId,
            IEnumerable<AiChatTurnDto> history,
            string message,
            IEnumerable<string>? images = null,
            CancellationToken ct = default) =>
            SendAuthedLongRunningAsync<AiChatResponse>(HttpMethod.Post, "/api/v1/ai/group-chat",
                new
                {
                    group_id = groupId,
                    history = history.ToList(),
                    message = message.Trim(),
                    images = images?.ToList() ?? []
                }, ct);

        public Task<ApiResult<AiChatResponse>> AgentAiChatAsync(
            IEnumerable<AiChatTurnDto> history,
            string message,
            IEnumerable<string>? images = null,
            CancellationToken ct = default) =>
            SendAuthedLongRunningAsync<AiChatResponse>(HttpMethod.Post, "/api/v1/ai/agent-chat",
                new
                {
                    history = history.ToList(),
                    message = message.Trim(),
                    images = images?.ToList() ?? []
                }, ct);

        public Task<ApiResult<AiTranscribeResponse>> TranscribeChatAudioAsync(
            string audioUrl,
            CancellationToken ct = default) =>
            SendAuthedLongRunningAsync<AiTranscribeResponse>(HttpMethod.Post, "/api/v1/ai/transcribe",
                new { audio_url = audioUrl.Trim() }, ct);

        // ---- WeChat 4.x Direct DB Batch Sync ----

        public Task<ApiResult<JsonElement>> UpsertWeChatAccountAsync(
            string accountType,
            string wechatId,
            string nickname,
            string status = "online",
            CancellationToken ct = default) =>
            SendAuthedAsync<JsonElement>(HttpMethod.Post, "/api/v1/wechat-accounts",
                new
                {
                    account_type = accountType,
                    wechat_id = wechatId,
                    nickname = nickname,
                    status = status
                }, ct);

        public Task<ApiResult<WeChatSyncCustomersResponse>> SyncWeChatCustomersAsync(
            List<WeChatSyncCustomerPayload> items,
            CancellationToken ct = default) =>
            SendAuthedLongRunningAsync<WeChatSyncCustomersResponse>(
                HttpMethod.Post,
                "/api/v1/wechat-sync/customers",
                new { items },
                ct);

        public Task<ApiResult<WeChatSyncGroupsResponse>> SyncWeChatGroupsAsync(
            List<WeChatSyncGroupPayload> items,
            CancellationToken ct = default) =>
            SendAuthedLongRunningAsync<WeChatSyncGroupsResponse>(
                HttpMethod.Post,
                "/api/v1/wechat-sync/groups",
                new { items },
                ct);

        public Task<ApiResult<WeChatSyncMessagesResponse>> SyncCustomerMessagesAsync(
            long customerId,
            List<WeChatSyncMessagePayload> items,
            CancellationToken ct = default) =>
            SendAuthedLongRunningAsync<WeChatSyncMessagesResponse>(
                HttpMethod.Post,
                $"/api/v1/wechat-sync/customers/{customerId}/messages",
                new { items },
                ct);

        public Task<ApiResult<WeChatSyncMessagesResponse>> SyncGroupMessagesAsync(
            long groupId,
            List<WeChatSyncMessagePayload> items,
            CancellationToken ct = default) =>
            SendAuthedLongRunningAsync<WeChatSyncMessagesResponse>(
                HttpMethod.Post,
                $"/api/v1/wechat-sync/groups/{groupId}/messages",
                new { items },
                ct);

        public Task<ApiResult<WeChatExistingIdsResponse>> CheckExistingMessageIdsAsync(
            string scope,
            long targetId,
            List<string> externalIds,
            CancellationToken ct = default) =>
            SendAuthedAsync<WeChatExistingIdsResponse>(
                HttpMethod.Post,
                "/api/v1/wechat-sync/existing-ids",
                new { scope, target_id = targetId, external_ids = externalIds },
                ct);

        private Task<ApiResult<T>> SendAuthedLongRunningAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct) =>
            SendAuthedCoreAsync(_aiHttpClient, method, path, body, async (resp, token) =>
            {
                var data = await resp.Content.ReadFromJsonAsync<T>(JsonOptions, token);
                return data is not null
                    ? ApiResult<T>.Ok(data, resp.StatusCode)
                    : ApiResult<T>.Fail("响应解析为空。", resp.StatusCode);
            }, ct);

        private Task<ApiResult<T>> SendAuthedAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct) =>
            SendAuthedCoreAsync(_httpClient, method, path, body, async (resp, token) =>
            {
                var data = await resp.Content.ReadFromJsonAsync<T>(JsonOptions, token);
                return data is not null
                    ? ApiResult<T>.Ok(data, resp.StatusCode)
                    : ApiResult<T>.Fail("响应解析为空。", resp.StatusCode);
            }, ct);

        private Task<ApiResult<bool>> SendAuthedNoContentAsync(HttpMethod method, string path, CancellationToken ct) =>
            SendAuthedCoreAsync(_httpClient, method, path, null, (resp, _) =>
                Task.FromResult(ApiResult<bool>.Ok(true, resp.StatusCode)), ct);

        private async Task<ApiResult<T>> SendAuthedCoreAsync<T>(
            HttpClient httpClient,
            HttpMethod method,
            string path,
            object? body,
            Func<HttpResponseMessage, CancellationToken, Task<ApiResult<T>>> onSuccess,
            CancellationToken ct)
        {
            if (!IsAuthenticated)
            {
                return ApiResult<T>.Fail("请先登录员工账号。");
            }
            try
            {
                using var req = CreateAuthedRequest(method, path);
                if (body is not null)
                {
                    req.Content = JsonContent.Create(body, options: JsonOptions);
                }
                using var resp = await httpClient.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                {
                    if (method != HttpMethod.Get)
                    {
                        AppServices.Logs.Success("API", $"{method} {path} -> {(int)resp.StatusCode}");
                    }
                    return await onSuccess(resp, ct);
                }
                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    ClearSession();
                }
                string errMsg = await ReadFriendlyErrorAsync(resp, ct);
                AppServices.Logs.Warn("API", $"{method} {path} -> {(int)resp.StatusCode} ({errMsg})");
                return ApiResult<T>.Fail(errMsg, resp.StatusCode);
            }
            catch (Exception ex)
            {
                AppServices.Logs.Error("API", $"{method} {path} 异常：{ex.Message}");
                return ApiResult<T>.Fail($"请求失败：{ex.Message}");
            }
        }

        private HttpRequestMessage CreateAuthedRequest(HttpMethod method, string relativePath)
        {
            var request = new HttpRequestMessage(method, $"{BaseUrl}{relativePath}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
            return request;
        }

        private void ClearSession()
        {
            AccessToken = null;
            CurrentEmployee = null;
            _avatarCache.Clear();
            _chatImageCache.Clear();
            _chatAudioCache.Clear();
        }

        private static async Task<string> ReadFriendlyErrorAsync(HttpResponseMessage response, CancellationToken ct)
        {
            string? code = null;
            try
            {
                var err = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(JsonOptions, ct);
                code = err?.Error;
            }
            catch { }

            return code switch
            {
                "authentication_unavailable" => "服务端尚未配置认证源，请联系管理员。",
                "invalid_credentials" => "员工账号或密码错误。",
                "unauthorized" => "登录状态已失效，请重新登录。",
                "conflict" => "该微信号在当前员工名下已存在，请勿重复绑定。",
                "group_name_required" => "群组名称不能为空。",
                "field_too_long" => "提交的字段超出长度限制。",
                not null when code.Length > 0 => $"请求失败 ({(int)response.StatusCode}): {code}",
                _ => $"请求失败，HTTP 状态码 {(int)response.StatusCode}"
            };
        }
    }
}
