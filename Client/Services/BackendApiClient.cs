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
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public BackendApiClient()
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
        }

        public string BaseUrl { get; private set; } = "http://127.0.0.1:8080";
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

        public async Task<ApiResult<string>> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var response = await _httpClient.GetAsync($"{BaseUrl}/healthz", cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return ApiResult<string>.Ok($"服务正常 ({(int)response.StatusCode}): {body}", response.StatusCode);
                }

                return ApiResult<string>.Fail($"健康检查失败 ({(int)response.StatusCode}): {body}", response.StatusCode);
            }
            catch (Exception ex)
            {
                return ApiResult<string>.Fail($"无法连接后端服务 ({BaseUrl})：{ex.Message}");
            }
        }

        public async Task<ApiResult<LoginResponse>> LoginAsync(
            string account,
            string password,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var payload = new LoginRequest
                {
                    Account = account.Trim(),
                    Password = password
                };

                using var response = await _httpClient.PostAsJsonAsync(
                    $"{BaseUrl}/api/v1/login",
                    payload,
                    JsonOptions,
                    cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var data = await response.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions, cancellationToken);
                    if (data is null || string.IsNullOrWhiteSpace(data.Token))
                    {
                        return ApiResult<LoginResponse>.Fail("登录响应缺少有效令牌。", response.StatusCode);
                    }

                    AccessToken = data.Token;
                    TokenExpiresAt = data.ExpiresAt;
                    CurrentEmployee = data.Employee;
                    SessionChanged?.Invoke(this, EventArgs.Empty);
                    return ApiResult<LoginResponse>.Ok(data, response.StatusCode);
                }

                var errorText = await ReadFriendlyErrorAsync(response, cancellationToken);
                return ApiResult<LoginResponse>.Fail(errorText, response.StatusCode);
            }
            catch (Exception ex)
            {
                return ApiResult<LoginResponse>.Fail($"登录请求失败：{ex.Message}");
            }
        }

        public async Task<ApiResult<EmployeeIdentity>> GetMeAsync(CancellationToken cancellationToken = default)
        {
            if (!IsAuthenticated)
            {
                return ApiResult<EmployeeIdentity>.Fail("当前尚未登录。");
            }

            try
            {
                using var request = CreateAuthedRequest(HttpMethod.Get, "/api/v1/me");
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var data = await response.Content.ReadFromJsonAsync<MeResponse>(JsonOptions, cancellationToken);
                    if (data?.Employee is not null)
                    {
                        CurrentEmployee = data.Employee;
                        SessionChanged?.Invoke(this, EventArgs.Empty);
                        return ApiResult<EmployeeIdentity>.Ok(data.Employee, response.StatusCode);
                    }
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    ClearSession();
                }

                return ApiResult<EmployeeIdentity>.Fail(
                    await ReadFriendlyErrorAsync(response, cancellationToken),
                    response.StatusCode);
            }
            catch (Exception ex)
            {
                return ApiResult<EmployeeIdentity>.Fail($"获取员工信息失败：{ex.Message}");
            }
        }

        public async Task<ApiResult<bool>> LogoutAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (!string.IsNullOrEmpty(AccessToken))
                {
                    using var request = CreateAuthedRequest(HttpMethod.Post, "/api/v1/logout");
                    using var response = await _httpClient.SendAsync(request, cancellationToken);
                    ClearSession();
                    return ApiResult<bool>.Ok(true, response.StatusCode);
                }

                ClearSession();
                return ApiResult<bool>.Ok(true);
            }
            catch (Exception ex)
            {
                ClearSession();
                return ApiResult<bool>.Fail($"本地会话已清除，但通知服务端登出失败：{ex.Message}");
            }
        }

        public async Task<ApiResult<CustomerListResponse>> ListCustomersAsync(
            int limit = 50,
            long cursor = 0,
            CancellationToken cancellationToken = default)
        {
            if (!IsAuthenticated)
            {
                return ApiResult<CustomerListResponse>.Fail("请先登录员工账号。");
            }

            try
            {
                var path = cursor > 0
                    ? $"/api/v1/customers?limit={limit}&cursor={cursor}"
                    : $"/api/v1/customers?limit={limit}";

                using var request = CreateAuthedRequest(HttpMethod.Get, path);
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var data = await response.Content.ReadFromJsonAsync<CustomerListResponse>(JsonOptions, cancellationToken);
                    return ApiResult<CustomerListResponse>.Ok(data ?? new CustomerListResponse(), response.StatusCode);
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    ClearSession();
                }

                return ApiResult<CustomerListResponse>.Fail(
                    await ReadFriendlyErrorAsync(response, cancellationToken),
                    response.StatusCode);
            }
            catch (Exception ex)
            {
                return ApiResult<CustomerListResponse>.Fail($"拉取客户列表失败：{ex.Message}");
            }
        }

        public async Task<ApiResult<CustomerDto>> CreateCustomerAsync(
            string wechatName,
            string wechatAccount,
            string note,
            CancellationToken cancellationToken = default)
        {
            if (!IsAuthenticated)
            {
                return ApiResult<CustomerDto>.Fail("请先登录员工账号后再保存客户资料。");
            }

            try
            {
                using var request = CreateAuthedRequest(HttpMethod.Post, "/api/v1/customers");
                request.Content = JsonContent.Create(new CreateCustomerRequest
                {
                    WechatName = wechatName.Trim(),
                    WechatAccount = wechatAccount.Trim(),
                    Note = note.Trim()
                }, options: JsonOptions);

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var data = await response.Content.ReadFromJsonAsync<CustomerDto>(JsonOptions, cancellationToken);
                    if (data is not null)
                    {
                        return ApiResult<CustomerDto>.Ok(data, response.StatusCode);
                    }
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    ClearSession();
                }

                return ApiResult<CustomerDto>.Fail(
                    await ReadFriendlyErrorAsync(response, cancellationToken),
                    response.StatusCode);
            }
            catch (Exception ex)
            {
                return ApiResult<CustomerDto>.Fail($"保存客户失败：{ex.Message}");
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
            catch
            {
                // Ignore JSON parsing failure and fall back to status code.
            }

            return code switch
            {
                "authentication_unavailable" => "服务端尚未配置员工认证源（本地联调请以 MOCK_AUTH=1 启动 Go 后端）。",
                "invalid_credentials" => "员工账号或密码错误。",
                "invalid_credentials_format" => "账号或密码格式无效。",
                "unauthorized" => "登录状态已失效，请重新登录。",
                "conflict" => "该微信号在当前员工名下已存在，请勿重复绑定。",
                "field_too_long" => "提交的字段超出长度限制。",
                not null when code.Length > 0 => $"请求失败 ({(int)response.StatusCode}): {code}",
                _ => $"请求失败，HTTP 状态码 {(int)response.StatusCode} ({response.ReasonPhrase})"
            };
        }
    }
}
