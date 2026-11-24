using System.Text.Json;
using Microsoft.UI.Xaml;

namespace Client.Services
{
    public sealed class AiChatMessageItem
    {
        public string Role { get; set; } = "assistant"; // "user" | "assistant"
        public string Content { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;

        public bool IsUser => string.Equals(Role, "user", StringComparison.OrdinalIgnoreCase);
        public Visibility UserVisibility => IsUser ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AssistantVisibility => IsUser ? Visibility.Collapsed : Visibility.Visible;
    }

    public sealed class CustomerAiWorkspaceData
    {
        public long CustomerId { get; set; }
        public string SummaryText { get; set; } = string.Empty;
        public string SummaryUpdatedAt { get; set; } = string.Empty;
        public string SummaryContextMeta { get; set; } = string.Empty;
        public List<AiChatMessageItem> Messages { get; set; } = [];
    }

    /// <summary>
    /// Stores per-customer AI summaries and AI discussion history locally on disk
    /// (not uploaded to the backend database).
    /// </summary>
    public sealed class LocalAiWorkspaceStore
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly string _rootDir;

        public LocalAiWorkspaceStore()
        {
            _rootDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "weixinORC",
                "ai_workspace");
        }

        private string GetCustomerFilePath(long customerId) =>
            Path.Combine(_rootDir, $"customer_{customerId}.json");

        public CustomerAiWorkspaceData Load(long customerId)
        {
            try
            {
                string path = GetCustomerFilePath(customerId);
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var data = JsonSerializer.Deserialize<CustomerAiWorkspaceData>(json, JsonOpts);
                    if (data is not null)
                    {
                        data.CustomerId = customerId;
                        data.Messages ??= [];
                        return data;
                    }
                }
            }
            catch
            {
                // Ignore corrupted local cache and start fresh
            }

            return new CustomerAiWorkspaceData { CustomerId = customerId };
        }

        public void Save(CustomerAiWorkspaceData data)
        {
            try
            {
                Directory.CreateDirectory(_rootDir);
                string json = JsonSerializer.Serialize(data, JsonOpts);
                File.WriteAllText(GetCustomerFilePath(data.CustomerId), json);
            }
            catch
            {
                // Best-effort local persistence
            }
        }
    }
}
