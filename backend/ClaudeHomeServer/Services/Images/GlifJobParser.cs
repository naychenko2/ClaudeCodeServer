using System.Text.Json;

namespace ClaudeHomeServer.Services.Images;

// Состояние джобы glif, к которому сводятся все варианты поля status.
public enum GlifJobState
{
    // Статус не распознан (пустой ответ, чужая форма) — опрос продолжается
    Unknown,
    // Ещё считается (working/running/queued/…)
    Working,
    // Готово, медиа можно забирать
    Completed,
    // Провал: JSON-RPC error, isError, status failed/cancelled
    Failed,
}

// Разобранный ответ compose_project / get_job_status.
public sealed record GlifJobResult(
    GlifJobState State,
    string? JobId,
    string? ProjectId,
    double? PollIntervalSeconds,
    IReadOnlyList<string> MediaUrls,
    string? Error)
{
    public static readonly GlifJobResult Empty =
        new(GlifJobState.Unknown, null, null, null, [], null);
}

// Чистый разбор ответов асинхронной генерации glif.app (HTTP MCP). Защитный:
// отсутствующие или переименованные поля дают пустой результат, а не исключение.
//
// Формы проверены на живом токене (2026-08-15, tools/list) и по боевым tool_result
// (см. GlifCostParserTests):
//   • compose_project → structuredContent { outputType:"project_job_started", projectId,
//     projectUrl, jobId, status:"working"|"failed", statusMessage, pollIntervalSeconds };
//   • get_job_status → outputSchema сервер не публикует; боевой completed-ответ несёт
//     content[] (text + resource_link с uri), structuredContent { outputType, projectId,
//     jobId, status, media:[{url,title,kind}] } и _meta.glif.
// Набор значений status get_job_status полностью НЕ ПРОВЕРЕН: точно встречались
// working / completed, остальные («running», «queued», «cancelled» и пр.) трактуются
// по смыслу — неизвестное значение считается «ещё считается», а не провалом.
public static class GlifJobParser
{
    public static GlifJobResult Parse(string? httpBody)
    {
        var payload = ExtractJsonRpcPayload(httpBody);
        if (payload is null) return GlifJobResult.Empty;

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            root = doc.RootElement.Clone();
        }
        catch (JsonException) { return GlifJobResult.Empty; }

        if (root.ValueKind != JsonValueKind.Object) return GlifJobResult.Empty;

        // Ошибка транспорта JSON-RPC — дальше смотреть нечего
        if (root.TryGetProperty("error", out var rpcError) && rpcError.ValueKind == JsonValueKind.Object)
            return GlifJobResult.Empty with
            {
                State = GlifJobState.Failed,
                Error = GetString(rpcError, "message") ?? "ошибка glif",
            };

        // Принимаем и полный JSON-RPC ответ, и голый result (так удобнее в тестах)
        var result = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object ? r : root;

        // Области поиска полей: structuredContent → _meta.glif → JSON внутри text-блоков → сам result
        var scopes = new List<JsonElement>();
        if (result.TryGetProperty("structuredContent", out var structured) && structured.ValueKind == JsonValueKind.Object)
            scopes.Add(structured);
        if (FindMetaGlif(result) is { ValueKind: JsonValueKind.Object } meta) scopes.Add(meta);
        scopes.AddRange(TextJsonScopes(result));
        scopes.Add(result);

        var jobId = FirstString(scopes, "jobId", "job_id");
        var projectId = FirstString(scopes, "projectId", "project_id");
        var poll = FirstNumber(scopes, "pollIntervalSeconds", "poll_interval_seconds");
        var media = CollectMediaUrls(result, scopes);
        var statusMessage = FirstString(scopes, "statusMessage", "status_message", "message");

        var isError = result.TryGetProperty("isError", out var err) && err.ValueKind == JsonValueKind.True;
        var status = FirstString(scopes, "status");
        var state = isError ? GlifJobState.Failed : MapState(status, media.Count);

        return new GlifJobResult(state, jobId, projectId, poll, media,
            state == GlifJobState.Failed ? statusMessage ?? "генерация glif не удалась" : null);
    }

    // Тело ответа — чистый JSON либо SSE-поток (data:-строки); берём последний data-кадр.
    // Логика та же, что в GlifAccountService: endpoint отвечает на прямой tools/call.
    private static string? ExtractJsonRpcPayload(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{')) return trimmed;
        string? last = null;
        foreach (var line in body.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l.StartsWith("data:", StringComparison.Ordinal))
                last = l["data:".Length..].Trim();
        }
        return last is { Length: > 0 } ? last : null;
    }

    private static GlifJobState MapState(string? status, int mediaCount)
    {
        var s = status?.Trim().ToLowerInvariant();
        return s switch
        {
            "completed" or "complete" or "succeeded" or "success" or "done" => GlifJobState.Completed,
            "failed" or "error" or "cancelled" or "canceled" or "timeout" => GlifJobState.Failed,
            null or "" => mediaCount > 0 ? GlifJobState.Completed : GlifJobState.Unknown,
            // working / running / queued / pending / processing и всё незнакомое — ещё считается
            _ => GlifJobState.Working,
        };
    }

    private static JsonElement FindMetaGlif(JsonElement result)
    {
        if (result.TryGetProperty("_meta", out var meta)
            && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("glif", out var glif)
            && glif.ValueKind == JsonValueKind.Object)
            return glif;
        return default;
    }

    // JSON-объекты, приехавшие строкой внутри text-блоков content[] (дубль structuredContent)
    private static IEnumerable<JsonElement> TextJsonScopes(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            if (!block.TryGetProperty("text", out var txt) || txt.ValueKind != JsonValueKind.String) continue;
            var s = txt.GetString()?.Trim();
            if (s is null || !s.StartsWith('{')) continue;
            JsonElement parsed;
            try
            {
                using var doc = JsonDocument.Parse(s);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                parsed = doc.RootElement.Clone();
            }
            catch (JsonException) { continue; }
            yield return parsed;
        }
    }

    // Ссылки на медиа: media[] из областей + блоки resource_link/resource в content[].
    // Порядок сохраняем, дубли (один файл пришёл двумя путями) схлопываем.
    private static IReadOnlyList<string> CollectMediaUrls(JsonElement result, IEnumerable<JsonElement> scopes)
    {
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            var u = url.Trim();
            if (seen.Add(u)) urls.Add(u);
        }

        foreach (var scope in scopes)
        {
            if (scope.ValueKind != JsonValueKind.Object) continue;
            if (!scope.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in media.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String) { Add(item.GetString()); continue; }
                if (item.ValueKind != JsonValueKind.Object) continue;
                Add(GetString(item, "url") ?? GetString(item, "uri") ?? GetString(item, "src"));
            }
        }

        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                var type = GetString(block, "type");
                if (type is not ("resource_link" or "resource")) continue;
                Add(GetString(block, "uri") ?? GetString(block, "url"));
                if (block.TryGetProperty("resource", out var res) && res.ValueKind == JsonValueKind.Object)
                    Add(GetString(res, "uri") ?? GetString(res, "url"));
            }
        }

        return urls;
    }

    private static string? FirstString(IEnumerable<JsonElement> scopes, params string[] names)
    {
        foreach (var scope in scopes)
            foreach (var name in names)
                if (GetString(scope, name) is { Length: > 0 } value)
                    return value;
        return null;
    }

    private static double? FirstNumber(IEnumerable<JsonElement> scopes, params string[] names)
    {
        foreach (var scope in scopes)
            foreach (var name in names)
            {
                if (scope.ValueKind != JsonValueKind.Object) continue;
                if (scope.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
                    && p.TryGetDouble(out var d))
                    return d;
            }
        return null;
    }

    private static string? GetString(JsonElement scope, string name)
    {
        if (scope.ValueKind != JsonValueKind.Object) return null;
        if (scope.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String)
        {
            var s = p.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        return null;
    }
}
