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

// Интеграционные тесты экспорта паспортов в ветку ccs/dossiers/v1 (ADR-004 §6, «Истории
// решений», волна 4): настоящий git CLI (plumbing-цепочка GitService) и настоящий
// SessionManager — сессии предзагружаются в sessions.json ДО конструирования менеджера
// (паттерн SessionStatusTests), процессы CLI не запускаются. Стор и провайдер секретов —
// реальные, на temp-конфиге. Пять сценариев изоляции: нетронутые рабочее дерево/HEAD,
// opt-out чат, секрет инстанса, идемпотентность повторного экспорта, отсечение чужих
// проектов и личных чатов.
[Trait("Category", "Slow")]
public class DossierGitExportTests : IDisposable
{
    private const string Owner = "dossier-owner";
    private const string Username = "dossier-user";

    // Секрет фикстуры: длиннее SecretRedactor.MinExactSecretLength (12) и не совпадает ни с
    // одним regex-форматом (sk-…, JWT, Bearer…) — маскирует его ТОЛЬКО точное значение из
    // InstanceSecretsProvider, то есть тест проверяет всю связку конфиг → провайдер → редакция.
    private const string Secret = "ccs-instance-secret-zq81xw40v7";

    private readonly string _temp;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly ChatHistoryService _history;
    private readonly Mock<IHubContext<SessionHub>> _hub;
    private readonly DossierStore _store;
    private readonly GitService _git = new(TestLauncherFactory.Instance);
    private readonly List<IDisposable> _disposables = [];

    public DossierGitExportTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "dossier_export_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_temp, "projects.json"),
            })
            .Build();

        var userStore = new UserStore(_config,
            new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, userStore, new AppSettingsService(_config));
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

    // Репозиторий проекта: init + начальный коммит (чистое рабочее дерево), затем сам проект
    private async Task<Project> MkRepoProjectAsync(string name)
    {
        var dir = Path.Combine(_temp, name);
        Directory.CreateDirectory(dir);
        await _git.InitAsync(null, dir);
        await _git.RunAsync(null, dir, ["config", "user.email", "dossiers@test"]);
        await _git.RunAsync(null, dir, ["config", "user.name", "Тест Досье"]);
        await File.WriteAllTextAsync(Path.Combine(dir, "readme.md"), "содержимое\n");
        await _git.StageAllAsync(null, dir);
        await _git.CommitAsync(null, dir, "начальный коммит");
        return _projects.Create(name, dir, Owner, Username);
    }

    private static Session Sess(string id, string? projectId, bool excludeFromDossiers = false) => new()
    {
        Id = id,
        // Владелец заполняется только у чата вне проекта: у проектной сессии он живёт у проекта
        OwnerId = projectId is null ? Owner : null,
        ProjectId = projectId,
        ExcludeFromDossiers = excludeFromDossiers,
        Status = SessionStatus.Finished,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static ChangeDossier Dossier(string projectId, string sha, string subject, string sessionId) => new()
    {
        OwnerId = Owner,
        ProjectId = projectId,
        CommitSha = sha,
        CommitSubject = subject,
        CommittedAt = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero),
        SessionId = sessionId,
        Why = "почему изменение сделано",
        Decisions = ["решили так"],
    };

    // SessionManager с предзагруженными сессиями: файл пишется ДО конструирования, реестр
    // наполняется через LoadSessions (паттерн SessionStatusTests) — без запуска процессов CLI.
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
        // паттерн, см. SessionStatusTests.CreateSessionManager), все сторы — на temp-конфиге.
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

    // Экспортёр собирается как в DossiersController — на живом графе зависимостей
    private DossierGitExporter MkExporter(SessionManager sessions) =>
        new(sessions, _store, _git, MkSecrets());

    // Провайдер секретов со своим конфигом: единственный секрет фикстуры — ключ провайдера
    private InstanceSecretsProvider MkSecrets() =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_temp, "projects.json"),
            ["LlmProviders:ccs-dummy:ApiKey"] = Secret,
        }).Build());

    // --- Хелперы git-проверок ---

    private Task<GitResult> GitAsync(string root, params string[] args) => _git.RunAsync(null, root, args);

    private async Task<string[]> BranchFilesAsync(string root)
    {
        var r = await GitAsync(root, "ls-tree", "-r", "--name-only", GitService.DossiersRef);
        r.Ok.Should().BeTrue("ветка паспортов должна существовать после экспорта: {0}", r.Stderr);
        return r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    // ВСЁ дерево ветки: путь + содержимое каждого файла (паспорта и index.json)
    private async Task<List<(string Path, string Content)>> BranchTreeAsync(string root)
    {
        var files = await BranchFilesAsync(root);
        var tree = new List<(string, string)>(files.Length);
        foreach (var f in files)
        {
            var c = await GitAsync(root, "show", $"{GitService.DossiersRef}:{f}");
            c.Ok.Should().BeTrue("файл ветки {0} обязан читаться: {1}", f, c.Stderr);
            tree.Add((f, c.Stdout));
        }
        return tree;
    }

    // --- (а) Экспорт идёт строго через плюминг: рабочее дерево, индекс и HEAD проекта не трогаются ---

    [Fact]
    public async Task Экспорт_НеТрогаетРабочееДеревоИHead()
    {
        var p = await MkRepoProjectAsync("repo_a");
        var s = Sess("sess-a", p.Id);
        _store.Add(Dossier(p.Id, "11aa22bb", "feat: первая фича", s.Id));
        var exporter = MkExporter(MkSessions(s));

        var headBefore = (await GitAsync(p.RootPath, "rev-parse", "HEAD")).Stdout.Trim();
        var branchBefore = (await GitAsync(p.RootPath, "symbolic-ref", "--short", "HEAD")).Stdout.Trim();

        var result = await exporter.ExportAsync(Owner, p);

        result.Committed.Should().BeTrue("первый экспорт создаёт коммит — иначе проверки ниже вакуумны");
        (await GitAsync(p.RootPath, "rev-parse", "--verify", GitService.DossiersRef)).Ok.Should().BeTrue();

        (await GitAsync(p.RootPath, "rev-parse", "HEAD")).Stdout.Trim()
            .Should().Be(headBefore, "HEAD проекта не сдвинулся");
        (await GitAsync(p.RootPath, "symbolic-ref", "--short", "HEAD")).Stdout.Trim()
            .Should().Be(branchBefore, "текущая ветка рабочего дерева не сменилась на ветку паспортов");
        (await GitAsync(p.RootPath, "status", "--porcelain")).Stdout.Trim()
            .Should().BeEmpty("рабочее дерево и индекс не тронуты: ни правок, ни новых файлов");
        Directory.Exists(Path.Combine(p.RootPath, "dossiers")).Should()
            .BeFalse("снапшот ветки не должен материализоваться в рабочем дереве");
    }

    // --- (б) opt-out «не включать в летопись» уважается и на выгрузке, не только при захвате ---

    [Fact]
    public async Task ЧатИсключённыйИзЛетописи_НеПопадаетВВетку()
    {
        var p = await MkRepoProjectAsync("repo_b");
        var ok = Sess("sess-b-ok", p.Id);
        var hidden = Sess("sess-b-hidden", p.Id, excludeFromDossiers: true);
        _store.Add(Dossier(p.Id, "11aa11aa", "feat: остаётся в летописи", ok.Id));
        _store.Add(Dossier(p.Id, "22bb22bb", "feat: скрыт тумблером чата", hidden.Id));
        var exporter = MkExporter(MkSessions(ok, hidden));

        var result = await exporter.ExportAsync(Owner, p);

        result.Exported.Should().Be(1, "паспорт чата с ExcludeFromDossiers отсекается на выгрузке");
        var tree = await BranchTreeAsync(p.RootPath);
        tree.Where(t => t.Path.StartsWith("dossiers/"))
            .Should().ContainSingle("в ветке ровно один паспорт — только чат без opt-out");
        tree.Should().Contain(t => t.Content.Contains("11aa11aa"), "паспорт обычного чата присутствует");
        tree.Should().NotContain(t => t.Content.Contains("22bb22bb"),
            "ни один файл ветки не упоминает коммит скрытого чата");
        tree.Should().NotContain(t => t.Content.Contains("скрыт тумблером"),
            "subject скрытого паспорта не протекает и в index.json");
    }

    // --- (в) секрет инстанса не протекает НИ В ОДИН файл ветки: сканируется всё дерево
    // (паспорт и index.json) плюс пути — subject с секретом участвует и в slug имени файла ---

    [Fact]
    public async Task СекретИнстанса_НеПопадаетНиВОдинФайлВетки()
    {
        var p = await MkRepoProjectAsync("repo_c");
        var s = Sess("sess-c", p.Id);
        _store.Add(new ChangeDossier
        {
            OwnerId = Owner,
            ProjectId = p.Id,
            CommitSha = "ab12cd34",
            // Секрет и в subject: slug пути строится из отредактированного subject — проверяем и его
            CommitSubject = $"feat: подключить провайдер ключом {Secret}",
            CommittedAt = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero),
            SessionId = s.Id,
            Why = $"нужен был обходной маршрут через ключ {Secret}",
            Decisions = [$"ключ {Secret} держать в Local.json"],
            Pitfalls = [$"не светить {Secret} в логах"],
        });
        var exporter = MkExporter(MkSessions(s));

        var result = await exporter.ExportAsync(Owner, p);

        result.Committed.Should().BeTrue();
        var tree = await BranchTreeAsync(p.RootPath);
        tree.Should().HaveCount(2, "паспорт + index.json");
        foreach (var (path, content) in tree)
        {
            path.Should().NotContain(Secret, "путь файла ветки не должен содержать секрет (slug из subject)");
            content.Should().NotContain(Secret, "содержимое {0} не должно содержать секрет", path);
        }
        tree.Should().Contain(t => t.Content.Contains("[REDACTED:instance-secret]"),
            "секрет замаскирован редакцией, а не потерян вместе с текстом");
    }

    // --- (г) повторный экспорт без новых паспортов не плодит коммит (дедуп по дереву) ---

    [Fact]
    public async Task ПовторныйЭкспорт_БезНовыхПаспортов_НеСоздаётКоммит()
    {
        var p = await MkRepoProjectAsync("repo_d");
        var s = Sess("sess-d", p.Id);
        _store.Add(Dossier(p.Id, "33cc33cc", "feat: единственный паспорт", s.Id));
        var exporter = MkExporter(MkSessions(s));

        var first = await exporter.ExportAsync(Owner, p);
        first.Committed.Should().BeTrue();
        first.CommitSha.Should().NotBeNullOrEmpty();

        var second = await exporter.ExportAsync(Owner, p);

        second.Committed.Should().BeFalse("дерево снапшота совпало с tip ветки — нового коммита быть не должно");
        second.CommitSha.Should().Be(first.CommitSha, "tip ветки не сдвинулся");
        (await GitAsync(p.RootPath, "rev-list", "--count", GitService.DossiersRef)).Stdout.Trim()
            .Should().Be("1", "в ветке по-прежнему ровно один коммит");
    }

    // --- (д) guard выгрузки fail-closed: паспорт чата другого проекта (того же владельца!)
    // и личного чата в ветку не едет, даже если стор их атрибутировал этому проекту ---

    [Fact]
    public async Task ЧатДругогоПроектаИЛичныйЧат_НеПопадаютВВетку()
    {
        var p1 = await MkRepoProjectAsync("repo_e1");
        var p2 = await MkRepoProjectAsync("repo_e2");
        var own = Sess("sess-e-own", p1.Id);
        var other = Sess("sess-e-other", p2.Id);
        var personal = Sess("sess-e-personal", projectId: null);
        // Все три паспорта лежат в сторе p1 — атрибуция стора «испорчена» (чаты-источники
        // чужие): именно такие записи и обязан отсекать guard выгрузки, стор сам их не чистит
        _store.Add(Dossier(p1.Id, "44dd44dd", "feat: паспорт чата этого проекта", own.Id));
        _store.Add(Dossier(p1.Id, "55ee55ee", "feat: паспорт чата другого проекта", other.Id));
        _store.Add(Dossier(p1.Id, "66ff66ff", "feat: паспорт личного чата", personal.Id));
        var exporter = MkExporter(MkSessions(own, other, personal));

        var result = await exporter.ExportAsync(Owner, p1);

        result.Exported.Should().Be(1, "чужой проект и личный чат отсекаются guard'ом выгрузки");
        var tree = await BranchTreeAsync(p1.RootPath);
        tree.Where(t => t.Path.StartsWith("dossiers/"))
            .Should().ContainSingle("в ветке ровно один паспорт — только чат этого проекта");
        tree.Should().Contain(t => t.Content.Contains("44dd44dd"));
        tree.Should().NotContain(t => t.Content.Contains("55ee55ee"),
            "паспорт чата другого проекта не попадает в ветку");
        tree.Should().NotContain(t => t.Content.Contains("66ff66ff"),
            "паспорт личного чата не попадает в ветку");
    }
}
