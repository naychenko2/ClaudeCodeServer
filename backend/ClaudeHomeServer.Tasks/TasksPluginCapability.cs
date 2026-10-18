using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Plugins;

namespace ClaudeHomeServer.Services.Tasks;

// Обработчик права tasks:read (ADR-031 §2.4, §3.4): операция tasks.list моста панели.
// Регистрирует САМА вертикаль Tasks (направление Tasks → Core ← Plugins, прямой ссылки
// вертикаль → вертикаль нет, ADR-014 инвариант 10): Plugins не знает, кто отвечает за
// tasks:read — шов IPluginCapability живёт в Core.
//
// Граница доступа: только задачи владельца (PluginCallContext.OwnerId), фильтр по проекту
// слота (PluginCallContext.ProjectId): null — личные задачи (ProjectId == null),
// guid — задачи того проекта. Только чтение.
//
// Лимиты (ADR-031 §3.4): число записей и суммарный объём; превышение → усечение
// с признаком truncated. Служебных полей (токены, пути хоста) в ответе нет.
public sealed class TasksPluginCapability : IPluginCapability
{
    public string Permission => PluginPermission.TasksRead;

    // --- Лимиты (ADR-031 §3.4): значения — дефолты, тесты переопределяют ---

    /// <summary>Потолок числа записей в списке.</summary>
    public int MaxRecords { get; set; } = 100;

    /// <summary>Потолок суммарного объёма ответа в байтах.</summary>
    public long MaxTotalBytes { get; set; } = 200 * 1024; // 200 KiB

    private readonly TaskManager _tasks;

    public TasksPluginCapability(TaskManager tasks) => _tasks = tasks;

    /// <inheritdoc/>
    public Task<PluginCapabilityResult> HandleAsync(
        PluginCallContext context, string op, JsonElement args, IReadOnlyList<string> granted, CancellationToken ct)
    {
        return op switch
        {
            "tasks.list" => ListAsync(context, args),
            _ => Task.FromResult(Err("invalid", $"Неизвестная операция: {op}")),
        };
    }

    // tasks.list: {status?, limit?} → задачи владельца в проекте слота, усечение.
    private Task<PluginCapabilityResult> ListAsync(PluginCallContext context, JsonElement args)
    {
        var status = GetString(args, "status");
        var limit = Math.Min(GetInt(args, "limit", MaxRecords), MaxRecords);

        // Только задачи владельца, фильтр по проекту слота:
        // null — личные (ProjectId == null), guid — задачи того проекта.
        var all = _tasks.GetByOwner(context.OwnerId)
            .Where(t => t.ProjectId == context.ProjectId);
        if (!string.IsNullOrWhiteSpace(status))
            all = all.Where(t => t.Status.ToString().Equals(status, StringComparison.OrdinalIgnoreCase));
        var (items, truncated) = Truncate(all.Select(ToTaskDto), limit);
        return Task.FromResult(Ok(new { tasks = items, truncated }));
    }

    // DTO задачи: минимум полей, без служебных (LinkedSessionId, SourceSessionId,
    // WorktreePath, PersonaId, ExecutorLaunchedBy* и т.п. — пути хоста и токены).
    private static object ToTaskDto(TaskItem t) => new
    {
        id = t.Id,
        title = t.Title,
        status = t.Status.ToString().ToLowerInvariant(),
        priority = t.Priority.ToString().ToLowerInvariant(),
        dueDate = t.DueDate,
        assignee = t.Assignee?.ToString().ToLowerInvariant(),
        updatedAt = t.UpdatedAt,
    };

    // Усечение: не более limit записей и не более MaxTotalBytes суммарно.
    // Превышение любого лимита → truncated=true (остаток записей не уходит).
    private (object[] Items, bool Truncated) Truncate(IEnumerable<object> source, int limit)
    {
        var items = new List<object>();
        var bytes = 0;
        var truncated = false;
        foreach (var item in source)
        {
            if (items.Count >= limit) { truncated = true; break; }
            var size = JsonSize(item);
            if (bytes + size > MaxTotalBytes) { truncated = true; break; }
            items.Add(item);
            bytes += size;
        }
        return (items.ToArray(), truncated);
    }

    // Размер записи в JSON (байты) — для учёта суммарного объёма.
    private static int JsonSize(object item) => JsonSerializer.SerializeToUtf8Bytes(item).Length;

    // Строковое поле JSON-объекта (null, если нет или не string).
    private static string? GetString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    // Числовое поле JSON-объекта (def, если нет или не number).
    private static int GetInt(JsonElement obj, string name, int def) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : def;

    private static PluginCapabilityResult Ok(object data) => new(true, Data: data);
    private static PluginCapabilityResult Err(string code, string message) =>
        new(false, Error: new PluginCallError(code, message));
}
