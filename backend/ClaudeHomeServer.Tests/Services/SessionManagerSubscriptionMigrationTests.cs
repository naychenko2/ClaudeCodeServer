using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Явный выбор подписки пула для продолжения чата на лимите (карточка «Продолжить на …»
// должна уметь предложить не только сторонних провайдеров, но и здоровые аккаунты того
// же пула подписок — а MigrateProviderAsync принимать конкретный ключ вместо автовыбора
// Pick). Своя, минимальная сборка SessionManager на тест — конфиг подписок/провайдеров
// у каждого теста свой, в отличие от общего SessionManagerTests с одним фиксированным.
public class SessionManagerSubscriptionMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<ServerMessage> _sentMessages = [];

    public SessionManagerSubscriptionMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "smgr_sub_migrate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private (SessionManager Sut, ClaudeSubscriptionPool Pool, LlmProviderRegistry LlmProviders,
        UserStore Users, ProjectManager Projects) BuildSut(Dictionary<string, string?> extra)
    {
        var dict = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            ["DefaultProjectsPath"] = Path.Combine(_tempDir, "homes"),
            ["ClaudeUserProfileDir"] = Path.Combine(_tempDir, "claude-profile"),
        };
        foreach (var (k, v) in extra) dict[k] = v;
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();

        var userStore = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projectManager = new ProjectManager(config, userStore, appSettings);
        var historyService = new ChatHistoryService(config);

        var clients = new Mock<IHubClients>();
        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, args, _) =>
            {
                if (args.Length > 0 && args[0] is ServerMessage msg) _sentMessages.Add(msg);
            })
            .Returns(Task.CompletedTask);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        var hub = new Mock<IHubContext<SessionHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

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
        var personaMemory = new PersonaMemoryService(knowledge, personas, userStore, config,
            NullLogger<PersonaMemoryService>.Instance);
        var bindings = new PersonaBindingsService(personas, projectManager, wkStore, notesSvc, notesKb,
            knowledge, new SkillsService(), userStore, config, NullLogger<PersonaBindingsService>.Instance);
        var promptBuilder = new PersonaPromptBuilder(llmProviders);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);

        var sut = new SessionManager(projectManager, hub.Object, historyService, config, adapters, falCost,
            usage, appSettings, userStore, jwt, server.Object, llmProviders, notesKb, flags, personas,
            personaMemory, bindings, promptBuilder, subPool, NullLogger<SessionManager>.Instance,
            TestLauncherFactory.Instance, sandbox);

        return (sut, subPool, llmProviders, userStore, projectManager);
    }

    private string MkProjectDir(string suffix) =>
        Directory.CreateDirectory(Path.Combine(_tempDir, "proj_" + suffix)).FullName;

    // --- OfferProviderFallbackAsync: опции карточки provider_limit ---

    [Fact]
    public async Task OfferProviderFallback_ЗдоровыйАккаунтПула_ПопадаетВОпцииКакSubscription()
    {
        var (sut, pool, _, users, _) = BuildSut(new Dictionary<string, string?>
        {
            [$"{ClaudeSubscriptionPool.Section}:acc-a:OAuthToken"] = "token-a",
            [$"{ClaudeSubscriptionPool.Section}:acc-a:Tier"] = "max",
            [$"{ClaudeSubscriptionPool.Section}:acc-b:OAuthToken"] = "token-b",
        });
        var user = users.Add("u1", "password123", "user");
        var session = await sut.CreateChatAsync(user.Id, ClaudeMode.Auto, model: "sonnet");
        session.Provider.Should().Be("acc-a", "выше тариф — Pick выбирает его при создании");
        pool.MarkExhausted(session.Provider!, DateTime.UtcNow.AddHours(1));

        await sut.OfferProviderFallbackAsync(session.Id, resetsAt: null);

        var msg = _sentMessages.OfType<ProviderLimitMessage>().Should().ContainSingle().Subject;
        var option = msg.Providers.Should().ContainSingle().Subject;
        option.Key.Should().Be("acc-b");
        option.Kind.Should().Be("subscription");
        option.Model.Should().Be("sonnet");
    }

    [Fact]
    public async Task OfferProviderFallback_ИсчерпанныйИТекущийАккаунт_НеПопадаютВОпции()
    {
        var (sut, pool, _, users, _) = BuildSut(new Dictionary<string, string?>
        {
            [$"{ClaudeSubscriptionPool.Section}:acc-a:OAuthToken"] = "token-a",
            [$"{ClaudeSubscriptionPool.Section}:acc-a:Tier"] = "max",
            [$"{ClaudeSubscriptionPool.Section}:acc-b:OAuthToken"] = "token-b",
            [$"{ClaudeSubscriptionPool.Section}:acc-c:OAuthToken"] = "token-c",
        });
        var user = users.Add("u2", "password123", "user");
        var session = await sut.CreateChatAsync(user.Id, ClaudeMode.Auto, model: "sonnet");
        session.Provider.Should().Be("acc-a");
        pool.MarkExhausted("acc-a", DateTime.UtcNow.AddHours(1)); // текущий (исчерпан — иначе не дошли бы сюда)
        pool.MarkExhausted("acc-b", DateTime.UtcNow.AddHours(1)); // тоже исчерпан — не годится

        await sut.OfferProviderFallbackAsync(session.Id, resetsAt: null);

        var msg = _sentMessages.OfType<ProviderLimitMessage>().Should().ContainSingle().Subject;
        msg.Providers.Select(o => o.Key).Should().BeEquivalentTo(["acc-c"]);
    }

    [Fact]
    public async Task OfferProviderFallback_АккаунтБезOpus_НеПопадаетВОпцииДляOpus()
    {
        var (sut, pool, _, users, _) = BuildSut(new Dictionary<string, string?>
        {
            [$"{ClaudeSubscriptionPool.Section}:acc-a:OAuthToken"] = "token-a",
            [$"{ClaudeSubscriptionPool.Section}:acc-a:Tier"] = "max",
            [$"{ClaudeSubscriptionPool.Section}:acc-b:OAuthToken"] = "token-b",
            [$"{ClaudeSubscriptionPool.Section}:acc-b:SupportsOpus"] = "false",
        });
        var user = users.Add("u3", "password123", "user");
        var session = await sut.CreateChatAsync(user.Id, ClaudeMode.Auto, model: "claude-opus-4");
        session.Provider.Should().Be("acc-a");
        pool.MarkExhausted("acc-a", DateTime.UtcNow.AddHours(1));

        await sut.OfferProviderFallbackAsync(session.Id, resetsAt: null);

        // acc-b жив, но без Opus — на opus-модели карточка не предложит его вовсе
        _sentMessages.OfType<ProviderLimitMessage>().Should().BeEmpty();
    }

    // --- MigrateProviderAsync: явный ключ подписки ---

    [Fact]
    public async Task MigrateProvider_ЯвныйКлючПодписки_МенялПровайдераИПереноситТранскрипт()
    {
        var (sut, _, llmProviders, users, projects) = BuildSut(new Dictionary<string, string?>
        {
            [$"{ClaudeSubscriptionPool.Section}:acc-a:OAuthToken"] = "token-a",
            [$"{ClaudeSubscriptionPool.Section}:acc-a:Tier"] = "max",
            [$"{ClaudeSubscriptionPool.Section}:acc-b:OAuthToken"] = "token-b",
        });
        var user = users.Add("u4", "password123", "user");
        var dir = MkProjectDir("migrate");
        var project = projects.Create("Migrate", dir, user.Id, "u4");
        const string claudeSessionId = "abc123def456";
        var session = await sut.CreateAsync(project.Id, ClaudeMode.Auto,
            resumeSessionId: claudeSessionId, model: "sonnet");
        session.Provider.Should().Be("acc-a");

        var srcDir = Path.Combine(llmProviders.GetProfileDir("sub-acc-a"), "projects",
            TranscriptMigrator.FlattenCwd(dir));
        Directory.CreateDirectory(srcDir);
        await File.WriteAllTextAsync(Path.Combine(srcDir, claudeSessionId + ".jsonl"), "{}");

        var updated = await sut.MigrateProviderAsync(session.Id, user.Id, "sonnet", subscriptionKey: "acc-b");

        updated.Provider.Should().Be("acc-b");
        var dstFile = Path.Combine(llmProviders.GetProfileDir("sub-acc-b"), "projects",
            TranscriptMigrator.FlattenCwd(dir), claudeSessionId + ".jsonl");
        File.Exists(dstFile).Should().BeTrue("транскрипт должен переехать в профиль целевой подписки");
        // Подпись разделителя — про подписку, а не безликое «Продолжено на AI»
        _sentMessages.OfType<ProviderSwitchedMessage>().Single().Label
            .Should().Be("Продолжено на подписке «acc-b»");
    }

    [Fact]
    public async Task MigrateProvider_НесуществующийКлюч_БросаетОшибку()
    {
        var (sut, _, _, users, _) = BuildSut(new Dictionary<string, string?>
        {
            [$"{ClaudeSubscriptionPool.Section}:acc-a:OAuthToken"] = "token-a",
        });
        var user = users.Add("u5", "password123", "user");
        var session = await sut.CreateChatAsync(user.Id, ClaudeMode.Auto, model: "sonnet");

        var act = () => sut.MigrateProviderAsync(session.Id, user.Id, "sonnet", subscriptionKey: "no-such-key");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task MigrateProvider_ВыключенныйКлюч_БросаетОшибку()
    {
        // Запись есть в конфиге, но без токена — Enabled=false, значит не входит в All
        var (sut, _, _, users, _) = BuildSut(new Dictionary<string, string?>
        {
            [$"{ClaudeSubscriptionPool.Section}:acc-a:OAuthToken"] = "token-a",
            [$"{ClaudeSubscriptionPool.Section}:acc-disabled:DisplayName"] = "Отключенная",
        });
        var user = users.Add("u6", "password123", "user");
        var session = await sut.CreateChatAsync(user.Id, ClaudeMode.Auto, model: "sonnet");

        var act = () => sut.MigrateProviderAsync(session.Id, user.Id, "sonnet", subscriptionKey: "acc-disabled");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task MigrateProvider_ТекущийКлюч_БросаетОшибку()
    {
        var (sut, _, _, users, _) = BuildSut(new Dictionary<string, string?>
        {
            [$"{ClaudeSubscriptionPool.Section}:acc-a:OAuthToken"] = "token-a",
        });
        var user = users.Add("u7", "password123", "user");
        var session = await sut.CreateChatAsync(user.Id, ClaudeMode.Auto, model: "sonnet");
        session.Provider.Should().Be("acc-a");

        var act = () => sut.MigrateProviderAsync(session.Id, user.Id, "sonnet", subscriptionKey: "acc-a");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task MigrateProvider_НесовместимаяСМодельюПодписка_БросаетОшибку()
    {
        var (sut, _, _, users, _) = BuildSut(new Dictionary<string, string?>
        {
            [$"{ClaudeSubscriptionPool.Section}:acc-a:OAuthToken"] = "token-a",
            [$"{ClaudeSubscriptionPool.Section}:acc-b:OAuthToken"] = "token-b",
            [$"{ClaudeSubscriptionPool.Section}:acc-b:SupportsOpus"] = "false",
        });
        var user = users.Add("u8", "password123", "user");
        var session = await sut.CreateChatAsync(user.Id, ClaudeMode.Auto, model: "claude-opus-4");
        session.Provider.Should().Be("acc-a");

        var act = () => sut.MigrateProviderAsync(session.Id, user.Id, "claude-opus-4", subscriptionKey: "acc-b");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MigrateProvider_ПустаяМодель_БросаетОшибку(string? model)
    {
        // Миграции нужна конкретная модель целевого провайдера (перевоз транскрипта + --resume),
        // пустую/whitespace она не принимает — фронт обязан резолвить её из назначения места
        // chat-new ДО вызова (handleModelChange), иначе «Не указана модель» тостом в чат.
        var (sut, _, _, users, _) = BuildSut(new Dictionary<string, string?>
        {
            [$"{ClaudeSubscriptionPool.Section}:acc-a:OAuthToken"] = "token-a",
        });
        var user = users.Add("u9", "password123", "user");
        var session = await sut.CreateChatAsync(user.Id, ClaudeMode.Auto, model: "sonnet");

        var act = () => sut.MigrateProviderAsync(session.Id, user.Id, model!);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("Не указана модель");
    }

    [Fact]
    public async Task MigrateProvider_НеизвестнаяМодельСПодписки_БросаетВнятнуюОшибку()
    {
        // Регрессия ложного «Чат уже на этом провайдере»: чат на подписке, фронт зовёт
        // миграцию на id, которого реестр не знает (рассинхрон каталога /api/models и
        // LlmProviderRegistry — напр. id сторонней модели, не прописанной в Models/префиксах).
        // Раньше target=null молча подменялся аккаунтом пула (Pick), совпадавшим с текущим,
        // и пользователь видел бессмысленное «уже на этом провайдере» вместо правды.
        var (sut, _, _, users, _) = BuildSut(new Dictionary<string, string?>
        {
            [$"{ClaudeSubscriptionPool.Section}:acc-a:OAuthToken"] = "token-a",
            [$"{ClaudeSubscriptionPool.Section}:acc-a:Tier"] = "max",
            [$"{ClaudeSubscriptionPool.Section}:acc-b:OAuthToken"] = "token-b",
        });
        var user = users.Add("u10", "password123", "user");
        var session = await sut.CreateChatAsync(user.Id, ClaudeMode.Auto, model: "sonnet");
        session.Provider.Should().Be("acc-a", "чуточку контекста: текущий провайдер — подписка пула");

        var act = () => sut.MigrateProviderAsync(session.Id, user.Id, "glm-9.99-несуществующая");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("Модель*не найдена среди настроенных провайдеров*");
    }
}
