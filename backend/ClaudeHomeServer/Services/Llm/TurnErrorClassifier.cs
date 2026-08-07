namespace ClaudeHomeServer.Services.Llm;

// Классы ошибок хода, запускающих фолбэк (ADR «Порядок резолва модели, классы ошибок
// фолбэка, защита от зацикливания» §2). Фолбэк запускают только ошибки ДОСТАВКИ,
// при которых та же просьба на другой паре «модель × подписка» имеет шанс пройти.
// Ошибки содержания (невалидный ответ, отказ модели, авторизация, сломанный запрос)
// подменой пары не лечатся — их фолбэк маскировал бы, а не лечил.
public enum FallbackErrorClass
{
    // Не фолбэк-класс: неизвестная ошибка либо не ошибка вовсе (успех, interrupt
    // пользователя). Fail-closed: лучше показать ошибку, чем молча жечь лимиты
    // других аккаунтов о неопознанную проблему.
    None,
    // Лимит запросов: HTTP 429; rate_limit_event rejected по окну исчерпания
    RateLimit,
    // Лимит использования: HTTP 403 с семантикой «usage limit reached»
    UsageLimit,
    // Ошибка провайдера: HTTP 5xx, overloaded_error
    ProviderError,
    // Недоступность эндпоинта: DNS-фейл, connection refused/timeout, обрыв TLS,
    // любой обрыв stream — в том числе посреди уже начатого ответа
    Unreachable,
}

// Итог одной попытки хода глазами потока событий адаптера. Всё, что нужно
// классификатору, собрано здесь — сама классификация есть одна функция Classify.
public sealed record TurnAttemptOutcome
{
    // Ход завершился result-событием CLI (иначе — процесс умер без result
    // либо запуск/цикл чтения упал исключением)
    public required bool HasResult { get; init; }
    public string? Subtype { get; init; }
    // api_error_status из result (HTTP-статус строкой либо ярлык CLI)
    public string? ApiErrorStatus { get; init; }
    // Текст ошибки хода (API-ошибка провайдера из result / исключение запуска)
    public string? ErrorText { get; init; }
    // rate_limit_event rejected по окну исчерпания внутри попытки — CLI приостановил ход
    public bool RateLimitRejected { get; init; }
    // Ход остановил пользователь (Interrupt) — это не ошибка доставки
    public bool InterruptedByUser { get; init; }
}

// Классификатор ошибок фолбэка: ОДНА функция с белым списком классов — по образцу
// TurnTelemetry.ClassifyErrorType (классификация в одной точке, а не разбросанные
// по коду if). Вызывается только для неудачных попыток; неизвестная ошибка = None
// (фолбэк НЕ запускается).
public static class TurnErrorClassifier
{
    // Node/сетевые коды ошибок — стабильные маркеры недоступности эндпоинта
    // (встречаются в тексте ошибки CLI, иногда в api_error_status)
    private static readonly string[] NetworkErrorCodes =
    [
        "ECONNREFUSED", "ECONNRESET", "ECONNABORTED", "ENOTFOUND", "ETIMEDOUT",
        "EAI_AGAIN", "EPIPE", "EHOSTUNREACH", "ENETUNREACH", "EPROTO", "UND_ERR_SOCKET",
    ];

    // Те же маркеры фразами: CLI не всегда присылает код ошибки целиком
    private static readonly string[] NetworkPhrases =
        ["fetch failed", "socket hang up", "network socket disconnected", "tls handshake"];

    public static FallbackErrorClass Classify(TurnAttemptOutcome outcome)
    {
        // Остановка пользователем — не ошибка доставки
        if (outcome.InterruptedByUser) return FallbackErrorClass.None;

        // Мягкий лимит: rejected по окну исчерпания (five_hour/seven_day) — CLI
        // приостановил ход до сброса окна
        if (outcome.RateLimitRejected) return FallbackErrorClass.RateLimit;

        // Процесс умер без result — любой обрыв потока, включая посреди начатого ответа
        if (!outcome.HasResult) return FallbackErrorClass.Unreachable;

        var status = outcome.ApiErrorStatus?.Trim();
        if (string.IsNullOrEmpty(status))
        {
            // Статуса нет — решаем по тексту (напр. «fetch failed» без статуса);
            // не опознали — None (fail-closed)
            return LooksUnreachable(outcome.ErrorText) ? FallbackErrorClass.Unreachable : FallbackErrorClass.None;
        }

        // Белый список статусов
        if (status is "429" or "rate_limit") return FallbackErrorClass.RateLimit;
        // Смерть CLI-процесса — тот же класс, что обрыв потока
        if (status == "process_exit") return FallbackErrorClass.Unreachable;
        // Перегрузка провайдера
        if (status == "overloaded_error") return FallbackErrorClass.ProviderError;
        // 403 неоднозначен: фолбэк только при семантике «usage limit reached»;
        // invalid key/авторизация — ошибка конфигурации, ротация её не лечит
        if (status == "403")
            return LooksUsageLimited(outcome.ErrorText) ? FallbackErrorClass.UsageLimit : FallbackErrorClass.None;
        // Класс «5xx» целиком
        if (int.TryParse(status, out var code) && code is >= 500 and <= 599) return FallbackErrorClass.ProviderError;
        // Сетевые маркеры в статусе или тексте ошибки
        if (LooksUnreachable(status) || LooksUnreachable(outcome.ErrorText)) return FallbackErrorClass.Unreachable;

        // Неизвестный статус, прочие 4xx (400/401), содержательные отказы — фолбэк НЕ запускается
        return FallbackErrorClass.None;
    }

    // Имя класса на проводе (ProviderSwitchedMessage.Reason): фронт по нему выбирает
    // каноническую формулировку подсказки. Стиль — как у api_error_status CLI
    // (rate_limit, overloaded_error): snake_case. None наружу не уходит — null.
    public static string? WireName(FallbackErrorClass cls) => cls switch
    {
        FallbackErrorClass.RateLimit => "rate_limit",
        FallbackErrorClass.UsageLimit => "usage_limit",
        FallbackErrorClass.ProviderError => "provider_error",
        FallbackErrorClass.Unreachable => "unreachable",
        _ => null,
    };

    // 403 с семантикой исчерпанного лимита использования (а не «ключ плохой»)
    private static bool LooksUsageLimited(string? text) =>
        text is not null
        && (text.Contains("usage limit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("usage_limit", StringComparison.OrdinalIgnoreCase));

    private static bool LooksUnreachable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        foreach (var code in NetworkErrorCodes)
            if (value.Contains(code, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var phrase in NetworkPhrases)
            if (value.Contains(phrase, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
