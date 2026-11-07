using System.Net;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Контракт шва IFalRequestCost (дефект 22068e9d, шаг S-SPEND-1): ResolveUsdAsync по
// request_id возвращает фактическую цену (USD) либо null, когда биллинга ещё нет —
// без исключения: «цены нет» штатный исход, не ошибка.
// Бэкофф цикла сжимается до [0] через internal-шов FalCostService (тот же приём, что
// internal-хуки FileWatcherService): цикл проверяется без реальных 77с ожидания,
// производственный дефолт [2,5,10,20,40] тестом не меняется.
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

    private static FalCostService MakeService(StubHandler handler)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Fal:ApiKey"] = "test-key" })
            .Build();
        var http = new Mock<IHttpClientFactory>();
        http.Setup(f => f.CreateClient("fal")).Returns(new HttpClient(handler));
        var svc = new FalCostService(http.Object, config);
        svc.BackoffSeconds = [0];
        return svc;
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
}
