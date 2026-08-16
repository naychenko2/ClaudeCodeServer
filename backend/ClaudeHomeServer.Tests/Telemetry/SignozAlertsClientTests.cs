using System.Net;
using ClaudeHomeServer.Telemetry.Alerts;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ClaudeHomeServer.Tests.Telemetry;

/// <summary>
/// Клиент опроса алертов: SigNoz — опциональная зависимость, и его отказ обязан давать
/// «мы не знаем» (null) без стектрейса в логе. Наружу летит ровно один случай — остановка
/// приложения; всё остальное цикл опроса переживает и тикает дальше.
/// </summary>
public class SignozAlertsClientTests
{
    private static readonly AlertsOptions Options = new()
    {
        Enabled = true,
        ApiKey = "ключ",
        SignozUrl = "http://localhost:3301/telemetry-proxy",
    };

    [Fact]
    public async Task ТаймаутЗапроса_ВозвращаетNullАНеБросает()
    {
        // HttpClient сообщает о своём таймауте TaskCanceledException — наследником
        // OperationCanceledException. Пока клиент пробрасывал ЛЮБУЮ отмену, этот таймаут
        // уходил наружу как «приложение останавливается», а там `catch { break; }` цикла
        // AlertPollingService — то есть один подвисший опрос убивал доставку алертов
        // насовсем, до рестарта сервера.
        var (client, sink) = Make(_ => throw new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout",
            new TimeoutException()));

        var result = await client.FetchAsync(CancellationToken.None);

        result.Should().BeNull();
        sink.Entries.Should().BeEmpty("о подвисшей зависимости отчитывается QuietHttpLogger клиента");
    }

    [Fact]
    public async Task ОстановкаПриложения_Пробрасывается()
    {
        var (client, _) = Make(_ => throw new OperationCanceledException());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => client.FetchAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "остановка приложения — не отказ опроса, цикл обязан выйти");
    }

    [Fact]
    public async Task ХостЛежит_ВозвращаетNullИМолчитВЛоге()
    {
        var (client, sink) = Make(_ => throw new HttpRequestException("Подключение не установлено"));

        var result = await client.FetchAsync(CancellationToken.None);

        result.Should().BeNull();
        // Дублировать QuietHttpLogger стектрейсом на каждый опрос — ровно та беда,
        // из-за которой боевой лог тонул в красных портянках
        sink.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task НеожиданныйСбой_ЛогируетсяСоСтектрейсом()
    {
        // Не сеть и не таймаут: такое QuietHttpLogger не объясняет, и стектрейс здесь —
        // единственная зацепка. Молчать в этой ветке было бы хуже, чем шуметь.
        var (client, sink) = Make(_ => throw new InvalidOperationException("внезапно"));

        var result = await client.FetchAsync(CancellationToken.None);

        result.Should().BeNull();
        sink.Entries.Should().ContainSingle().Which.HasException.Should().BeTrue();
    }

    [Fact]
    public async Task ОтказПоКодуОтвета_ВозвращаетNull()
    {
        var (client, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var result = await client.FetchAsync(CancellationToken.None);

        // Именно null, а не пустой список: пустота означала бы «алертов нет» и разослала бы
        // «всё восстановилось» ровно тогда, когда связь с SigNoz пропала
        result.Should().BeNull();
    }

    private static (SignozAlertsClient Client, CollectingSink Sink) Make(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var http = new HttpClient(new StubHandler(respond));
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(http);

        var sink = new CollectingSink();
        // Без using: фабрику пережить обязан созданный ею логгер — тест читает sink после выхода
        var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(sink);
        });

        return (new SignozAlertsClient(
            factory.Object, Options, loggerFactory.CreateLogger<SignozAlertsClient>()), sink);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
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
