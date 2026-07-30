using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Миграция транскриптов у container-пользователей: и корни профилей, и рабочая папка у них
// другие — профиль подменяется на песочный {ProfilesHostDir}/{ownerId}/{ключ}
// (DockerProcessRunner.RewriteProfileEnv), а CLI внутри контейнера видит путь /projects/…
// Тесты сторожат зеркало SessionManager.ConfigRootFor ↔ RewriteProfileEnv: разойдись они —
// миграция молча копировала бы транскрипт мимо, и --resume начал бы разговор с нуля.
//
// Сборка своя (а не общий SessionManagerTests): нужны настоящая LauncherFactory (маппер
// путей песочницы), Sandbox:ProjectsRoot и сторонний провайдер как цель миграции.
public class SessionManagerContainerMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _projectsRoot;
    private readonly List<ServerMessage> _sentMessages = [];

    public SessionManagerContainerMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "smgr_container_migrate_" + Guid.NewGuid().ToString("N"));
        _projectsRoot = Path.Combine(_tempDir, "sandbox-projects");
        Directory.CreateDirectory(_projectsRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private (SessionManager Sut, SandboxManager Sandbox, LlmProviderRegistry LlmProviders,
        UserStore Users, ProjectManager Projects) BuildSut()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "data", "projects.json"),
            ["DefaultProjectsPath"] = Path.Combine(_tempDir, "homes"),
            ["ClaudeUserProfileDir"] = Path.Combine(_tempDir, "claude-profile"),
            // Песочница включена (корень задан) — docker в тестах не зовётся:
            // нужны только маппер путей и раскладка профилей
            ["Sandbox:ProjectsRoot"] = _projectsRoot,
            // Сторонний провайдер — цель миграции
            [$"{LlmProviderRegistry.Section}:glm:DisplayName"] = "GLM",
            [$"{LlmProviderRegistry.Section}:glm:ApiKey"] = "test-key",
            [$"{LlmProviderRegistry.Section}:glm:AnthropicBaseUrl"] = "https://example.invalid/api",
            [$"{LlmProviderRegistry.Section}:glm:Models:0:Id"] = "glm-4",
        }).Build();

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
        var jwt = new JwtService(config, NullLogger<JwtService>.Instance);
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
        var sandbox = new SandboxManager(config, NullLogger<SandboxManager>.Instance);
        // Настоящая фабрика: container-пользователь должен получить песочный драйвер с
        // маппером путей (docker при этом не запускается — Start в тестах не зовётся)
        var launchers = new LauncherFactory(userStore, sandbox);

        var sut = new SessionManager(projectManager, hub.Object, historyService, config, adapters, falCost,
            usage, appSettings, userStore, jwt, server.Object, llmProviders, notesKb, flags, personas,
            personaMemory, bindings, promptBuilder, subPool, NullLogger<SessionManager>.Instance,
            launchers, sandbox);

        return (sut, sandbox, llmProviders, userStore, projectManager);
    }

    // Проект container-пользователя обязан лежать внутри Sandbox:ProjectsRoot —
    // иначе ProjectManager его не примет, а маппер путей не переведёт
    private string MkContainerProjectDir(User user, string suffix) =>
        Directory.CreateDirectory(Path.Combine(_projectsRoot, user.Username, "proj_" + suffix)).FullName;

    // Профиль песочницы владельца: {ProfilesHostDir}/{ownerId}/{ключ}
    private static string SandboxProfile(SandboxManager sandbox, string ownerId, string key) =>
        Path.Combine(sandbox.ProfilesHostDir, ownerId, key);

    private static string SeedTranscript(string configRoot, string cwd, string csid)
    {
        var dir = Path.Combine(configRoot, "projects", TranscriptMigrator.FlattenCwd(cwd));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, csid + ".jsonl");
        File.WriteAllText(file, "{\"type\":\"user\"}");
        return file;
    }

    [Fact]
    public async Task MigrateProvider_Container_ПереноситТранскриптВПесочныйПрофильПровайдера()
    {
        var (sut, sandbox, _, users, projects) = BuildSut();
        var user = users.Add("cu1", "password123", "user", ExecutionEnvironments.Container);
        var dir = MkContainerProjectDir(user, "migrate");
        var project = projects.Create("Migrate", dir, user.Id, user.Username);
        const string csid = "abc123def456";
        var session = await sut.CreateAsync(project.Id, ClaudeMode.Auto, resumeSessionId: csid, model: "sonnet");

        // Раскладка хода: профиль «без оверрайда» → {owner}/default, cwd — контейнерный
        var containerCwd = "/projects/" + user.Username + "/proj_migrate";
        SeedTranscript(SandboxProfile(sandbox, user.Id, "default"), containerCwd, csid);

        var updated = await sut.MigrateProviderAsync(session.Id, user.Id, "glm-4");

        updated.Provider.Should().Be("glm");
        File.Exists(Path.Combine(SandboxProfile(sandbox, user.Id, "glm"), "projects",
                TranscriptMigrator.FlattenCwd(containerCwd), csid + ".jsonl"))
            .Should().BeTrue("транскрипт должен переехать в песочный профиль целевого провайдера");
    }

    [Fact]
    public async Task MigrateProvider_Container_ТранскриптИщетсяПоКонтейнерномуCwd()
    {
        // Посев строго по соглашению от контейнерного пути: если бы cwd брался хостовый,
        // спасал бы только фолбэк-скан — здесь единственная папка совпадает с ожидаемой,
        // так что промах маппинга виден сразу
        var (sut, sandbox, _, users, projects) = BuildSut();
        var user = users.Add("cu2", "password123", "user", ExecutionEnvironments.Container);
        var dir = MkContainerProjectDir(user, "cwd");
        var project = projects.Create("Cwd", dir, user.Id, user.Username);
        const string csid = "cwd123456789";
        var session = await sut.CreateAsync(project.Id, ClaudeMode.Auto, resumeSessionId: csid, model: "sonnet");

        var containerCwd = "/projects/" + user.Username + "/proj_cwd";
        TranscriptMigrator.FlattenCwd(containerCwd).Should().NotBe(TranscriptMigrator.FlattenCwd(dir),
            "хостовый и контейнерный пути уплощаются по-разному — в этом вся суть перевода");
        SeedTranscript(SandboxProfile(sandbox, user.Id, "default"), containerCwd, csid);

        await sut.MigrateProviderAsync(session.Id, user.Id, "glm-4");

        var dstProjects = Path.Combine(SandboxProfile(sandbox, user.Id, "glm"), "projects");
        Directory.GetDirectories(dstProjects).Select(Path.GetFileName)
            .Should().BeEquivalentTo([TranscriptMigrator.FlattenCwd(containerCwd)]);
    }

    [Fact]
    public async Task MigrateProvider_Container_БезХодов_ПростоПереключает()
    {
        // Ходов не было → ClaudeSessionId пуст, переносить нечего: миграция обязана пройти
        var (sut, _, _, users, projects) = BuildSut();
        var user = users.Add("cu3", "password123", "user", ExecutionEnvironments.Container);
        var dir = MkContainerProjectDir(user, "fresh");
        var project = projects.Create("Fresh", dir, user.Id, user.Username);
        var session = await sut.CreateAsync(project.Id, ClaudeMode.Auto, model: "sonnet");

        var updated = await sut.MigrateProviderAsync(session.Id, user.Id, "glm-4");

        updated.Provider.Should().Be("glm");
        updated.Model.Should().Be("glm-4");
    }

    [Fact]
    public async Task MigrateProvider_Container_ПутьВнеМонтирований_400()
    {
        // Рабочая папка вне монтирований песочницы (проект заведён до перевода в container
        // или корень песочницы сменили) — ToRuntime отвергает путь, и явная операция обязана
        // отдать причину наружу, а не сделать вид, что контекст переехал
        var (sut, _, _, users, projects) = BuildSut();
        var user = users.Add("cu4", "password123", "user", ExecutionEnvironments.Container);
        var dir = MkContainerProjectDir(user, "outside");
        var project = projects.Create("Outside", dir, user.Id, user.Username);
        const string csid = "out123456789";
        var session = await sut.CreateAsync(project.Id, ClaudeMode.Auto, resumeSessionId: csid, model: "sonnet");
        // Уводим корень проекта наружу песочницы уже после создания
        var outside = Directory.CreateDirectory(Path.Combine(_tempDir, "outside-root")).FullName;
        projects.GetById(project.Id)!.RootPath = outside;

        var act = () => sut.MigrateProviderAsync(session.Id, user.Id, "glm-4");

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*недоступен в песочнице*");
        sut.GetById(session.Id)!.Provider.Should().NotBe("glm", "провайдер не меняем без контекста");
    }

    [Fact]
    public async Task MigrateProvider_Local_ПоведениеНеИзменилось()
    {
        // Тот же стенд, но обычный пользователь: корни — хостовые профили, cwd — хостовый
        var (sut, sandbox, llmProviders, users, projects) = BuildSut();
        var user = users.Add("lu1", "password123", "user");
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, "local_proj")).FullName;
        var project = projects.Create("Local", dir, user.Id, user.Username);
        const string csid = "loc123456789";
        var session = await sut.CreateAsync(project.Id, ClaudeMode.Auto, resumeSessionId: csid, model: "sonnet");
        SeedTranscript(llmProviders.UserProfileDir, dir, csid);

        var updated = await sut.MigrateProviderAsync(session.Id, user.Id, "glm-4");

        updated.Provider.Should().Be("glm");
        File.Exists(Path.Combine(llmProviders.GetProfileDir("glm"), "projects",
            TranscriptMigrator.FlattenCwd(dir), csid + ".jsonl")).Should().BeTrue();
        Directory.Exists(Path.Combine(sandbox.ProfilesHostDir, user.Id))
            .Should().BeFalse("local-пользователь песочных профилей не касается");
    }
}
