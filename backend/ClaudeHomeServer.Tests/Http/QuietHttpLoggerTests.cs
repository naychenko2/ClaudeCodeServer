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
