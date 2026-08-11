using ClaudeHomeServer.Services.Llm;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Классификатор ошибок фолбэка (ADR «Порядок резолва модели…» §2): одна функция
// с белым списком классов, неизвестная ошибка = None (фолбэк не запускается).
public class TurnErrorClassifierTests
{
    private static TurnAttemptOutcome Result(string? status, string? text = null, string subtype = "success") => new()
    {
        HasResult = true,
        Subtype = subtype,
        ApiErrorStatus = status,
        ErrorText = text,
    };

    [Theory]
    [InlineData("429")]
    [InlineData("rate_limit")]
    public void ЛимитЗапросов_ЗапускаетФолбэк(string status)
        => TurnErrorClassifier.Classify(Result(status)).Should().Be(FallbackErrorClass.RateLimit);

    // Прод-кейс 2026-08-09 (qwen/alibabacloud): статус пришёл пустым, текст содержит и код
    // 429, и «quota … exhausted». Исчерпание недельной квоты токен-плана — это UsageLimit
    // (исчерпание биллингового цикла, а не секундное окно запросов), поэтому «quota exhausted»
    // доминирует над цитатой 429: для стороннего провайдера это значит кулдаун, а не долбление
    // каждый ход до конца цикла. Фолбэк запускается в обоих случаях — меняется только кулдаун.
    [Fact]
    public void ИсчерпаниеКвоты_ПустойСтатус_ТекстС429ИQuotaExhausted_КлассUsageLimit()
        => TurnErrorClassifier.Classify(Result(null,
                "API Error: Request rejected (429) · Your token-plan 1-week quota has been exhausted. The quota will reset at 08-11 07:55:00 UTC."))
            .Should().Be(FallbackErrorClass.UsageLimit,
                "«quota … exhausted» — исчерпание квоты (UsageLimit), доминирует над цитатой 429");

    [Theory]
    [InlineData("rate limit exceeded")]
    [InlineData("Too Many Requests")]
    [InlineData("Request rejected (429)")]
    [InlineData("Error 429: slow down")]
    [InlineData("HTTP 429")]
    [InlineData("status 429 returned")]
    public void ЛимитЗапросов_ПустойСтатус_ПоМаркерамВТексте(string text)
        => TurnErrorClassifier.Classify(Result(null, text)).Should().Be(FallbackErrorClass.RateLimit);

    // Цитата «429» в тексте без квалификатора — не RateLimit: голый код в разборе инцидента
    // не должен запускать фолбэк. Узкие маркеры (error/status/http/rejected + 429) не срабатывают.
    [Theory]
    [InlineData("Разбираем инцидент: клиент получил 429 на соседнем сервисе")]
    [InlineData("В логе мелькнул 429, но запрос прошёл успешно")]
    public void Цитата429_ПустойСтатус_НеКлассифицируетсяКакRateLimit(string text)
        => TurnErrorClassifier.Classify(Result(null, text)).Should().Be(FallbackErrorClass.None,
            "голое «429» в тексте — цитата, а не сам код ошибки");

    [Fact]
    public void RateLimitEventRejected_ПоОкнуИсчерпания_ЗапускаетФолбэк()
        => TurnErrorClassifier.Classify(new TurnAttemptOutcome
        {
            HasResult = false,
            RateLimitRejected = true,
        }).Should().Be(FallbackErrorClass.RateLimit);

    [Theory]
    [InlineData("500")]
    [InlineData("502")]
    [InlineData("503")]
    [InlineData("504")]
    [InlineData("507")]   // класс «5xx» целиком, не только знакомые коды
    [InlineData("529")]
    [InlineData("overloaded_error")]
    public void ОшибкаПровайдера_5xx_ЗапускаетФолбэк(string status)
        => TurnErrorClassifier.Classify(Result(status)).Should().Be(FallbackErrorClass.ProviderError);

    // Паритет «статус ↔ текст»: 5xx/перегрузка при ПУСТОМ статусе распознаётся по тексту.
    // Сценарий — CLI отдал код только в тексте (инциденты, где api_error_status пришёл null).
    // Маркеры — канонические reason phrases/type, по одному на каждый 5xx + overloaded_error.
    [Theory]
    [InlineData("API Error: overloaded_error")]
    [InlineData("Internal Server Error")]
    [InlineData("Error 502 Bad Gateway")]
    [InlineData("Service Unavailable")]
    [InlineData("Gateway Timeout")]
    public void ОшибкаПровайдера_ПустойСтатус_ПоМаркерамВТексте(string text)
        => TurnErrorClassifier.Classify(Result(null, text)).Should().Be(FallbackErrorClass.ProviderError);

    // Гейт от ложных срабатываний: общие слова «server error»/«overloaded» НЕ входят в маркеры.
    // Ход, ЦИТИРУЮЩИЙ их в разборе инцидента, не должен уезжать на другого провайдера — тот же
    // приём, что у ContextOverflow (узкие формулировки) и RateLimit (429 с квалификатором).
    [Theory]
    [InlineData("Разбираем инцидент: у нас была server error на эндпоинте")]
    [InlineData("В логах мелькнула server error, но запрос прошёл")]
    [InlineData("Сервис был overloaded весь вчерашний день")]
    public void ЦитатаServerError_ПустойСтатус_НеКлассифицируетсяКакProviderError(string text)
        => TurnErrorClassifier.Classify(Result(null, text)).Should().Be(FallbackErrorClass.None,
            "голые «server error»/«overloaded» слишком обычны в разборе — нужны точные reason phrases 5xx");

    // Регрессия: статус 5xx решает по коду и не анализирует текст — даже цитата overflow
    // не переключает класс (ветка статусов доходит до 5xx раньше ContextOverflow-проверки).
    [Fact]
    public void Статус500_ОстаётсяProviderError_ДажеПриOverflowТексте()
        => TurnErrorClassifier.Classify(Result("500", "Разбираем «Prompt is too long» из прошлого"))
            .Should().Be(FallbackErrorClass.ProviderError,
                "статус 5xx решает по коду, текст не анализируется");

    [Fact]
    public void ОбрывПотока_БезResult_ЗапускаетФолбэк()
        => TurnErrorClassifier.Classify(new TurnAttemptOutcome { HasResult = false })
            .Should().Be(FallbackErrorClass.Unreachable,
                "любой обрыв stream — в том числе посреди уже начатого ответа — ошибка доставки");

    [Theory]
    [InlineData("process_exit")]
    [InlineData("ECONNREFUSED")]
    [InlineData("ENOTFOUND")]
    public void НедоступностьЭндпоинта_ПоСтатусу(string status)
        => TurnErrorClassifier.Classify(Result(status)).Should().Be(FallbackErrorClass.Unreachable);

    [Theory]
    [InlineData("fetch failed")]
    [InlineData("connect ECONNREFUSED 104.18.32.7:443")]
    [InlineData("getaddrinfo ENOTFOUND api.example.com")]
    [InlineData("socket hang up")]
    public void НедоступностьЭндпоинта_ПоТекстуОшибки(string text)
        => TurnErrorClassifier.Classify(Result(null, text)).Should().Be(FallbackErrorClass.Unreachable);

    [Fact]
    public void UsageLimit403_ЗапускаетФолбэк()
        => TurnErrorClassifier.Classify(Result("403",
                "Your usage limit has been reached. Resets at 5pm."))
            .Should().Be(FallbackErrorClass.UsageLimit);

    [Fact]
    public void InvalidKey403_ФолбэкНеЗапускает_ЭтоОшибкаКонфигурации()
        => TurnErrorClassifier.Classify(Result("403", "invalid_api_key: invalid x-api-key"))
            .Should().Be(FallbackErrorClass.None);

    // Прод-кейс 2026-08-11 (kimi, чат f9eebaaa): CLI не положил код в api_error_status —
    // 403 остался только внутри текста ошибки. Ветка пустого статуса обязана спросить про
    // usage limit, иначе класс выходит None и фолбэк не стартует (пользователь переключал
    // модель руками). Текст — целиком, как пришёл в ленту.
    [Fact]
    public void UsageLimit_ПустойСтатус_ТекстИнцидента_КлассUsageLimit()
        => TurnErrorClassifier.Classify(Result(null,
                "Failed to authenticate. API Error: 403 You've reached your usage limit for this billing cycle. Your quota will be refreshed in the next cycle..."))
            .Should().Be(FallbackErrorClass.UsageLimit,
                "фраза «usage limit» в тексте — исчерпание квоты, фолбэк нужен");

    // Fail-closed: настоящая ошибка аутентификации сменой модели не лечится. «Failed to
    // authenticate» в начале сообщения Kimi к обратному выводу приводить не должно —
    // решает наличие фразы про usage limit, а не преамбула.
    [Fact]
    public void ОшибкаКлюча_ПустойСтатус_ОстаётсяNone()
        => TurnErrorClassifier.Classify(Result(null,
                "Failed to authenticate. API Error: 401 invalid api key"))
            .Should().Be(FallbackErrorClass.None,
                "неверный ключ — ошибка конфигурации, фолбэк её маскировал бы");

    // «quota» + «exhausted» — исчерпание квоты (UsageLimit), не RateLimit-окно: lived раньше
    // в LooksRateLimited, но провайдер с такой формулировкой при пустом статусе уходил бы в
    // RateLimit и не помечался в кулдауне — тот же инцидент, другая формулировка.
    [Theory]
    [InlineData("Your quota has been exhausted. Please upgrade your plan.")]
    [InlineData("quota is exhausted")]
    public void ИсчерпаниеКвоты_ПустойСтатус_QuotaExhausted_КлассUsageLimit(string text)
        => TurnErrorClassifier.Classify(Result(null, text)).Should().Be(FallbackErrorClass.UsageLimit);

    // Голое слово quota/exhausted по отдельности — НЕ UsageLimit (слишком обычны в текстах).
    [Theory]
    [InlineData("Your daily quota is 1000")]
    [InlineData("resources exhausted")]
    public void ГолаяQuota_Exhausted_НеКлассифицируется(string text)
        => TurnErrorClassifier.Classify(Result(null, text)).Should().Be(FallbackErrorClass.None);

    // 429 по статусу остаётся RateLimit независимо от текста — ветка решает по статусу,
    // до LooksUsageLimited/LooksRateLimited дело не доходит (Minor 3 ревью: проверка).
    [Fact]
    public void Статус429_ОстаётсяRateLimit_ДажеПриQuotaExhaustedВТексте()
        => TurnErrorClassifier.Classify(Result("429",
                "Your quota has been exhausted."))
            .Should().Be(FallbackErrorClass.RateLimit,
                "статус 429 решает по коду, текст не анализируется");

    [Fact]
    public void Auth403_БезТекста_ФолбэкНеЗапускает()
        => TurnErrorClassifier.Classify(Result("403")).Should().Be(FallbackErrorClass.None);

    [Theory]
    [InlineData("401")]
    [InlineData("400")]
    [InlineData("404")]
    [InlineData("authentication_error")]
    public void ОшибкиАвторизацииИЗапроса_ФолбэкНеЗапускают(string status)
        => TurnErrorClassifier.Classify(Result(status)).Should().Be(FallbackErrorClass.None);

    // Переполнение контекста: «Prompt is too long» у Anthropic (видели на проде: kimi-k3 со
    // заявленным окном 1M) и эквиваленты сторонних провайдеров. Класс отдельный от None —
    // повторять ту же модель бессмысленно, но шагнуть по цепочке к бóльшему окну можно.
    [Theory]
    [InlineData("Prompt is too long: 210000 tokens > 200000 maximum.")]
    [InlineData("input length exceeds 200000, max is 128000")]
    [InlineData("This model's maximum context length is 128000 tokens.")]
    [InlineData("context_length_exceeded")]
    [InlineData("Your request is longer than the model's context window.")]
    [InlineData("The input is too long for the model")]
    public void ПереполнениеКонтекста_ПоТексту_КлассContextOverflow(string text)
        => TurnErrorClassifier.Classify(Result(null, text)).Should().Be(FallbackErrorClass.ContextOverflow);

    [Fact]
    public void ПереполнениеКонтекста_Статус400_ПоТексту_КлассContextOverflow()
        => TurnErrorClassifier.Classify(Result("400", "Prompt is too long."))
            .Should().Be(FallbackErrorClass.ContextOverflow,
                "400 сам по себе — содержательная ошибка (None), но с overflow-текстом это ContextOverflow");

    [Fact]
    public void ПереполнениеКонтекста_Статус413_ПоТексту_КлассContextOverflow()
        => TurnErrorClassifier.Classify(Result("413", "context_length_exceeded"))
            .Should().Be(FallbackErrorClass.ContextOverflow,
                "413 — куда OpenAI-совместимые эндпоинты кладут overflow; с маркером в тексте это ContextOverflow");

    // Страховка на провайдеров, кладущих в поле статуса не код, а ТИП ошибки (invalid_request_error,
    // request_too_large) — при overflow-тексте это тот же класс. Видели на проде: в инциденте
    // kimi-k3 статус пришёл пустым, а эта ветка ловит провайдеров, заполняющих поле типом ошибки.
    [Theory]
    [InlineData("invalid_request_error")]
    [InlineData("request_too_large")]
    public void ПереполнениеКонтекста_ТипОшибкиВместоКода_КлассContextOverflow(string status)
        => TurnErrorClassifier.Classify(Result(status, "Prompt is too long."))
            .Should().Be(FallbackErrorClass.ContextOverflow,
                "некоторые провайдеры кладут в статус тип ошибки, а не HTTP-код; с overflow-текстом это ContextOverflow");

    [Theory]
    [InlineData("invalid_request_error")]
    [InlineData("request_too_large")]
    public void ТипОшибкиВместоКода_БезOverflowТекста_ОстаётсяNone(string status)
        => TurnErrorClassifier.Classify(Result(status, "malformed request body"))
            .Should().Be(FallbackErrorClass.None,
                "ярлык без overflow-маркера — содержательная ошибка, fail-closed сохраняется");

    // Minor-review: overflow-фразы ищутся в тексте ошибки, поэтому ход, ЦИТИРУЮЩИЙ «Prompt is
    // too long» (разбор таких инцидентов в чатах), при прочем сбое классифицировался бы ложно.
    // Маркеры в тексте трактуем как overflow ТОЛЬКО при пустом статусе или 400/413 — куда
    // провайдеры реально кладут эту ошибку. При прочих статусах цитата — не overflow.
    [Theory]
    [InlineData("418")]
    [InlineData("404")]
    [InlineData("401")]
    [InlineData("200")]
    public void ЦитатаOverflowПриЧужомСтатусе_НеКлассифицируетсяКакOverflow(string status)
        => TurnErrorClassifier.Classify(Result(status,
                "Разбираем ошибку «Prompt is too long: 210000 tokens > 200000 maximum.»"))
            .Should().Be(FallbackErrorClass.None,
                "overflow-маркеры в тексте при нерелевантном статусе — это цитата, а не сама ошибка переполнения");

    [Fact]
    public void ContextOverflow_WireNameДляМаркера()
        => TurnErrorClassifier.WireName(FallbackErrorClass.ContextOverflow)
            .Should().Be("context_overflow");

    [Fact]
    public void Статус400_БезOverflowТекста_ОстаётсяNone()
        => TurnErrorClassifier.Classify(Result("400", "invalid request body"))
            .Should().Be(FallbackErrorClass.None, "обычный 400 — не переполнение контекста");

    [Fact]
    public void НеизвестнаяОшибка_ФолбэкНеЗапускает_FailClosed()
        => TurnErrorClassifier.Classify(Result("418", "teapot"))
            .Should().Be(FallbackErrorClass.None,
                "лучше показать ошибку, чем молча жечь лимиты других аккаунтов о неопознанную проблему");

    [Fact]
    public void ОшибкаБезСтатусаИТекста_ФолбэкНеЗапускает()
        => TurnErrorClassifier.Classify(Result(null, subtype: "error"))
            .Should().Be(FallbackErrorClass.None);

    [Fact]
    public void InterruptПользователя_НеОшибкаДоставки()
        => TurnErrorClassifier.Classify(new TurnAttemptOutcome
        {
            HasResult = false,
            InterruptedByUser = true,
        }).Should().Be(FallbackErrorClass.None);

    [Fact]
    public void ErrorMaxTurns_СодержательнаяОшибка_ФолбэкНеЗапускает()
        => TurnErrorClassifier.Classify(Result(null, subtype: "error_max_turns"))
            .Should().Be(FallbackErrorClass.None, "это проблема содержания хода, а не доставки");
}
