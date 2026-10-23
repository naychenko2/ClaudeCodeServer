using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// POST /api/chats/{id}/handoff — «Перенести в новый чат» (шаг B-4). Оркестрация покрыта
// ChatHandoffServiceTests; здесь контракт эндпоинта: 200 {chatId, projectId, draft, warm},
// 404 — чат не найден/чужой (per-owner: чужой id — 404, не 500), 409 — пустой чат, 502 —
// модель не дала записку. Настоящий дешёвый вызов подменяет своя заглушка ICheapTextRunner
// через ExtraServices, тёплый ход — шов SendOverride сервиса.
public class ChatsControllerHandoffTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ChatsControllerHandoffTests()
    {
        _factory = new TestWebApplicationFactory
        {
            ExtraServices = services => services.AddSingleton<ICheapTextRunner>(
                new StubRunner("### Цель\nпродолжить настройку значков")),
        };
        _client = _factory.CreateAuthenticatedClient();
    }

    private async Task<string> CreateSessionAsync(HttpClient client)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
            "ccs_handoff_" + Guid.NewGuid().ToString("N"))).FullName;
        var proj = await client.PostAsJsonAsync("/api/projects", new { name = "HandoffProject", rootPath = dir });
        proj.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await proj.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    // Живая запись сессии (приватный реестр) — накопитель хода, как в ChatsControllerContinueTests
    private object EntryOf(string sessionId)
    {
        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        var field = typeof(SessionManager).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((System.Collections.IDictionary)field.GetValue(sessions)!)[sessionId]!;
    }

    private void SeedAgentTurn(string sessionId, PromptCacheState? cache = null)
    {
        var result = new StoredResultMessage("success", 1, 1);
        if (cache is not null) result = new StoredResultMessage("success", 1, 1) { PromptCache = cache };
        List<StoredMessage> turns = [new StoredUserMessage("вопрос"), new StoredTextMessage("ответ"), result];
        var entry = EntryOf(sessionId);
        entry.GetType().GetField("Accumulator")!.SetValue(entry, new TurnAccumulator(turns, "csid-" + sessionId));
    }

    [Fact]
    public async Task Перенос_УспешныйПуть_200_ФормаОтвета()
    {
        var id = await CreateSessionAsync(_client);
        SeedAgentTurn(id);

        var resp = await _client.PostAsync($"/api/chats/{id}/handoff", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("chatId").GetString().Should().NotBeNullOrEmpty().And.NotBe(id);
        body.GetProperty("draft").GetString().Should().Contain("продолжить настройку значков");
        body.GetProperty("warm").GetBoolean().Should().BeFalse("без данных кэша — холодный путь");
    }

    [Fact]
    public async Task Перенос_ЧужойЧат_404_Не500()
    {
        // Чат другого владельца: per-owner изоляция — чужой id отвергается, а не роняет ручку
        var other = _factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername,
            TestWebApplicationFactory.SecondPassword);
        var id = await CreateSessionAsync(other);

        var resp = await _client.PostAsync($"/api/chats/{id}/handoff", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        // Тело с текстом, а не пустой 404: без него фронт подставляет свой «Not Found»
        (await resp.Content.ReadAsStringAsync()).Should().Contain("Чат не найден или удалён");
    }

    [Fact]
    public async Task Перенос_НесуществующийЧат_404_СТекстом()
    {
        var resp = await _client.PostAsync("/api/chats/no-such-chat/handoff", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("Чат не найден или удалён");
    }

    [Fact]
    public async Task Перенос_ПустойЧат_409_ДоМодели()
    {
        var id = await CreateSessionAsync(_client);

        var resp = await _client.PostAsync($"/api/chats/{id}/handoff", null);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("переносить нечего");
    }

    [Fact]
    public async Task Перенос_ТёплыйКэш_ОтветWarmИБезДешёвогоРаннера()
    {
        var id = await CreateSessionAsync(_client);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SeedAgentTurn(id, new PromptCacheState(60_000, 0, now, "5m", null, PromptCacheMissReason.None));
        // Тёплому пути нужен разговор в CLI — ставим csid живой записи
        ((Session)EntryOf(id).GetType().GetField("Info")!.GetValue(EntryOf(id))!).ClaudeSessionId = "csid-" + id;

        var sentWarm = false;
        _factory.Services.GetRequiredService<ChatHandoffService>().SendOverride = (_, prompt, _) =>
        {
            sentWarm = prompt == ChatHandoffService.BuildWarmPrompt();
            return Task.FromResult<SendAndWaitResult>(
                new SendAndWaitResult.Completed(new TurnResult("### Цель\nтёплый путь", 1, null, Succeeded: true)));
        };

        var resp = await _client.PostAsync($"/api/chats/{id}/handoff", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("warm").GetBoolean().Should().BeTrue();
        body.GetProperty("draft").GetString().Should().Contain("тёплый путь");
        sentWarm.Should().BeTrue("записку просит сам чат, а не раннер по транскрипту");
    }

    [Fact]
    public async Task Перенос_ТёплыйКэшХодЗанят_409_Не502()
    {
        // «Чат занят другим ходом» — временное состояние, а не отказ модели: 409, человек
        // может повторить клик (минор ревью B-4: раньше это была 502)
        var id = await CreateSessionAsync(_client);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SeedAgentTurn(id, new PromptCacheState(60_000, 0, now, "5m", null, PromptCacheMissReason.None));
        ((Session)EntryOf(id).GetType().GetField("Info")!.GetValue(EntryOf(id))!).ClaudeSessionId = "csid-" + id;
        _factory.Services.GetRequiredService<ChatHandoffService>().SendOverride = (_, _, _) =>
            Task.FromResult<SendAndWaitResult>(new SendAndWaitResult.Busy(SessionStatus.Working));

        var resp = await _client.PostAsync($"/api/chats/{id}/handoff", null);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("занят");
    }

    [Fact]
    public async Task Перенос_ХолодныйПутьНеУложилсяВПотолок_502БезПреемника()
    {
        // Холодный путь имеет явный потолок (4 мин): без него медленный раннер оборвался бы
        // на стороне клиента (фронт ждёт 6 мин). Тест укорачивает потолок: раннер висит,
        // token гасится — честный 502, преемник не создан
        var id = await CreateSessionAsync(_client);
        SeedAgentTurn(id);
        var service = _factory.Services.GetRequiredService<ChatHandoffService>();
        service.ColdPathTimeout = TimeSpan.FromMilliseconds(50);
        var manager = _factory.Services.GetRequiredService<SessionManager>();
        var before = manager.GetAll().Count;
        var runner = (StubRunner)_factory.Services.GetRequiredService<ICheapTextRunner>();
        runner.Hang = true;

        var resp = await _client.PostAsync($"/api/chats/{id}/handoff", null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("перенос не успел завершиться");
        manager.GetAll().Count.Should().Be(before, "таймаут холодного пути не создаёт преемника");
        runner.Hang = false;
    }

    // Заглушка раннера: фиксированный ответ, отказ при назначении на локаль. Hang — для
    // теста потолка холодного пути: вызов висит, пока его не отменят
    private sealed class StubRunner(string answer) : ICheapTextRunner
    {
        public bool Hang;

        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => false;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";

        public async Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            if (Hang) await Task.Delay(TimeSpan.FromSeconds(2), ct);
            return answer;
        }

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) => Task.FromResult<string?>(answer);

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
