using System.Globalization;
using System.Text.Json;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services;

// Извлекает учётные данные завершённой glif-генерации из результата инструмента.
// Покрывает два боевых вида tool_result:
//   • полный JSON-RPC ответ с content/structuredContent/_meta.glif;
//   • сплющенный текст "[Resource link: ...]" + JSON-хвост structuredContent.
// Дедупликация по jobId — на стороне вызывающего (SessionManager / TurnAccumulator).
public static class GlifCostParser
{
    // Признаки glif-результата: достаточно одного для попытки парсинга.
    private static readonly string[] GlifMarkers =
    [
        "glif.app", "glifusercontent.com", "\"projectId\"", "\"jobId\"",
        "\"project_id\"", "\"job_id\"", "\"_meta\""
    ];

    public static GlifCostMessage? TryParse(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        if (!GlifMarkers.Any(content.Contains)) return null;

        JsonElement root;
        bool parsed;
        try
        {
            using var doc = JsonDocument.Parse(content);
            root = doc.RootElement.Clone();
            parsed = root.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            // Не валидный JSON целиком — ищем JSON-хвост
            parsed = TryParseTrailingJson(content, out root);
        }

        if (!parsed) return null;

        var jobId = FindJobId(root);
        if (string.IsNullOrWhiteSpace(jobId)) return null;

        // structuredContent приоритетнее _meta.glif: там итоговый outputType/media
        var structured = FindStructured(root);
        var meta = FindMetaGlif(root);

        // Учитываем только завершённые генерации. Явные промежуточные статусы
        // (working/running/queued/processing) отбрасываем, чтобы first-wins
        // дедуп по jobId не зафиксировал пустую запись раньше completed.
        if (!IsCompleted(structured, meta, root))
            return null;

        var outputType = GetString(structured, "outputType")
            ?? GetString(meta, "outputType")
            ?? GetString(root, "outputType");

        var mediaCount = GetMediaCount(structured) ?? GetMediaCount(meta) ?? GetMediaCount(root) ?? 0;

        var credits = GetDouble(meta, "subtractedCredits");

        var model = meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("toolExecutions", out var tools)
            && tools.ValueKind == JsonValueKind.Array
            && tools.EnumerateArray().FirstOrDefault() is var first
            && first.ValueKind == JsonValueKind.Object
            ? GetString(first, "nativeToolId")
            : null;

        return new GlifCostMessage(jobId, outputType, mediaCount, credits, model);
    }

    // Ищет JSON-объект в конце строки. Берётся ПЕРВЫЙ с конца '{' с которого парсится
    // валидный объект — иначе вложенные объекты (media[]) ломают LastIndexOf.
    private static bool TryParseTrailingJson(string content, out JsonElement root)
    {
        root = default;
        var span = content.AsSpan();
        var start = span.LastIndexOf('{');
        while (start >= 0)
        {
            var tail = span[start..].ToString();
            try
            {
                using var doc = JsonDocument.Parse(tail);
                var parsed = doc.RootElement.Clone();
                if (parsed.ValueKind == JsonValueKind.Object)
                {
                    root = parsed;
                    return true;
                }
            }
            catch { /* пробуем более ранний '{' */ }
            // Ищем предыдущее '{'
            start = span.Slice(0, start).LastIndexOf('{');
        }
        return false;
    }

    private static string? FindJobId(JsonElement root)
    {
        // Сначала ищем в _meta.glif (идентификатор job-а у инструмента get_job_status)
        if (FindMetaGlif(root) is { ValueKind: JsonValueKind.Object } meta)
        {
            var id = GetString(meta, "jobId");
            if (!string.IsNullOrWhiteSpace(id)) return id;
        }

        // Затем в structuredContent / корне
        foreach (var scope in new[] { FindStructured(root), root })
        {
            var id = GetString(scope, "jobId") ?? GetString(scope, "job_id");
            if (!string.IsNullOrWhiteSpace(id)) return id;
        }

        return null;
    }

    private static JsonElement FindStructured(JsonElement root)
    {
        if (root.TryGetProperty("structuredContent", out var sc) && sc.ValueKind == JsonValueKind.Object)
            return sc;
        return default;
    }

    private static JsonElement FindMetaGlif(JsonElement root)
    {
        if (root.TryGetProperty("_meta", out var meta)
            && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("glif", out var glif)
            && glif.ValueKind == JsonValueKind.Object)
            return glif;
        return default;
    }

    // Завершённая генерация: status == "completed" в structuredContent/_meta.glif/корне.
    // Если status отсутствует, но есть media — считаем completed (обратная совместимость
    // на случай упрощённого ответа); пустой ответ без статуса — не генерация.
    private static bool IsCompleted(JsonElement structured, JsonElement meta, JsonElement root)
    {
        var status = GetString(structured, "status") ?? GetString(meta, "status") ?? GetString(root, "status");
        if (status is not null)
            return status == "completed";

        return (GetMediaCount(structured) ?? 0) > 0
            || (GetMediaCount(meta) ?? 0) > 0
            || (GetMediaCount(root) ?? 0) > 0;
    }

    private static int? GetMediaCount(JsonElement scope)
    {
        if (scope.ValueKind != JsonValueKind.Object) return null;
        if (scope.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Array)
            return media.GetArrayLength();
        return null;
    }

    private static string? GetString(JsonElement scope, string name)
    {
        if (scope.ValueKind != JsonValueKind.Object) return null;
        if (scope.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String)
            return p.GetString();
        return null;
    }

    private static double? GetDouble(JsonElement scope, string name)
    {
        if (scope.ValueKind != JsonValueKind.Object) return null;
        if (!scope.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d)) return d;
        if (p.ValueKind == JsonValueKind.String
            && double.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out d))
            return d;
        return null;
    }
}
