using ClaudeHomeServer.Services.Http;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ClaudeHomeServer.Tests.Http;

/// <summary>
/// Логгер опциональной зависимости: недоступный хост — это Warning в одну строку и не чаще
/// раза в интервал, а не Error со стектрейсом на каждый запрос.
/// </summary>
public class QuietHttpLoggerTests
{
    private static readonly Uri Endpoint = new("http://localhost:4318/v1/traces");

    private static readonly QuietHttpClientProfile Profile = new(
        Category: "Tests.Quiet",
        Subject: "OTLP-коллектором",
        Consequence: "Телеметрия не уходит.");

    [Fact]
    public void LogRequestFailed_ПишетОдинWarningБезСтектрейса()
    {
        var (logger, sink) = Build(out _);

        Fail(logger);

        sink.Entries.Should().ContainSingle();
        var entry = sink.Entries[0];
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should()
            .Contain("OTLP-коллектором").And
            .Contain("http://localhost:4318").And
            .Contain("Телеметрия не уходит.").And
            // Точка сообщения исключения не должна складываться с точкой шаблона
            .NotContain("..");
        // Исключение в запись не кладём: стектрейс здесь всегда один и тот же.
        entry.HasException.Should().BeFalse();
    }

    [Fact]
    public void LogRequestFailed_ПовторВИнтервалеМолчит()
    {
        var (logger, sink) = Build(out var clock);

        Fail(logger);
        clock.Advance(QuietHttpLogger.ReportInterval - TimeSpan.FromSeconds(1));
        Fail(logger);

        sink.Entries.Should().ContainSingle("зависимость дёргается часто — жалоба не чаще раза в интервал");
    }

    [Fact]
    public void LogRequestFailed_ПослеИнтервалаЖалуетсяСнова()
    {
        var (logger, sink) = Build(out var clock);

        Fail(logger);
        clock.Advance(QuietHttpLogger.ReportInterval + TimeSpan.FromSeconds(1));
        Fail(logger);

        sink.Entries.Should().HaveCount(2);
    }

    [Fact]
    public void УспешныйЗапросСбрасываетТроттлинг()
    {
        var (logger, sink) = Build(out var clock);

        Fail(logger);
        clock.Advance(TimeSpan.FromSeconds(5));
        // Зависимость поднялась...
        logger.LogRequestStop(null, Request(), new HttpResponseMessage(System.Net.HttpStatusCode.OK), TimeSpan.Zero);
        clock.Advance(TimeSpan.FromSeconds(5));
        // ...и снова упала — об этом надо узнать сразу, а не через интервал тишины.
        Fail(logger);

        sink.Entries.Should().HaveCount(2);
    }

    [Fact]
    public void ОтказПоКодуОтветаТожеWarning()
    {
        var (logger, sink) = Build(out _);

        logger.LogRequestStop(
            null, Request(), new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable), TimeSpan.Zero);

        sink.Entries.Should().ContainSingle();
        sink.Entries[0].Level.Should().Be(LogLevel.Warning);
        sink.Entries[0].Message.Should().Contain("503");
    }

    [Fact]
    public void LogRequestStop_4xx_ПечатаетТелоИНейтральнуюФормулировку()
    {
        // Главный кейс задачи: 400 от llama-server с диагностическим JSON раньше
        // читался как «локальная модель недоступна», потому что тело не дочитывалось.
        var (logger, sink) = Build(out _);

        logger.LogRequestStop(
            null,
            Request(),
            ResponseWith(System.Net.HttpStatusCode.BadRequest,
                """{"error":{"message":"request (9012 tokens) exceeds the available context size (8192 tokens)","type":"exceed_context_size_error"}}"""),
            TimeSpan.Zero);

        sink.Entries.Should().ContainSingle();
        var entry = sink.Entries[0];
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should()
            .Contain("HTTP 400").And
            .Contain("Запрос отвергнут").And
            .Contain("OTLP-коллектором").And
            // Тело целиком, без статуса одной цифрой — это и есть диагноз.
            .Contain("exceed_context_size_error").And
            .Contain("Телеметрия не уходит.");
        // 5xx-формулировка про «на стороне» тут неуместна: зависимость работает, она отвергла запрос.
        entry.Message.Should().NotContain("Ошибка на стороне");
    }

    [Fact]
    public void LogRequestStop_4xx_ПустоеТелоПечатаетЗаглушку()
    {
        var (logger, sink) = Build(out _);

        logger.LogRequestStop(
            null, Request(), ResponseWith(System.Net.HttpStatusCode.Unauthorized, ""), TimeSpan.Zero);

        sink.Entries.Should().ContainSingle();
        sink.Entries[0].Message.Should().Contain("<пусто>");
    }

    [Fact]
    public void LogRequestStop_4xx_БольшоеТелоУсекается()
    {
        var (logger, sink) = Build(out _);

        var big = new string('x', QuietHttpLogger.MaxBodyChars * 3);
        logger.LogRequestStop(
            null, Request(), ResponseWith(System.Net.HttpStatusCode.BadRequest, big), TimeSpan.Zero);

        sink.Entries.Should().ContainSingle();
        // Полное тело не пролезло — после усечения остаётся ровно MaxBodyChars символов
        // исходного текста и многоточие. Соседние пробелы из нормализации в подсчёт не идут.
        sink.Entries[0].Message.Should()
            .Contain(new string('x', QuietHttpLogger.MaxBodyChars))
            .And.Contain("…")
            .And.NotContain(big);
    }

    [Fact]
    public void LogRequestStop_4xx_ПовторТогоЖеТелаМолчитБезИнтервала()
    {
        // Тело уже сказало диагноз — лить ту же строку снова нет смысла. Время не нужно:
        // у нас per-body дедуп, а не time-based троттлинг.
        var (logger, sink) = Build(out _);

        var resp1 = ResponseWith(System.Net.HttpStatusCode.BadRequest, """{"error":"context overflow"}""");
        var resp2 = ResponseWith(System.Net.HttpStatusCode.BadRequest, """{"error":"context overflow"}""");

        logger.LogRequestStop(null, Request(), resp1, TimeSpan.Zero);
        logger.LogRequestStop(null, Request(), resp2, TimeSpan.Zero);

        sink.Entries.Should().ContainSingle("повтор того же тела — диагноз уже в логе");
    }

    [Fact]
    public void LogRequestStop_4xx_ДругоеТелоНеГлушитсяПрошлым()
    {
        // Сценарий «новый вид отказа потерялся за тишиной интервала» из задачи.
        var (logger, sink) = Build(out _);

        logger.LogRequestStop(null, Request(),
            ResponseWith(System.Net.HttpStatusCode.BadRequest, """{"error":"context overflow"}"""), TimeSpan.Zero);
        logger.LogRequestStop(null, Request(),
            ResponseWith(System.Net.HttpStatusCode.Unauthorized, """{"error":"invalid api key"}"""), TimeSpan.Zero);

        sink.Entries.Should().HaveCount(2);
        sink.Entries[0].Message.Should().Contain("context overflow");
        sink.Entries[1].Message.Should().Contain("invalid api key");
    }

    [Fact]
    public void LogRequestStop_4xx_УспехСбрасываетДедуп()
    {
        // Между двумя 4xx с одним телом прошёл успешный запрос — следующая 4xx должна
        // залогироваться, иначе рецидив после починки прошёл бы молча.
        var (logger, sink) = Build(out _);

        logger.LogRequestStop(null, Request(),
            ResponseWith(System.Net.HttpStatusCode.BadRequest, """{"error":"context overflow"}"""), TimeSpan.Zero);
        logger.LogRequestStop(null, Request(),
            new HttpResponseMessage(System.Net.HttpStatusCode.OK), TimeSpan.Zero);
        logger.LogRequestStop(null, Request(),
            ResponseWith(System.Net.HttpStatusCode.BadRequest, """{"error":"context overflow"}"""), TimeSpan.Zero);

        sink.Entries.Should().HaveCount(2);
    }

    [Fact]
    public async Task LogRequestStop_4xx_ContentОстаётсяЧитаемымПослеЛога()
    {
        // Имитируем ResponseHeadersRead: HttpContent указывает на стрим, который ещё
        // не вычитан. После LogRequestStop вызывающий (ChatTurnAsync потокового режима)
        // должен суметь прочитать тело сам.
        var (logger, sink) = Build(out _);
        var payload = """{"error":"request too large"}""";
        var response = ResponseWithUnbufferedContent(System.Net.HttpStatusCode.BadRequest, payload);

        logger.LogRequestStop(null, Request(), response, TimeSpan.Zero);

        sink.Entries.Should().ContainSingle();
        sink.Entries[0].Message.Should().Contain("request too large");

        // Главное: тело всё ещё доступно вызывающему. Без LoadIntoBufferAsync здесь был бы
        // пустой/обрезанный стрим — логгер съел бы сетевой поток раньше владельца.
        var stillReadable = await response.Content.ReadAsStringAsync();
        stillReadable.Should().Contain("request too large");
    }

    [Fact]
    public void LogRequestStop_5xx_ТелоНеЧитаетсяИИспользуетСтаруюФормулировку()
    {
        var (logger, sink) = Build(out _);

        logger.LogRequestStop(
            null,
            Request(),
            ResponseWith(System.Net.HttpStatusCode.InternalServerError, "this body must not be logged"),
            TimeSpan.Zero);

        sink.Entries.Should().ContainSingle();
        sink.Entries[0].Message.Should()
            .Contain("HTTP 500").And
            .Contain("Ошибка на стороне").And
            // 5xx-формулировка НЕ упоминает «Тело:»: вычитка накладна и бесполезна.
            .NotContain("Тело:");
    }

    private static HttpResponseMessage ResponseWith(System.Net.HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    // Конструируем ответ так, чтобы Content ссылался на собственный MemoryStream без
    // внутренней буферизации через StreamContent — это ближе к тому, как HttpClient
    // отдаёт ResponseHeadersRead, и проверяет, что логгер действительно буферизует
    // поток сам, не съедая его.
    private static HttpResponseMessage ResponseWithUnbufferedContent(System.Net.HttpStatusCode code, string body)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        var response = new HttpResponseMessage(code)
        {
            Content = new StreamContent(new MemoryStream(bytes)),
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return response;
    }

    private static void Fail(QuietHttpLogger logger) =>
        logger.LogRequestFailed(
            null, Request(), null,
            new HttpRequestException("Подключение не установлено", new Exception("отказ в подключении")),
            TimeSpan.FromSeconds(4));

    private static HttpRequestMessage Request() => new(HttpMethod.Post, Endpoint);

    private static (QuietHttpLogger Logger, CollectingSink Sink) Build(out FakeClock clock)
    {
        var sink = new CollectingSink();
        var factory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(sink);
        });
        var c = new FakeClock();
        clock = c;
        return (new QuietHttpLogger(factory, Profile, () => c.Now), sink);
    }

    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => Now += by;
    }

    private sealed record Entry(LogLevel Level, string Message, bool HasException);

    private sealed class CollectingSink : ILoggerProvider, ILogger
    {
        public List<Entry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new Entry(logLevel, formatter(state, exception), exception is not null));
    }
}
