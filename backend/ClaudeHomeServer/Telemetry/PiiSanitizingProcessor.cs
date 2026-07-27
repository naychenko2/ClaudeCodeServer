using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using OpenTelemetry;

namespace ClaudeHomeServer.Telemetry;

/// <summary>
/// Санитайзер PII перед экспортом в OTLP. Сидит в начале pipeline (CompositeProcessor),
/// так что ОБА бэкенда (Aspire + SigNoz) получают очищенные данные.
///
/// Стратегия: allowlist + drop-by-default. Список разрешённых атрибутов захардкожен —
/// любой атрибут ВНЕ списка дропается или хэшируется (для paths).
///
/// Порядок проверок (важен — первое совпадение выигрывает):
/// 1. Hash-паттерны (file_path, *.path) → sha256(value)[..8]
/// 2. Keep-список (operational metadata) → пропустить как есть
/// 3. Drop-паттерны (persona, user_id, prompt…) → удалить
/// 4. Всё остальное → удалить (safe default)
/// </summary>
public sealed class PiiSanitizingProcessor : BaseProcessor<Activity>
{
    /// <summary>Теги, которые ХЭШИРУЮТСЯ (не дропаются — нужны для корреляции в дашбордах).</summary>
    private static readonly HashSet<string> HashTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "file_path", "file.name", "filepath", "path",
        "working_dir", "working.directory", "root_path", "cwd",
    };

    /// <summary>Теги, которые ОСТАЮТСЯ как есть (operational metadata, не PII).</summary>
    private static readonly HashSet<string> KeepTags = new(StringComparer.OrdinalIgnoreCase)
    {
        // Идентификаторы (не PII — opaque GUIDs)
        "session_id", "turn_id", "trace_id", "span_id",
        // Operational
        "provider", "model", "direction", "tool_name", "outcome",
        "error_type", "reason", "kind", "command",
        // OTel standard
        "otel.status_code", "otel.status_description", "otel.name", "otel.kind",
        // HTTP
        "http.method", "http.url", "http.status_code", "http.route", "http.scheme", "http.host",
        // Custom operational
        "mcp_config_hash", "duration_ms", "tokens_input", "tokens_output",
    };

    /// <summary>Подстроки PII-паттернов для дропа (проверяются через Contains).</summary>
    private static readonly string[] DropSubstrings =
    {
        "persona", "user_id", "user_name", "owner_id", "owner_name",
        "prompt", "content", "body", "message",
        "email", "phone", "address", "api_key", "password", "secret",
        // "text" и "token" — отдельно (суффиксный матч, чтобы не задеть tool_name/tokens_*)
    };

    /// <summary>Точные имена тегов, дропаемых как чистый пользовательский текст.</summary>
    private static readonly HashSet<string> DropExactText = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "token",
    };

    public override void OnEnd(Activity activity)
    {
        // Собираем изменения в списки, применяем ПОСЛЕ итерации (безопасная мутация)
        var replacements = new List<KeyValuePair<string, object?>>();
        var removals = new List<string>();

        foreach (var tag in activity.TagObjects)
        {
            var key = tag.Key;

            if (IsHashKey(key))
            {
                var hash = ComputeHash(tag.Value?.ToString() ?? string.Empty);
                replacements.Add(new(key, hash));
            }
            else if (KeepTags.Contains(key))
            {
                continue;
            }
            else if (ShouldDrop(key))
            {
                removals.Add(key);
            }
            else
            {
                // Default: дропаем неизвестные теги (safe default)
                removals.Add(key);
            }
        }

        foreach (var key in removals)
            activity.SetTag(key, null);

        foreach (var replacement in replacements)
            activity.SetTag(replacement.Key, replacement.Value);
    }

    /// <summary>
    /// true если ключ попадает в hash-категорию: точное совпадение с HashTags
    /// или суффикс <c>.path</c> (правило таблицы <c>*.path</c>).
    /// </summary>
    private static bool IsHashKey(string key)
    {
        if (HashTags.Contains(key))
            return true;

        return key.EndsWith(".path", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>true если ключ содержит PII-подстроку или совпадает с точным drop-тегом.</summary>
    private static bool ShouldDrop(string key)
    {
        var lower = key.ToLowerInvariant();

        foreach (var substring in DropSubstrings)
        {
            if (lower.Contains(substring))
                return true;
        }

        return DropExactText.Contains(key);
    }

    /// <summary>Детерминированный sha256-префикс (8 hex-символов) для корреляции путей.</summary>
    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }
}
