using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Ручка, которой прокси локальной модели просит показать карточку обрезки контекста.
//
// Зовёт её служба рядом, без токена пользователя, — значит замок тут не JWT, а общий секрет, и
// проверять надо именно его: ручка пишет в ЧУЖУЮ ленту, а веб-морда торчит наружу. Три отказа
// разные по смыслу и их легко перепутать: секрет не настроен — ручки нет вовсе (404), секрет
// не тот — 401, сессии нет — 404.
public class LlmProxyEventsControllerTests
{
    private const string Url = "/api/internal/llm-proxy/events";
    private const string Secret = "proxy-secret-for-tests";

    private static object Body(string sessionId, string kind = "prune") => new
    {
        sessionId,
        kind,
        tokensBefore = 234_000,
        tokensAfter = 46_000,
        blocks = 66,
        resultBlocks = 64,
        inputBlocks = 1,
        thinkingBlocks = 1,
        prefillSeconds = 87.5,
        cacheReadTokens = 18_000,
        promptTokens = 20_000,
    };

    private static TestWebApplicationFactory WithSecret(string? secret)
    {
        var factory = new TestWebApplicationFactory();
        if (secret is not null) factory.ExtraConfig[LlmProxyEventsController.SecretKey] = secret;
        return factory;
    }

    private static HttpClient Anonymous(TestWebApplicationFactory factory) => factory.CreateClient();

    [Fact]
    public async Task Секрет_НеНастроен_РучкиНет()
    {
        using var factory = WithSecret(null);
        var client = Anonymous(factory);

        var response = await client.PostAsJsonAsync(Url, Body("any-session"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "выключенная фича не смеет оставлять открытый вход");
    }

    [Fact]
    public async Task БезСекрета_Отказ()
    {
        using var factory = WithSecret(Secret);
        var client = Anonymous(factory);

        var response = await client.PostAsJsonAsync(Url, Body("any-session"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ЧужойСекрет_Отказ()
    {
        using var factory = WithSecret(Secret);
        var client = Anonymous(factory);
        client.DefaultRequestHeaders.Add(LlmProxyEventsController.SecretHeader, Secret + "x");

        var response = await client.PostAsJsonAsync(Url, Body("any-session"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task НеизвестнаяСессия_404()
    {
        using var factory = WithSecret(Secret);
        var client = Anonymous(factory);
        client.DefaultRequestHeaders.Add(LlmProxyEventsController.SecretHeader, Secret);

        var response = await client.PostAsJsonAsync(Url, Body("нет-такой-сессии"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task НеизвестныйВид_ОтказПоТелу()
    {
        using var factory = WithSecret(Secret);
        var client = Anonymous(factory);
        client.DefaultRequestHeaders.Add(LlmProxyEventsController.SecretHeader, Secret);

        var response = await client.PostAsJsonAsync(Url, Body("any-session", kind: "что-то-новое"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "вид карточки — закрытый список, фронт рисует только его");
    }

    [Fact]
    public async Task ВерныйСекрет_КарточкаЛожитсяВИсторию()
    {
        using var factory = WithSecret(Secret);
        var owner = factory.CreateAuthenticatedClient();
        var (projectId, sessionId) = await CreateSessionAsync(factory, owner);

        var client = Anonymous(factory);
        client.DefaultRequestHeaders.Add(LlmProxyEventsController.SecretHeader, Secret);
        var response = await client.PostAsJsonAsync(Url, Body(sessionId));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // История переживает перезагрузку страницы — ради этого запись и делается
        var history = await owner.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/sessions/{sessionId}/history");
        var card = history.EnumerateArray()
            .Single(m => m.GetProperty("kind").GetString() == "context_pruned");
        card.GetProperty("blocks").GetInt32().Should().Be(66);
        card.GetProperty("tokensBefore").GetInt32().Should().Be(234_000);
        card.GetProperty("tokensAfter").GetInt32().Should().Be(46_000);
        card.GetProperty("resultBlocks").GetInt32().Should().Be(64);
        card.GetProperty("prefillSeconds").GetDouble().Should().Be(87.5);
        card.GetProperty("cacheReadTokens").GetInt32().Should().Be(18_000);
    }

    // МЕХАНИЗМ ЗАДВОЕНИЯ (диагностика 2026-09-23): одно принятое событие уезжает в ДВЕ группы
    // одним и тем же сообщением — в session-группу чата и в project-группу проекта
    // (SessionManager.BroadcastSessionMessageAsync). Вкладка открытого чата состоит в обеих
    // (useSession.joinSession + WorkspacePage.joinProject на ОДНОМ соединении signalr.ts),
    // поэтому получает событие дважды и без дедупа рисует вторую строку с теми же числами.
    //
    // Веер тут не дефект (он страхует вкладку, не успевшую войти в session-группу) — дефектом
    // было отсутствие ЛИЧНОСТИ у карточки: различить вторую доставку от второго сдвига было
    // нечем. Тест держит и веер, и одинаковый eventId в обеих доставках.
    [Fact]
    public async Task КарточкаУезжаетВДвеГруппы_ОднимСобытиемИСОднойЛичностью()
    {
        var broadcaster = new TestSessionBroadcaster();
        using var factory = WithSecret(Secret);
        factory.ExtraServices = s => s.AddSingleton<ISessionBroadcaster>(broadcaster);
        var owner = factory.CreateAuthenticatedClient();
        var (projectId, sessionId) = await CreateSessionAsync(factory, owner);
        broadcaster.Clear();

        var client = Anonymous(factory);
        client.DefaultRequestHeaders.Add(LlmProxyEventsController.SecretHeader, Secret);
        (await client.PostAsJsonAsync(Url, Body(sessionId))).EnsureSuccessStatusCode();

        var вСессию = broadcaster.Session.Where(t => t.SessionId == sessionId)
            .Select(t => t.Message).OfType<ContextPrunedMessage>().ToList();
        var вПроект = broadcaster.Project.Where(t => t.ProjectId == projectId)
            .Select(t => t.Message).OfType<ContextPrunedMessage>().ToList();
        вСессию.Should().HaveCount(1);
        вПроект.Should().HaveCount(1, "веер: то же событие уходит и в project-группу");
        вПроект[0].Should().BeSameAs(вСессию[0],
            "обе доставки — ОДНО сообщение: вкладка в двух группах получает его дважды");
        вСессию[0].EventId.Should().NotBeNullOrEmpty(
            "личность сдвига — единственный способ отличить вторую доставку от второго сдвига");

        // Личность едет и в историю: после перезагрузки страницы дедупу нужна та же точка сравнения
        var history = await owner.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/sessions/{sessionId}/history");
        var card = history.EnumerateArray()
            .Single(m => m.GetProperty("kind").GetString() == "context_pruned");
        card.GetProperty("eventId").GetString().Should().Be(вСессию[0].EventId);
    }

    [Fact]
    public async Task ДваСдвига_ПолучаютРазныеЛичности()
    {
        var broadcaster = new TestSessionBroadcaster();
        using var factory = WithSecret(Secret);
        factory.ExtraServices = s => s.AddSingleton<ISessionBroadcaster>(broadcaster);
        var owner = factory.CreateAuthenticatedClient();
        var (_, sessionId) = await CreateSessionAsync(factory, owner);

        var client = Anonymous(factory);
        client.DefaultRequestHeaders.Add(LlmProxyEventsController.SecretHeader, Secret);
        (await client.PostAsJsonAsync(Url, Body(sessionId))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(Url, Body(sessionId))).EnsureSuccessStatusCode();

        var карточки = broadcaster.Session.Select(t => t.Message)
            .OfType<ContextPrunedMessage>().ToList();
        карточки.Should().HaveCount(2);
        карточки[0].EventId.Should().NotBe(карточки[1].EventId,
            "два сдвига — две строки, даже если числа совпали");
    }

    // Сессия с посеянной историей — как в SessionsControllerTests: без реального хода и claude.exe.
    private static async Task<(string ProjectId, string SessionId)> CreateSessionAsync(
        TestWebApplicationFactory factory, HttpClient owner)
    {
        var dir = Path.Combine(factory.TempDir, "proxy_events_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var projectResponse = await owner.PostAsJsonAsync("/api/projects",
            new { name = "ProxyEvents", rootPath = dir });
        projectResponse.EnsureSuccessStatusCode();
        var projectId = (await projectResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;

        var csid = "history-" + Guid.NewGuid().ToString("N")[..16];
        using (var scope = factory.Services.CreateScope())
        {
            var history = scope.ServiceProvider.GetRequiredService<ChatHistoryService>();
            await history.SaveAsync(csid, [new StoredTextMessage("привет")]);
        }

        var sessionResponse = await owner.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "auto", resumeSessionId = csid });
        sessionResponse.EnsureSuccessStatusCode();
        var sessionId = (await sessionResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
        return (projectId, sessionId);
    }
}
