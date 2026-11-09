using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Plugins;

namespace ClaudeHomeServer.Services.Notes;

// Обработчик права notes:read (ADR-031 §2.4, §3.4): операции notes.search и notes.read
// моста панели. Регистрирует САМА вертикаль Notes (направление Notes → Core ← Plugins,
// прямой ссылки вертикаль → вертикаль нет, ADR-014 инвариант 10): Plugins не знает,
// кто отвечает за notes:read — шов IPluginCapability живёт в Core.
//
// Граница доступа: только заметки владельца (PluginCallContext.OwnerId) — личные и
// проектные в рамках проекта слота (PluginCallContext.ProjectId). Чужой владелец,
// выход за vault, обход через «..», чужой id заметки → not_found (не denied, чтобы
// не подтверждать существование).
//
// Лимиты (ADR-031 §3.4): число записей и суммарный объём; превышение → усечение
// с признаком truncated. Служебных полей (токены, пути хоста) в ответе нет.
public sealed class NotesPluginCapability : IPluginCapability
{
    public string Permission => PluginPermission.NotesRead;

    // --- Лимиты (ADR-031 §3.4): значения — дефолты, тесты переопределяют ---

    /// <summary>Потолок числа записей в списке (notes.search / tasks.list).</summary>
    public int MaxRecords { get; set; } = 100;

    /// <summary>Потолок суммарного объёма ответа в байтах (notes.search / notes.read / tasks.list).</summary>
    public long MaxTotalBytes { get; set; } = 200 * 1024; // 200 KiB

    private readonly NotesService _notes;

    public NotesPluginCapability(NotesService notes) => _notes = notes;

    /// <inheritdoc/>
    public Task<PluginCapabilityResult> HandleAsync(
        PluginCallContext context, string op, JsonElement args, IReadOnlyList<string> granted, CancellationToken ct)
    {
        return op switch
        {
            "notes.search" => SearchAsync(context, args),
            "notes.read" => ReadAsync(context, args),
            _ => Task.FromResult(Err("invalid", $"Неизвестная операция: {op}")),
        };
    }

    // notes.search: {query, limit?} → список заметок (личные + проект слота), усечение.
    private Task<PluginCapabilityResult> SearchAsync(PluginCallContext context, JsonElement args)
    {
        var query = GetString(args, "query");
        var limit = Math.Min(GetInt(args, "limit", MaxRecords), MaxRecords);

        // GetSummaries(userId, source: null, query) — ВСЕ источники владельца;
        // граница (личные + проект слота) — фильтром ниже.
        var summaries = _notes.GetSummaries(context.OwnerId, null, query);
        var allowed = summaries.Where(n => IsAllowedSource(n.Source, context));
        var (items, truncated) = Truncate(allowed.Select(ToSummaryDto), limit);
        return Task.FromResult(Ok(new { notes = items, truncated }));
    }

    // notes.read: {id} → заметка (личная или проект слота), иначе not_found.
    private Task<PluginCapabilityResult> ReadAsync(PluginCallContext context, JsonElement args)
    {
        var id = GetString(args, "id");
        if (string.IsNullOrWhiteSpace(id))
            return Task.FromResult(Err("invalid", "Не задан id"));

        var detail = _notes.GetDetail(context.OwnerId, id);
        // Чужой владелец / выход за vault / обход «..» / чужой id → null или
        // источник вне границы → not_found (не denied, чтобы не подтверждать существование).
        if (detail is null || !IsAllowedSource(detail.Source, context))
            return Task.FromResult(Err("not_found", "Заметка не найдена"));

        // Усечение контента: больше лимита → режем и ставим truncated.
        var truncated = false;
        var content = detail.Content;
        if (content.Length > MaxTotalBytes)
        {
            // content.Length > MaxTotalBytes ⇒ MaxTotalBytes < int.MaxValue → каст безопасен.
            content = content[..(int)MaxTotalBytes];
            truncated = true;
        }
        return Task.FromResult(Ok(new
        {
            id = detail.Id,
            title = detail.Title,
            source = detail.Source,
            sourceLabel = detail.SourceLabel,
            path = detail.Path,
            content,
            updatedAt = detail.UpdatedAt,
            truncated,
        }));
    }

    // Граница доступа: личные заметки всегда; проектные — только в рамках проекта слота.
    private static bool IsAllowedSource(string source, PluginCallContext context) =>
        source == "personal" || (context.ProjectId is not null && source == context.ProjectId);

    // --- Хелперы ---

    // DTO заметки для списка: минимум полей, без служебных (токены, пути хоста,
    // LinkedSessionId, SourceSessionId и т.п.).
    private static object ToSummaryDto(NoteSummary n) => new
    {
        id = n.Id,
        title = n.Title,
        source = n.Source,
        sourceLabel = n.SourceLabel,
        path = n.Path,
        updatedAt = n.UpdatedAt,
    };

    // Усечение списка: не более limit записей и не более MaxTotalBytes суммарно.
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
