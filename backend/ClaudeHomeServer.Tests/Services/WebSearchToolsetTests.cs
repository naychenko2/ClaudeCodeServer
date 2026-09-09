using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Reader;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.WebSearch;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Тулсет веб-поиска (websearch: web_search + web_read). Проверяется то, ради чего он писался:
/// - пустой Perplexity:ApiKey → инструментов нет и вызов не падает, а честно отказывает;
/// - разбор ответа Sonar с ЦИТАТАМИ (половина ценности инструмента для локальной модели)
///   и запись траты источником websearch;
/// - web_read идёт через существующий ридер с его SsrfGuard: приватный адрес отклоняется
///   ДО выхода в сеть (хендлер, который взорвался бы на запросе, остаётся нетронутым);
/// - ключ не доезжает наружу: даже если чужой сервис вернёт его эхом в теле ошибки, ни ответ
///   инструмента, ни лог его не содержат.
/// Сессия чужого владельца отсекается на входе (fail-closed, как у watch/dify).
/// </summary>
public class WebSearchToolsetTests : IDisposable
{
    private const string TestUserId = "test-user-id";
    private const string TestUsername = "test-user";
    private const string ApiKey = "pplx-TESTKEY-000111";

    private readonly string _tempDir;

    public WebSearchToolsetTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "websearch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    // ─── Состав ──────────────────────────────────────────────────────────────

    [Fact]
    public void ПустойКлюч_СоставаНет()
    {
        var env = Build(apiKey: "");

        env.Toolset.ToolsFor(env.Context).Should().BeEmpty(
            "без Perplexity:ApiKey сервер не должен занимать окно модели схемами");
    }

    [Fact]
    public async Task ПустойКлюч_ВызовОтказываетЧестно_АНеПадает()
    {
        var env = Build(apiKey: "");

        var result = await env.Toolset.CallAsync("web_search",
            new JsonObject { ["query"] = "что нового" }, env.Context, default);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Perplexity:ApiKey");
    }

    [Fact]
    public void ЕстьКлюч_ДваИнструмента_ПоискИЧтение()
    {
        var env = Build();

        env.Toolset.ToolsFor(env.Context).Select(t => t.Name)
            .Should().Equal("web_search", "web_read");
    }

    [Fact]
    public void ЧужаяСессия_НиСостава_НиДоступа()
    {
        var env = Build();
        var foreign = new McpToolCallContext("someone-else", env.Session.Id, env.Session.Id);

        env.Toolset.ToolsFor(foreign).Should().BeEmpty();
    }

    // ─── web_search ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ВебПоиск_РазбираетОтвет_ИзвлекаетЦитаты_ПишетТрату()
    {
        var env = Build();
        env.Perplexity.Respond(HttpStatusCode.OK, SonarResponse);

        var result = await env.Toolset.CallAsync("web_search",
            new JsonObject { ["query"] = "релиз .NET 10", ["recency"] = "week" }, env.Context, default);

        result.IsError.Should().BeFalse();
        var json = JsonNode.Parse(result.Text)!.AsObject();
        json["answer"]!.GetValue<string>().Should().Contain(".NET 10");

        var citations = json["citations"]!.AsArray();
        citations.Should().HaveCount(3, "две ссылки из search_results плюс уникальная из citations");
        citations[0]!["url"]!.GetValue<string>().Should().Be("https://learn.microsoft.com/dotnet");
        citations[0]!["title"]!.GetValue<string>().Should().Be("Документация .NET");
        citations[2]!["url"]!.GetValue<string>().Should().Be("https://devblogs.microsoft.com/dotnet/");
        // Номера источников — по ним модель разбирает маркеры «[2]» в тексте ответа
        citations.Select(c => c!["index"]!.GetValue<int>()).Should().Equal(1, 2, 3);

        // Фильтр свежести доехал до запроса — иначе «за неделю» молча превращалось бы
        // в поиск без ограничения
        var sent = JsonNode.Parse(env.Perplexity.LastBody!)!.AsObject();
        sent["search_recency_filter"]!.GetValue<string>().Should().Be("week");
        sent["model"]!.GetValue<string>().Should().Be("sonar");

        var record = env.Spend.Records.Should().ContainSingle().Subject;
        record.Source.Should().Be(SpendSources.WebSearch);
        record.Provider.Should().Be("perplexity");
        record.InputTokens.Should().Be(120);
        record.OutputTokens.Should().Be(340);
        record.SessionId.Should().Be(env.Session.Id);
        record.ProjectId.Should().Be(env.Session.ProjectId);
        // Цена не задана в конфиге — денег в записи нет (выдуманный тариф хуже отсутствующего)
        record.CostUsd.Should().BeNull();
    }

    [Fact]
    public async Task ВебПоиск_МусорВRecency_ТихоИгнорируется()
    {
        var env = Build();
        env.Perplexity.Respond(HttpStatusCode.OK, SonarResponse);

        await env.Toolset.CallAsync("web_search",
            new JsonObject { ["query"] = "погода", ["recency"] = "вчера" }, env.Context, default);

        JsonNode.Parse(env.Perplexity.LastBody!)!.AsObject()
            .ContainsKey("search_recency_filter").Should().BeFalse();
    }

    [Fact]
    public async Task ВебПоиск_ПустойЗапрос_Отказ_БезПоходаВСеть()
    {
        var env = Build();

        var result = await env.Toolset.CallAsync("web_search",
            new JsonObject { ["query"] = "   " }, env.Context, default);

        result.IsError.Should().BeTrue();
        env.Perplexity.Calls.Should().Be(0);
        env.Spend.Records.Should().BeEmpty();
    }

    // ─── Ключ наружу не уезжает ──────────────────────────────────────────────

    [Fact]
    public async Task КлючНеПопадает_НиВОтветИнструмента_НиВЛог()
    {
        var env = Build();
        // Худший случай: чужой сервис вернул ключ эхом в теле ошибки
        env.Perplexity.Respond(HttpStatusCode.Unauthorized,
            $"{{\"error\":{{\"message\":\"Invalid token {ApiKey}\"}}}}");

        var result = await env.Toolset.CallAsync("web_search",
            new JsonObject { ["query"] = "проверка" }, env.Context, default);

        result.IsError.Should().BeTrue();
        result.Text.Should().NotContain(ApiKey);
        result.Text.Should().Contain("***");
        result.Text.Should().Contain("401");
        env.Log.Lines.Should().NotContain(l => l.Contains(ApiKey));
    }

    [Fact]
    public async Task ТекстЗапроса_ВЛогНеИдёт()
    {
        var env = Build();
        env.Perplexity.Respond(HttpStatusCode.OK, SonarResponse);

        await env.Toolset.CallAsync("web_search",
            new JsonObject { ["query"] = "секретный запрос пользователя" }, env.Context, default);

        env.Log.Lines.Should().NotContain(l => l.Contains("секретный запрос"));
    }

    // ─── web_read ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ВебЧтение_ПриватныйАдрес_ОтклонёнДоВыходаВСеть()
    {
        var env = Build();

        var result = await env.Toolset.CallAsync("web_read",
            new JsonObject { ["url"] = "http://127.0.0.1/admin" }, env.Context, default);

        result.IsError.Should().BeTrue();
        // Текст отказа — общий с «домен не резолвится» (ADR-005 §6): по нему модель не должна
        // понять, что за адресом внутренняя сеть
        result.Text.Should().Be(
            WebSearchToolset.ReadErrorText(ReaderOutcome.Fail(ReaderErrorCode.DnsFailed)));
        env.Site.Calls.Should().Be(0, "SsrfGuard обязан отсечь адрес до запроса");
    }

    [Theory]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    public async Task ВебЧтение_ВнутренниеДиапазоны_Отклонены(string url)
    {
        var env = Build();

        var result = await env.Toolset.CallAsync("web_read",
            new JsonObject { ["url"] = url }, env.Context, default);

        result.IsError.Should().BeTrue();
        env.Site.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ВебЧтение_НеHttpСхема_Отклонена()
    {
        var env = Build();

        var result = await env.Toolset.CallAsync("web_read",
            new JsonObject { ["url"] = "file:///c:/secrets.txt" }, env.Context, default);

        result.IsError.Should().BeTrue();
        env.Site.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ВебЧтение_НеТратитДеньгиПоиска()
    {
        var env = Build();

        await env.Toolset.CallAsync("web_read",
            new JsonObject { ["url"] = "http://127.0.0.1/" }, env.Context, default);

        env.Spend.Records.Should().BeEmpty();
        env.Perplexity.Calls.Should().Be(0);
    }

    // ─── Классы отказа чтения разведены (дефект 2026-09-07) ──────────────────

    [Fact]
    public void ТекстОтказа_СайтОтветилИОтверг_НесётHttpКод()
    {
        var blocked = WebSearchToolset.ReadErrorText(
            ReaderOutcome.Fail(ReaderErrorCode.BlockedBySite, 403));
        var limited = WebSearchToolset.ReadErrorText(
            ReaderOutcome.Fail(ReaderErrorCode.BlockedBySite, 429));

        blocked.Should().Contain("HTTP 403");
        limited.Should().Contain("HTTP 429");
        blocked.Should().NotBe(limited, "по тексту должно быть видно, каким кодом отказал сайт");
    }

    [Fact]
    public void ТекстОтказа_СетевойСбой_ОтличенОтОтказаСайта_ИБезКода()
    {
        var network = WebSearchToolset.ReadErrorText(ReaderOutcome.Fail(ReaderErrorCode.Unreachable));
        var blocked = WebSearchToolset.ReadErrorText(ReaderOutcome.Fail(ReaderErrorCode.BlockedBySite, 403));
        var timeout = WebSearchToolset.ReadErrorText(ReaderOutcome.Fail(ReaderErrorCode.Timeout));

        network.Should().Contain("соединиться");
        network.Should().NotContain("HTTP", "ответа не было — кода взяться неоткуда");
        network.Should().NotBe(blocked, "«сеть лежит» и «сайт защищается» — разные решения для модели");
        timeout.Should().NotBe(network);
        timeout.Should().Contain("таймаут");
    }

    [Fact]
    public void ТекстОтказа_ВнутреннийАдресИНерезолвДаютОдинТекст_БезОракулаСети()
    {
        // ADR-005 §6: разные тексты сделали бы инструмент оракулом внутренней сети — модель
        // перебором имён отличала бы «имя есть и приватное» от «имени нет», и эта карта уезжала
        // бы стороннему провайдеру. В панели «Чтение» коды остаются разными (их видит владелец).
        var local = WebSearchToolset.ReadErrorText(ReaderOutcome.Fail(ReaderErrorCode.LocalAddress));
        var dns = WebSearchToolset.ReadErrorText(ReaderOutcome.Fail(ReaderErrorCode.DnsFailed));

        local.Should().Be(dns, "по тексту нельзя отличить внутреннее имя от несуществующего");
        local.Should().NotContainAny("внутренн", "локальн", "резолв", "DNS");
    }

    // ─── Разбор ответа Sonar (без сети и сессий) ─────────────────────────────

    [Fact]
    public void Разбор_БезЦитатИБезОтвета_ЭтоОтказ()
    {
        var outcome = PerplexitySearchService.Parse("{\"choices\":[]}", 1000, 10);

        outcome.Success.Should().BeFalse();
    }

    [Fact]
    public void Разбор_НечитаемыйJson_НеБросает()
    {
        var outcome = PerplexitySearchService.Parse("<html>502</html>", 1000, 10);

        outcome.Success.Should().BeFalse();
        outcome.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Разбор_ДлинныйОтвет_Обрезается()
    {
        var long_ = new string('я', 5000);
        var raw = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = long_ } } },
            citations = new[] { "https://example.com/" },
        });

        var outcome = PerplexitySearchService.Parse(raw, maxAnswerChars: 100, maxCitations: 10);

        outcome.Success.Should().BeTrue();
        outcome.Answer.Should().StartWith(new string('я', 100));
        outcome.Answer.Should().Contain("обрезан");
    }

    [Fact]
    public void Разбор_ЛишниеЦитаты_Режутся()
    {
        var raw = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = "ответ" } } },
            citations = Enumerable.Range(1, 30).Select(i => $"https://example.com/{i}").ToArray(),
        });

        var outcome = PerplexitySearchService.Parse(raw, maxAnswerChars: 1000, maxCitations: 5);

        outcome.Citations.Should().HaveCount(5);
    }

    [Theory]
    [InlineData("day", "day")]
    [InlineData("year", "year")]
    [InlineData("hour", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ФильтрСвежести_ТолькоИзвестныеЗначения(string? value, string? expected) =>
        PerplexitySearchService.NormalizeRecency(value).Should().Be(expected);

    // ─── Фикстура ────────────────────────────────────────────────────────────

    private const string SonarResponse = """
    {
      "id": "req-1",
      "model": "sonar",
      "choices": [
        { "message": { "role": "assistant", "content": ".NET 10 вышел в ноябре 2025 года." } }
      ],
      "search_results": [
        { "title": "Документация .NET", "url": "https://learn.microsoft.com/dotnet", "date": "2025-11-11" },
        { "title": "Release notes", "url": "https://github.com/dotnet/core" }
      ],
      "citations": [
        "https://learn.microsoft.com/dotnet",
        "https://devblogs.microsoft.com/dotnet/"
      ],
      "usage": { "prompt_tokens": 120, "completion_tokens": 340, "total_tokens": 460 }
    }
    """;

    private sealed record Env(
        WebSearchToolset Toolset,
        McpToolCallContext Context,
        Session Session,
        ScriptedHandler Perplexity,
        ScriptedHandler Site,
        RecordingSpend Spend,
        CapturingLogger<PerplexitySearchService> Log);

    private Env Build(string apiKey = ApiKey)
    {
        var (sessions, projects) = BuildSessionManager();
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N"))).FullName;
        var project = projects.Create("Проект поиска", dir, TestUserId, TestUsername);
        var session = sessions.CreateAsync(project.Id, ClaudeMode.Auto).GetAwaiter().GetResult();

        var perplexity = new ScriptedHandler();
        var site = new ScriptedHandler();
        var log = new CapturingLogger<PerplexitySearchService>();
        var options = new PerplexityOptions { ApiKey = apiKey, Model = "sonar" };
        var search = new PerplexitySearchService(new StubHttpClientFactory(perplexity), options, log);

        var readerConfig = new ConfigurationBuilder().Build();
        var reader = new ReaderService(new StubHttpClientFactory(site), readerConfig,
            NullLogger<ReaderService>.Instance);
        var spend = new RecordingSpend();

        var toolset = new WebSearchToolset(search, reader, new ReaderQuotaService(), sessions, spend);
        var context = new McpToolCallContext(TestUserId, session.Id, session.Id);
        return new Env(toolset, context, session, perplexity, site, spend, log);
    }

    // Минимальный граф SessionManager — как в ChatArchiveFlagTests: тулсету нужен
    // только резолв «хвост маршрута → чат владельца»
    private (SessionManager Sessions, ProjectManager Projects) BuildSessionManager()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            // Фоновый автосейв не должен вмешиваться между правкой и ассертами
            ["Session:AutoSaveSeconds"] = "0",
            ["DefaultProjectsPath"] = Path.Combine(_tempDir, "homes"),
            ["ClaudeUserProfileDir"] = Path.Combine(_tempDir, "claude-profile"),
        }).Build();

        var userStore = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projectManager = new ProjectManager(config, userStore, appSettings);
        var history = new ChatHistoryService(config);

        var broadcaster = new TestSessionBroadcaster();

        var llmProviders = new LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new LlmSessionAdapterFactory(config, new SkillsService(),
            new WorkspaceKnowledgeStore(config), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var jwt = new JwtService(config, userStore, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var flags = new FeatureFlagService(userStore);
        var notesSvc = new NotesService(projectManager, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, userStore, config,
            NullLogger<NotesKnowledgeService>.Instance);
        var personas = new PersonaManager(config);
        var bindings = new PersonaBindingsService(personas, projectManager, wkStore, notesKb,
            knowledge, new SkillsService(), userStore, config, NullLogger<PersonaBindingsService>.Instance, notes: notesSvc);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);

        return (new SessionManager(projectManager, history, config, adapters, falCost,
            usage, appSettings, userStore, jwt, server.Object, llmProviders, flags, personas,
            bindings, subPool, NullLogger<SessionManager>.Instance,
            TestLauncherFactory.Instance, sandbox, broadcaster), projectManager);
    }

    // Хендлер со сценарием: отдаёт заданный ответ и запоминает тело запроса. Если ответ
    // не задан, любой вызов — провал теста: так «запрос не должен был уйти» проверяется
    // самим фактом обращения к сети
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _body = "";
        private bool _armed;

        public int Calls { get; private set; }
        public string? LastBody { get; private set; }

        public void Respond(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
            _armed = true;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct)
        {
            Calls++;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            if (!_armed)
                throw new InvalidOperationException(
                    $"Неожиданный выход в сеть: {request.Method} {request.RequestUri}");
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class RecordingSpend : ISpendCollector
    {
        private readonly List<SpendRecord> _records = [];

        public IReadOnlyList<SpendRecord> Records => _records;

        public void Record(SpendRecord record) => _records.Add(record);
    }

    // Логгер, запоминающий отформатированные строки: сторож «ключ и текст запроса
    // в лог не идут»
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines => _lines;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => _lines.Add(formatter(state, exception));
    }
}
