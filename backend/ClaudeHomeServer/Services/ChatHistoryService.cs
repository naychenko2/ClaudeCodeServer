using System.Text.Json;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services;

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

    public Task<List<StoredMessage>> LoadAsync(string claudeSessionId)
    {
        var path = GetPath(claudeSessionId);
        return Task.FromResult(JsonFileStore.Load<List<StoredMessage>>(path, _opts) ?? []);
    }

    public Task SaveAsync(string claudeSessionId, List<StoredMessage> messages)
    {
        JsonFileStore.Save(GetPath(claudeSessionId), messages, _opts);
        return Task.CompletedTask;
    }

    // Удалить историю чата вместе с папкой сессии (при удалении чата)
    public void Delete(string claudeSessionId)
    {
        var dir = Path.Combine(_basePath, claudeSessionId);
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { /* файл занят — мусор дочистится при следующем удалении вручную */ }
        catch (UnauthorizedAccessException) { }
    }

    private string GetPath(string id) => Path.Combine(_basePath, id, "history.json");
}
