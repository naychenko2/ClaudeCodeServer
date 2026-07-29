using System.Diagnostics;
using System.Security.Cryptography;

namespace ClaudeHomeServer.Telemetry;

/// <summary>
/// Инструментирование хода ClaudeSession: OTel-спаны (chat.turn, process.start)
/// и метрики (LLM duration/errors/rate-limit). Вызывается из ClaudeSession в
/// ключевых точках жизненного цикла хода.
///
/// Токены здесь НЕ учитываются (C4 — SpendStore = source of truth для биллинга).
/// </summary>
internal static class TurnTelemetry
{
    /// <summary>
    /// Запуск корневого спана хода. Возвращает Activity (null, если ни один
    /// listener не присоединён). Caller распоряжается Dispose через using.
    /// </summary>
    public static Activity? StartTurnSpan(string sessionId, string turnId, string? model, string provider)
    {
        var activity = ServerActivitySource.Instance.StartActivity(ServerActivitySource.SpanNames.ChatTurn);
        if (activity is null) return null;
        activity
            .SetTag("session_id", sessionId)
            .SetTag("turn_id", turnId)
            .SetTag("model", model ?? "unknown")
            .SetTag("provider", provider);
        return activity;
    }

    /// <summary>
    /// Дочерний спан запуска процесса claude CLI. Родитель — активный chat.turn
    /// (Activity.Current на момент вызова). kind: "local" | "docker".
    /// </summary>
    public static Activity? StartProcessSpan(
        string kind, string command, string sessionId, string mcpConfigHash)
    {
        var activity = ServerActivitySource.Instance.StartActivity(ServerActivitySource.SpanNames.ProcessStart);
        if (activity is null) return null;
        activity
            .SetTag("kind", kind)
            .SetTag("command", command)
            .SetTag("session_id", sessionId)
            .SetTag("mcp_config_hash", mcpConfigHash);
        return activity;
    }

    /// <summary>
    /// Запись результата хода по result-событию CLI: длительность из duration_ms
    /// самого CLI (не пересчитывается), плюс счётчик ошибок при отказе.
    ///
    /// Отказом считается не только subtype=error: при API-ошибке провайдера (напр. 429)
    /// CLI отдаёт subtype=success с is_error=true — вызывающий обязан свести оба
    /// признака в <paramref name="isError"/>, иначе отказ уедет в метрику как success.
    /// </summary>
    public static void RecordTurnResult(
        long durationMs, string provider, string? model, bool isError, string? apiErrorStatus)
    {
        var outcome = isError ? "error" : "success";
        ServerMetrics.RecordLlmDuration(durationMs, provider, model ?? "unknown", outcome);
        if (isError)
            ServerMetrics.RecordLlmError(provider, ClassifyErrorType(apiErrorStatus));
    }

    /// <summary>
    /// Признак отказа хода по result-событию CLI.
    ///
    /// Отказ приходит двумя разными путями, и учитывать надо ОБА:
    /// <list type="bullet">
    /// <item>жёсткий сбой CLI — <c>subtype=error</c>;</item>
    /// <item>API-ошибка провайдера (напр. 429) — <c>subtype=success</c> при <c>is_error=true</c>.</item>
    /// </list>
    /// Пока учитывался только первый, отказы провайдера уезжали в метрику как success.
    /// </summary>
    public static bool IsTurnFailure(string? subtype, bool isErrorFlag) =>
        subtype == "error" || isErrorFlag;

    /// <summary>
    /// Срабатывание мягкого rate-limit (rate_limit_event от CLI).
    /// </summary>
    public static void RecordRateLimit(string provider) =>
        ServerMetrics.RecordRateLimitHit(provider);

    /// <summary>
    /// Прямая запись ошибки — когда категория известна заранее (process_exit и т.д.).
    /// </summary>
    public static void RecordError(string provider, string errorType) =>
        ServerMetrics.RecordLlmError(provider, errorType);

    /// <summary>
    /// Короткий SHA-256-хеш (8 hex-символов) содержимого MCP-конфига —
    /// для корреляции ходов с одинаковым набором MCP-серверов.
    /// Пустой/отсутствующий путь → "none".
    /// </summary>
    public static string McpConfigHash(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "none";
        try
        {
            if (!File.Exists(path)) return "missing";
            var hash = SHA256.HashData(File.ReadAllBytes(path));
            return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
        }
        catch
        {
            return "error";
        }
    }

    /// <summary>
    /// Классификация api_error_status CLI в категорию для метрики.
    /// rate_limit | network | auth | process_exit | unknown
    /// </summary>
    internal static string ClassifyErrorType(string? apiErrorStatus) => apiErrorStatus switch
    {
        "429" or "rate_limit" => "rate_limit",
        "401" or "403" or "authentication_error" => "auth",
        "500" or "502" or "503" or "504" or "overloaded_error" => "network",
        "process_exit" => "process_exit",
        _ => "unknown",
    };
}
