using System.Security.Cryptography;
using System.Text;

namespace ClaudeHomeServer.Telemetry;

/// <summary>Что делать с атрибутом телеметрии.</summary>
public enum PiiAction
{
    /// <summary>Заменить на sha256-префикс (нужен для корреляции, но не раскрывает значение).</summary>
    Hash,

    /// <summary>Пропустить как есть (operational metadata, не PII).</summary>
    Keep,

    /// <summary>Удалить.</summary>
    Drop,
}

/// <summary>
/// Единые правила очистки PII для трейсов и логов.
///
/// Вынесены из <see cref="PiiSanitizingProcessor"/>, чтобы у спанов и логов был ОДИН
/// набор правил: иначе тег с именем персоны дропался бы в спане и уезжал в логе
/// (ровно так и было, пока санитайзер существовал только для трейсов).
///
/// Стратегия — allowlist + drop-by-default: неизвестный атрибут удаляется.
/// Порядок проверок важен, первое совпадение выигрывает:
/// 1. Hash-паттерны (file_path, *.path) → sha256(value)[..8]
/// 2. Keep-список (operational metadata) → пропустить
/// 3. Drop-паттерны (persona, user_id, prompt…) → удалить
/// 4. Всё остальное → удалить (safe default)
///
/// Имена сравниваются нормализованно (без разделителей, регистронезависимо), потому что
/// один и тот же смысл приходит в разных стилях: тег спана <c>session_id</c> и параметр
/// лога <c>{SessionId}</c> — это одно и то же, и правило для них должно быть одно.
/// </summary>
public static class PiiRules
{
    /// <summary>Атрибуты, которые ХЭШИРУЮТСЯ (не дропаются — нужны для корреляции).</summary>
    private static readonly HashSet<string> HashTags = Normalize(
    [
        "file_path", "file.name", "filepath", "path",
        "working_dir", "working.directory", "root_path", "cwd",
    ]);

    /// <summary>Атрибуты, которые ОСТАЮТСЯ как есть (operational metadata, не PII).</summary>
    private static readonly HashSet<string> KeepTags = Normalize(
    [
        // Идентификаторы (не PII — opaque GUIDs).
        // chat_id держит связку «инцидент → чат» (Telemetry/Incidents): правила здесь
        // работают по default-deny, поэтому без этой строки тег выбрасывался бы МОЛЧА,
        // а разбор инцидента переставал бы находить чаты — без единой ошибки в логе.
        // Сторож — PiiSanitizerTests.ChatId_IsKept (падает, если строку убрать).
        "session_id", "chat_id", "turn_id", "trace_id", "span_id",
        // Operational
        "provider", "model", "direction", "tool_name", "outcome",
        "error_type", "reason", "kind", "command",
        // OTel standard
        "otel.status_code", "otel.status_description", "otel.name", "otel.kind",
        // HTTP — стабильные semconv-имена (их пишут инструментации AspNetCore/Http 1.17.0).
        // ВАЖНО: url.full и url.query сюда НЕ входят намеренно — в query-строке уезжают
        // API-ключи (Dify, OpenRouter). Путь запроса виден через http.route — это шаблон
        // роута без значений параметров, поэтому он безопасен.
        "http.request.method", "http.response.status_code", "http.route",
        "url.scheme", "server.address", "server.port",
        "network.protocol.version", "error.type",
        // HTTP — легаси-имена semconv доOTel-1.0, на случай сторонней инструментации
        "http.method", "http.url", "http.status_code", "http.scheme", "http.host",
        // RPC — спаны SignalR-хаба (rpc.method = имя метода хаба, не пользовательские данные)
        "rpc.system", "rpc.service", "rpc.method",
        // Логи: уровень/категория/шаблон сообщения — без значений параметров
        "{OriginalFormat}", "log.level", "category.name", "event.id", "event.name",
        // Custom operational
        "mcp_config_hash", "duration_ms", "tokens_input", "tokens_output",
    ]);

    /// <summary>Подстроки PII-паттернов для дропа (проверяются через Contains по нормализованному имени).</summary>
    private static readonly string[] DropSubstrings =
    [
        "persona", "userid", "username", "ownerid", "ownername",
        "prompt", "content", "body", "message",
        "email", "phone", "address", "apikey", "password", "secret",
        // "text" и "token" — отдельно (точный матч, чтобы не задеть tool_name/tokens_*)
    ];

    /// <summary>Точные имена атрибутов, дропаемых как чистый пользовательский текст.</summary>
    private static readonly HashSet<string> DropExactText = Normalize(["text", "token"]);

    /// <summary>Классифицировать атрибут по его имени.</summary>
    public static PiiAction Classify(string key)
    {
        var norm = Norm(key);

        // 1. Пути хэшируем: и точные имена, и суффикс path (file_path, project.path, FilePath)
        if (HashTags.Contains(norm) || norm.EndsWith("path", StringComparison.Ordinal))
            return PiiAction.Hash;

        // 2. Разрешённые — как есть
        if (KeepTags.Contains(norm))
            return PiiAction.Keep;

        // 3. Явные PII-паттерны
        foreach (var substring in DropSubstrings)
        {
            if (norm.Contains(substring, StringComparison.Ordinal))
                return PiiAction.Drop;
        }

        if (DropExactText.Contains(norm))
            return PiiAction.Drop;

        // 4. Неизвестное — удаляем (safe default)
        return PiiAction.Drop;
    }

    /// <summary>Детерминированный sha256-префикс (8 hex-символов) для корреляции значений.</summary>
    public static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    /// <summary>
    /// Нормализация имени: нижний регистр без разделителей.
    /// <c>session_id</c>, <c>SessionId</c> и <c>session.id</c> становятся одним ключом.
    /// </summary>
    private static string Norm(string key)
    {
        Span<char> buffer = key.Length <= 128 ? stackalloc char[key.Length] : new char[key.Length];
        var length = 0;
        foreach (var c in key)
        {
            if (c is '_' or '.' or '-') continue;
            buffer[length++] = char.ToLowerInvariant(c);
        }
        return new string(buffer[..length]);
    }

    private static HashSet<string> Normalize(IEnumerable<string> keys)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys) set.Add(Norm(key));
        return set;
    }
}
