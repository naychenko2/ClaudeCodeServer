using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Mcp;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторожа регрессии failure-classification для Higgsfield (мутационное ревью Глеба,
/// коммит `3aa69833`). Сам код был принят как корректный, но без тестов каждая правка
/// могла быть откачена без шума — Глеб именно так получил 103/103 зелёных, откатив обе.
/// Здесь стоят два сторожа: один на немой путь EnsureFresh, второй на потолок возраста снимка.
/// </summary>
public class HiggsfieldFailureClassRegressionTests : IDisposable
{
    private readonly string _dir;

    public HiggsfieldFailureClassRegressionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(),
            "ccs-hf-failclass-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Тест 1 (находка п.2): немой EnsureFresh пишет NeedsAuth ──────────────────────────

    [Fact]
    public void EnsureFresh_ConnectedНоAdminOwnerIdПуст_ПишетNeedsAuthВMcpStatusStore()
    {
        var (service, _, _, statuses) = NewHiggsfieldOAuthService();

        // higgsfield.json помнит Connected=true (например, восстановили data из архива
        // до миграции ф.2.1: файл про подключение знает, токенов нет). AdminOwnerId пуст.
        WriteHiggsfieldState(connected: true, adminOwnerId: null);

        var token = service.EnsureFresh();

        token.Should().BeNull("без AdminOwnerId токен отдать неоткуда");
        var entry = statuses.Get(HiggsfieldOAuthService.ServiceOwnerId, HiggsfieldOAuthService.Key);
        entry.Should().NotBeNull("немой путь обязан попасть в McpStatusStore — иначе UI врёт");
        entry!.Status.Should().Be(McpServerStatuses.NeedsAuth,
            "правильный класс отказа — «нужен вход», а не поломка апстрима");
        entry.Error.Should().NotBeNullOrEmpty("текст причины нужен для диагностики");
        entry.Error.Should().Contain("AdminOwnerId",
            "текст должен называть причину — иначе оператор не поймёт, что именно сломалось");
    }

    [Fact]
    public void EnsureFresh_ConnectedНоСервиснаяЗаписьОтсутствует_ПишетNeedsAuthВMcpStatusStore()
    {
        var (service, _, _, statuses) = NewHiggsfieldOAuthService();

        // higgsfield.json говорит Connected=true, AdminOwnerId проставлен — но сервисная
        // запись реестра удалена (или миграция не отработала). EnsureFresh должен сообщить
        // о пропаже через NeedsAuth, а не молча вернуть null.
        WriteHiggsfieldState(connected: true, adminOwnerId: "admin-1");
        // Запись реестра не создаём — TryGetServiceRecord() вернёт null.

        var token = service.EnsureFresh();

        token.Should().BeNull("без сервисной записи токен взять негде");
        var entry = statuses.Get(HiggsfieldOAuthService.ServiceOwnerId, HiggsfieldOAuthService.Key);
        entry.Should().NotBeNull("немой путь обязан попасть в McpStatusStore");
        entry!.Status.Should().Be(McpServerStatuses.NeedsAuth);
        entry.Error.Should().NotBeNullOrEmpty("причина нужна — иначе оператор не отличит «нет токена» от «нет записи»");
        entry.Error.Should().Contain("сервисной",
            "текст должен указывать на отсутствие записи реестра, а не на пустой AdminOwnerId");
    }

    // Контраст: когда higgsfield.json говорит «не подключено» (чистая инсталляция) —
    // молчаливый возврат null остаётся штатным путём, статус НЕ пишется. Иначе сломанная
    // интеграция у каждого нового пользователя создавала бы красную карточку на ровном месте.
    [Fact]
    public void EnsureFresh_ConnectedFalse_МолчаливыйВозвратNullБезЗаписиВStore()
    {
        var (service, _, _, statuses) = NewHiggsfieldOAuthService();
        WriteHiggsfieldState(connected: false, adminOwnerId: null);

        var token = service.EnsureFresh();

        token.Should().BeNull();
        statuses.Get(HiggsfieldOAuthService.ServiceOwnerId, HiggsfieldOAuthService.Key)
            .Should().BeNull("Connected=false — штатный «не подключено», статусы НЕ пишем");
    }

    // ── Тест 2 (находка п.4): потолок возраста снимка ─────────────────────────────────────

    // Снимок старше SnapshotMaxAge (24 ч) не должен отдаваться в ToolsFor: апстрим мог
    // переименовать инструменты, и кормить модель фантомами опаснее, чем оставить чат без
    // интеграции. До правки GetCachedTools возвращал старый _cachedTools бессрочно —
    // именно эту регрессию и должен поймать тест (Глеб откатил правку и получил 103/103
    // зелёных, потому что прежний тест «ToolsFor без сессии = []» не доходил до проверки).
    //
    // Контракт проверки: кладём валидный снимок в кэш + lastSuccessAt старше 24 ч, зовём
    // ToolsFor с настоящим SessionManager — TryResolveSession проходит, доходим до
    // проверки потолка и должны получить пустой состав (а не фантомные инструменты).
    [Fact]
    public void ToolsFor_СнимокСтаршеПотолка_ВозвращаетПустойСостав()
    {
        var toolset = NewToolsetWithFreshCache(out _);

        // Возраст 25 часов — перевалили за 24-часовой потолок.
        SetPrivate(toolset, "_lastSuccessAt", DateTime.UtcNow.AddHours(-25));

        var result = toolset.ToolsFor(AnyValidContext());

        result.Should().BeEmpty(
            "протухший снимок старше SnapshotMaxAge кормить модель фантомами нельзя — ToolsFor обязан вернуть []");
    }

    // Граничный кейс, который спрашивал Глеб: у потолка (24 ч - 1 секунда) снимок ещё
    // отдаётся, у потолка + 1 секунда — уже нет. Сравнение в реализации строгое
    // `> SnapshotMaxAge`, поэтому ровно 24 ч — пограничный момент, который нельзя
    // проверить детерминированно (к моменту вызова ToolsFor проходит несколько мс,
    // и «ровно 24 ч назад» превращается в «24 ч + N мс назад», что уже за потолком).
    // Внутри потолка берём с запасом (минус секунда), за потолком — с тем же запасом.
    [Theory]
    [InlineData("23:59:59", true, "23:59:59 — внутри потолка, снимок ещё свежий")]
    [InlineData("24:00:01", false, "24:00:01 — перевалили за потолок")]
    [InlineData("25:00:00", false, "25 ч — глубоко за потолком")]
    public void ToolsFor_ГраницаПотолкаВозвраста_ВедётСебяПоКонтракту(
        string ageString, bool shouldReturnSnapshot, string because)
    {
        var toolset = NewToolsetWithFreshCache(out _);
        // lastSuccessAt = now - age. Парсим строку вида «hh:mm:ss» — это явнее, чем
        // знаковая путаница с AddHours/AddMinutes/AddSeconds (все три ПРИБАВЛЯЮТ,
        // вычитание только знаком). Возраст всегда положительный — нам нужен
        // именно отрицательный сдвиг от текущего момента.
        var parts = ageString.Split(':').Select(int.Parse).ToArray();
        var age = new TimeSpan(parts[0], parts[1], parts[2]);
        var lastSuccessAt = DateTime.UtcNow - age;
        SetPrivate(toolset, "_lastSuccessAt", lastSuccessAt);
        // Чтобы фоновый поток из ScheduleRefresh НЕ перезатёр кэш и lastSuccessAt,
        // пока мы измеряем — последняя попытка «только что», и refresh задержан.
        SetPrivate(toolset, "_lastFetchAttempt", DateTime.UtcNow);

        var result = toolset.ToolsFor(AnyValidContext());

        if (shouldReturnSnapshot)
        {
            result.Should().NotBeEmpty(because);
        }
        else
        {
            result.Should().BeEmpty(because);
        }
    }

    // ── Вспомогательные ─────────────────────────────────────────────────────────────────

    // Фикстура HiggsfieldOAuthService по образцу HiggsfieldConnectCallbackTests:
    // McpSecretStore + McpRegistry + McpStatusStore + McpOAuthService (с заглушкой сети).
    private (HiggsfieldOAuthService Service, McpRegistry Registry, McpSecretStore Secrets,
        McpStatusStore Statuses) NewHiggsfieldOAuthService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
            }).Build();

        var secrets = new McpSecretStore(config);
        var registry = new McpRegistry(config, secrets);
        var statuses = new McpStatusStore(config);
        var oauth = new McpOAuthService(registry, secrets, statuses,
            new NotFoundHandlerFactory(), config, NullLogger<McpOAuthService>.Instance);
        var service = new HiggsfieldOAuthService(registry, secrets, statuses, oauth,
            config, NullLogger<HiggsfieldOAuthService>.Instance);
        return (service, registry, secrets, statuses);
    }

    private void WriteHiggsfieldState(bool connected, string? adminOwnerId)
    {
        var path = Path.Combine(_dir, "higgsfield.json");
        // Сериализуем напрямую — State в HiggsfieldOAuthService публичный.
        var state = new HiggsfieldOAuthService.State
        {
            Connected = connected,
            AdminOwnerId = adminOwnerId,
            ExpiresAt = null,
            AuthVersion = 1,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(state,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class NotFoundHandlerFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new NotFoundHandler());
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    // ── Фикстура HiggsfieldToolset с реальным SessionManager ──────────────────────────────

    // Сборка SessionManager нужна, чтобы ToolsFor прошёл TryResolveSession и дошёл до
    // проверки потолка. Без сессии TryResolveSession возвращает false и ToolsFor отдаёт
    // [] ДО проверки — это и был бесполезный прежний тест. Шаблон — WebSearchToolsetTests.
    private HiggsfieldToolset NewToolsetWithFreshCache(out Session session)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["Session:AutoSaveSeconds"] = "0",
                ["DefaultProjectsPath"] = Path.Combine(_dir, "homes"),
                ["ClaudeUserProfileDir"] = Path.Combine(_dir, "claude-profile"),
            }).Build();

        var sessionManager = BuildSessionManager(config, out var projectManager);
        var projectDir = Directory.CreateDirectory(
            Path.Combine(_dir, "proj-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var project = projectManager.Create("higgsfield-failclass", projectDir,
            userId: TestUserId, username: TestUsername);
        session = sessionManager.CreateAsync(project.Id, ClaudeMode.Auto)
            .GetAwaiter().GetResult();
        // Сохраняем для AnyValidContext — ToolsFor ищет сессию по RouteTail через
        // sessions.GetOwned(sessionId, ownerId), и фейковый id вернёт null. С реальным
        // id живой сессии TryResolveSession проходит и мы доходим до проверки потолка.
        _realSession = session;

        var toolset = new HiggsfieldToolset(
            higgsfield: null!,
            clientFactory: new NoOpHandlerFactory(),
            sessions: sessionManager,
            config,
            mcpStatus: null,
            NullLogger<HiggsfieldToolset>.Instance);

        // Свежий снимок в кэше — один инструмент из белого списка, чтобы не-граничные
        // кейсы возвращали не-пустой состав (анти-тест: проверка не «всё пусто»).
        var snapshot = new List<McpToolSchema>
        {
            new("generate_image", "phantom", new JsonObject()),
        };
        SetPrivate(toolset, "_cachedTools", snapshot);
        // _lastSuccessAt подставляется в каждом кейсе.

        return toolset;
    }

    private const string TestUserId = "higgsfield-failclass-user";
    private const string TestUsername = "higgsfield-failclass";

    // Реальная сессия из NewToolsetWithFreshCache — без неё TryResolveSession откажет
    // и ToolsFor вернёт [] ДО проверки потолка (тот же баг, что был в прежнем тесте).
    private Session _realSession = null!;

    private McpToolCallContext AnyValidContext() =>
        new(TestUserId, CallerSessionId: null, RouteTail: _realSession.Id);

    private static SessionManager BuildSessionManager(IConfiguration config, out ProjectManager projectManager)
    {
        var userStore = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        projectManager = new ProjectManager(config, userStore, appSettings);
        var history = new ChatHistoryService(config);

        var broadcaster = new TestSessionBroadcaster();

        var llmProviders = new LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new LlmSessionAdapterFactory(config, new AgentPromptSourceAdapter(new SkillsService()),
            new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(config)), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var jwt = new JwtService(config, userStore, NullLogger<JwtService>.Instance);
        var server = new Mock<IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var flags = new FeatureFlagService(userStore);
        var notesSvc = new NotesService(projectManager, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, userStore, config,
            NullLogger<NotesKnowledgeService>.Instance);
        var personas = new PersonaManager(config);
        var bindings = new PersonaBindingsService(personas, projectManager, wkStore,
            knowledge, new SkillsService(), userStore, config, NullLogger<PersonaBindingsService>.Instance,
            notes: notesSvc, notesKb: notesKb);
        var sandbox = new SandboxManager(config, NullLogger<SandboxManager>.Instance);

        return new SessionManager(projectManager, history, config, adapters, falCost,
            usage, appSettings, userStore, jwt, server.Object, llmProviders, flags, personas,
            bindings, subPool, NullLogger<SessionManager>.Instance,
            TestLauncherFactory.Instance, sandbox, broadcaster);
    }

    private static void SetPrivate(object target, string fieldName, object value)
    {
        var f = target.GetType().GetField(fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        f.SetValue(target, value);
    }

    // HttpMessageHandler, который бросает на любой вызов: тестам протухшего снимка сеть
    // вообще не нужна — на путь ScheduleRefresh они не заходят (см. HiggsfieldToolset:154,
    // ScheduleRefresh только когда нужно обновить; для 25-часового снимка обновлять всё
    // равно пошлют фоном, но на результат ToolsFor это не влияет).
    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<HttpResponseMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            tcs.TrySetException(new InvalidOperationException(
                "ToolsFor теста протухшего снимка не должен трогать сеть"));
            return tcs.Task;
        }
    }

    private sealed class NoOpHandlerFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new NoOpHandler(), disposeHandler: false);
    }
}
