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
