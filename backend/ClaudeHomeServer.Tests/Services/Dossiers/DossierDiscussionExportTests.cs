using System.Text.Json;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Dossiers;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services.Dossiers;

// Интеграционные тесты конспектов обсуждений в ветке ccs/dossiers/v1 (ADR-004 §6):
// настоящий git CLI и настоящий SessionManager (сессии и history.json предзагружаются
// ДО конструирования — паттерн DossierGitExportTests), стор паспортов — реальный на
// temp-конфиге, LLM — мок ICheapTextRunner с захватом промпта. Четыре сценария:
// снятие по фиктивной ленте + путь/index.json, дедуп повторного экспорта, opt-out
// чата, редакция секретов до и после модели.
[Trait("Category", "Slow")]
public class DossierDiscussionExportTests : IDisposable
{
    private const string Owner = "disc-owner";
    private const string Username = "disc-user";

    // Секрет фикстуры: длиннее SecretRedactor.MinExactSecretLength (12), маскируется
    // только точное значение — тест проверяет связку конфиг → провайдер → редакция
    private const string Secret = "ccs-discussion-secret-k4n7vw29qp";

    private const string ModelDigest =
        """
        ## Решения
        - конспект вместо транскрипта

        ## Отвергнуто
        - дословная лента: убивает откровенность
        """;

    private readonly string _temp;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly ChatHistoryService _history;
    private readonly Mock<IHubContext<SessionHub>> _hub;
    private readonly DossierStore _store;
    private readonly DossierDiscussionStore _digests;
    private readonly GitService _git = new(TestLauncherFactory.Instance);
    private readonly List<IDisposable> _disposables = [];

    // Вызовы LLM: ключ места + промпт (для проверок контракта и дедупа)
    private readonly List<(string Key, string Prompt)> _llmCalls = [];
    private readonly Mock<ICheapTextRunner> _cheap = new();

    public DossierDiscussionExportTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "dossier_disc_" + Guid.NewGuid().ToString("N"));
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
        _digests = new DossierDiscussionStore(_config);

        var clients = new Mock<IHubClients>();
        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        _hub = new Mock<IHubContext<SessionHub>>();
        _hub.Setup(h => h.Clients).Returns(clients.Object);

        _cheap
            .Setup(c => c.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, object?, CancellationToken>(
                (key, prompt, _, _, _, _) => _llmCalls.Add((key, prompt)))
            .ReturnsAsync(ModelDigest);
    }

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        try { Directory.Delete(_temp, recursive: true); }
        catch { /* git на Windows держит readonly-объекты — не роняем прогон */ }
    }

    // --- Фикстуры ---

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

    private static Session Sess(string id, string projectId, string csid, string name,
        bool excludeFromDossiers = false) => new()
    {
        Id = id,
        // Владелец заполняется только у чата вне проекта: у проектной сессии он живёт у проекта
        OwnerId = null,
        ProjectId = projectId,
        ClaudeSessionId = csid,
        Name = name,
        ExcludeFromDossiers = excludeFromDossiers,
        Status = SessionStatus.Finished,
        // Год папки конспекта — год создания чата; фиксируем явно, не UtcNow
        CreatedAt = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc),
        UpdatedAt = DateTime.UtcNow,
    };

    private static ChangeDossier Dossier(string projectId, string sha, string subject, string sessionId) => new()
    {
        OwnerId = Owner,
        ProjectId = projectId,
        CommitSha = sha,
        CommitSubject = subject,
        CommittedAt = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero),
        SessionId = sessionId,
        Why = "почему изменение сделано",
        Decisions = ["решили так"],
    };

    // Лента чата — файл ДО конструирования SessionManager, как sessions.json
    private void WriteHistory(string csid, params StoredMessage[] messages)
    {
        var dir = Path.Combine(_temp, "sessions", csid);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "history.json"),
            JsonSerializer.Serialize(messages, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }));
    }

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

    // Конспект-сервис и экспортёр на живом графе зависимостей; LLM — мок класса
    private DossierGitExporter MkExporter(SessionManager sessions)
    {
        var discussions = new DossierDiscussionService(_digests, _store, sessions,
            _cheap.Object, MkSecrets());
        return new DossierGitExporter(sessions, _store, _git, MkSecrets(), discussions);
    }

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

    private async Task<string> BranchFileAsync(string root, string path)
    {
        var r = await GitAsync(root, "show", $"{GitService.DossiersRef}:{path}");
        r.Ok.Should().BeTrue("файл ветки {0} обязан читаться: {1}", path, r.Stderr);
        return r.Stdout;
    }

    // --- (а) снятие конспекта по ленте: файл discussions/{год}/{sess7}-{slug}.md в ветке,
    // шапка с якорями, запись паспорта в index.json ссылается на конспект ---

    [Fact]
    public async Task КонспектСнимаетсяПоЛентеИЕдетВВеткуСIndex()
    {
        var p = await MkRepoProjectAsync("repo_disc_a");
        var s = Sess("sess-disc-a", p.Id, "csid-disc-a", "Обсуждение конспектов");
        WriteHistory("csid-disc-a",
            new StoredUserMessage("нужна ли история решений в git?"),
            new StoredTextMessage("Да: ветка рядом с кодом, конспект вместо транскрипта"));
        _store.Add(Dossier(p.Id, "a1b2c3d4", "feat: конспекты обсуждений", s.Id));
        var exporter = MkExporter(MkSessions(s));

        var result = await exporter.ExportAsync(Owner, p);

        result.Committed.Should().BeTrue("первый экспорт с конспектом создаёт коммит");
        _llmCalls.Should().ContainSingle(c => c.Key == LocalActionCatalog.DiscussionDigest,
            "конспект снимается одним вызовом на чат");
        _llmCalls.Single(c => c.Key == LocalActionCatalog.DiscussionDigest).Prompt
            .Should().Contain("нужна ли история решений в git?")
            .And.Contain("конспект вместо транскрипта",
                "сырьё промпта — реплики пользователя и ответы ассистента");

        var files = await BranchFilesAsync(p.RootPath);
        var digestPath = files.Should().Contain(
            "discussions/2026/sess-di-obsuzhdenie-konspektov.md",
            "год — год создания чата, sess7 — префикс id, slug — транслит темы").Which;

        var md = await BranchFileAsync(p.RootPath, digestPath);
        md.Should().Contain("# Обсуждение конспектов")
            .And.Contain("- Чат: sess-disc-a")
            .And.Contain("конспект вместо транскрипта", "тело конспекта от модели");

        // index.json: запись паспорта этого чата ссылается на конспект
        var index = await BranchFileAsync(p.RootPath, "index.json");
        index.Should().Contain(digestPath, "запись паспорта в index.json несёт путь конспекта");
    }

    // --- (б) дедуп: повторный экспорт не зовёт модель и не плодит коммит (конспект из стора) ---

    [Fact]
    public async Task ПовторныйЭкспорт_МодельНеЗовётсяИКоммитаНет()
    {
        var p = await MkRepoProjectAsync("repo_disc_b");
        var s = Sess("sess-disc-b", p.Id, "csid-disc-b", "Обсуждение дедупа");
        WriteHistory("csid-disc-b", new StoredUserMessage("реплика обсуждения"));
        _store.Add(Dossier(p.Id, "b1b2b3b4", "feat: паспорт для дедупа", s.Id));
        var exporter = MkExporter(MkSessions(s));

        var first = await exporter.ExportAsync(Owner, p);
        first.Committed.Should().BeTrue();
        var callsAfterFirst = _llmCalls.Count(c => c.Key == LocalActionCatalog.DiscussionDigest);

        var second = await exporter.ExportAsync(Owner, p);

        _llmCalls.Count(c => c.Key == LocalActionCatalog.DiscussionDigest)
            .Should().Be(callsAfterFirst, "снятый конспект лежит в сторе — модель повторно не зовётся");
        second.Committed.Should().BeFalse("дерево из стора побайтово совпало с tip — коммита нет");
        (await GitAsync(p.RootPath, "rev-list", "--count", GitService.DossiersRef)).Stdout.Trim()
            .Should().Be("1", "в ветке по-прежнему один коммит");
    }

    // --- (в) opt-out чата: конспект не снимается (модель не зовём вовсе) и не едет в ветку ---

    [Fact]
    public async Task ОптАутЧата_КонспектНеСнимаетсяИНеЕдет()
    {
        var p = await MkRepoProjectAsync("repo_disc_c");
        var hidden = Sess("sess-disc-hidden", p.Id, "csid-disc-hidden", "Скрытый чат", excludeFromDossiers: true);
        var ok = Sess("sess-disc-ok", p.Id, "csid-disc-ok", "Обычный чат");
        WriteHistory("csid-disc-hidden", new StoredUserMessage("тайное обсуждение"));
        WriteHistory("csid-disc-ok", new StoredUserMessage("обычное обсуждение"));
        // Паспорта обоих чатов: guard отсекает hidden на выгрузке, конспект — тем более
        _store.Add(Dossier(p.Id, "c1c2c3c4", "feat: скрытый тумблером", hidden.Id));
        _store.Add(Dossier(p.Id, "d1d2d3d4", "feat: остаётся", ok.Id));
        var exporter = MkExporter(MkSessions(hidden, ok));

        await exporter.ExportAsync(Owner, p);

        _llmCalls.Where(c => c.Key == LocalActionCatalog.DiscussionDigest)
            .Should().ContainSingle().Which.Prompt
            .Should().NotContain("тайное обсуждение",
                "для opt-out чата модель не зовётся вовсе — деньги не тратим");
        var files = await BranchFilesAsync(p.RootPath);
        // Реальный путь конспекта hidden-чата: год — год создания (2026), 7 символов
        // id («sess-di»), slug — транслитерация имени «Скрытый чат» («skrytyy-chat»).
        // Прошлый ассерт f.Contains("hidden") был ложнозелёным: «hidden» в slug не входит,
        // и guard BuildFiles при удалении проходил тест без сопротивления
        files.Should().NotContain(DossierGitExporter.DiscussionPath(
                hidden.CreatedAt.Year, hidden.Id, hidden.Name ?? ""),
            "конспект opt-out чата в ветку не едет");
    }

    // --- (д) сценарий «конспект снят раньше, opt-out включили позже»: конспект лежит
    // в DossierDiscussionStore (снят старой выгрузкой, когда чат ещё не был скрыт), а
    // чат теперь ExcludeFromDossiers=true. Guard ShouldExportDossier в BuildFiles обязан
    // отсечь его на ПОВТОРНОЙ выгрузке — без этого записи, снятые до включения тумблера,
    // жили бы в ней бессрочно, и единственный работающий предохранитель на фоновом пути
    // выпал бы из покрытия. По образцу DossierAutoExportTests.ОптАутВключённыйПослеЗахвата. ---

    [Fact]
    public async Task КонспектСнятРаньше_ОптАутВключёнПозже_НеЕдетВВетку()
    {
        var p = await MkRepoProjectAsync("repo_disc_optout_late");
        // Сначала чат без opt-out — выжимаем конспект, кладём в стор напрямую (имитация
        // того, что предыдущая ручная выгрузка сняла и записала его)
        var hidden = Sess("sess-disc-hidden-late", p.Id, "csid-disc-hidden-late", "Скрытый чат");
        var ok = Sess("sess-disc-ok-late", p.Id, "csid-disc-ok-late", "Обычный чат");
        WriteHistory("csid-disc-hidden-late", new StoredUserMessage("обсуждение, которое потом скрыли"));
        WriteHistory("csid-disc-ok-late", new StoredUserMessage("обычное обсуждение"));
        // Положить конспект hidden-чата в стор напрямую: так делает EnsureAsync при
        // первой выгрузке. ok-чат оставляем без конспекта, чтобы guard был единственным,
        // что держит скрытый конспект вне ветки.
        _digests.Set(Owner, p.Id, new DossierDiscussionRecord(
            hidden.Id, hidden.Name ?? "Обсуждение", "конспект от прошлой выгрузки",
            DateTimeOffset.UtcNow));
        _store.Add(Dossier(p.Id, "f1f2f3f4", "feat: паспорт скрытого чата", hidden.Id));
        _store.Add(Dossier(p.Id, "a1a2a3a4", "feat: паспорт обычного чата", ok.Id));

        // Между «снят раньше» и «выгружаем сейчас» пользователь включил opt-out у
        // hidden. Sess() создаёт объект с указанными полями, так что меняем прямо здесь
        hidden.ExcludeFromDossiers = true;
        var exporter = MkExporter(MkSessions(hidden, ok));

        await exporter.ExportAsync(Owner, p);

        var files = await BranchFilesAsync(p.RootPath);
        // Конспект скрытого чата, снятый ДО включения opt-out, не должен появиться в
        // ветке: BuildFiles перепроверяет ShouldExportDossier для каждого конспекта,
        // а не доверяет факту записи в стор. Это второй контур защиты после guard'а
        // EnsureOneAsync (тот вообще не зовёт модель, если чат уже скрыт)
        files.Should().NotContain(DossierGitExporter.DiscussionPath(
                hidden.CreatedAt.Year, hidden.Id, hidden.Name ?? ""),
            "конспект, снятый ДО включения opt-out, отсекается guard'ом BuildFiles");
        // Паспорт чата, у которого ExcludeFromDossiers=true, тоже не едет в ветку.
        // В имени файла — 7-символьный префикс sha (DossierPath), не полный sha
        files.Should().NotContain(f => f.StartsWith("dossiers/", StringComparison.Ordinal)
            && f.Contains("f1f2f3f", StringComparison.Ordinal),
            "паспорт скрытого чата в ветку не едет");
        // А контрольный паспорт обычного чата — едет, чтобы тест не был зелёным по
        // нерелевантной причине (например, ExportAsync бросил исключение)
        files.Should().Contain(f => f.StartsWith("dossiers/", StringComparison.Ordinal)
            && f.Contains("a1a2a3", StringComparison.Ordinal),
            "паспорт обычного чата едет в ветку");
    }

    // --- (г) секреты: из ленты вырезается ДО модели (в промпт не уходит), из ответа
    // модели — ПОСЛЕ (в файл ветки не попадает), маска остаётся ---

    [Fact]
    public async Task Секрет_ВырезаетсяИзПромптаИИзФайлаВетки()
    {
        var p = await MkRepoProjectAsync("repo_disc_d");
        var s = Sess("sess-disc-d", p.Id, "csid-disc-d", "Обсуждение с секретом");
        WriteHistory("csid-disc-d",
            new StoredUserMessage($"подключим провайдер ключом {Secret}"));
        // Мок отвечает конспектом, цитирующим «секрет»: модель могла выдать его сама
        _cheap
            .Setup(c => c.RunAsync(It.Is<string>(k => k == LocalActionCatalog.DiscussionDigest),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<object?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, object?, CancellationToken>(
                (key, prompt, _, _, _, _) => _llmCalls.Add((key, prompt)))
            .ReturnsAsync($"## Решения\n- ключ {Secret} держать в Local.json");
        _store.Add(Dossier(p.Id, "e1e2e3e4", "feat: провайдер", s.Id));
        var exporter = MkExporter(MkSessions(s));

        await exporter.ExportAsync(Owner, p);

        _llmCalls.Should().ContainSingle(c => c.Key == LocalActionCatalog.DiscussionDigest)
            .Which.Prompt.Should().NotContain(Secret).And.Contain("[REDACTED",
                "лента редактируется до модели — секрет не уходит в промпт");

        var files = await BranchFilesAsync(p.RootPath);
        var digestPath = files.Single(f => f.StartsWith("discussions/", StringComparison.Ordinal));
        var md = await BranchFileAsync(p.RootPath, digestPath);
        md.Should().NotContain(Secret, "пост-редакция вычищает секрет из ответа модели");
        md.Should().Contain("[REDACTED", "секрет замаскирован, а не потерян вместе с текстом");
    }
}
