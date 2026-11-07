using System.Net;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Контракт шва IFalRequestCost (дефект 22068e9d, шаг S-SPEND-1): ResolveUsdAsync по
// request_id возвращает фактическую цену (USD) либо null, когда биллинга ещё нет —
// без исключения: «цены нет» штатный исход, не ошибка.
// Бэкофф цикла сжимается до [0] через internal-init FalCostService при создании:
// цикл проверяется без реальных 77с ожидания; производственный дефолт [2,5,10,20,40]
// тестом не меняется и после создания не сдвигается.
public class FalCostResolveTests
{
    // Заглушка billing-events: отвечает по делегату и помнит последний запрос — чтобы
    // проверить, что опрос идёт именно по переданному request_id.
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }

    // Заглушка с транспортным сбоем: первая попытка — TaskCanceledException (так же бросает
    // HttpClient при своём Timeout на неотменённом ct), дальше — ответ-эталон.
    private sealed class TimeoutThenHandler(HttpResponseMessage ok) : HttpMessageHandler
    {
        private int _attempt;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _attempt++;
            return _attempt == 1
                ? Task.FromException<HttpResponseMessage>(new TaskCanceledException("Транспортный таймаут"))
                : Task.FromResult(ok);
        }
    }

    // Заглушка счёта запросов: мера «повторный запрос цены не идёт в сеть».
    private sealed class CountingStub : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"billing_events":[{"request_id":"req-cache","endpoint_id":"fal-ai/flux/schnell","cost_estimate_nano_usd":123000000}]}
                    """),
            });
        }
    }

    private static FalCostService MakeService(HttpMessageHandler handler, int[]? backoff = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Fal:ApiKey"] = "test-key" })
            .Build();
        var http = new Mock<IHttpClientFactory>();
        http.Setup(f => f.CreateClient("fal")).Returns(new HttpClient(handler));
        // Бэкофф сжимается при создании (internal-init): цикл без реальных 77с.
        return new FalCostService(http.Object, config) { BackoffSeconds = backoff ?? [0] };
    }

    [Fact]
    public async Task ResolveUsdAsync_БиллингЕсть_ВозвращаетЦенуПоRequestId()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"billing_events":[{"request_id":"req-1","endpoint_id":"fal-ai/flux/schnell","cost_estimate_nano_usd":123000000,"output_units":1,"unit_price":123000000}]}
                """),
        });
        var svc = MakeService(handler);

        var usd = await svc.ResolveUsdAsync("req-1", CancellationToken.None);

        usd.Should().Be(0.123, "nanoSum/1e9 — та же конверсия, что у публикации стоимости в чат");
        handler.LastRequest!.RequestUri!.Query.Should().Contain("request_id=req-1");
    }

    [Fact]
    public async Task ResolveUsdAsync_БиллингаНет_ВозвращаетNullБезИсключения()
    {
        // 404 — события ещё нет (штатная ранняя попытка).
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var svc = MakeService(handler);

        double? usd = null;
        Func<Task> call = async () => usd = await svc.ResolveUsdAsync("req-404", CancellationToken.None);
        await call.Should().NotThrowAsync();
        usd.Should().BeNull();
    }

    [Fact]
    public async Task ResolveUsdAsync_СобытиеБезСтоимости_ВозвращаетNullБезИсключения()
    {
        // Событие биллинга есть, но cost_estimate_nano_usd ещё null — «ждём», не «посчитано».
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"billing_events":[{"request_id":"req-2","endpoint_id":"fal-ai/flux/schnell","cost_estimate_nano_usd":null}]}
                """),
        });
        var svc = MakeService(handler);

        double? usd = null;
        Func<Task> call = async () => usd = await svc.ResolveUsdAsync("req-2", CancellationToken.None);
        await call.Should().NotThrowAsync();
        usd.Should().BeNull();
    }

    [Fact]
    public async Task ResolveUsdAsync_ОтменённыйCt_ВозвращаетNullБезИсключения()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"billing_events":[]}"""),
        });
        var svc = MakeService(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        double? usd = null;
        Func<Task> call = async () => usd = await svc.ResolveUsdAsync("req-cancel", cts.Token);
        await call.Should().NotThrowAsync("отмена вызывающего — штатный исход шва, а не исключение");
        usd.Should().BeNull();
        handler.LastRequest.Should().BeNull("отменённый цикл выходит до обращения к транспорту");
    }

    [Fact]
    public async Task ResolveUsdAsync_ТранспортныйТаймаут_ПродолжаетЦиклИВозвращаетЦену()
    {
        // Таймаут транспорта (ct не отменён) — не отмена вызывающего: цикл жив, следующая
        // попытка находит цену. Две попытки с нулевым бэкоффом — чтобы не ждать реальных 77 с.
        var ok = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"billing_events":[{"request_id":"req-3","endpoint_id":"fal-ai/flux/schnell","cost_estimate_nano_usd":123000000}]}
                """),
        };
        var svc = MakeService(new TimeoutThenHandler(ok), [0, 0]);

        var usd = await svc.ResolveUsdAsync("req-3", CancellationToken.None);

        usd.Should().Be(0.123, "таймаут первой попытки не убивает цикл: цена со второй попытки");
    }

    [Fact]
    public async Task ResolveUsdAsync_ПовторныйЗапросТойжеЦены_ТотЖеРезультатБезВторойСети()
    {
        var handler = new CountingStub();
        var svc = MakeService(handler);

        var first = await svc.ResolveUsdAsync("req-cache", CancellationToken.None);
        var second = await svc.ResolveUsdAsync("req-cache", CancellationToken.None);

        first.Should().Be(0.123);
        second.Should().Be(first, "повторный запрос цены обслуживается из общего task опроса, а не из нового цикла");
        handler.Calls.Should().Be(1, "второй запрос не пошёл в billing-events");
    }

    [Fact]
    public async Task TrackИResolveUsdAsync_ОдномуRequestId_ОдинСетевойЦиклНаДвоих()
    {
        // Сценарий ревью d524e017: чат уже опрашивает request_id через Track, потребитель
        // (Images/ImageEditor) приходит за той же ценой — берёт тот же результат,
        // а не начинает второй 77-секундный опрос.
        var handler = new CountingStub();
        var svc = MakeService(handler);
        var published = new TaskCompletionSource<FalCostMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.OnCostResolved = (_, msg) =>
        {
            published.TrySetResult(msg);
            return Task.CompletedTask;
        };

        svc.Track("s-1", "req-cache");
        var usd = await svc.ResolveUsdAsync("req-cache", CancellationToken.None);

        usd.Should().Be(0.123);
        var msg = await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        msg.CostUsd.Should().Be(0.123, "чат получил ту же цену из общего опроса");
        handler.Calls.Should().Be(1, "опрос чата и потребитель делят один task: второго сетевого цикла нет");
    }
}
