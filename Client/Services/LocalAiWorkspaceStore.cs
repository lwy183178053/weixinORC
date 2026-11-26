using System.Text.Json;
using System.Text.Json.Serialization;
using Client.Models;
using Microsoft.UI.Xaml;

namespace Client.Services
{
    public sealed class AiChatMessageItem
    {
        public string Role { get; set; } = "assistant"; // "user" | "assistant"
        public string Content { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
        public List<AiToolTraceDto> ToolTrace { get; set; } = [];
        public List<string> Images { get; set; } = [];

        [JsonIgnore]
        public bool IsPending { get; set; }

        [JsonIgnore]
        public bool IsUser => string.Equals(Role, "user", StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public Visibility UserVisibility => IsUser ? Visibility.Visible : Visibility.Collapsed;

        [JsonIgnore]
        public Visibility UserImagesVisibility => IsUser && Images is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;

        [JsonIgnore]
        public Visibility UserTextVisibility => IsUser && !string.IsNullOrWhiteSpace(Content) ? Visibility.Visible : Visibility.Collapsed;

        [JsonIgnore]
        public Visibility AssistantVisibility => !IsUser ? Visibility.Visible : Visibility.Collapsed;

        [JsonIgnore]
        public Visibility PendingVisibility => !IsUser && IsPending ? Visibility.Visible : Visibility.Collapsed;

        [JsonIgnore]
        public Visibility AssistantContentVisibility => !IsUser && !IsPending ? Visibility.Visible : Visibility.Collapsed;

        [JsonIgnore]
        public Visibility ToolTraceVisibility => !IsUser && !IsPending && ToolTrace is { Count: > 0 }
            ? Visibility.Visible
            : Visibility.Collapsed;

        [JsonIgnore]
        public Visibility TimestampVisibility => !IsPending && !string.IsNullOrWhiteSpace(Timestamp)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public interface IEntityAiWorkspace
    {
        string SummaryText { get; set; }
        string SummaryUpdatedAt { get; set; }
        string SummaryContextMeta { get; set; }
        List<AiChatMessageItem> Messages { get; set; }
    }

    public sealed class CustomerAiWorkspaceData : IEntityAiWorkspace
    {
        public long CustomerId { get; set; }
        public string SummaryText { get; set; } = string.Empty;
        public string SummaryUpdatedAt { get; set; } = string.Empty;
        public string SummaryContextMeta { get; set; } = string.Empty;
        public List<AiChatMessageItem> Messages { get; set; } = [];
    }

    public sealed class GroupAiWorkspaceData : IEntityAiWorkspace
    {
        public long GroupId { get; set; }
        public string SummaryText { get; set; } = string.Empty;
        public string SummaryUpdatedAt { get; set; } = string.Empty;
        public string SummaryContextMeta { get; set; } = string.Empty;
        public List<AiChatMessageItem> Messages { get; set; } = [];
    }

    public sealed class AgentSessionData
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
        public string Title { get; set; } = "新对话";
        public string UpdatedAt { get; set; } = DateTime.Now.ToString("MM-dd HH:mm");
        public List<AiChatMessageItem> Messages { get; set; } = [];

        public string PreviewLine
        {
            get
            {
                if (Messages.Count == 0) return "等待提问...";
                var last = Messages[^1];
                string txt = last.Content.Replace("\r", " ").Replace("\n", " ").Trim();
                if (!string.IsNullOrEmpty(txt)) return txt;
                return last.Images is { Count: > 0 } ? $"[图片 ×{last.Images.Count}]" : "等待提问...";
            }
        }
    }

    /// <summary>
    /// Stores per-customer AI summaries/chats and global AI Assistant sessions locally on disk
    /// (conversation history stays on the employee machine and is not stored in the backend DB).
    /// </summary>
    public sealed class LocalAiWorkspaceStore
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly string _rootDir;
        private readonly string _agentSessionsPath;

        public LocalAiWorkspaceStore()
        {
            _rootDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "weixinORC",
                "ai_workspace");
            _agentSessionsPath = Path.Combine(_rootDir, "agent_sessions.json");
        }

        private string GetCustomerFilePath(long customerId) =>
            Path.Combine(_rootDir, $"customer_{customerId}.json");

        private string GetGroupFilePath(long groupId) =>
            Path.Combine(_rootDir, $"group_{groupId}.json");

        public GroupAiWorkspaceData LoadGroup(long groupId)
        {
            try
            {
                string path = GetGroupFilePath(groupId);
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var data = JsonSerializer.Deserialize<GroupAiWorkspaceData>(json, JsonOpts);
                    if (data is not null)
                    {
                        data.GroupId = groupId;
                        data.Messages ??= [];
                        return data;
                    }
                }
            }
            catch
            {
            }

            return new GroupAiWorkspaceData { GroupId = groupId };
        }

        private static void WriteFileAtomic(string path, string content)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            string tmpPath = path + ".tmp";
            File.WriteAllText(tmpPath, content);
            File.Move(tmpPath, path, overwrite: true);
        }

        public void SaveGroup(GroupAiWorkspaceData data)
        {
            try
            {
                string json = JsonSerializer.Serialize(data, JsonOpts);
                WriteFileAtomic(GetGroupFilePath(data.GroupId), json);
            }
            catch
            {
            }
        }

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
            }

            return new CustomerAiWorkspaceData { CustomerId = customerId };
        }

        public void Save(CustomerAiWorkspaceData data)
        {
            try
            {
                string json = JsonSerializer.Serialize(data, JsonOpts);
                WriteFileAtomic(GetCustomerFilePath(data.CustomerId), json);
            }
            catch
            {
            }
        }

        public List<AgentSessionData> LoadAgentSessions()
        {
            try
            {
                if (File.Exists(_agentSessionsPath))
                {
                    string json = File.ReadAllText(_agentSessionsPath);
                    var list = JsonSerializer.Deserialize<List<AgentSessionData>>(json, JsonOpts);
                    if (list is not null)
                    {
                        foreach (var s in list) s.Messages ??= [];
                        return list;
                    }
                }
            }
            catch
            {
            }

            return [];
        }

        public void SaveAgentSessions(List<AgentSessionData> sessions)
        {
            try
            {
                string json = JsonSerializer.Serialize(sessions, JsonOpts);
                WriteFileAtomic(_agentSessionsPath, json);
            }
            catch
            {
            }
        }
    }
}
