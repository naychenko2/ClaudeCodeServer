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

        // Читаем историю поэлементно, а не целиком как List<StoredMessage>: у старых чатов
        // в history.json могут лежать записи с уже удалёнными дискриминаторами kind
        // (например, legacy meeting_phase/pipeline_phase от выпиленных механик «Совещание»
        // и «Конвейер»). Полиморфная десериализация System.Text.Json на неизвестном kind
        // бросает исключение — при чтении всего массива разом пропала бы ВСЯ история чата.
        // Поэтому сначала парсим сырые элементы, затем каждый десериализуем отдельно:
        // неизвестные/битые записи тихо пропускаем, остальная история остаётся живой.
        var rawItems = JsonFileStore.Load<List<JsonElement>>(path, _opts);
        if (rawItems is null) return Task.FromResult(new List<StoredMessage>());

        var messages = new List<StoredMessage>(rawItems.Count);
        foreach (var raw in rawItems)
        {
            try
            {
                if (raw.Deserialize<StoredMessage>(_opts) is { } message)
                    messages.Add(message);
            }
            // NotSupportedException — неизвестный дискриминатор kind (легаси-запись),
            // JsonException — запись не по схеме; в обоих случаях элемент пропускаем
            catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
        }
        return Task.FromResult(messages);
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
