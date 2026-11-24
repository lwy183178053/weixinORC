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
        private readonly Dictionary<string, byte[]> _avatarCache = new(StringComparer.Ordinal);
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public BackendApiClient()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        }

        public string BaseUrl { get; private set; } = "http://127.0.0.1:28765";
        public string? AccessToken { get; private set; }
        public DateTimeOffset? TokenExpiresAt { get; private set; }
        public EmployeeIdentity? CurrentEmployee { get; private set; }
        public bool IsAuthenticated => !string.IsNullOrEmpty(AccessToken) && CurrentEmployee is not null;

        public event EventHandler? SessionChanged;

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
                    TokenExpiresAt = data.ExpiresAt;
                    CurrentEmployee = data.Employee;
                    SessionChanged?.Invoke(this, EventArgs.Empty);
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

        public Task<ApiResult<CustomerDto>> CreateCustomerAsync(CreateCustomerRequest payload, CancellationToken ct = default) =>
            SendAuthedAsync<CustomerDto>(HttpMethod.Post, "/api/v1/customers", payload, ct);

        public Task<ApiResult<CustomerDto>> CreateCustomerAsync(
            string wechatName, string wechatAccount, string note, CancellationToken ct = default) =>
            CreateCustomerAsync(new CreateCustomerRequest
            {
                WechatName = wechatName.Trim(),
                WechatAccount = wechatAccount.Trim(),
                Note = note.Trim()
            }, ct);

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

        public Task<ApiResult<CustomerDto>> DeleteCustomerAvatarAsync(long customerId, CancellationToken ct = default) =>
            SendAuthedAsync<CustomerDto>(HttpMethod.Delete, $"/api/v1/customers/{customerId}/avatar", null, ct);

        public async Task<byte[]?> DownloadAvatarBytesAsync(string? avatarUrl, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(avatarUrl) || !IsAuthenticated) return null;
            if (_avatarCache.TryGetValue(avatarUrl, out var cached)) return cached;

            try
            {
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

        public Task<ApiResult<MessageListResponse>> ListCustomerMessagesAsync(long customerId, int limit = 100, CancellationToken ct = default) =>
            SendAuthedAsync<MessageListResponse>(HttpMethod.Get, $"/api/v1/customers/{customerId}/messages?limit={limit}", null, ct);

        public Task<ApiResult<MessageDto>> CreateCustomerMessageAsync(
            long customerId, string direction, string content, CancellationToken ct = default) =>
            SendAuthedAsync<MessageDto>(HttpMethod.Post, $"/api/v1/customers/{customerId}/messages",
                new CreateMessageRequest { Direction = direction, Content = content.Trim() }, ct);

        // ---- Groups ----

        public Task<ApiResult<GroupListResponse>> ListGroupsAsync(int limit = 100, CancellationToken ct = default) =>
            SendAuthedAsync<GroupListResponse>(HttpMethod.Get, $"/api/v1/groups?limit={limit}", null, ct);

        public Task<ApiResult<GroupDto>> CreateGroupAsync(CreateGroupRequest payload, CancellationToken ct = default) =>
            SendAuthedAsync<GroupDto>(HttpMethod.Post, "/api/v1/groups", payload, ct);

        public Task<ApiResult<GroupDto>> UpdateGroupAsync(long groupId, CreateGroupRequest payload, CancellationToken ct = default) =>
            SendAuthedAsync<GroupDto>(HttpMethod.Patch, $"/api/v1/groups/{groupId}", payload, ct);

        public Task<ApiResult<bool>> DeleteGroupAsync(long groupId, CancellationToken ct = default) =>
            SendAuthedNoContentAsync(HttpMethod.Delete, $"/api/v1/groups/{groupId}", ct);

        public Task<ApiResult<GroupMessageListResponse>> ListGroupMessagesAsync(long groupId, int limit = 100, CancellationToken ct = default) =>
            SendAuthedAsync<GroupMessageListResponse>(HttpMethod.Get, $"/api/v1/groups/{groupId}/messages?limit={limit}", null, ct);

        public Task<ApiResult<GroupMessageDto>> CreateGroupMessageAsync(
            long groupId, string senderName, string direction, string content, CancellationToken ct = default) =>
            SendAuthedAsync<GroupMessageDto>(HttpMethod.Post, $"/api/v1/groups/{groupId}/messages",
                new CreateGroupMessageRequest
                {
                    SenderName = senderName.Trim(),
                    Direction = direction,
                    Content = content.Trim()
                }, ct);

        // ---- WeChat Accounts, Group Members & Audit Logs ----

        public Task<ApiResult<WeChatAccountListResponse>> ListWeChatAccountsAsync(CancellationToken ct = default) =>
            SendAuthedAsync<WeChatAccountListResponse>(HttpMethod.Get, "/api/v1/wechat-accounts", null, ct);

        public Task<ApiResult<WeChatAccountDto>> UpsertWeChatAccountAsync(UpsertWeChatAccountRequest payload, CancellationToken ct = default) =>
            SendAuthedAsync<WeChatAccountDto>(HttpMethod.Post, "/api/v1/wechat-accounts", payload, ct);

        public Task<ApiResult<GroupMemberListResponse>> ListGroupMembersAsync(long groupId, int limit = 100, CancellationToken ct = default) =>
            SendAuthedAsync<GroupMemberListResponse>(HttpMethod.Get, $"/api/v1/groups/{groupId}/members?limit={limit}", null, ct);

        public Task<ApiResult<GroupMemberDto>> CreateGroupMemberAsync(long groupId, CreateGroupMemberRequest payload, CancellationToken ct = default) =>
            SendAuthedAsync<GroupMemberDto>(HttpMethod.Post, $"/api/v1/groups/{groupId}/members", payload, ct);

        public Task<ApiResult<AuditLogListResponse>> ListAuditLogsAsync(int limit = 100, CancellationToken ct = default) =>
            SendAuthedAsync<AuditLogListResponse>(HttpMethod.Get, $"/api/v1/audit-logs?limit={limit}", null, ct);

        public Task<ApiResult<AuditLogDto>> CreateAuditLogAsync(string action, string targetType, string targetId, string detail, CancellationToken ct = default) =>
            SendAuthedAsync<AuditLogDto>(HttpMethod.Post, "/api/v1/audit-logs",
                new CreateAuditLogRequest { Action = action, TargetType = targetType, TargetId = targetId, Detail = detail }, ct);

        private async Task<ApiResult<T>> SendAuthedAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
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
                using var resp = await _httpClient.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                {
                    var data = await resp.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
                    if (method != HttpMethod.Get)
                    {
                        AppServices.Logs.Success("API", $"{method} {path} -> {(int)resp.StatusCode}");
                    }
                    return data is not null
                        ? ApiResult<T>.Ok(data, resp.StatusCode)
                        : ApiResult<T>.Fail("响应解析为空。", resp.StatusCode);
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

        private async Task<ApiResult<bool>> SendAuthedNoContentAsync(HttpMethod method, string path, CancellationToken ct)
        {
            if (!IsAuthenticated)
            {
                return ApiResult<bool>.Fail("请先登录员工账号。");
            }
            try
            {
                using var req = CreateAuthedRequest(method, path);
                using var resp = await _httpClient.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                {
                    AppServices.Logs.Success("API", $"{method} {path} -> {(int)resp.StatusCode}");
                    return ApiResult<bool>.Ok(true, resp.StatusCode);
                }
                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    ClearSession();
                }
                string errMsg = await ReadFriendlyErrorAsync(resp, ct);
                AppServices.Logs.Warn("API", $"{method} {path} -> {(int)resp.StatusCode} ({errMsg})");
                return ApiResult<bool>.Fail(errMsg, resp.StatusCode);
            }
            catch (Exception ex)
            {
                AppServices.Logs.Error("API", $"{method} {path} 异常：{ex.Message}");
                return ApiResult<bool>.Fail($"请求失败：{ex.Message}");
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
            TokenExpiresAt = null;
            CurrentEmployee = null;
            _avatarCache.Clear();
            SessionChanged?.Invoke(this, EventArgs.Empty);
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
