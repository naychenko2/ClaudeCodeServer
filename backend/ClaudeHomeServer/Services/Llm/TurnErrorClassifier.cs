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
    // Контекст хода не помещается в окно модели («Prompt is too long» и эквиваленты
    // сторонних провайдеров). Отдельный класс: ту же модель/подписку повторять бессмысленно
    // (окно — свойство модели, не аккаунта), но шагать по цепочке к модели с бóльшим окном —
    // можно. Не помечает подписку исчерпанной и эндпоинт недоступным (он ответил, просто отказал).
    ContextOverflow,
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
            // порядок: недоступность → лимит использования → ошибка провайдера → лимит
            // запросов → переполнение контекста; не опознали — None (fail-closed).
            // Инвариант паритета «статус ↔ текст» (ADR §2): каждый признак, что ловится по
            // статусу, обязан ловиться и по тексту — CLI часто отдаёт apiErrorStatus=null,
            // и тогда текст единственный сигнал (на этом перекосе вскрылись прод-инциденты).
            if (LooksUnreachable(outcome.ErrorText)) return FallbackErrorClass.Unreachable;
            // Исчерпание квоты при пустом статусе (инцидент 2026-08-11, kimi: CLI не положил
            // 403 в api_error_status, код остался только в тексте «Failed to authenticate.
            // API Error: 403 You've reached your usage limit for this billing cycle»). Раньше
            // ветка про usage limit не спрашивала — класс выходил None, и фолбэк не стартовал.
            // ПЕРЕД лимитом запросов: «usage limit» — маркер более узкий и более длящийся
            // (квота биллингового цикла, а не окно запросов), и он же включает кулдаун
            // провайдера. Текст, где обе семантики сразу, разумнее считать исчерпанием квоты.
            // Fail-closed не страдает: сама фраза «usage limit» обязательна, поэтому настоящая
            // ошибка ключа («invalid api key») остаётся None, несмотря на «Failed to
            // authenticate» в начале сообщения Kimi.
            if (LooksUsageLimited(outcome.ErrorText)) return FallbackErrorClass.UsageLimit;
            // Ошибка провайдера (5xx/перегрузка) перед лимитом запросов: перегруженный эндпоинт
            // уходит в кулдаун, а не ждёт сброса секундного окна — тяжелее по последствиям.
            // Маркеры — канонические reason phrases/type 5xx, не общие слова (см. LooksProviderError):
            // закрывает паритет со статусами overloaded_error/5xx при пустом api_error_status.
            if (LooksProviderError(outcome.ErrorText)) return FallbackErrorClass.ProviderError;
            if (LooksRateLimited(outcome.ErrorText)) return FallbackErrorClass.RateLimit;
            if (LooksContextOverflow(outcome.ErrorText)) return FallbackErrorClass.ContextOverflow;
            return FallbackErrorClass.None;
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
        // Контекст не помещается в окно модели. Anthropic шлёт «Prompt is too long» (видели на проде:
        // kimi-k3 с заявленным окном 1M), OpenAI-совместимые — «context_length_exceeded». Провайдеры
        // кладут это на 400/413, а иные — в поле статуса ТИП ошибки: invalid_request_error,
        // request_too_large. Маркеры в ErrorText трактуем как overflow ТОЛЬКО при этих статусах:
        // иначе ход, ЦИТИРУЮЩИЙ «Prompt is too long» (разбор таких инцидентов в чатах), при прочем
        // сбое классифицировался бы ложно как overflow. Без overflow-текста любой из этих статусов —
        // содержательная ошибка (None): fail-closed сохраняется и для новых ярлыков.
        // Пустой статус (когда маркеры в тексте — единственный сигнал) разобран отдельной веткой выше.
        if (status is "400" or "413" or "invalid_request_error" or "request_too_large"
            && LooksContextOverflow(outcome.ErrorText))
            return FallbackErrorClass.ContextOverflow;

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
        FallbackErrorClass.ContextOverflow => "context_overflow",
        _ => null,
    };

    // Семантика исчерпанного лимита использования (а не «ключ плохой»). Спрашивается и при
    // статусе 403, и при пустом статусе — сторонние провайдеры оставляют код только в тексте.
    // «quota» + «exhausted» — исчерпание квоты (биллинговый цикл), а не окно запросов: lived
    // здесь, чтобы провайдер уходил в кулдаун, а не долбился каждый ход (инцидент 2026-08-11,
    // другая формулировка). По отдельности слова не трактуем — слишком обычны в разборе ошибок.
    private static bool LooksUsageLimited(string? text) =>
        text is not null
        && (text.Contains("usage limit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("usage_limit", StringComparison.OrdinalIgnoreCase)
            || (text.Contains("quota", StringComparison.OrdinalIgnoreCase)
                && text.Contains("exhausted", StringComparison.OrdinalIgnoreCase)));

    // Признаки переполнения контекста. Anthropic CLI шлёт «Prompt is too long» (видели на проде:
    // kimi-k3 с заявленным окном 1M упал с этим текстом — тариф режет раньше конфига).
    // Сторонние провайдеры через Anthropic-скин и OpenAI-совместимые эндпоинты дают свои
    // формулировки: «context_length_exceeded», «input length exceeds», «longer than the model's
    // context window». Берём по подстроке без учёта регистра — точного кода ошибки у них нет.
    private static readonly string[] ContextOverflowPhrases =
    [
        "prompt is too long",
        "input length exceeds",
        "context length exceeded",
        "context_length_exceeded",
        "maximum context length",
        "longer than the model",
        "exceeds the model",
        "too long for the model",
    ];

    private static bool LooksContextOverflow(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        foreach (var phrase in ContextOverflowPhrases)
            if (value.Contains(phrase, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Маркеры лимита запросов в тексте ошибки при пустом статусе (прод-кейс: сторонние
    // провайдеры отдают в поле статуса «—», а HTTP 429 — только текстом). Формулировки без
    // кода ошибки. Исчерпание квоты («quota exhausted») сюда НЕ относится — это UsageLimit.
    private static readonly string[] RateLimitPhrases =
    [
        "rate limit",
        "too many requests",
    ];

    // Код 429 в связке со словом-квалификатором. Голое «429» ловить нельзя — текст
    // ошибки может ЦИТИРОВАТЬ код (тот же класс грабель, что у overflow-маркеров выше):
    // узнаваем только в окружении «error/status/http/rejected», характерном для самой
    // ошибки, а не для разбора инцидента в чате.
    private static readonly string[] RateLimitCodeMarkers =
    [
        "rejected (429)",
        "error 429",
        "status 429",
        "http 429",
    ];

    private static bool LooksRateLimited(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        foreach (var phrase in RateLimitPhrases)
            if (value.Contains(phrase, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var marker in RateLimitCodeMarkers)
            if (value.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Маркеры ошибки провайдера в тексте при пустом статусе — паритет со статусами
    // overloaded_error/5xx (прод-кейс: CLI отдал 5xx без кода в api_error_status). Каждый
    // маркер — канонический error type или HTTP reason phrase 5xx: overloaded_error (Anthropic),
    // internal server error (500), bad gateway (502), service unavailable (503),
    // gateway timeout (504). Голое «server error»/«overloaded» сюда НЕ входят — слишком
    // обычны в разборе инцидентов, и ход, ЦИТИРУЮЩИЙ их, не должен уезжать на фолбэк (тот же
    // гейт от ложных срабатываний, что у ContextOverflow и RateLimit). Редкие 5xx без общей
    // формулировки (501 Not Implemented и т.п.) при пустом статусе остаются None: их код почти
    // всегда приезжает в api_error_status, а ловить «not implemented» как маркер — ловить и
    // содержательные ответы о нереализованных функциях. Fail-closed сохраняется.
    private static readonly string[] ProviderErrorPhrases =
    [
        "overloaded_error",
        "internal server error",
        "bad gateway",
        "service unavailable",
        "gateway timeout",
    ];

    private static bool LooksProviderError(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        foreach (var phrase in ProviderErrorPhrases)
            if (value.Contains(phrase, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

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
