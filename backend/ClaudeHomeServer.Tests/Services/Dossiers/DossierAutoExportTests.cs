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
    private readonly DossierDiscussionStore _digests;
    private readonly DossierCaptureState _state;
    private readonly GitService _git = new(TestLauncherFactory.Instance);
    private readonly List<IDisposable> _disposables = [];

    // Вызовы LLM по ключу места: автовыгрузка обязана не звать модель вовсе (снятие
    // конспектов — только по явной команде человека), счётчик ловит и один вызов
    private readonly List<string> _llmCalls = [];
    private readonly Mock<ICheapTextRunner> _cheap = new();

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
        _digests = new DossierDiscussionStore(_config);
        _state = new DossierCaptureState(_config);

        _cheap
            .Setup(c => c.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, object?, CancellationToken>(
                (key, _, _, _, _, _) => _llmCalls.Add(key))
            .ReturnsAsync("конспект");

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

    private static Session Sess(string id, string projectId, string? csid = null,
        bool excludeFromDossiers = false) => new()
    {
        Id = id,
        // Владелец заполняется только у чата вне проекта: у проектной сессии он живёт у проекта
        OwnerId = null,
        ProjectId = projectId,
        ClaudeSessionId = csid,
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

    // Лента чата — файл пишется ДО конструирования SessionManager (тот же паттерн, что
    // sessions.json). Нужна тестам конспектов: без ленты снятие молча пропустило бы чат,
    // и счётчик вызовов LLM не поймал бы регрессию
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

    // Автовыгрузчик на живом графе зависимостей; StartAsync подключает подписку на стор —
    // как это делает хост в проде. Конспекты: сервис на моке LLM со счётчиком вызовов
    // (автовыгрузка модель звать не обязана вовсе)
    private DossierDiscussionService MkDiscussions(SessionManager sessions) =>
        new(_digests, _store, sessions, _cheap.Object, new InstanceSecretsProvider(_config));

    private DossierAutoExporter MkAutoExporter(SessionManager sessions) =>
        new(_store, _projects, sessions, _git,
            new InstanceSecretsProvider(_config), MkDiscussions(sessions), _flags, _state, _config);

    // Автоимпортёр на тех же сторах, что и автовыгрузка, — для тестов петли
    // «автовыгрузка двигает tip → тик автоимпорта» (разбор 23.08)
    private DossierAutoImporter MkAutoImporter() => new(_projects, _store, _state, _git,
        new InstanceSecretsProvider(_config), _flags);

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

    // --- Фикстура чужой ветки: как её написал бы сосед по общей папке / вторая машина
    // (git pull) — пишется напрямую plumbing-методом, минуя наш экспортёр ---

    private static readonly JsonSerializerOptions IndexOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private async Task WriteForeignBranchAsync(Project p, params ChangeDossier[] dossiers)
    {
        var files = new List<GitDossierFile>();
        var entries = new List<DossierIndexEntry>();
        foreach (var d in dossiers)
        {
            var path = DossierGitExporter.DossierPath(d.CommittedAt, d.CommitSha, d.CommitSubject);
            files.Add(new GitDossierFile(path, DossierGitExporter.FormatDossier(d, [])));
            entries.Add(new DossierIndexEntry(d.CommitSha, path, d.CommitSubject, d.CommittedAt,
                Discussion: null, TaskId: d.TaskId, SupersededSha: d.SupersededSha));
        }
        files.Add(new GitDossierFile("index.json",
            JsonSerializer.Serialize(new DossierBranchIndex(1, entries), IndexOpts)));
        await _git.WriteDossiersBranchAsync(p.OwnerId, p.RootPath, files, "test: ветка соседа");
    }

    // --- (д) петля автовыгрузка→автоимпорт (разбор 23.08): tip, созданный нашей
    // автовыгрузкой, помечается своим — тик автоимпорта не завозит копии собственных паспортов ---

    [Fact]
    public async Task АвтовыгрузкаЗатемТикАвтоимпорта_НичегоНеИмпортирует()
    {
        var (p, user) = await MkRepoProjectAsync("repo_loop_own", flagOn: true);
        _projects.Update(p.Id, name: null, rootPath: null, autoImportDossiers: true);
        var s = Sess("sess-loop-own", p.Id);
        var auto = MkAutoExporter(MkSessions(s));
        await auto.StartAsync(default);

        _store.Add(Dossier(user.Id, p.Id, "a1b2c3d4", "feat: свой паспорт", s.Id));
        await auto.ExportSafeAsync(user.Id, p.Id);   // та же работа, что по дебаунсу — без ожидания

        await MkAutoImporter().TickAsync();

        var list = _store.List(user.Id, p.Id);
        list.Should().ContainSingle("тик автоимпорта не завёз копию собственного паспорта — tip помечен нашим");
        list.Single().Origin.Should().Be(DossierOrigin.Own);
    }

    // --- (е) чужой tip после нашей выгрузки: ветка переписана извне (pull соседа) —
    // отличается от помеченного своего tip и импортируется штатно ---

    [Fact]
    public async Task ЧужойTipПослеНашейВыгрузки_ИмпортируетсяТиком()
    {
        var (p, user) = await MkRepoProjectAsync("repo_loop_foreign", flagOn: true);
        _projects.Update(p.Id, name: null, rootPath: null, autoImportDossiers: true);
        var s = Sess("sess-loop-foreign", p.Id);
        var auto = MkAutoExporter(MkSessions(s));
        // StartAsync НЕ вызываем: тест сам зовёт ExportSafeAsync, а подписка через
        // OnOwnDossierChanged поставила бы параллельный таймер дебаунса (тестовая пауза
        // 1 с), который мог бы сработать ПОСЛЕ WriteForeignBranchAsync и перезаписать
        // чужой tip нашим — ранее это ломало тест на медленных CI-раннерах.

        _store.Add(Dossier(user.Id, p.Id, "b2c3d4e5", "feat: свой паспорт", s.Id));
        await auto.ExportSafeAsync(user.Id, p.Id);
        await MkAutoImporter().TickAsync();
        _store.List(user.Id, p.Id).Should()
            .ContainSingle("свой tip пропущен — в сторе только own-запись");

        var foreign = Dossier("neighbor-owner", p.Id, "c3d4e5f6", "feat: паспорт соседа", "sess-neighbor");
        await WriteForeignBranchAsync(p, foreign);

        await MkAutoImporter().TickAsync();

        var list = _store.List(user.Id, p.Id);
        list.Should().HaveCount(2, "чужой tip отличается от помеченного своего — записи соседа импортированы");
        list.Should().Contain(d => d.Origin == DossierOrigin.Own && d.CommitSha == "b2c3d4e5");
        list.Should().Contain(d => d.Origin == DossierOrigin.Imported && d.CommitSha == "c3d4e5f6");
    }

    // --- (ж) конспекты — только по решению человека (разбор 23.08): автовыгрузка модель
    // не зовёт вовсе, паспорта везёт в ветку; уже снятый конспект из стора едет как обычно ---

    [Fact]
    public async Task Автовыгрузка_КонспектыНеСнимает_СнятыйРаньшеВезётВВетку()
    {
        var (p, user) = await MkRepoProjectAsync("repo_auto_digest", flagOn: true);
        var withDigest = Sess("sess-auto-dig", p.Id, "csid-auto-dig");
        var fresh = Sess("sess-auto-fresh", p.Id, "csid-auto-fresh");
        // Ленты у обоих чатов непустые: если бы автовыгрузка звала EnsureAsync, модель
        // была бызвана на каждый чат без конспекта — счётчик поймает и один вызов
        WriteHistory("csid-auto-dig", new StoredUserMessage("обсуждение, конспект уже снят"));
        WriteHistory("csid-auto-fresh", new StoredUserMessage("обсуждение без конспекта"));
        var auto = MkAutoExporter(MkSessions(withDigest, fresh));
        await auto.StartAsync(default);

        // Конспект первого чата снят раньше (ручной выгрузкой) и лежит в сторе
        _digests.Set(user.Id, p.Id, new DossierDiscussionRecord(
            withDigest.Id, "Тема снятого конспекта", "конспект из стора", DateTimeOffset.UtcNow));
        _store.Add(Dossier(user.Id, p.Id, "ab12cd34", "feat: паспорт с конспектом", withDigest.Id));
        _store.Add(Dossier(user.Id, p.Id, "ef56ab78", "feat: паспорт без конспекта", fresh.Id));

        await auto.ExportSafeAsync(user.Id, p.Id);

        _llmCalls.Should().BeEmpty("автовыгрузка не снимает конспекты — модель не зовётся ни разу");
        var tree = await GitAsync(p.RootPath, "ls-tree", "-r", "--name-only", GitService.DossiersRef);
        tree.Stdout.Should().Contain("ab12cd3").And.Contain("ef56ab7", "паспорта в ветке");
        tree.Stdout.Should().Contain("discussions/",
            "уже снятый конспект из стора едет в ветку и на авто-пути");
    }

    // --- Гейт «ветка заведомо наша» (блокеры консилиума 23.08, вариант «б»: сузить
    // автовыгрузку, а не делать merge-режим). Снапшот собирается только из своих
    // паспортов — фон обязан молчать, когда ветка может содержать чужое. ---

    private async Task<string> BranchTipAsync(string root) =>
        (await GitAsync(root, "rev-parse", GitService.DossiersRef)).Stdout.Trim();

    // (а) чужой tip (git pull соседа/второй машины): автовыгрузка не пишет ничего,
    // ветка не изменилась — полный снапшот стёр бы записи соседа
    [Fact]
    public async Task ЧужойTip_АвтовыгрузкаНеТрогаетВетку()
    {
        var (p, user) = await MkRepoProjectAsync("repo_gate_foreign", flagOn: true);
        var s = Sess("sess-gate-foreign", p.Id);
        var auto = MkAutoExporter(MkSessions(s));

        var foreign = Dossier("neighbor-owner", p.Id, "77aa77aa", "feat: паспорт соседа", "sess-neighbor");
        await WriteForeignBranchAsync(p, foreign);
        var tipBefore = await BranchTipAsync(p.RootPath);

        _store.Add(Dossier(user.Id, p.Id, "88bb88bb", "feat: свой паспорт", s.Id));
        await auto.ExportSafeAsync(user.Id, p.Id);

        (await BranchTipAsync(p.RootPath)).Should().Be(tipBefore,
            "чужой tip — фон не пишет в ветку ничего, судьбу ветки решает ручная кнопка «Выгрузить»");
        var tree = await GitAsync(p.RootPath, "ls-tree", "-r", "--name-only", GitService.DossiersRef);
        tree.Stdout.Should().NotContain("88bb88",
            "свой паспорт не доехал до чужой ветки: записи соседа не затираются снапшотом");
    }

    // (б) наш tip: после собственной выгрузки следующая автовыгрузка работает как раньше
    [Fact]
    public async Task НашTip_СледующаяАвтовыгрузкаРаботаетКакРаньше()
    {
        var (p, user) = await MkRepoProjectAsync("repo_gate_ours", flagOn: true);
        var s = Sess("sess-gate-ours", p.Id);
        var auto = MkAutoExporter(MkSessions(s));

        _store.Add(Dossier(user.Id, p.Id, "99cc99cc", "feat: первый паспорт", s.Id));
        await auto.ExportSafeAsync(user.Id, p.Id);
        (await HasBranchAsync(p.RootPath)).Should()
            .BeTrue("первая выгрузка: ветки не было нигде — фон её создаёт");

        _store.Add(Dossier(user.Id, p.Id, "a0d0a0d0", "feat: второй паспорт", s.Id));
        await auto.ExportSafeAsync(user.Id, p.Id);

        var tree = await GitAsync(p.RootPath, "ls-tree", "-r", "--name-only", GitService.DossiersRef);
        tree.Stdout.Should().Contain("99cc99c").And.Contain("a0d0a0d",
            "tip помечен MarkOwnTip после первой выгрузки — вторая пишет поверх как раньше");
        (await GitAsync(p.RootPath, "rev-list", "--count", GitService.DossiersRef)).Stdout.Trim()
            .Should().Be("2", "каждая выгрузка с изменившимся деревом — отдельный коммит");
    }

    // (в) есть только origin-ветка (репо стянули fetch'ем): фон не создаёт локальную —
    // корневой коммит без родителя сделал бы ветку непрошиваемой сиротой
    [Fact]
    public async Task ТолькоОriginВетка_ФонНеСоздаётЛокальную()
    {
        var (p, user) = await MkRepoProjectAsync("repo_gate_orphan", flagOn: true);
        var s = Sess("sess-gate-orphan", p.Id);
        var auto = MkAutoExporter(MkSessions(s));

        await InitBareRemoteAsync(p.RootPath, "remote_gate_orphan.git");
        await WriteForeignBranchAsync(p,
            Dossier("neighbor-owner", p.Id, "11bb22cc", "feat: паспорт соседа", "sess-neighbor"));
        var push = await GitAsync(p.RootPath, "push", "origin", GitService.DossiersRef);
        push.Ok.Should().BeTrue("фикстура: ветка запушена в origin: {0}", push.Stderr);
        // Сносим локальную копию — репо, куда ветку привёз fetch: remote-tracking остаётся
        (await GitAsync(p.RootPath, "update-ref", "-d", GitService.DossiersRef)).Ok.Should().BeTrue();

        _store.Add(Dossier(user.Id, p.Id, "33dd44ee", "feat: свой паспорт", s.Id));
        await auto.ExportSafeAsync(user.Id, p.Id);

        (await HasBranchAsync(p.RootPath)).Should().BeFalse(
            "только origin-ветка — фон не создаёт локальную сироту поверх origin-версии");
        (await GitAsync(p.RootPath, "rev-parse", "--verify", "--quiet",
                "refs/remotes/origin/ccs/dossiers/v1")).Ok.Should()
            .BeTrue("origin-версия ветки не тронута фоном");
    }

    // (г) общая папка двух владельцев: фон не выгружает вовсе (переток паспортов к
    // соседу без единого клика недопустим), ручной экспорт с предупреждением работает
    [Fact]
    public async Task ОбщаяПапкаДвухВладельцев_ФонМолчит_РучнойЭкспортРаботает()
    {
        var (p, user) = await MkRepoProjectAsync("repo_gate_shared", flagOn: true);
        var neighbor = _users.Add("auto-shared-nb", "password-123456", "user");
        // Сосед подключил ту же папку своим проектом — признак фазы confirmShared
        _projects.Create("gate-shared-neighbor", p.RootPath, neighbor.Id, neighbor.Username);

        var s = Sess("sess-gate-shared", p.Id);
        var sessions = MkSessions(s);
        var auto = MkAutoExporter(sessions);
        _store.Add(Dossier(user.Id, p.Id, "55ee66ff", "feat: паспорт общей папки", s.Id));

        await auto.ExportSafeAsync(user.Id, p.Id);
        (await HasBranchAsync(p.RootPath)).Should().BeFalse(
            "папку делят два владельца — фон не выгружает без ведома человека");

        // Ручной путь (тот же DossierGitExporter, что за POST /dossiers/export): человек
        // видел диалог и предупреждение об общей папке — выгрузка работает
        var manual = new DossierGitExporter(sessions, _store, _git,
            new InstanceSecretsProvider(_config), MkDiscussions(sessions));
        var result = await manual.ExportAsync(user.Id, p, ensureDigests: false);
        result.Committed.Should().BeTrue("ручная выгрузка общей папкой не ограничена");
        (await HasBranchAsync(p.RootPath)).Should().BeTrue("ручной экспорт создал ветку");
    }

    // (д) чистый случай «ветки нет вовсе» (ни локальной, ни origin): фон создаёт ветку
    // и возит паспорта, как раньше
    [Fact]
    public async Task ВеткиНетВовсе_ФонСоздаётВетку()
    {
        var (p, user) = await MkRepoProjectAsync("repo_gate_clean", flagOn: true);
        var s = Sess("sess-gate-clean", p.Id);
        var auto = MkAutoExporter(MkSessions(s));

        _store.Add(Dossier(user.Id, p.Id, "77ff88aa", "feat: первая выгрузка", s.Id));
        await auto.ExportSafeAsync(user.Id, p.Id);

        (await HasBranchAsync(p.RootPath)).Should()
            .BeTrue("ветки не было нигде — первая выгрузка на этой машине, фон создаёт её");
        var tree = await GitAsync(p.RootPath, "ls-tree", "-r", "--name-only", GitService.DossiersRef);
        tree.Stdout.Should().Contain("77ff88a", "паспорт доехал до ветки");
    }

    // --- Сторож: «push никогда не автоматический» (инвариант ADR-004 §6). Bare-репозиторий
    // как origin — идеальная ловушка: туда уезжает всё, что хоть раз дёрнет «git push». Если
    // автовыгрузка хоть когда-нибудь сама опубликует ветку паспортов, ls-remote вернёт
    // непустой список. До этой правки слово push не встречалось ни в одном тесте Dossier. ---

    private async Task<string> InitBareRemoteAsync(string projectRoot, string bareName)
    {
        var bare = Path.Combine(_temp, bareName);
        Directory.CreateDirectory(bare);
        var init = await GitAsync(bare, "init", "--bare");
        init.Ok.Should().BeTrue("bare-репозиторий обязан создаться: {0}", init.Stderr);
        // Настраиваем origin так, чтобы bare был приёмником push: это делает сторож строгим —
        // пустой bare-репозиторий после ls-remote --heads ДО теста означает «нечему отвечать»
        var add = await GitAsync(projectRoot, "remote", "add", "origin", bare);
        add.Ok.Should().BeTrue("remote add origin: {0}", add.Stderr);
        return bare;
    }

    [Fact]
    public async Task Автовыгрузка_НикогдаНеПушитВRemote()
    {
        var (p, user) = await MkRepoProjectAsync("repo_push_guard", flagOn: true);
        var s = Sess("sess-push-guard", p.Id);
        await InitBareRemoteAsync(p.RootPath, "remote_push_guard.git");

        var auto = MkAutoExporter(MkSessions(s));
        await auto.StartAsync(default);

        _store.Add(Dossier(user.Id, p.Id, "deadbeef", "feat: проверка сторожа push", s.Id));
        await AwaitBranchAsync(p.RootPath);

        // Локальная ветка паспортов создана — это нормальная часть автовыгрузки
        (await HasBranchAsync(p.RootPath)).Should().BeTrue(
            "автовыгрузка создаёт ветку паспортов локально — это её работа");

        // А вот в origin её быть не должно. ls-remote --heads возвращает ровно
        // запрошенный ref, если он есть в remote: пустой stdout — гарантия «push не было».
        var ls = await GitAsync(p.RootPath, "ls-remote", "--heads", "origin", GitService.DossiersRef);
        ls.Ok.Should().BeTrue("ls-remote не должен падать: {0}", ls.Stderr);
        ls.Stdout.Trim().Should().BeEmpty(
            "ветка {0} в origin — это автоматический push, а инвариант «push никогда не автоматический»",
            GitService.DossiersRef);

        // Контрольный: даже без фильтра по ref в origin нет ни одной ветки
        var lsAll = await GitAsync(p.RootPath, "ls-remote", "--heads", "origin");
        lsAll.Stdout.Trim().Should().BeEmpty(
            "bare-репозиторий полностью пуст — автовыгрузка не отправляла ничего, ни под каким предлогом");
    }
}
