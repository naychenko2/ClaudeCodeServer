using ClaudeHomeServer.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ClaudeHomeServer.Tests.Telemetry;

/// <summary>
/// Логгер неудачного экспорта телеметрии: недоступный коллектор — это Warning в одну строку
/// и не чаще раза в интервал, а не Error со стектрейсом на каждую попытку экспорта.
/// </summary>
public class OtlpExportHttpLoggerTests
{
    private static readonly Uri Endpoint = new("http://localhost:4318/v1/traces");

    [Fact]
    public void LogRequestFailed_ПишетОдинWarningБезСтектрейса()
    {
        var (logger, sink) = Build(out _);

        Fail(logger);

        sink.Entries.Should().ContainSingle();
        var entry = sink.Entries[0];
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain("http://localhost:4318").And.Contain("недоступен");
        // Исключение в запись не кладём: стектрейс здесь всегда один и тот же.
        entry.HasException.Should().BeFalse();
    }

    [Fact]
    public void LogRequestFailed_ПовторВИнтервалеМолчит()
    {
        var (logger, sink) = Build(out var clock);

        Fail(logger);
        clock.Advance(OtlpExportHttpLogger.ReportInterval - TimeSpan.FromSeconds(1));
        Fail(logger);

        sink.Entries.Should().ContainSingle("экспорт идёт по расписанию — жалоба не чаще раза в интервал");
    }

    [Fact]
    public void LogRequestFailed_ПослеИнтервалаЖалуетсяСнова()
    {
        var (logger, sink) = Build(out var clock);

        Fail(logger);
        clock.Advance(OtlpExportHttpLogger.ReportInterval + TimeSpan.FromSeconds(1));
        Fail(logger);

        sink.Entries.Should().HaveCount(2);
    }

    [Fact]
    public void УспешныйЭкспортСбрасываетТроттлинг()
    {
        var (logger, sink) = Build(out var clock);

        Fail(logger);
        clock.Advance(TimeSpan.FromSeconds(5));
        // Коллектор поднялся...
        logger.LogRequestStop(null, Request(), new HttpResponseMessage(System.Net.HttpStatusCode.OK), TimeSpan.Zero);
        clock.Advance(TimeSpan.FromSeconds(5));
        // ...и снова упал — об этом надо узнать сразу, а не через интервал тишины.
        Fail(logger);

        sink.Entries.Should().HaveCount(2);
    }

    [Fact]
    public void ОтказКоллектораПоКодуОтветаТожеWarning()
    {
        var (logger, sink) = Build(out _);

        logger.LogRequestStop(
            null, Request(), new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable), TimeSpan.Zero);

        sink.Entries.Should().ContainSingle();
        sink.Entries[0].Level.Should().Be(LogLevel.Warning);
        sink.Entries[0].Message.Should().Contain("503");
    }

    private static void Fail(OtlpExportHttpLogger logger) =>
        logger.LogRequestFailed(
            null, Request(), null,
            new HttpRequestException("Подключение не установлено", new Exception("отказ в подключении")),
            TimeSpan.FromSeconds(4));

    private static HttpRequestMessage Request() => new(HttpMethod.Post, Endpoint);

    private static (OtlpExportHttpLogger Logger, CollectingSink Sink) Build(out FakeClock clock)
    {
        var sink = new CollectingSink();
        var factory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(sink);
        });
        var c = new FakeClock();
        clock = c;
        return (new OtlpExportHttpLogger(factory, () => c.Now), sink);
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
