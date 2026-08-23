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
    // Назначения мест каталога (то, что админ ставит в диалоге «Поставщики моделей») последней
    // сборки BuildSut: в кортеж не выносим — он есть у каждого теста файла, а нужен одному.
    // Каждый тест зовёт BuildSut ровно раз, так что «последняя» здесь = «своя».
    private LocalActionOverridesStore _actionOverrides = null!;

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

        // Резолвер назначений — как в DI (со стором оверрайдов): без него назначение места
        // не переставить, а именно оно разводит «сырую» и «эффективную» модель чата.
        _actionOverrides = new LocalActionOverridesStore(config);
        var assignments = new ModelAssignmentResolver(appSettings, _actionOverrides,
            new UserModelTierResolver(userStore, appSettings));

        var sut = new SessionManager(projectManager, hub.Object, historyService, config, adapters, falCost,
            usage, appSettings, userStore, jwt, server.Object, llmProviders, notesKb, flags, personas,
            personaMemory, bindings, promptBuilder, subPool, NullLogger<SessionManager>.Instance,
            TestLauncherFactory.Instance, sandbox, assignments: assignments);

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
    public async Task MigrateProvider_АвтовыборАккаунтаПула_БезРазделителяВЛенте()
    {
        // Ротация подписок пула по договорённости тихая: маркер в ленте ставим только при
        // смене ТИПА поставщика. Автовыбор (target = null, ключ подписки не задан) переезжает
        // между аккаунтами того же вендора — «Продолжено на AI» читалось бы как уход к другому.
        var (sut, pool, llmProviders, users, projects) = BuildSut(new Dictionary<string, string?>
        {
            [$"{ClaudeSubscriptionPool.Section}:acc-a:OAuthToken"] = "token-a",
            [$"{ClaudeSubscriptionPool.Section}:acc-a:Tier"] = "max",
            [$"{ClaudeSubscriptionPool.Section}:acc-b:OAuthToken"] = "token-b",
        });
        var user = users.Add("u18", "password123", "user");
        var dir = MkProjectDir("rotate");
        var project = projects.Create("Rotate", dir, user.Id, "u18");
        const string claudeSessionId = "rotate123456";
        var session = await sut.CreateAsync(project.Id, ClaudeMode.Auto,
            resumeSessionId: claudeSessionId, model: "sonnet");
        session.Provider.Should().Be("acc-a");
        var srcDir = Path.Combine(llmProviders.GetProfileDir("sub-acc-a"), "projects",
            TranscriptMigrator.FlattenCwd(dir));
        Directory.CreateDirectory(srcDir);
        await File.WriteAllTextAsync(Path.Combine(srcDir, claudeSessionId + ".jsonl"), "{}");
        pool.MarkExhausted("acc-a", DateTime.UtcNow.AddHours(1)); // иначе Pick вернёт тот же аккаунт

        var updated = await sut.MigrateProviderAsync(session.Id, user.Id, "sonnet");

        updated.Provider.Should().Be("acc-b");
        _sentMessages.OfType<ProviderSwitchedMessage>().Should().ContainSingle()
            .Which.Label.Should().BeNull("смены типа поставщика не было — ротация внутри пула");
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
    public async Task MigrateProvider_ПустаяМодель_ПереездНаРоднойClaudeБезЗакрепления(string? model)
    {
        // Пустая модель = «По умолчанию» из настроек чата, когда назначение места модель не
        // даёт (UpdateAsync): переезжаем на родной Claude, ничего не закрепляя — иначе чат
        // перестал бы следовать назначению места. Требование «модель обязательна» осталось у
        // эндпоинта migrate-provider (ChatsController), где оно и есть проверка ввода.
        var (sut, _, _, users, _) = BuildSut(new Dictionary<string, string?>
        {
            [$"{ClaudeSubscriptionPool.Section}:acc-a:OAuthToken"] = "token-a",
            ["LlmProviders:glm:ApiKey"] = "sk-test",
            ["LlmProviders:glm:AnthropicBaseUrl"] = "https://glm.example.com",
            ["LlmProviders:glm:Models:0:Id"] = "glm-5.2",
        });
        var user = users.Add("u9", "password123", "user");
        var session = await sut.CreateChatAsync(user.Id, ClaudeMode.Auto, model: "glm-5.2");
        session.Provider.Should().Be("glm");

        var updated = await sut.MigrateProviderAsync(session.Id, user.Id, model);

        updated.Provider.Should().Be("acc-a", "родной Claude — аккаунт пула");
        updated.Model.Should().BeNull("модель не закрепляем: чат следует назначению места");
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

    // --- Десктопный чат: кадры рабочего стола наружу не уезжают (ADR-008) ---

    [Fact]
    public async Task MigrateProvider_ДесктопныйЧатНаСтороннего_Отказ()
    {
        // Инвариант ADR-008: в транскрипте десктопного чата лежат кадры экрана, а миграция —
        // копия .jsonl в чужой профиль плюс --resume с чужим ANTHROPIC_BASE_URL. Обрезка
        // цепочки (TrimChainForDesktop) закрывает только АВТОМАТИЧЕСКИЙ фолбэк, ручной путь
        // (настройки чата и кнопка «Продолжить на …») держит этот гейт.
        var (sut, _, _, users, _) = BuildSut(new Dictionary<string, string?>
        {
            [$"{ClaudeSubscriptionPool.Section}:acc-a:OAuthToken"] = "token-a",
            ["LlmProviders:glm:ApiKey"] = "sk-test",
            ["LlmProviders:glm:AnthropicBaseUrl"] = "https://glm.example.com",
            ["LlmProviders:glm:Models:0:Id"] = "glm-5.2",
        });
        var user = users.Add("u11", "password123", "user");
        var session = await sut.CreateChatAsync(user.Id, ClaudeMode.Auto, model: "sonnet");
        session.DesktopChat = true;

        var act = () => sut.MigrateProviderAsync(session.Id, user.Id, "glm-5.2");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("Десктопный чат нельзя перевести на стороннего провайдера*");
        session.Provider.Should().Be("acc-a", "отказ не двигает провайдера");
    }

    [Fact]
    public async Task MigrateProvider_ДесктопныйЧатНаДругуюПодпискуПула_Проходит()
    {
        // Ротация внутри пула Claude — не утечка: эндпоинт и владелец данных те же, а без неё
        // десктопный чат намертво вставал бы на исчерпанном аккаунте.
        var (sut, _, _, users, _) = BuildSut(new Dictionary<string, string?>
        {
            [$"{ClaudeSubscriptionPool.Section}:acc-a:OAuthToken"] = "token-a",
            [$"{ClaudeSubscriptionPool.Section}:acc-a:Tier"] = "max",
            [$"{ClaudeSubscriptionPool.Section}:acc-b:OAuthToken"] = "token-b",
        });
        var user = users.Add("u12", "password123", "user");
        var session = await sut.CreateChatAsync(user.Id, ClaudeMode.Auto, model: "sonnet");
        session.Provider.Should().Be("acc-a");
        session.DesktopChat = true;

        var updated = await sut.MigrateProviderAsync(session.Id, user.Id, "sonnet", subscriptionKey: "acc-b");

        updated.Provider.Should().Be("acc-b");
    }

    [Fact]
    public async Task MigrateProvider_ДесктопныйЧатАвтовыборАккаунтаПула_Проходит()
    {
        // Та же ротация, но БЕЗ явного ключа подписки — ветка target = null → Pick. Именно она
        // срабатывает при переезде из настроек чата (UpdateAsync ключей подписок не передаёт),
        // и гейт десктопа обязан её пропускать: аккаунт пула — тот же вендор.
        var (sut, pool, _, users, _) = BuildSut(new Dictionary<string, string?>
        {
            [$"{ClaudeSubscriptionPool.Section}:acc-a:OAuthToken"] = "token-a",
            [$"{ClaudeSubscriptionPool.Section}:acc-a:Tier"] = "max",
            [$"{ClaudeSubscriptionPool.Section}:acc-b:OAuthToken"] = "token-b",
        });
        var user = users.Add("u13", "password123", "user");
        var session = await sut.CreateChatAsync(user.Id, ClaudeMode.Auto, model: "sonnet");
        session.Provider.Should().Be("acc-a");
        session.DesktopChat = true;
        pool.MarkExhausted("acc-a", DateTime.UtcNow.AddHours(1)); // иначе Pick вернёт тот же аккаунт

        var updated = await sut.MigrateProviderAsync(session.Id, user.Id, "sonnet");

        updated.Provider.Should().Be("acc-b", "автовыбор пула обходит исчерпанный аккаунт");
    }

    // --- Настройки чата при живом пуле подписок: родная модель Claude ≠ неизвестная ---

    [Fact]
    public async Task Update_ПулПодписокИНазначениеМестаНаСтороннего_ВыборРоднойМоделиПроходит()
    {
        // Боевой расклад: чат на «По умолчанию» и на аккаунте пула, админ ПОСЛЕ этого
        // переставил назначение места chat-new на glm. Пользователь выбирает opus:
        // по эффективным моделям это glm → claude (UpdateAsync зовёт миграцию), а по сырым
        // (Model = null, Provider = acc-a) переезжать некуда. Эвристика «неизвестная модель»
        // различала эти случаи сравнением с Info.Model — при «По умолчанию» она null, и opus
        // на инсталляции С ПУЛОМ выглядел неизвестным: весь PATCH падал 400 с ложным текстом.
        var (sut, _, _, users, _) = BuildSut(new Dictionary<string, string?>
        {
            [$"{ClaudeSubscriptionPool.Section}:acc-a:OAuthToken"] = "token-a",
            ["LlmProviders:glm:ApiKey"] = "sk-test",
            ["LlmProviders:glm:AnthropicBaseUrl"] = "https://glm.example.com",
            ["LlmProviders:glm:Models:0:Id"] = "glm-5.2",
        });
        var user = users.Add("u14", "password123", "user");
        var session = await sut.CreateChatAsync(user.Id, ClaudeMode.Auto);
        session.Model.Should().BeNull("чат следует назначению места");
        session.Provider.Should().Be("acc-a", "родной Claude при живом пуле — аккаунт пула");
        _actionOverrides.Set(LocalActionCatalog.ChatNew, "glm-5.2");

        var updated = await sut.UpdateAsync(session.Id, user.Id, "Новое", "opus", "low");

        updated!.Model.Should().Be("opus");
        updated.Provider.Should().Be("acc-a", "opus — родная модель Claude, аккаунт пула тот же");
        updated.Name.Should().Be("Новое");
        updated.Effort.Should().Be("low");
    }

    [Fact]
    public async Task Update_ПулПодписокИНеизвестнаяМодель_Отказ()
    {
        // Обратная половина: у модели, которой нет ни у одного провайдера, отказ обязан
        // остаться честным 400 — иначе чат молча уехал бы на аккаунт пула с мусорным id
        // в --model и упал бы уже на ходе.
        var (sut, _, _, users, _) = BuildSut(new Dictionary<string, string?>
        {
            [$"{ClaudeSubscriptionPool.Section}:acc-a:OAuthToken"] = "token-a",
            ["LlmProviders:glm:ApiKey"] = "sk-test",
            ["LlmProviders:glm:AnthropicBaseUrl"] = "https://glm.example.com",
            ["LlmProviders:glm:Models:0:Id"] = "glm-5.2",
        });
        var user = users.Add("u15", "password123", "user");
        var session = await sut.CreateChatAsync(user.Id, ClaudeMode.Auto);
        session.Provider.Should().Be("acc-a");
        _actionOverrides.Set(LocalActionCatalog.ChatNew, "glm-5.2");

        // Префикс чужого ключа брать нельзя: "glm-…" резолвится в провайдера glm по префиксу
        var act = () => sut.UpdateAsync(session.Id, user.Id, "Новое", "kimi-k3-нет-такой", null);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("Модель*не найдена среди настроенных провайдеров*");
        session.Model.Should().BeNull("отказ не закрепляет мусорную модель");
        session.Name.Should().BeNull("PATCH не применился целиком — модель проверяется первой");
    }

    // --- Смена модели В ПРЕДЕЛАХ пула: аккаунт менять нельзя, транскрипт лежит в его профиле ---

    [Fact]
    public async Task Update_СменаМоделиВПределахПула_ОставляетТекущийАккаунт()
    {
        // Чат уехал на acc-b фолбэком (тот перенёс .jsonl в его профиль), окно acc-a с тех пор
        // сбросилось. Смена sonnet → opus провайдера по смыслу не меняет (обе модели родные),
        // миграция не зовётся — и ветка «родной Claude» звала Pick, который при разных тарифах
        // детерминированно возвращал acc-a. Info.Provider уезжал на acc-a БЕЗ переноса
        // транскрипта: живой адаптер держит старый корень, но после рестарта --resume идёт в
        // профиль acc-a, где файла нет, — «No conversation found» и разговор с нуля.
        var (sut, _, _, users, _) = BuildSut(new Dictionary<string, string?>
        {
            [$"{ClaudeSubscriptionPool.Section}:acc-a:OAuthToken"] = "token-a",
            [$"{ClaudeSubscriptionPool.Section}:acc-a:Tier"] = "max",
            [$"{ClaudeSubscriptionPool.Section}:acc-b:OAuthToken"] = "token-b",
            [$"{ClaudeSubscriptionPool.Section}:acc-b:Tier"] = "pro",
        });
        var user = users.Add("u16", "password123", "user");
        var session = await sut.CreateChatAsync(user.Id, ClaudeMode.Auto, model: "sonnet");
        session.Provider.Should().Be("acc-a", "выше тариф — Pick выбирает его при создании");
        session.Provider = "acc-b"; // как после фолбэка: и провайдер, и транскрипт — у acc-b

        var updated = await sut.UpdateAsync(session.Id, user.Id, null, "opus", null);

        updated!.Model.Should().Be("opus");
        updated.Provider.Should().Be("acc-b", "аккаунт пула тянет opus — перевешивать чат некуда");
    }

    [Fact]
    public async Task Update_ТекущийАккаунтНеТянетМодель_УводитНаСпособный()
    {
        // Обратная половина: пин Opus на аккаунте без Opus — вот здесь Pick и нужен, иначе CLI
        // упал бы «There's an issue with the selected model (opus)».
        var (sut, _, _, users, _) = BuildSut(new Dictionary<string, string?>
        {
            [$"{ClaudeSubscriptionPool.Section}:acc-a:OAuthToken"] = "token-a",
            [$"{ClaudeSubscriptionPool.Section}:acc-a:Tier"] = "max",
            [$"{ClaudeSubscriptionPool.Section}:acc-b:OAuthToken"] = "token-b",
            [$"{ClaudeSubscriptionPool.Section}:acc-b:Tier"] = "pro",
            [$"{ClaudeSubscriptionPool.Section}:acc-b:SupportsOpus"] = "false",
        });
        var user = users.Add("u17", "password123", "user");
        var session = await sut.CreateChatAsync(user.Id, ClaudeMode.Auto, model: "sonnet");
        session.Provider = "acc-b";

        var updated = await sut.UpdateAsync(session.Id, user.Id, null, "opus", null);

        updated!.Provider.Should().Be("acc-a", "acc-b без Opus — уводим на способный аккаунт");
    }
}
