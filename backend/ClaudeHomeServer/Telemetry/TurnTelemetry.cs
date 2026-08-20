using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

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
    ///
    /// Два разных идентификатора, и путать их нельзя:
    /// <list type="bullet">
    /// <item><c>chat_id</c> — стабильный id чата CCS. По нему и только по нему инцидент
    /// связывается с чатом (см. IncidentDossierService).</item>
    /// <item><c>session_id</c> — csid claude CLI. Его нет на первом ходу и он
    /// перезаписывается на каждом <c>system/init</c>, поэтому фолбэка «подставить id чата»
    /// здесь НЕТ: без csid тег просто не ставится, а не врёт двумя пространствами id.</item>
    /// </list>
    /// </summary>
    public static Activity? StartTurnSpan(
        string chatId, string? claudeSessionId, string turnId, string? model, string provider)
    {
        var activity = ServerActivitySource.Instance.StartActivity(ServerActivitySource.SpanNames.ChatTurn);
        if (activity is null) return null;
        activity
            .SetTag("chat_id", chatId)
            .SetTag("turn_id", turnId)
            .SetTag("model", model ?? "unknown")
            .SetTag("provider", provider);
        if (!string.IsNullOrEmpty(claudeSessionId))
            activity.SetTag("session_id", claudeSessionId);
        return activity;
    }

    /// <summary>
    /// Помечает спан хода исходом: тег <c>outcome</c>, а для отказа — <c>error_type</c>
    /// и статус <see cref="ActivityStatusCode.Error"/>.
    ///
    /// Без этого упавший ход в трейсах НИЧЕМ не отличался от успешного: outcome и
    /// error_type жили только в метриках, а статус спана оставался Unset. То есть
    /// на дашборде было видно, что отказы есть, а открыть в Traces Explorer именно
    /// их — нечем: отобрать не по чему.
    ///
    /// Кардинальность здесь не проблема (в отличие от метрик): оба значения берутся
    /// из замкнутых наборов, а спаны не образуют временных рядов.
    /// </summary>
    public static void MarkTurnOutcome(Activity? activity, bool isError, string? apiErrorStatus)
    {
        if (activity is null) return;

        activity.SetTag("outcome", isError ? "error" : "success");
        if (!isError) return;

        var errorType = ClassifyErrorType(apiErrorStatus);
        activity.SetTag("error_type", errorType);
        // Description не заполняем: PiiSanitizingProcessor всё равно его обнуляет,
        // а в текст ошибки провайдера легко попадают данные пользователя.
        activity.SetStatus(ActivityStatusCode.Error);
    }

    /// <summary>
    /// Дочерний спан запуска процесса claude CLI. Родитель — активный chat.turn
    /// (Activity.Current на момент вызова). kind: "local" | "docker".
    ///
    /// Пара идентификаторов та же, что у спана хода: <c>chat_id</c> обязателен,
    /// <c>session_id</c> ставится только когда csid CLI реально есть.
    /// </summary>
    public static Activity? StartProcessSpan(
        string kind, string command, string chatId, string? claudeSessionId, string mcpConfigHash)
    {
        var activity = ServerActivitySource.Instance.StartActivity(ServerActivitySource.SpanNames.ProcessStart);
        if (activity is null) return null;
        activity
            .SetTag("kind", kind)
            .SetTag("command", ExecutableName(command))
            .SetTag("chat_id", chatId)
            .SetTag("mcp_config_hash", mcpConfigHash);
        if (!string.IsNullOrEmpty(claudeSessionId))
            activity.SetTag("session_id", claudeSessionId);
        return activity;
    }

    /// <summary>
    /// Только имя исполняемого файла — без каталогов.
    ///
    /// Вызывающий передаёт команду запуска как есть, а на хосте это абсолютный путь вида
    /// <c>C:\Users\{имя}\AppData\Roaming\npm\...\claude.exe</c> — то есть имя пользователя ОС
    /// внутри значения. Санитайзер это не ловил: он классифицирует по имени тега, а тег
    /// <c>command</c> состоит в allowlist (пути хэшируются по ключам вида *_path). Утечку
    /// нашли на боевых данных: спан приехал в SigNoz с полным путём.
    ///
    /// Режем в источнике, а не правилом санитайзера: диагностическая ценность тега — «какой
    /// бинарь запустили» (claude.exe против docker), каталог для этого не нужен, а хэш вместо
    /// имени сделал бы тег нечитаемым. Разделители режем оба сразу (<c>/</c> и <c>\</c>) руками,
    /// не через <c>Path.GetFileName</c>: тот ориентируется на разделители текущей ОС, и на Linux
    /// (где гоняется CI) обратный слэш — обычный символ имени, поэтому Windows-путь не резался бы.
    /// </summary>
    internal static string ExecutableName(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "unknown";
        var trimmed = command.Trim();
        var cut = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        var name = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        return string.IsNullOrEmpty(name) ? "unknown" : name;
    }

    /// <summary>
    /// Среда исполнения хода одним словом: <c>docker</c> — процесс claude идёт в песочнице,
    /// <c>local</c> — на машине сервера. Выбирает её <c>ILauncherFactory.ForOwner</c> по полю
    /// <c>User.ExecutionEnvironment</c> ВЛАДЕЛЬЦА процесса, поэтому в одном инстансе ходы
    /// разных пользователей идут в разных средах.
    ///
    /// Общая точка для спана <c>process.start</c> (тег <c>kind</c>) и метрик хода
    /// (тег <c>execution</c>): словарь значений один, иначе трейс и метрика перестанут
    /// биться друг с другом при разборе «песочница тормозит или нет».
    /// </summary>
    public static string ExecutionKind(bool isSandboxed) => isSandboxed ? "docker" : "local";

    /// <summary>
    /// Запись результата хода по result-событию CLI: длительность из duration_ms
    /// самого CLI (не пересчитывается), плюс счётчик ошибок при отказе.
    ///
    /// Отказом считается не только subtype=error: при API-ошибке провайдера (напр. 429)
    /// CLI отдаёт subtype=success с is_error=true — вызывающий обязан свести оба
    /// признака в <paramref name="isError"/>, иначе отказ уедет в метрику как success.
    /// </summary>
    public static void RecordTurnResult(
        long durationMs, string provider, string? model, bool isError, string? apiErrorStatus,
        bool isSandboxed = false)
    {
        var outcome = isError ? "error" : "success";
        var execution = ExecutionKind(isSandboxed);
        ServerMetrics.RecordLlmDuration(durationMs, provider, model ?? "unknown", outcome, execution);
        if (isError)
            ServerMetrics.RecordLlmError(provider, ClassifyErrorType(apiErrorStatus), execution);
    }

    /// <summary>
    /// Модель, которой РЕАЛЬНО идёт ход, из события stream-json.
    ///
    /// Зачем: <c>Session.Model</c> и слоты тиров — это НАМЕРЕНИЕ. Когда модель у чата не задана
    /// и слот пуст, резолвер отдаёт null («решает CLI»), и в телеметрию уходил литерал
    /// <c>unknown</c> — на боевом ходе так и вышло. Ответить «чем считали» по такой метрике
    /// нельзя, а именно за этим на дашборд заведена панель моделей.
    ///
    /// CLI называет модель сам, в двух видах событий:
    /// <list type="bullet">
    /// <item><c>system/init</c> — поле <c>model</c> верхнего уровня (модель прогона);</item>
    /// <item><c>assistant</c> — <c>message.model</c> (модель, которая выдала этот ответ).</item>
    /// </list>
    /// Второе точнее: init называет модель на старте прогона, а ответ — по факту.
    /// Возвращает null, если события не того типа или поле пустое.
    /// </summary>
    public static string? ModelFromEvent(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        // assistant: message.model — берём первым, он ближе к факту
        if (root.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("model", out var msgModel)
            && msgModel.ValueKind == JsonValueKind.String
            && msgModel.GetString() is { Length: > 0 } fromMessage)
        {
            return fromMessage;
        }

        // system/init: model верхнего уровня
        if (root.TryGetProperty("model", out var topModel)
            && topModel.ValueKind == JsonValueKind.String
            && topModel.GetString() is { Length: > 0 } fromTop)
        {
            return fromTop;
        }

        return null;
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
    /// Событие лимитов подписки (<c>rate_limit_event</c> от CLI).
    ///
    /// CLI шлёт его ~на каждый ход, в том числе со <c>status="allowed"</c>: без разреза по
    /// статусу счётчик меряет активность, а не упор в лимиты. Так и вышло 2026-08-20 —
    /// правило «Лимиты провайдера жмут» зажглось на 29 событиях, среди которых отклонённых
    /// ходов не было вовсе. Поэтому статус — обязательный аргумент, а не опция.
    /// </summary>
    public static void RecordRateLimit(string provider, string? status) =>
        ServerMetrics.RecordRateLimitHit(provider, ClassifyRateLimitStatus(status));

    /// <summary>
    /// Значение тега <c>status</c>. Множество замкнуто протоколом CLI
    /// (allowed | allowed_warning | rejected), поэтому лимитер <see cref="MetricTagGuard"/>
    /// не нужен — хватает белого списка, а незнакомое значение схлопывается в <c>unknown</c>.
    /// </summary>
    internal static string ClassifyRateLimitStatus(string? status) => status switch
    {
        "allowed" or "allowed_warning" or "rejected" => status,
        _ => "unknown",
    };

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
