using System.Text.Json.Serialization;

namespace Client.Models
{
    public sealed class EmployeeIdentity
    {
        [JsonPropertyName("employee_id")]
        public string EmployeeId { get; set; } = string.Empty;

        [JsonPropertyName("account")]
        public string Account { get; set; } = string.Empty;

        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = string.Empty;
    }

    public sealed class LoginRequest
    {
        [JsonPropertyName("account")]
        public string Account { get; set; } = string.Empty;

        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;
    }

    public sealed class LoginResponse
    {
        [JsonPropertyName("token")]
        public string Token { get; set; } = string.Empty;

        [JsonPropertyName("expires_at")]
        public DateTimeOffset ExpiresAt { get; set; }

        [JsonPropertyName("employee")]
        public EmployeeIdentity Employee { get; set; } = new();
    }

    public sealed class MeResponse
    {
        [JsonPropertyName("employee")]
        public EmployeeIdentity Employee { get; set; } = new();
    }

    public sealed class ApiErrorResponse
    {
        [JsonPropertyName("error")]
        public string Error { get; set; } = string.Empty;
    }

    public sealed class CustomerDto
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("wechat_name")]
        public string WechatName { get; set; } = string.Empty;

        [JsonPropertyName("wechat_account")]
        public string WechatAccount { get; set; } = string.Empty;

        [JsonPropertyName("note")]
        public string Note { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = string.Empty;

        [JsonPropertyName("updated_at")]
        public string UpdatedAt { get; set; } = string.Empty;
    }

    public sealed class CustomerListResponse
    {
        [JsonPropertyName("items")]
        public List<CustomerDto> Items { get; set; } = [];

        [JsonPropertyName("next_cursor")]
        public long NextCursor { get; set; }
    }

    public sealed class CreateCustomerRequest
    {
        [JsonPropertyName("wechat_name")]
        public string WechatName { get; set; } = string.Empty;

        [JsonPropertyName("wechat_account")]
        public string WechatAccount { get; set; } = string.Empty;

        [JsonPropertyName("note")]
        public string Note { get; set; } = string.Empty;
    }
}
