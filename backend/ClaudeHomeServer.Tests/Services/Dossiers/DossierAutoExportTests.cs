using System.Text.Json;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Dossiers;
using ClaudeHomeServer.Services.Git;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.Dossiers;

// Интеграционные тесты автовыгрузки паспортов в ЛОКАЛЬНУЮ ветку ccs/dossiers/v1 после
// захвата: настоящий git CLI и настоящий SessionManager (сессии предзагружаются в
// sessions.json ДО конструирования — паттерн DossierGitExportTests), стор и флаги —
// реальные на temp-конфиге. Дебаунс тестовый — 1 с (Dossiers:AutoExportDebounceSeconds),
// ветку ждём поллингом с дедлайном, а не фиксированной паузой (раннер CI медленнее хоста).
[Trait("Category", "Slow")]
public class DossierAutoExportTests : IDisposable
{
    private readonly string _temp;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly UserStore _users;
    private readonly FeatureFlagService _flags;
    private readonly ChatHistoryService _history;
    private readonly Mock<IHubContext<SessionHub>> _hub;
    private readonly DossierStore _store;
    private readonly GitService _git = new(TestLauncherFactory.Instance);
    private readonly List<IDisposable> _disposables = [];

    public DossierAutoExportTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "dossier_auto_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_temp, "projects.json"),
                ["Dossiers:AutoExportDebounceSeconds"] = "1",
            })
            .Build();

        _users = new UserStore(_config,
            new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, _users, new AppSettingsService(_config));
        _flags = new FeatureFlagService(_users);
        _history = new ChatHistoryService(_config);
        _store = new DossierStore(_config);

        var clients = new Mock<IHubClients>();
        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        _hub = new Mock<IHubContext<SessionHub>>();
        _hub.Setup(h => h.Clients).Returns(clients.Object);
    }

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        try { Directory.Delete(_temp, recursive: true); }
        catch { /* git на Windows держит readonly-объекты — не роняем прогон */ }
    }

    // --- Фикстуры ---

    private async Task<(Project Project, User User)> MkRepoProjectAsync(string name, bool flagOn)
    {
        var user = _users.Add("auto-" + name, "password-123456", "user");
        if (flagOn)
            _users.SetFeatureFlag(user.Id, FeatureFlagKeys.ChangeDossiersRecall, true).Should().BeTrue();

        var dir = Path.Combine(_temp, name);
        Directory.CreateDirectory(dir);
        await _git.InitAsync(null, dir);
        await _git.RunAsync(null, dir, ["config", "user.email", "dossiers@test"]);
        await _git.RunAsync(null, dir, ["config", "user.name", "Тест Досье"]);
        await File.WriteAllTextAsync(Path.Combine(dir, "readme.md"), "содержимое\n");
        await _git.StageAllAsync(null, dir);
        await _git.CommitAsync(null, dir, "начальный коммит");
        return (_projects.Create(name, dir, user.Id, user.Username), user);
    }

    private static Session Sess(string id, string projectId, bool excludeFromDossiers = false) => new()
    {
        Id = id,
        // Владелец заполняется только у чата вне проекта: у проектной сессии он живёт у проекта
        OwnerId = null,
        ProjectId = projectId,
        ExcludeFromDossiers = excludeFromDossiers,
        Status = SessionStatus.Finished,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static ChangeDossier Dossier(string ownerId, string projectId, string sha, string subject, string sessionId) => new()
    {
        OwnerId = ownerId,
        ProjectId = projectId,
        CommitSha = sha,
        CommitSubject = subject,
        CommittedAt = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero),
        SessionId = sessionId,
        Why = "почему изменение сделано",
        Decisions = ["решили так"],
    };

    // SessionManager с предзагруженными сессиями: файл пишется ДО конструирования
    // (паттерн DossierGitExportTests), процессы CLI не запускаются
    private SessionManager MkSessions(params Session[] sessions)
    {
        File.WriteAllText(Path.Combine(_temp, "sessions.json"), JsonSerializer.Serialize(sessions));
        var manager = CreateSessionManager();
        _disposables.Add(manager);
        return manager;
    }

    private SessionManager CreateSessionManager()
    {
        // Полный граф зависимостей менеджера — тяжёлый конструктор не мокается (проектный
        // паттерн, см. DossierGitExportTests.CreateSessionManager), все сторы — на temp-конфиге
        var llmProviders = new ClaudeHomeServer.Services.Llm.LlmProviderRegistry(_config);
        var subPool = new ClaudeSubscriptionPool(_config);
        var adapters = new ClaudeHomeServer.Services.Llm.LlmSessionAdapterFactory(
            _config, new SkillsService(), new WorkspaceKnowledgeStore(_config), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, _config);
        var usage = new UsageService(_config);
        var userStore = new UserStore(_config,
            new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(_config);
        var jwt = new JwtService(_config, userStore, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(_config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var flags = new FeatureFlagService(userStore);
        var notesSvc = new NotesService(_projects, _config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, userStore, _config,
            NullLogger<NotesKnowledgeService>.Instance);
        var personas = new PersonaManager(_config);
        var personaMemory = new PersonaMemoryService(knowledge, personas, userStore, _config,
            NullLogger<PersonaMemoryService>.Instance);
        var bindings = new PersonaBindingsService(personas, _projects, wkStore, notesSvc, notesKb,
            knowledge, new SkillsService(), userStore, _config, NullLogger<PersonaBindingsService>.Instance);
        var promptBuilder = new PersonaPromptBuilder(llmProviders);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(_config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        return new SessionManager(_projects, _hub.Object, _history, _config, adapters, falCost, usage, appSettings,
            userStore, jwt, server.Object, llmProviders, notesKb, flags, personas, personaMemory, bindings,
            promptBuilder, subPool, NullLogger<SessionManager>.Instance, TestLauncherFactory.Instance, sandbox);
    }

    // Автовыгрузчик на живом графе зависимостей; StartAsync подключает подписку на стор —
    // как это делает хост в проде. Конспекты: сервис на моке LLM — лент чатов в фикстурах
    // нет, модель не зовётся (пустая лента → конспект не снимается)
    private DossierAutoExporter MkAutoExporter(SessionManager sessions)
    {
        var discussions = new DossierDiscussionService(new DossierDiscussionStore(_config),
            _store, sessions, new Mock<ClaudeHomeServer.Services.Llm.ICheapTextRunner>().Object,
            new InstanceSecretsProvider(_config));
        return new DossierAutoExporter(_store, _projects, sessions, _git,
            new InstanceSecretsProvider(_config), discussions, _flags, _config);
    }

    // --- Хелперы git-проверок ---

    private Task<GitResult> GitAsync(string root, params string[] args) => _git.RunAsync(null, root, args);

    private async Task<bool> HasBranchAsync(string root)
    {
        var r = await GitAsync(root, "rev-parse", "--quiet", GitService.DossiersRef);
        return r.Ok && !string.IsNullOrWhiteSpace(r.Stdout);
    }

    // Ждём появления ветки поллингом с дедлайном: дебаунс срабатывает по таймеру,
    // фиксированная пауза на медленном раннере гонки бы не выиграла
    private async Task AwaitBranchAsync(string root, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < deadline)
        {
            if (await HasBranchAsync(root)) return;
            await Task.Delay(200);
        }
        throw new TimeoutException("автовыгрузка не создала ветку за отведённое время");
    }

    // --- (а) гейт флага владельца: без change-dossiers-recall автовыгрузки нет ---

    [Fact]
    public async Task ФлагВыключен_АвтовыгрузкаНеСоздаётВетку()
    {
        var (p, user) = await MkRepoProjectAsync("repo_flag_off", flagOn: false);
        var s = Sess("sess-auto-a", p.Id);
        var sessions = MkSessions(s);
        var auto = MkAutoExporter(sessions);
        await auto.StartAsync(default);

        _store.Add(Dossier(user.Id, p.Id, "aa11aa11", "feat: паспорт без флага", s.Id));
        await auto.ExportSafeAsync(user.Id, p.Id);   // немедленная попытка, без дебаунса

        (await HasBranchAsync(p.RootPath)).Should()
            .BeFalse("флаг владельца выключен — та же граница, что у ручного POST /export");
    }

    // --- (б) захват через событие стора → после дебаунса ветка создана и чиста ---

    [Fact]
    public async Task ЗахватЧерезСобытие_ПослеDebounceВыгружаетВетку()
    {
        var (p, user) = await MkRepoProjectAsync("repo_auto_on", flagOn: true);
        var s = Sess("sess-auto-b", p.Id);
        var auto = MkAutoExporter(MkSessions(s));
        await auto.StartAsync(default);

        _store.Add(Dossier(user.Id, p.Id, "bb22bb22", "feat: паспорт для автовыгрузки", s.Id));

        await AwaitBranchAsync(p.RootPath);
        var tree = await GitAsync(p.RootPath, "ls-tree", "-r", "--name-only", GitService.DossiersRef);
        // имя файла ветки — 7-символьный префикс sha коммита
        tree.Stdout.Should().Contain("bb22bb2", "паспорт захвата доехал до ветки");
        (await GitAsync(p.RootPath, "status", "--porcelain")).Stdout.Trim().Should()
            .BeEmpty("автовыгрузка не трогает рабочее дерево");
    }

    // --- (в) дебаунс накопления: серия захватов подряд сливается в ОДИН коммит ветки ---

    [Fact]
    public async Task СерияЗахватов_ДебаунсСливаетВОдинКоммит()
    {
        var (p, user) = await MkRepoProjectAsync("repo_auto_batch", flagOn: true);
        var s = Sess("sess-auto-c", p.Id);
        var auto = MkAutoExporter(MkSessions(s));
        await auto.StartAsync(default);

        // Два паспорта подряд (реалистично: несколько коммитов одного хода) — оба события
        // должны перезаписать один и тот же таймер дебаунса
        _store.Add(Dossier(user.Id, p.Id, "cc33cc33", "feat: первый из серии", s.Id));
        _store.Add(Dossier(user.Id, p.Id, "dd44dd44", "feat: второй из серии", s.Id));

        await AwaitBranchAsync(p.RootPath);
        (await GitAsync(p.RootPath, "rev-list", "--count", GitService.DossiersRef)).Stdout.Trim()
            .Should().Be("1", "серия захватов батчится в один экспорт, а не по коммиту на паспорт");
        var tree = await GitAsync(p.RootPath, "ls-tree", "-r", "--name-only", GitService.DossiersRef);
        tree.Stdout.Should().Contain("cc33cc3").And.Contain("dd44dd4",
            "оба паспорта серии вошли в единственный коммит");
    }

    // --- (г) opt-out, включённый ПОСЛЕ захвата: паспорт не едет в ветку даже автоматически
    // (предохранители выгрузки перепроверяют чат на месте, а не верят моменту захвата) ---

    [Fact]
    public async Task ОптАутВключённыйПослеЗахвата_НеПопадаетВАвтовыгрузку()
    {
        var (p, user) = await MkRepoProjectAsync("repo_auto_optout", flagOn: true);
        var hidden = Sess("sess-auto-hidden", p.Id, excludeFromDossiers: true);
        var ok = Sess("sess-auto-ok", p.Id);
        var auto = MkAutoExporter(MkSessions(hidden, ok));
        await auto.StartAsync(default);

        // hidden был захвачен раньше, opt-out включили потом; событие (и экспорт) даёт ok
        _store.Add(Dossier(user.Id, p.Id, "ee55ee55", "feat: скрыт тумблером", hidden.Id));
        _store.Add(Dossier(user.Id, p.Id, "ff66ff66", "feat: остаётся", ok.Id));

        await AwaitBranchAsync(p.RootPath);
        var tree = await GitAsync(p.RootPath, "ls-tree", "-r", "--name-only", GitService.DossiersRef);
        tree.Stdout.Should().Contain("ff66ff6");
        tree.Stdout.Should().NotContain("ee55ee5",
            "паспорт чата с ExcludeFromDossiers отсекается на автовыгрузке, как и на ручной");
    }
}
