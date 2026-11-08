using System.Net;
using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Тест ПРОДОВОГО пути учёта fal (хвост ревью R-S-SPEND-ПАКЕТ, п.1). TryTrackFalCost и
// BackfillFalCosts в TurnCostLedger (Services/Sessions/TurnCostLedger.cs:85, :97) —
// единственное место, где список id из tool_result
// превращается в отдельные Track'ы, а значит — в N публикаций fal_cost на N запросов пакета.
// Прежний тест (FalCostBatchTests.ПакетныйToolResult…) повторял этот цикл вручную и проверял
// копию логики: мутация «одиночный вызов вместо цикла» в SessionManager не краснела нигде
// (проба 08.11: SpendMapping|FalCost — OK 77/77). Здесь оба пути идут через сам SessionManager
// — живой tool_result хода и бэкфилл загруженной истории — и краснеют ровно там, где id
// пакета потерян.
//
// Держится публичный контракт: одному сетевому циклу — одна запись, пакету из N запросов —
// N записей, по одной на id. Дедуп _seen в FalCostService не даёт двойной публикации, поэтому
// любое отклонение числа публикаций — дефект вызывающего, а не сервиса.
public class FalCostProductionPathTests : SessionManagerTestBase
{
    private const string IdA = "req-a";
    private const string IdB = "req-b";
    private const string IdC = "req-c";

    // Форма ответа пакетных submit_jobs/wait_for_jobs: верхнего request_id нет, id — в jobs[].
    private static string BatchJson(params string[] ids)
    {
        var jobs = string.Join(",", ids.Select(i =>
            $$"""{"request_id":"{{i}}","status":"COMPLETED","response_url":"https://queue.fal.run/fal-ai/flux/requests/{{i}}"}"""));
        return $$"""{"status":"COMPLETED","jobs":[{{jobs}}]}""";
    }

    // Заглушка billing-events: на каждый запрошенный id — событие с ценой. Пачка отвечает
    // всем разом — как живой лимит fal на 1–50 request_id в одном запросе.
    private sealed class BillingStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var ids = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query).GetValues("request_id") ?? [];
            var events = string.Join(",", ids.Select(i =>
                $$"""{"request_id":"{{i}}","cost_estimate_nano_usd":1000000}"""));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"billing_events":[{{events}}]}"""),
            });
        }
    }

    public FalCostProductionPathTests()
    {
        var http = new Mock<IHttpClientFactory>();
        http.Setup(f => f.CreateClient("fal")).Returns(new HttpClient(new BillingStub()));
        var fal = new FalCostService(http.Object, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Fal:ApiKey"] = "test-key" })
            .Build())
        {
            BackoffSeconds = [0],
            MinRequestIntervalMs = 0,
            CollectWindowMs = 100,
        };
        // Та же проводка, что ставит конструктор SessionManager: найденная цена публикуется
        // в чат (broadcast + история). Тест читает эфир — до него доходят только те id, которых
        // вызывающий реально поставил в Track.
        fal.OnCostResolved = _sut.PublishFalCostAsync;
        // Цены хода живут в роли TurnCostLedger (ядро S3-2): подменяем сервис в ней, фасад держит лишь ссылку
        var ledger = typeof(SessionManager).GetField("_costs", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(_sut)!;
        ledger.GetType().GetField("_falCost", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(ledger, fal);
    }

    private async Task<Session> MkChatAsync(string suffix, string? resumeSessionId = null)
    {
        var dir = MkProjectDir("falprod_" + suffix);
        var project = _projectManager.Create("FP-" + suffix, dir, TestUserId, TestUsername);
        return await _sut.CreateAsync(project.Id, ClaudeMode.Auto, resumeSessionId: resumeSessionId);
    }

    // Ждать публикаций fal_cost в эфире: цена каждого id приходит через очередь FalCostService
    // (пачка billing-events), как в проде. После порога — короткая тишина: проверка «ровно один
    // раз» читается, когда дубли уже не могут прийти.
    private async Task<List<FalCostMessage>> WaitForFalCostsAsync(int count)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            var got = Sent<FalCostMessage>();
            if (got.Count >= count)
            {
                await Task.Delay(150);
                return Sent<FalCostMessage>();
            }
            await Task.Delay(30);
        }
        return Sent<FalCostMessage>();
    }

    // Живой путь: tool_result пакета хода → TryTrackFalCost (TurnCostLedger) → каждый id своим
    // Track'ом → N публикаций. Мутация «одиночный вызов вместо цикла» в TryTrackFalCost
    // (Take(1)) даёт ровно одну публикацию — тест краснеет на числе и на составе id.
    [Fact]
    public async Task ЖивойПакетныйToolResult_КаждыйIdИзJobs_ПубликуетсяРовноОдинРаз()
    {
        var session = await MkChatAsync("live");

        await _sut.OnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ToolResultMessage("t1", BatchJson(IdA, IdB, IdC), false,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        var costs = await WaitForFalCostsAsync(3);
        costs.Should().HaveCount(3, "число публикаций равно числу запросов в пакете");
        costs.Select(c => c.RequestId).Should().BeEquivalentTo(new[] { IdA, IdB, IdC },
            "каждый id пакета дошёл до своей публикации");
    }

    // Путь бэкфилла: история с пакетным tool_result без fal_cost → GetHistoryAsync →
    // BackfillFalCosts (TurnCostLedger) → каждый id, ещё не учтённый, своим Track'ом. Мутация
    // «одиночный вызов вместо цикла» в BackfillFalCosts краснеет здесь же — на числе и составе id.
    [Fact]
    public async Task БэкфиллИсторииСПакетнымToolResult_КаждыйIdИзJobs_ПубликуетсяРовноОдинРаз()
    {
        var session = await MkChatAsync("bf", resumeSessionId: "cs-falprod-bf");
        var key = session.ClaudeSessionId!;
        await _historyService.SaveAsync(key, [new StoredToolUseMessage
        {
            Id = "t1",
            Name = "mcp__fal__submit_jobs",
            Result = BatchJson(IdA, IdB, IdC),
            IsError = false,
            FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }]);
        // Бэкфилл работает по загруженной истории: без аккумулятора GetHistoryAsync идёт
        // дисковой веткой — тем путём, которым возвращается к чату после перезапуска.
        ClearAccumulator(GetEntry(session.Id));

        await _sut.GetHistoryAsync(session.Id); // боевой вход бэкфилла: загрузка истории сессии

        var costs = await WaitForFalCostsAsync(3);
        costs.Should().HaveCount(3, "число публикаций равно числу запросов в пакете истории");
        costs.Select(c => c.RequestId).Should().BeEquivalentTo(new[] { IdA, IdB, IdC },
            "каждый id пакета истории дошёл до своей публикации");
    }
}
