using System.Text.Json;
using ClaudeCodeServer.Protocol;

namespace ClaudeCodeServer.Services;

public class ChatHistoryService
{
    private readonly string _basePath;
    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ChatHistoryService(IConfiguration config)
    {
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _basePath = Path.Combine(dataDir, "sessions");
    }

    public async Task<List<StoredMessage>> LoadAsync(string claudeSessionId)
    {
        var path = GetPath(claudeSessionId);
        if (!File.Exists(path)) return [];
        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<List<StoredMessage>>(json, _opts) ?? [];
        }
        catch { return []; }
    }

    public async Task SaveAsync(string claudeSessionId, List<StoredMessage> messages)
    {
        var path = GetPath(claudeSessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(messages, _opts));
    }

    private string GetPath(string id) => Path.Combine(_basePath, id, "history.json");
}
